using System.Collections.Concurrent;
using YamuraView.Core;
using IImage = Microsoft.Maui.Graphics.IImage;
#if WINDOWS
using Microsoft.Maui.Graphics.Win2D;
#else
using Microsoft.Maui.Graphics.Platform;
#endif

namespace YamuraView;

/// <summary>
/// Supplies decoded image tiles to the track map. <see cref="GetTile"/> never blocks the
/// repaint: it returns a tile already in memory, or null and starts loading it (from the disk
/// cache when the source allows one, otherwise the network), raising <see cref="TileLoaded"/>
/// when it arrives so the map can repaint.
/// </summary>
public sealed class MapTileCache : IDisposable
{
    private const int MemoryCapacity = 400;
    private const int MaxConcurrentDownloads = 4;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(30);

    private static readonly HttpClient Http = CreateHttpClient();

    private readonly string diskFolder;
    private readonly object sync = new();
    private readonly Dictionary<(int Z, int X, int Y), LinkedListNode<((int Z, int X, int Y) Key, IImage Image)>> memory = new();
    private readonly LinkedList<((int Z, int X, int Y) Key, IImage Image)> recency = new();
    private readonly HashSet<(int Z, int X, int Y)> pending = new();
    private readonly ConcurrentDictionary<(int Z, int X, int Y), DateTime> failedUntil = new();
    private readonly SemaphoreSlim downloads = new(MaxConcurrentDownloads, MaxConcurrentDownloads);
    private readonly CancellationTokenSource cts = new();
    private (int Zoom, int X0, int X1, int Y0, int Y1)? lastViewport;

    public IMapTileProvider Provider { get; }

    /// <summary>Raised on a worker thread when a tile or the attribution text changes.</summary>
    public event Action? TileLoaded;

    /// <summary>The most recent failure, shown on the map; cleared when a tile loads.</summary>
    public string? LastError { get; private set; }

    public MapTileCache(IMapTileProvider provider, string cacheRoot)
    {
        Provider = provider;
        diskFolder = Path.Combine(cacheRoot, provider.Id);
    }

    /// <summary>Folder holding every source's disk-cached tiles.</summary>
    public static string DefaultCacheRoot => Path.Combine(FileSystem.AppDataDirectory, "MapTiles");

    /// <summary>Deletes every disk-cached tile; returns how many files were removed.</summary>
    public static int ClearDiskCache(string cacheRoot)
    {
        if (!Directory.Exists(cacheRoot))
        {
            return 0;
        }
        int count = Directory.EnumerateFiles(cacheRoot, "*", SearchOption.AllDirectories).Count();
        Directory.Delete(cacheRoot, recursive: true);
        return count;
    }

    /// <summary>The tile if it's in memory; otherwise null, and it's queued to load.</summary>
    public IImage? GetTile(int zoom, int x, int y)
    {
        (int, int, int) key = (zoom, x, y);
        lock (sync)
        {
            if (memory.TryGetValue(key, out var node))
            {
                recency.Remove(node);
                recency.AddFirst(node);
                return node.Value.Image;
            }
            if (pending.Contains(key) ||
                (failedUntil.TryGetValue(key, out DateTime until) && DateTime.UtcNow < until))
            {
                return null;
            }
            pending.Add(key);
        }
        // off the UI thread from the start - the disk read and decode are synchronous
        _ = Task.Run(() => LoadAsync(key));
        return null;
    }

    /// <summary>Tells the source which area is on screen, for sources whose credits vary by area.
    /// Cheap to call every repaint: it only acts when the visible tile range changes.</summary>
    public void NoteViewport(int zoom, int x0, int x1, int y0, int y1)
    {
        (int, int, int, int, int) viewport = (zoom, x0, x1, y0, y1);
        if (lastViewport == viewport)
        {
            return;
        }
        lastViewport = viewport;
        double north = WebMercator.TileYToLat(y0, zoom);
        double south = WebMercator.TileYToLat(y1 + 1, zoom);
        double west = WebMercator.TileXToLon(x0, zoom);
        double east = WebMercator.TileXToLon(x1 + 1, zoom);
        _ = Task.Run(async () =>
        {
            try
            {
                if (await Provider.UpdateAttributionAsync(Http, zoom, north, south, east, west, cts.Token))
                {
                    TileLoaded?.Invoke();
                }
            }
            catch (OperationCanceledException)
            {
            }
        });
    }

    private async Task LoadAsync((int Z, int X, int Y) key)
    {
        try
        {
            byte[]? bytes = Provider.AllowsDiskCache ? ReadDisk(key) : null;
            if (bytes == null)
            {
                await downloads.WaitAsync(cts.Token);
                try
                {
                    bytes = await Provider.FetchTileAsync(Http, key.Z, key.X, key.Y, cts.Token);
                }
                finally
                {
                    downloads.Release();
                }
                if (Provider.AllowsDiskCache)
                {
                    WriteDisk(key, bytes);
                }
            }
            IImage? image = Decode(bytes);
            if (image == null)
            {
                Fail(key, "Map tile could not be decoded", permanent: true);
                return;
            }
            lock (sync)
            {
                pending.Remove(key);
                if (cts.IsCancellationRequested)
                {
                    image.Dispose();
                    return;
                }
                recency.AddFirst((key, image));
                memory[key] = recency.First!;
                while (recency.Count > MemoryCapacity)
                {
                    var oldest = recency.Last!;
                    recency.RemoveLast();
                    memory.Remove(oldest.Value.Key);
                    oldest.Value.Image.Dispose();
                }
            }
            LastError = null;
            TileLoaded?.Invoke();
        }
        catch (OperationCanceledException)
        {
            lock (sync)
            {
                pending.Remove(key);
            }
        }
        catch (MapTileException ex)
        {
            Fail(key, ex.Message, ex.Permanent);
        }
        catch (Exception ex)
        {
            // network down, DNS failure, timeout - retried after a pause
            Fail(key, "Map imagery unavailable (no connection?)", permanent: false);
            AppLogger.Log($"Map tile {key} failed: {ex.Message}");
        }
    }

    private void Fail((int Z, int X, int Y) key, string message, bool permanent)
    {
        lock (sync)
        {
            pending.Remove(key);
        }
        failedUntil[key] = permanent ? DateTime.MaxValue : DateTime.UtcNow + RetryDelay;
        if (LastError != message)
        {
            LastError = message;
            AppLogger.Log($"Map imagery: {message}");
            TileLoaded?.Invoke();
        }
    }

    private string DiskPath((int Z, int X, int Y) key) =>
        Path.Combine(diskFolder, key.Z.ToString(), key.X.ToString(), $"{key.Y}.img");

    private byte[]? ReadDisk((int Z, int X, int Y) key)
    {
        try
        {
            string path = DiskPath(key);
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }
        catch (Exception ex)
        {
            AppLogger.Log($"Map tile cache read failed: {ex.Message}");
            return null;
        }
    }

    private void WriteDisk((int Z, int X, int Y) key, byte[] bytes)
    {
        try
        {
            string path = DiskPath(key);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, bytes);
        }
        catch (Exception ex)
        {
            AppLogger.Log($"Map tile cache write failed: {ex.Message}");
        }
    }

    private static IImage? Decode(byte[] bytes)
    {
        try
        {
            using MemoryStream stream = new(bytes);
#if WINDOWS
            return new W2DImageLoadingService().FromStream(stream);
#else
            return PlatformImage.FromStream(stream);
#endif
        }
        catch (Exception ex)
        {
            AppLogger.Log($"Map tile decode failed: {ex.Message}");
            return null;
        }
    }

    private static HttpClient CreateHttpClient()
    {
        HttpClient client = new() { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"YamuraView/{AppInfo.Current.VersionString}");
        return client;
    }

    public void Dispose()
    {
        cts.Cancel();
        lock (sync)
        {
            foreach (var entry in recency)
            {
                entry.Image.Dispose();
            }
            recency.Clear();
            memory.Clear();
        }
    }
}
