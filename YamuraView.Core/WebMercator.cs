namespace YamuraView.Core;

/// <summary>
/// Web Mercator ("slippy map") tile math shared by every online imagery source: Esri,
/// Google, OpenStreetMap and others all number 256-pixel tiles the same way, with tile
/// (0,0) at the north-west corner and 2^zoom tiles across at each zoom level.
/// </summary>
public static class WebMercator
{
    /// <summary>Latitude limit of the projection - tiles cover nothing nearer the poles.</summary>
    public const double MaxLatitude = 85.05112878;

    /// <summary>Tile edge length in pixels used by every supported imagery source.</summary>
    public const int TileSize = 256;

    public static int TileCount(int zoom) => 1 << zoom;

    /// <summary>Fractional tile X for a longitude (the integer part is the tile column).</summary>
    public static double LonToTileX(double longitude, int zoom) =>
        (longitude + 180.0) / 360.0 * TileCount(zoom);

    /// <summary>Fractional tile Y for a latitude (the integer part is the tile row; north is 0).</summary>
    public static double LatToTileY(double latitude, int zoom)
    {
        double lat = Math.Clamp(latitude, -MaxLatitude, MaxLatitude) * Math.PI / 180.0;
        return (1.0 - Math.Log(Math.Tan(lat) + 1.0 / Math.Cos(lat)) / Math.PI) / 2.0 * TileCount(zoom);
    }

    /// <summary>Longitude of a tile column's west edge (pass x + 1 for its east edge).</summary>
    public static double TileXToLon(double tileX, int zoom) =>
        tileX / TileCount(zoom) * 360.0 - 180.0;

    /// <summary>Latitude of a tile row's north edge (pass y + 1 for its south edge).</summary>
    public static double TileYToLat(double tileY, int zoom)
    {
        double n = Math.PI - 2.0 * Math.PI * tileY / TileCount(zoom);
        return 180.0 / Math.PI * Math.Atan(Math.Sinh(n));
    }

    /// <summary>
    /// Zoom level whose tiles come out closest to <paramref name="targetTilePixels"/> wide on
    /// screen, for a view showing <paramref name="degreesLongitudePerPixel"/> - so a tile's
    /// pixels land roughly one-to-one on the screen's.
    /// </summary>
    public static int ChooseZoom(double degreesLongitudePerPixel, double targetTilePixels, int minZoom, int maxZoom)
    {
        if (degreesLongitudePerPixel <= 0 || targetTilePixels <= 0 || double.IsNaN(degreesLongitudePerPixel))
        {
            return minZoom;
        }
        // tile width on screen = (360 / 2^z) / degreesPerPixel  =>  z = log2(360 / (target * dpp))
        double zoom = Math.Log2(360.0 / (targetTilePixels * degreesLongitudePerPixel));
        return Math.Clamp((int)Math.Round(zoom), minZoom, maxZoom);
    }
}
