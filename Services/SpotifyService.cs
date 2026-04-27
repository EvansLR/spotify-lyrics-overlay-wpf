using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SpotifyLyricsOverlay.Wpf.Models;

namespace SpotifyLyricsOverlay.Wpf.Services;

public sealed class SpotifyService
{
    private const string AuthUrl = "https://accounts.spotify.com/authorize";
    private const string TokenUrl = "https://accounts.spotify.com/api/token";
    private const string ApiUrl = "https://api.spotify.com/v1";
    private const string RedirectUri = "http://127.0.0.1:8766/callback";
    private static readonly string[] Scopes = { "user-read-currently-playing", "user-read-playback-state" };

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private readonly SettingsStore _store;
    private string _pkceVerifier = "";

    public SpotifyService(SettingsStore store)
    {
        _store = store;
    }

    public async Task<bool> HasTokenAsync()
    {
        var token = await _store.ReadAsync<TokenInfo>("token.json");
        return !string.IsNullOrWhiteSpace(token?.AccessToken);
    }

    public async Task StartAuthAsync(string clientId)
    {
        if (string.IsNullOrWhiteSpace(clientId)) throw new InvalidOperationException("Client ID is required.");

        var settings = await _store.ReadSettingsAsync();
        settings.ClientId = clientId.Trim();
        await _store.WriteAsync("settings.json", settings);

        _pkceVerifier = RandomString(96);
        var challenge = Sha256Base64Url(_pkceVerifier);
        using var listener = new HttpListener();
        listener.Prefixes.Add("http://127.0.0.1:8766/");
        listener.Start();

        var query = new Dictionary<string, string>
        {
            ["response_type"] = "code",
            ["client_id"] = clientId.Trim(),
            ["scope"] = string.Join(' ', Scopes),
            ["redirect_uri"] = RedirectUri,
            ["code_challenge_method"] = "S256",
            ["code_challenge"] = challenge
        };

        OpenExternal($"{AuthUrl}?{ToForm(query)}");

        var context = await listener.GetContextAsync();
        if (context.Request.Url?.AbsolutePath != "/callback")
        {
            await WriteBrowserResponse(context, 404, "Not found");
            throw new InvalidOperationException("Unexpected authorization callback.");
        }

        var code = context.Request.QueryString["code"];
        var error = context.Request.QueryString["error"];

        if (!string.IsNullOrWhiteSpace(error) || string.IsNullOrWhiteSpace(code))
        {
            await WriteBrowserResponse(context, 400, error ?? "Missing authorization code");
            throw new InvalidOperationException(error ?? "Missing authorization code");
        }

        await ExchangeCodeAsync(clientId.Trim(), code);
        await WriteBrowserResponse(context, 200, "Spotify connected. You can close this tab.");
    }

    public void Disconnect()
    {
        _store.Delete("token.json");
    }

    public async Task<TrackInfo?> GetPlayerAsync()
    {
        using var request = await CreateApiRequestAsync("/me/player");
        if (request is null) return null;

        using var response = await _http.SendAsync(request);
        if (response.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.NotFound) return null;
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            _store.Delete("token.json");
            return null;
        }

        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync();
        using var doc = await JsonDocument.ParseAsync(stream);
        return NormalizeTrack(doc.RootElement);
    }

    private async Task<HttpRequestMessage?> CreateApiRequestAsync(string path)
    {
        var token = await GetValidTokenAsync();
        if (token is null) return null;

        var request = new HttpRequestMessage(HttpMethod.Get, $"{ApiUrl}{path}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        return request;
    }

    private async Task<TokenInfo?> GetValidTokenAsync()
    {
        var token = await _store.ReadAsync<TokenInfo>("token.json");
        if (token is null) return null;
        if (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() < token.ExpiresAt - 60_000) return token;

        var settings = await _store.ReadSettingsAsync();
        if (string.IsNullOrWhiteSpace(settings.ClientId) || string.IsNullOrWhiteSpace(token.RefreshToken)) return null;

        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = token.RefreshToken,
            ["client_id"] = settings.ClientId
        });

        using var response = await _http.PostAsync(TokenUrl, content);
        if (!response.IsSuccessStatusCode)
        {
            if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized) _store.Delete("token.json");
            return null;
        }

        await StoreTokenAsync(response, token.RefreshToken);
        return await _store.ReadAsync<TokenInfo>("token.json");
    }

    private async Task ExchangeCodeAsync(string clientId, string code)
    {
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = RedirectUri,
            ["client_id"] = clientId,
            ["code_verifier"] = _pkceVerifier
        });

        using var response = await _http.PostAsync(TokenUrl, content);
        response.EnsureSuccessStatusCode();
        await StoreTokenAsync(response, "");
    }

    private async Task StoreTokenAsync(HttpResponseMessage response, string previousRefreshToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync();
        using var doc = await JsonDocument.ParseAsync(stream);
        var root = doc.RootElement;
        var accessToken = root.GetProperty("access_token").GetString() ?? "";
        var refreshToken = root.TryGetProperty("refresh_token", out var refresh)
            ? refresh.GetString() ?? previousRefreshToken
            : previousRefreshToken;
        var expiresIn = root.TryGetProperty("expires_in", out var expires) ? expires.GetInt32() : 3600;

        await _store.WriteAsync("token.json", new TokenInfo
        {
            AccessToken = accessToken,
            RefreshToken = refreshToken,
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresIn).ToUnixTimeMilliseconds()
        });
    }

    private static TrackInfo? NormalizeTrack(JsonElement player)
    {
        if (!player.TryGetProperty("item", out var item) || item.ValueKind == JsonValueKind.Null) return null;
        if (!item.TryGetProperty("type", out var type) || type.GetString() != "track") return null;

        var artists = item.GetProperty("artists").EnumerateArray()
            .Select(artist => artist.GetProperty("name").GetString() ?? "")
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToList();

        return new TrackInfo(
            item.GetProperty("id").GetString() ?? "",
            item.GetProperty("name").GetString() ?? "",
            string.Join(", ", artists),
            artists.FirstOrDefault() ?? "",
            item.GetProperty("album").GetProperty("name").GetString() ?? "",
            item.GetProperty("duration_ms").GetInt32(),
            player.TryGetProperty("progress_ms", out var progress) && progress.ValueKind != JsonValueKind.Null ? progress.GetInt32() : 0,
            player.TryGetProperty("is_playing", out var playing) && playing.GetBoolean());
    }

    private static string RandomString(int length)
    {
        return Convert.ToBase64String(RandomNumberGenerator.GetBytes(length))
            .Replace("+", "-")
            .Replace("/", "_")
            .Replace("=", "")
            .Substring(0, length);
    }

    private static string Sha256Base64Url(string value)
    {
        var hash = SHA256.HashData(Encoding.ASCII.GetBytes(value));
        return Convert.ToBase64String(hash).Replace("+", "-").Replace("/", "_").TrimEnd('=');
    }

    private static string ToForm(Dictionary<string, string> values)
    {
        return string.Join('&', values.Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
    }

    private static void OpenExternal(string url)
    {
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    private static async Task WriteBrowserResponse(HttpListenerContext context, int status, string text)
    {
        var bytes = Encoding.UTF8.GetBytes($"<!doctype html><meta charset=\"utf-8\"><body>{WebUtility.HtmlEncode(text)}</body>");
        context.Response.StatusCode = status;
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes);
        context.Response.Close();
    }
}
