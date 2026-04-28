using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using SpotifyLyricsOverlay.Wpf.Models;
using SpotifyLyricsOverlay.Wpf.Services;
using WinForms = System.Windows.Forms;

namespace SpotifyLyricsOverlay.Wpf;

public partial class MainWindow : Window
{
    private readonly SettingsStore _store = new();
    private readonly SpotifyService _spotify;
    private readonly LyricsService _lyricsService;
    private readonly DispatcherTimer _playerTimer = new();
    private readonly DispatcherTimer _lyricsTimer = new();
    private WinForms.NotifyIcon? _tray;
    private WinForms.ToolStripMenuItem? _lockTrayItem;
    private HwndSource? _source;
    private AppSettings _settings = new();
    private TrackInfo? _track;
    private LyricsResult? _lyrics;
    private DateTimeOffset _trackStartedAt = DateTimeOffset.UtcNow;
    private int _progressAtStart;
    private int _lastProgressMs;
    private string _lyricsTrackId = "";
    private string _renderedCurrent = "";
    private string _renderedNext = "";
    private bool _pollInFlight;
    private bool _isQuitting;
    private bool _isLocked;
    private bool _loaded;

    public MainWindow()
    {
        InitializeComponent();
        _spotify = new SpotifyService(_store);
        _lyricsService = new LyricsService(_store);

        _playerTimer.Interval = TimeSpan.FromMilliseconds(2500);
        _playerTimer.Tick += async (_, _) => await PollPlayerAsync();
        _lyricsTimer.Tick += (_, _) => RenderSyncedLyrics();
        SizeChanged += (_, _) => QueueMarqueeUpdate();
    }

    protected override async void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var helper = new WindowInteropHelper(this);
        _source = HwndSource.FromHwnd(helper.Handle);
        _source?.AddHook(WndProc);
        NativeMethods.RegisterToggleHotKey(helper);
        NativeMethods.SetClickThrough(helper.Handle, false);

        CreateTray();
        await LoadSettingsAsync();
        _loaded = true;

        if (await _spotify.HasTokenAsync()) ShowLyrics();
        else ShowSetup();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_isQuitting)
        {
            e.Cancel = true;
            Hide();
            StopPolling();
            return;
        }

        StopPolling();
        var helper = new WindowInteropHelper(this);
        NativeMethods.UnregisterToggleHotKey(helper);
        _tray?.Dispose();
        base.OnClosing(e);
    }

    private async Task LoadSettingsAsync()
    {
        _settings = await _store.ReadSettingsAsync();
        ClientIdBox.Text = _settings.ClientId;
        FontSlider.Value = _settings.FontSize;
        ColorBox.Text = _settings.TextColor;
        ApplyLineMode();
        ApplyStyle();
    }

    private void CreateTray()
    {
        _tray = new WinForms.NotifyIcon
        {
            Text = "Spotify Lyrics Overlay",
            Icon = System.Drawing.SystemIcons.Application,
            Visible = true,
            ContextMenuStrip = new WinForms.ContextMenuStrip()
        };
        _tray.ContextMenuStrip.Items.Add("Show / Hide", null, (_, _) => ToggleWindow());
        _lockTrayItem = new WinForms.ToolStripMenuItem("Lock", null, async (_, _) => await SetLockedAsync(!_isLocked));
        _tray.ContextMenuStrip.Items.Add(_lockTrayItem);
        _tray.ContextMenuStrip.Items.Add(new WinForms.ToolStripSeparator());
        _tray.ContextMenuStrip.Items.Add("Quit", null, (_, _) => Quit());
        _tray.MouseClick += async (_, args) =>
        {
            if (args.Button != WinForms.MouseButtons.Left) return;
            if (_isLocked)
            {
                await SetLockedAsync(false);
                return;
            }

            ToggleWindow();
        };
    }

    private void ToggleWindow()
    {
        if (IsVisible)
        {
            Hide();
            StopPolling();
            return;
        }

        Show();
        Activate();
        Topmost = true;
        if (LyricsPanel.Visibility == Visibility.Visible) StartPolling();
    }

    private void ShowSetup()
    {
        StopPolling();
        SetupPanel.Visibility = Visibility.Visible;
        LyricsPanel.Visibility = Visibility.Collapsed;
    }

    private void ShowLyrics()
    {
        SetupPanel.Visibility = Visibility.Collapsed;
        LyricsPanel.Visibility = Visibility.Visible;
        StartPolling();
    }

    private void StartPolling()
    {
        if (!IsVisible || _playerTimer.IsEnabled) return;
        _ = PollPlayerAsync();
        _playerTimer.Start();
        ScheduleLyricsRender(TimeSpan.FromMilliseconds(300));
    }

    private void StopPolling()
    {
        _playerTimer.Stop();
        _lyricsTimer.Stop();
        _pollInFlight = false;
    }

    private async Task PollPlayerAsync()
    {
        if (_pollInFlight) return;
        _pollInFlight = true;

        try
        {
            var track = await _spotify.GetPlayerAsync();
            if (track is null)
            {
                ResetPlaybackState();
                TrackTitle.Text = "Waiting for Spotify";
                TrackArtist.Text = "Play a song in Spotify.";
                RenderLines("No active track", "", 0, 0);
                return;
            }

            var changed = _track?.Id != track.Id;
            var previousProgress = CurrentProgress();
            _track = track;
            SyncProgress(track, changed, previousProgress);
            TrackTitle.Text = track.Name;
            TrackArtist.Text = track.Artist;

            if (changed || _lyrics is null || _lyricsTrackId != track.Id)
            {
                _lyrics = null;
                _lyricsTrackId = "";
                RenderLines("Loading lyrics...", "", 0, 0);
                _lyrics = await _lyricsService.GetLyricsAsync(track);
                _lyricsTrackId = track.Id;
                RenderSyncedLyrics();
            }
        }
        catch (Exception ex)
        {
            if (_track is null) RenderLines(ex.Message, "", 0, 0);
            else TrackArtist.Text = "Spotify unavailable; retrying...";
        }
        finally
        {
            _pollInFlight = false;
        }
    }

    private void ResetPlaybackState()
    {
        _track = null;
        _lyrics = null;
        _lyricsTrackId = "";
        _progressAtStart = 0;
        _lastProgressMs = 0;
        _renderedCurrent = "";
        _renderedNext = "";
    }

    private int CurrentProgress()
    {
        if (_track is null) return 0;
        if (!_track.IsPlaying) return _track.ProgressMs;
        return _progressAtStart + (int)(DateTimeOffset.UtcNow - _trackStartedAt).TotalMilliseconds;
    }

    private void SyncProgress(TrackInfo track, bool changed, int previousProgress)
    {
        var now = DateTimeOffset.UtcNow;
        if (changed || !track.IsPlaying)
        {
            _trackStartedAt = now;
            _progressAtStart = track.ProgressMs;
            _lastProgressMs = track.ProgressMs;
            return;
        }

        var localProgress = Math.Max(previousProgress, _lastProgressMs);
        var backwardsJump = localProgress - track.ProgressMs;
        var forwardsJump = track.ProgressMs - localProgress;
        if (backwardsJump > 3000 || forwardsJump > 3000)
        {
            _trackStartedAt = now;
            _progressAtStart = track.ProgressMs;
            _lastProgressMs = track.ProgressMs;
            return;
        }

        var stable = Math.Max(localProgress, track.ProgressMs);
        _trackStartedAt = now;
        _progressAtStart = stable;
        _lastProgressMs = stable;
    }

    private void RenderSyncedLyrics()
    {
        _lyricsTimer.Stop();
        var delay = TimeSpan.FromSeconds(1);

        if (_track is null || _lyrics is null)
        {
            ScheduleLyricsRender(delay);
            return;
        }

        if (_lyricsTrackId != "" && _lyricsTrackId != _track.Id)
        {
            ScheduleLyricsRender(delay);
            return;
        }

        if (_lyrics.Source == "plain")
        {
            RenderLines(_lyrics.Plain.ElementAtOrDefault(0) ?? "No synced lyrics", _lyrics.Plain.ElementAtOrDefault(1) ?? "", 0, 0);
            ScheduleLyricsRender(TimeSpan.FromSeconds(5));
            return;
        }

        if (_lyrics.Source != "synced" || _lyrics.Lines.Count == 0)
        {
            RenderLines("No synced lyrics", "", 0, 0);
            ScheduleLyricsRender(TimeSpan.FromSeconds(5));
            return;
        }

        var progress = CurrentProgress() + 250;
        var activeIndex = -1;
        for (var i = 0; i < _lyrics.Lines.Count; i++)
        {
            if (_lyrics.Lines[i].Time <= progress) activeIndex = i;
            else break;
        }

        RenderLines(
            GetLineText(activeIndex, "..."),
            GetLineText(activeIndex + 1, ""),
            GetLyricLineDuration(activeIndex),
            GetLyricLineDuration(activeIndex + 1));
        delay = GetNextRenderDelay(activeIndex, progress);
        ScheduleLyricsRender(delay);
    }

    private TimeSpan GetNextRenderDelay(int activeIndex, int progress)
    {
        var nextIndex = activeIndex + 1;
        var next = _lyrics is not null && nextIndex >= 0 && nextIndex < _lyrics.Lines.Count ? _lyrics.Lines[nextIndex] : null;
        if (next is null) return _track?.IsPlaying == true ? TimeSpan.FromSeconds(1) : TimeSpan.FromMilliseconds(2500);
        if (_track?.IsPlaying != true) return TimeSpan.FromMilliseconds(2500);
        return TimeSpan.FromMilliseconds(Math.Clamp(next.Time - progress + 20, 120, 5000));
    }

    private string GetLineText(int index, string fallback)
    {
        if (_lyrics is null || index < 0 || index >= _lyrics.Lines.Count) return fallback;
        return string.IsNullOrWhiteSpace(_lyrics.Lines[index].Text) ? fallback : _lyrics.Lines[index].Text;
    }

    private void ScheduleLyricsRender(TimeSpan delay)
    {
        if (!_playerTimer.IsEnabled) return;
        _lyricsTimer.Interval = delay;
        _lyricsTimer.Start();
    }

    private int GetLyricLineDuration(int index)
    {
        if (_lyrics is null || index < 0 || index >= _lyrics.Lines.Count) return 0;
        if (index + 1 >= _lyrics.Lines.Count) return 4500;
        return Math.Max(1200, _lyrics.Lines[index + 1].Time - _lyrics.Lines[index].Time);
    }

    private void RenderLines(string current, string next, int currentDurationMs, int nextDurationMs)
    {
        var nextText = _settings.LineMode == "two" ? next : "";
        if (_renderedCurrent == current && _renderedNext == nextText) return;
        _renderedCurrent = current;
        _renderedNext = nextText;

        CurrentLine.Text = current;
        NextLine.Text = nextText;
        CurrentLine.Tag = currentDurationMs;
        NextLine.Tag = nextDurationMs;
        QueueMarqueeUpdate();
    }

    private void QueueMarqueeUpdate()
    {
        Dispatcher.BeginInvoke(() =>
        {
            ApplyMarquee(CurrentLineClip, CurrentLine);
            ApplyMarquee(NextLineClip, NextLine);
        }, DispatcherPriority.Loaded);
    }

    private static void ApplyMarquee(FrameworkElement clip, System.Windows.Controls.TextBlock line)
    {
        if (line.RenderTransform is not TranslateTransform transform)
        {
            transform = new TranslateTransform();
            line.RenderTransform = transform;
        }

        transform.BeginAnimation(TranslateTransform.XProperty, null);
        line.Width = double.NaN;
        line.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));

        var textWidth = line.DesiredSize.Width;
        var clipWidth = clip.ActualWidth;
        if (clipWidth <= 0 || textWidth <= 0 || string.IsNullOrWhiteSpace(line.Text))
        {
            line.Width = double.NaN;
            transform.X = 0;
            return;
        }

        line.Width = textWidth;
        var overflow = textWidth - clipWidth;
        if (overflow <= 8)
        {
            transform.X = Math.Max(0, (clipWidth - textWidth) / 2);
            return;
        }

        var distance = overflow + 28;
        var availableMs = line.Tag is int duration ? duration : 0;
        var durationSeconds = availableMs > 0
            ? Math.Max(1.8, Math.Min(16, availableMs / 1000d - 0.35))
            : Math.Max(1.8, Math.Min(16, distance / 36d));
        var delaySeconds = availableMs > 0
            ? Math.Max(0.25, Math.Min(0.9, availableMs / 1000d * 0.16))
            : 0.75;

        transform.X = 0;
        var animation = new DoubleAnimation
        {
            From = 0,
            To = -distance,
            Duration = TimeSpan.FromSeconds(durationSeconds),
            BeginTime = TimeSpan.FromSeconds(delaySeconds),
            FillBehavior = FillBehavior.HoldEnd
        };
        transform.BeginAnimation(TranslateTransform.XProperty, animation);
    }

    private async void OnConnect(object sender, RoutedEventArgs e)
    {
        SetupStatus.Text = "Opening Spotify authorization...";
        try
        {
            await _spotify.StartAuthAsync(ClientIdBox.Text.Trim());
            SetupStatus.Text = "";
            await LoadSettingsAsync();
            ShowLyrics();
        }
        catch (Exception ex)
        {
            SetupStatus.Text = ex.Message;
        }
    }

    private async void OnDisconnect(object sender, RoutedEventArgs e)
    {
        _spotify.Disconnect();
        ResetPlaybackState();
        ShowSetup();
        await LoadSettingsAsync();
    }

    private async void OnToggleLineMode(object sender, RoutedEventArgs e)
    {
        _settings.LineMode = _settings.LineMode == "two" ? "one" : "two";
        await _store.WriteAsync("settings.json", _settings);
        ApplyLineMode();
        RenderSyncedLyrics();
    }

    private void ApplyLineMode()
    {
        LineModeButton.Content = _settings.LineMode == "two" ? "2 lines" : "1 line";
        CurrentLine.FontSize = _settings.LineMode == "two" ? _settings.FontSize : _settings.FontSize * 1.12;
        NextLineClip.Visibility = _settings.LineMode == "two" ? Visibility.Visible : Visibility.Collapsed;
        NextLine.Visibility = _settings.LineMode == "two" ? Visibility.Visible : Visibility.Collapsed;
        _renderedCurrent = "";
        _renderedNext = "";
        QueueMarqueeUpdate();
    }

    private void OnToggleStyle(object sender, RoutedEventArgs e)
    {
        StylePanel.Visibility = StylePanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void OnFontChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_loaded) return;
        _settings.FontSize = Math.Clamp(FontSlider.Value, 20, 56);
        ApplyStyle();
        await _store.WriteAsync("settings.json", _settings);
    }

    private async void OnColorChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (!_loaded || !IsHexColor(ColorBox.Text)) return;
        _settings.TextColor = ColorBox.Text;
        ApplyStyle();
        await _store.WriteAsync("settings.json", _settings);
    }

    private async void OnResetStyle(object sender, RoutedEventArgs e)
    {
        _settings.FontSize = 38;
        _settings.TextColor = "#f6fff8";
        FontSlider.Value = _settings.FontSize;
        ColorBox.Text = _settings.TextColor;
        ApplyStyle();
        await _store.WriteAsync("settings.json", _settings);
    }

    private void ApplyStyle()
    {
        CurrentLine.FontSize = _settings.LineMode == "two" ? _settings.FontSize : _settings.FontSize * 1.12;
        NextLine.FontSize = Math.Round(_settings.FontSize * 0.62);
        CurrentLine.Foreground = BrushFromHex(_settings.TextColor, 1);
        NextLine.Foreground = BrushFromHex(_settings.TextColor, 0.58);
        QueueMarqueeUpdate();
    }

    private async void OnToggleLock(object sender, RoutedEventArgs e)
    {
        await SetLockedAsync(!_isLocked);
    }

    private Task SetLockedAsync(bool locked)
    {
        _isLocked = locked;
        Toolbar.Opacity = locked ? 0 : 1;
        StylePanel.Visibility = locked ? Visibility.Collapsed : StylePanel.Visibility;
        LockButton.Content = locked ? "Unlock" : "Lock";
        if (_lockTrayItem is not null) _lockTrayItem.Text = locked ? "Unlock" : "Lock";
        NativeMethods.SetClickThrough(new WindowInteropHelper(this).Handle, locked);
        return Task.CompletedTask;
    }

    private void OnDragWindow(object sender, MouseButtonEventArgs e)
    {
        if (!_isLocked && e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    private void OnQuit(object sender, RoutedEventArgs e)
    {
        Quit();
    }

    private void Quit()
    {
        _isQuitting = true;
        Close();
        System.Windows.Application.Current.Shutdown();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WmHotKey && wParam.ToInt32() == NativeMethods.HotKeyId)
        {
            _ = SetLockedAsync(!_isLocked);
            handled = true;
        }

        return IntPtr.Zero;
    }

    private static bool IsHexColor(string value)
    {
        return value.Length == 7 && value[0] == '#' && value.Skip(1).All(Uri.IsHexDigit);
    }

    private static System.Windows.Media.Brush BrushFromHex(string value, double alpha)
    {
        if (!IsHexColor(value)) value = "#f6fff8";
        var color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(value);
        color.A = (byte)Math.Round(Math.Clamp(alpha, 0, 1) * 255);
        return new SolidColorBrush(color);
    }
}
