using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

namespace YamuraView;

/// <summary>Where the track map's background satellite images come from.</summary>
public enum MapImageryProvider
{
    None,
    Esri,
    Google,
}

/// <summary>Track map imagery choices from the Settings page (stored in <see cref="AppSettings"/>).</summary>
public sealed record MapImagerySettings(MapImageryProvider Provider, string EsriApiKey, string GoogleApiKey, int DarkenPercent);

/// <summary>A tile request the source rejected or couldn't serve - the message is shown on the map.</summary>
public sealed class MapTileException : Exception
{
    /// <summary>True when retrying won't help (bad key, no imagery there) - the tile isn't asked for again.</summary>
    public bool Permanent { get; }

    public MapTileException(string message, bool permanent) : base(message)
    {
        Permanent = permanent;
    }
}

/// <summary>
/// One online source of 256-pixel Web Mercator image tiles. Implementations only fetch bytes;
/// <see cref="MapTileCache"/> does the caching, decoding and request limiting.
/// </summary>
public interface IMapTileProvider
{
    /// <summary>Folder name for this source's cached tiles.</summary>
    string Id { get; }

    /// <summary>Deepest zoom level the source serves.</summary>
    int MaxZoom { get; }

    /// <summary>Whether tiles may be kept on disk between sessions (so maps work offline at the track).</summary>
    bool AllowsDiskCache { get; }

    /// <summary>Attribution text the source requires on the map, for the area last passed to
    /// <see cref="UpdateAttributionAsync"/>.</summary>
    string Attribution { get; }

    /// <summary>Downloads one tile's image bytes. Throws <see cref="MapTileException"/> on failure.</summary>
    Task<byte[]> FetchTileAsync(HttpClient http, int zoom, int x, int y, CancellationToken ct);

    /// <summary>Refreshes <see cref="Attribution"/> for the visible area, for sources whose
    /// credits depend on where you look. Returns true if the text changed.</summary>
    Task<bool> UpdateAttributionAsync(HttpClient http, int zoom, double north, double south, double east, double west, CancellationToken ct);
}

/// <summary>
/// Esri World Imagery. Without a key it uses the public ArcGIS Online tile service; with an
/// ArcGIS Location Platform API key it uses the keyed basemap service, which is the one Esri
/// supports for applications.
/// </summary>
public sealed class EsriImageryProvider : IMapTileProvider
{
    private readonly string apiKey;

    public EsriImageryProvider(string apiKey)
    {
        this.apiKey = apiKey.Trim();
    }

    public string Id => "esri";
    public int MaxZoom => 19;
    public bool AllowsDiskCache => true;
    // Esri's short form for small maps
    public string Attribution => "Esri, Maxar, Earthstar Geographics";

    public async Task<byte[]> FetchTileAsync(HttpClient http, int zoom, int x, int y, CancellationToken ct)
    {
        // Esri orders the path zoom/row/column (y before x)
        string url = apiKey.Length > 0
            ? $"https://ibasemaps-api.arcgis.com/arcgis/rest/services/World_Imagery/MapServer/tile/{zoom}/{y}/{x}?token={Uri.EscapeDataString(apiKey)}"
            : $"https://server.arcgisonline.com/ArcGIS/rest/services/World_Imagery/MapServer/tile/{zoom}/{y}/{x}";
        using HttpResponseMessage response = await http.GetAsync(url, ct);
        if (!response.IsSuccessStatusCode)
        {
            bool permanent = response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;
            string reason = response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                ? "Esri rejected the API key"
                : $"Esri tile request failed ({(int)response.StatusCode})";
            throw new MapTileException(reason, permanent);
        }
        byte[] bytes = await response.Content.ReadAsByteArrayAsync(ct);
        // the keyed service reports token errors as a small JSON body with HTTP 200
        if (bytes.Length > 0 && bytes[0] == (byte)'{')
        {
            throw new MapTileException("Esri rejected the API key", permanent: true);
        }
        return bytes;
    }

    public Task<bool> UpdateAttributionAsync(HttpClient http, int zoom, double north, double south, double east, double west, CancellationToken ct) =>
        Task.FromResult(false);
}

/// <summary>
/// Google satellite imagery through the Map Tiles API (2D tiles). Needs an API key from a
/// Google Cloud project with billing and the Map Tiles API enabled. Google's terms limit
/// caching, so tiles are kept in memory only, and the map credits come from Google's viewport
/// service for the area shown.
/// </summary>
public sealed class GoogleSatelliteProvider : IMapTileProvider
{
    private readonly string apiKey;
    private readonly SemaphoreSlim sessionLock = new(1, 1);
    private string? session;
    private DateTimeOffset sessionExpiry;
    private string copyright = "";

    public GoogleSatelliteProvider(string apiKey)
    {
        this.apiKey = apiKey.Trim();
    }

    public string Id => "google";
    public int MaxZoom => 21;
    public bool AllowsDiskCache => false;
    public string Attribution => copyright.Length > 0 ? $"Google | {copyright}" : "Google";

    public async Task<byte[]> FetchTileAsync(HttpClient http, int zoom, int x, int y, CancellationToken ct)
    {
        if (apiKey.Length == 0)
        {
            throw new MapTileException("Google imagery needs an API key (Settings)", permanent: true);
        }
        for (int attempt = 0; attempt < 2; attempt++)
        {
            string sessionToken = await GetSessionAsync(http, forceNew: attempt > 0, ct);
            string url = $"https://tile.googleapis.com/v1/2dtiles/{zoom}/{x}/{y}?session={Uri.EscapeDataString(sessionToken)}&key={Uri.EscapeDataString(apiKey)}";
            using HttpResponseMessage response = await http.GetAsync(url, ct);
            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadAsByteArrayAsync(ct);
            }
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                throw new MapTileException("No Google imagery here", permanent: true);
            }
            // an expired session comes back as a client error - get a new one and retry once
            if (attempt == 0 && response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized)
            {
                continue;
            }
            bool keyProblem = response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized;
            throw new MapTileException(
                keyProblem ? "Google rejected the API key (check Map Tiles API and billing)" : $"Google tile request failed ({(int)response.StatusCode})",
                permanent: keyProblem);
        }
        throw new MapTileException("Google tile request failed", permanent: false);
    }

    public async Task<bool> UpdateAttributionAsync(HttpClient http, int zoom, double north, double south, double east, double west, CancellationToken ct)
    {
        if (apiKey.Length == 0)
        {
            return false;
        }
        try
        {
            string sessionToken = await GetSessionAsync(http, forceNew: false, ct);
            string url = string.Create(CultureInfo.InvariantCulture,
                $"https://tile.googleapis.com/tile/v1/viewport?session={Uri.EscapeDataString(sessionToken)}&key={Uri.EscapeDataString(apiKey)}&zoom={zoom}&north={north:F6}&south={south:F6}&east={east:F6}&west={west:F6}");
            using HttpResponseMessage response = await http.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
            {
                return false;
            }
            using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            string text = doc.RootElement.TryGetProperty("copyright", out JsonElement c) ? c.GetString() ?? "" : "";
            if (text == copyright)
            {
                return false;
            }
            copyright = text;
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AppLogger.Log($"Google imagery credits request failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>The Map Tiles API session token (valid about two weeks), created on first use.</summary>
    private async Task<string> GetSessionAsync(HttpClient http, bool forceNew, CancellationToken ct)
    {
        await sessionLock.WaitAsync(ct);
        try
        {
            if (!forceNew && session != null && DateTimeOffset.UtcNow < sessionExpiry)
            {
                return session;
            }
            string body = "{\"mapType\":\"satellite\",\"language\":\"en-US\",\"region\":\"US\"}";
            using StringContent content = new(body, Encoding.UTF8, "application/json");
            using HttpResponseMessage response = await http.PostAsync(
                $"https://tile.googleapis.com/v1/createSession?key={Uri.EscapeDataString(apiKey)}", content, ct);
            if (!response.IsSuccessStatusCode)
            {
                bool keyProblem = response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized or HttpStatusCode.BadRequest;
                throw new MapTileException(
                    keyProblem ? "Google rejected the API key (check Map Tiles API and billing)" : $"Google session request failed ({(int)response.StatusCode})",
                    permanent: keyProblem);
            }
            using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            session = doc.RootElement.GetProperty("session").GetString()
                ?? throw new MapTileException("Google session response had no token", permanent: false);
            // expiry is Unix seconds as a string; renew an hour early
            sessionExpiry = doc.RootElement.TryGetProperty("expiry", out JsonElement e) &&
                            long.TryParse(e.GetString(), out long unixSeconds)
                ? DateTimeOffset.FromUnixTimeSeconds(unixSeconds).AddHours(-1)
                : DateTimeOffset.UtcNow.AddDays(1);
            return session;
        }
        finally
        {
            sessionLock.Release();
        }
    }
}
