using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SafePathBD.Web.Integrations.Routing;
using SafePathBD.Web.Models.DTOs.Routing;

namespace SafePathBD.Tests;

public class OsrmRoutingProviderTests
{
    private static readonly RouteCoordinate Start = new(23.75, 90.37);
    private static readonly RouteCoordinate End = new(23.76, 90.39);

    [Fact]
    public async Task SuccessfulRoute_ParsesDistanceDurationAndGeoJsonOrder()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, """
            {"code":"Ok","routes":[{"distance":6200,"duration":1080,"geometry":{"type":"LineString","coordinates":[[90.37,23.75],[90.39,23.76]]}}]}
            """));
        var provider = CreateProvider(handler);

        var routes = await provider.GetRoutesAsync(new RoutingProviderRequest(Start, End));

        var route = Assert.Single(routes);
        Assert.Equal(6200, route.DistanceMeters);
        Assert.Equal(1080, route.DurationSeconds);
        Assert.Equal(23.75, route.Geometry[0].Latitude, 6);
        Assert.Equal(90.37, route.Geometry[0].Longitude, 6);
        Assert.Contains("90.37,23.75", handler.LastRequestUri!.ToString());
    }

    [Fact]
    public async Task MultipleAlternatives_AreReturnedWithoutAssumingThree()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, """
            {"code":"Ok","routes":[
              {"distance":6000,"duration":1000,"geometry":{"type":"LineString","coordinates":[[90.37,23.75],[90.39,23.76]]}},
              {"distance":6500,"duration":900,"geometry":{"type":"LineString","coordinates":[[90.37,23.75],[90.38,23.755],[90.39,23.76]]}}
            ]}
            """));
        var routes = await CreateProvider(handler).GetRoutesAsync(new RoutingProviderRequest(Start, End));

        Assert.Equal(2, routes.Count);
    }

    [Fact]
    public async Task NoRoute_IsReportedDistinctly()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, "{" + "\"code\":\"NoRoute\",\"routes\":[]}"));

        await Assert.ThrowsAsync<RoutingProviderNoRouteException>(() =>
            CreateProvider(handler).GetRoutesAsync(new RoutingProviderRequest(Start, End)));
    }

    [Fact]
    public async Task Timeout_BecomesProviderUnavailable()
    {
        var handler = new StubHandler(_ => throw new TaskCanceledException("timeout"));

        await Assert.ThrowsAsync<RoutingProviderUnavailableException>(() =>
            CreateProvider(handler).GetRoutesAsync(new RoutingProviderRequest(Start, End)));
    }

    [Fact]
    public async Task Http500_BecomesProviderUnavailable()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        await Assert.ThrowsAsync<RoutingProviderUnavailableException>(() =>
            CreateProvider(handler).GetRoutesAsync(new RoutingProviderRequest(Start, End)));
    }

    [Fact]
    public async Task InvalidJson_BecomesProviderUnavailable()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, "not-json"));

        await Assert.ThrowsAsync<RoutingProviderUnavailableException>(() =>
            CreateProvider(handler).GetRoutesAsync(new RoutingProviderRequest(Start, End)));
    }

    [Fact]
    public async Task MissingGeometry_IsNotAcceptedAsARoute()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, """
            {"code":"Ok","routes":[{"distance":6200,"duration":1080}]}
            """));

        await Assert.ThrowsAsync<RoutingProviderUnavailableException>(() =>
            CreateProvider(handler).GetRoutesAsync(new RoutingProviderRequest(Start, End)));
    }

    private static OsrmRoutingProvider CreateProvider(HttpMessageHandler handler)
    {
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://router.test/") };
        var options = Options.Create(new OsrmRoutingOptions { BaseUrl = "https://router.test/", Profile = "driving" });
        return new OsrmRoutingProvider(client, options, NullLogger<OsrmRoutingProvider>.Instance);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string content) =>
        new(status) { Content = new StringContent(content, System.Text.Encoding.UTF8, "application/json") };

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;
        public Uri? LastRequestUri { get; private set; }

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) => _handler = handler;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestUri = request.RequestUri;
            return Task.FromResult(_handler(request));
        }
    }
}
