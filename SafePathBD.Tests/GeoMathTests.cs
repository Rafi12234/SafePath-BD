using SafePathBD.Web.Common;

namespace SafePathBD.Tests;

public class GeoMathTests
{
    // Reference coordinates around Dhaka.
    private const double ShahbagLat = 23.7386;
    private const double ShahbagLng = 90.3956;
    private const double UttaraLat = 23.8759;
    private const double UttaraLng = 90.3795;

    [Theory]
    [InlineData(0, 0, true)]
    [InlineData(90, 180, true)]
    [InlineData(-90, -180, true)]
    [InlineData(23.81, 90.41, true)]
    [InlineData(90.0001, 0, false)]
    [InlineData(-90.0001, 0, false)]
    [InlineData(0, 180.0001, false)]
    [InlineData(0, -180.0001, false)]
    [InlineData(double.NaN, 0, false)]
    public void IsValidCoordinate_EnforcesTheDocumentedRanges(double latitude, double longitude, bool expected)
    {
        Assert.Equal(expected, GeoMath.IsValidCoordinate(latitude, longitude));
    }

    [Fact]
    public void HaversineDistanceKm_IsZeroForTheSamePoint()
    {
        Assert.Equal(0, GeoMath.HaversineDistanceKm(ShahbagLat, ShahbagLng, ShahbagLat, ShahbagLng), 6);
    }

    [Fact]
    public void HaversineDistanceKm_MatchesAKnownDistance()
    {
        var km = GeoMath.HaversineDistanceKm(ShahbagLat, ShahbagLng, UttaraLat, UttaraLng);

        // Shahbag to Uttara is roughly 15.3 km in a straight line.
        Assert.InRange(km, 15.0, 15.6);
    }

    [Fact]
    public void HaversineDistanceKm_IsSymmetric()
    {
        var forward = GeoMath.HaversineDistanceKm(ShahbagLat, ShahbagLng, UttaraLat, UttaraLng);
        var backward = GeoMath.HaversineDistanceKm(UttaraLat, UttaraLng, ShahbagLat, ShahbagLng);

        Assert.Equal(forward, backward, 9);
    }

    [Fact]
    public void HaversineDistanceKm_HandlesAntipodalPoints()
    {
        var km = GeoMath.HaversineDistanceKm(0, 0, 0, 180);

        // Half the Earth's circumference.
        Assert.InRange(km, 20000, 20040);
    }

    [Fact]

