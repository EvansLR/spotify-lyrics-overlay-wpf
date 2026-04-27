using System.IO;
using System.Text.Json;
using SpotifyLyricsOverlay.Wpf.Models;

namespace SpotifyLyricsOverlay.Wpf.Services;

public sealed class SettingsStore
{
    private readonly string _dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "SpotifyLyricsOverlayWpf");

    private readonly JsonSerializerOptions _json = new() { WriteIndented = true };

    public string DataDirectory => _dir;

    public async Task<T?> ReadAsync<T>(string name)
    {
        try
        {
            var path = Path.Combine(_dir, name);
            if (!File.Exists(path)) return default;
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<T>(stream, _json);
        }
        catch
        {
            return default;
        }
    }

    public async Task WriteAsync<T>(string name, T value)
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, name);
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, value, _json);
    }

    public async Task<AppSettings> ReadSettingsAsync()
    {
        return await ReadAsync<AppSettings>("settings.json") ?? new AppSettings();
    }

    public void Delete(string name)
    {
        var path = Path.Combine(_dir, name);
        if (File.Exists(path)) File.Delete(path);
    }
}
