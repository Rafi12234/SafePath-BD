namespace SafePathBD.Web.Common;

/// <summary>
/// Geographic helpers shared by services. Distances are straight-line, never travel distance.
/// </summary>
public static class GeoMath
{
   
    private const double EarthRadiusKm = 6371.0088;

    public static bool IsValidLatitude(double latitude) =>
        !double.IsNaN(latitude) && latitude is >= -90 and <= 90;

    public static bool IsValidLongitude(double longitude) =>
        !double.IsNaN(longitude) && longitude is >= -180 and <= 180;

    public static bool IsValidCoordinate(double latitude, double longitude) =>
        IsValidLatitude(latitude) && IsValidLongitude(longitude);

    /// <summary>Great-circle distance in kilometres between two coordinates.</summary>
    public static double HaversineDistanceKm(double lat1, double lon1, double lat2, double lon2)
    {
        var dLat = ToRadians(lat2 - lat1);
        var dLon = ToRadians(lon2 - lon1);

        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
                + Math.Cos(ToRadians(lat1)) * Math.Cos(ToRadians(lat2))
                * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);

        return 2 * EarthRadiusKm * Math.Asin(Math.Min(1.0, Math.Sqrt(a)));
    }

    /// <summary>
    /// Bounding box that fully contains the radius, used to pre-filter rows in SQL
    /// before the exact Haversine distance is applied.
    /// </summary>
    public static (double MinLat, double MaxLat, double MinLon, double MaxLon) BoundingBox(
        double latitude,
        double longitude,
        double radiusKm)
    {
        var latDelta = radiusKm / 111.32;

        // Longitude degrees shrink towards the poles; clamp cos() so the box never collapses.
        var cos = Math.Max(Math.Cos(ToRadians(latitude)), 0.000001);
        var lonDelta = radiusKm / (111.32 * cos);

        return (
            Math.Max(-90, latitude - latDelta),
            Math.Min(90, latitude + latDelta),
            Math.Max(-180, longitude - lonDelta),
            Math.Min(180, longitude + lonDelta));
    }

    private static double ToRadians(double degrees) => degrees * Math.PI / 180.0;
}
