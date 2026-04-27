namespace SpotifyLyricsOverlay.Wpf.Models;

public sealed record TrackInfo(
    string Id,
    string Name,
    string Artist,
    string FirstArtist,
    string Album,
    int DurationMs,
    int ProgressMs,
    bool IsPlaying);

public sealed record LyricLine(int Time, string Text);

public sealed class LyricsResult
{
    public List<LyricLine> Lines { get; set; } = new();
    public List<string> Plain { get; set; } = new();
    public string Source { get; set; } = "none";
}

public sealed class AppSettings
{
    public string ClientId { get; set; } = "";
    public string LineMode { get; set; } = "two";
    public double FontSize { get; set; } = 38;
    public string TextColor { get; set; } = "#f6fff8";
}

public sealed class TokenInfo
{
    public string AccessToken { get; set; } = "";
    public string RefreshToken { get; set; } = "";
    public long ExpiresAt { get; set; }
}
