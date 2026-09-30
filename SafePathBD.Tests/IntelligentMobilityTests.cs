using Microsoft.Extensions.Options;
using SafePathBD.Web.Common;
using SafePathBD.Web.Models.DTOs.IntelligentRouting;
using SafePathBD.Web.Models.DTOs.Routing;
using SafePathBD.Web.Models.DTOs.Traffic;
using SafePathBD.Web.Services.Implementations;
using SafePathBD.Web.Services.IntelligentRouting;
using SafePathBD.Web.Services.Interfaces;

namespace SafePathBD.Tests;

public sealed class IntelligentMobilityTests
{
    private static readonly RouteCoordinate A = new(23.760, 90.400);
    private static readonly RouteCoordinate B = new(23.761, 90.401);
    private static readonly RouteCoordinate C = new(23.762, 90.402);
    private static readonly RouteCoordinate D = new(23.763, 90.403);

    [Fact]
    public async Task VehicleEta_IsModeSpecific()
    {
        var traffic = new FixedTrafficPrediction(60);
        var data = new FixedTrafficData();
        var service = new VehicleTravelTimeService(traffic, data, new FakeTransit());
        var request = new TrafficPredictionRequest("R1", A.Latitude, A.Longitude, DateTimeOffset.Now, "Clear", RoadClass:"secondary");
        var walk = await service.EstimateAsync(MobilityModes.Walk, 1000, request);
        var rickshaw = await service.EstimateAsync(MobilityModes.Rickshaw, 1000, request);
        var motorbike = await service.EstimateAsync(MobilityModes.Motorbike, 1000, request);
        var bus = await service.EstimateAsync(MobilityModes.Bus, 1000, request, "B1", true);
        var car = await service.EstimateAsync(MobilityModes.Car, 1000, request);
        Assert.True(walk.TravelMinutes > rickshaw.TravelMinutes);
        Assert.True(rickshaw.TravelMinutes > motorbike.TravelMinutes);
        Assert.True(bus.ExpectedWaitMinutes > 0);
        Assert.True(car.TravelMinutes > 0);
    }

    [Fact]
    public async Task TimeDependentDijkstra_CanChooseRickshawBusWalk()
    {
        var graph = BuildMultimodalGraph();
        var router = Router();
        var request = Request(MobilityModes.Walk, MobilityModes.Rickshaw, MobilityModes.Bus);
        request.MaxTransfers = 2;
        var path = await router.FindBestAsync(graph, request);
        Assert.NotNull(path);
        var modes = path!.Steps.Where(x => x.Edge is not null).Select(x => x.Mode).ToArray();
        Assert.Contains(MobilityModes.Rickshaw, modes);
        Assert.Contains(MobilityModes.Bus, modes);
        Assert.Contains(MobilityModes.Walk, modes);
    }

    [Fact]
    public async Task MaxTransfers_RejectsJourneyRequiringTwoTransfers()
    {
        var request = Request(MobilityModes.Walk, MobilityModes.Rickshaw, MobilityModes.Bus);
        request.MaxTransfers = 1;
        var path = await Router().FindBestAsync(BuildMultimodalGraph(), request);
        Assert.Null(path);
    }

    [Fact]
    public async Task MaxWalking_IsEnforced()
    {
        var graph = BasicGraph();
        graph.AddEdge(Road(1,0,1,1200,MobilityModes.Walk));
        var request = Request(MobilityModes.Walk); request.MaxWalkingMeters = 500;
        Assert.Null(await Router().FindBestAsync(graph, request));
    }

    [Fact]
    public async Task CriticalHardBlock_IsExcludedEvenIfFaster()
    {
        var graph = BasicGraph();
        graph.AddNode(new MobilityGraphNode{Id=2,Coordinate=B});
        graph.AddEdge(Road(1,0,1,100,MobilityModes.Car, "BLOCKED"));
        graph.AddEdge(Road(2,0,2,500,MobilityModes.Car, "CLEAR"));
        graph.AddEdge(Road(3,2,1,500,MobilityModes.Car, "CLEAR"));
        var router = new TimeDependentDijkstraRouter(new FixedTravel(), new HardBlockSafety(), new FakeTransit(), Opt());
        var path = await router.FindBestAsync(graph, Request(MobilityModes.Car));
        Assert.NotNull(path);
        Assert.DoesNotContain(path!.PhysicalEdgeIds, id => id == 1);
    }

    [Fact]
    public async Task EdgeCost_UsesArrivalTimeNotOnlyInitialDeparture()
    {
        var graph = BasicGraph();
        graph.AddNode(new MobilityGraphNode{Id=2,Coordinate=B});
        graph.AddEdge(Road(1,0,2,100,MobilityModes.Car));
        graph.AddEdge(Road(2,2,1,100,MobilityModes.Car));
        var travel = new RecordingTravel();
        var router = new TimeDependentDijkstraRouter(travel, new ClearSafety(), new FakeTransit(), Opt());
        var request = Request(MobilityModes.Car); request.DepartureTime = new DateTimeOffset(2026,9,16,8,55,0,TimeSpan.FromHours(6));
        await router.FindBestAsync(graph, request);
        Assert.True(travel.SeenTimes.Count >= 2);
        Assert.True(travel.SeenTimes[1] > travel.SeenTimes[0]);
    }

    [Fact]
    public void Resilience_LowerForBottleneckAndRiskExposure()
    {
        var data = new FixedTrafficData();
        var service = new RouteResilienceService(data);
        var stable = PathWithDurations(5,5,5);
        var fragile = PathWithDurations(1,1,13);
        var high = new SafetyExposureDto(8,3,0,4);
        var low = new SafetyExposureDto(0,1,2,12);
        Assert.True(service.Calculate(stable, low, 15,2,3,DateTimeOffset.Now) > service.Calculate(fragile, high,15,2,1,DateTimeOffset.Now));
    }

    [Fact]
    public void Explanation_UsesCalculatedTradeoffs()
    {
        var service = new RouteExplanationService();
        var best = Option("A",20,40,2,0,80);
        var other = Option("B",16,65,9,1,60);
        var tradeoffs = service.ExplainTradeoffs(other,best);
        Assert.Contains(tradeoffs, x => x.Title == "Travel time" && x.Detail.Contains("faster"));
        Assert.Contains(tradeoffs, x => x.Title == "Predicted traffic" && x.Detail.Contains("higher"));
        Assert.Contains(tradeoffs, x => x.Title == "High-risk exposure" && x.Detail.Contains("more"));
    }

    [Fact]
    public void RouteDiversity_UsesGeometryInsteadOfRequestLocalEdgeIds()
    {
        var nearA = PathAlong([A, B, C, D], 1);
        var nearB = PathAlong([
            new RouteCoordinate(A.Latitude + 0.00002, A.Longitude + 0.00002),
            new RouteCoordinate(B.Latitude + 0.00002, B.Longitude + 0.00002),
            new RouteCoordinate(C.Latitude + 0.00002, C.Longitude + 0.00002),
            new RouteCoordinate(D.Latitude + 0.00002, D.Longitude + 0.00002)
        ], 100);
        var far = PathAlong([
            new RouteCoordinate(23.70, 90.32),
            new RouteCoordinate(23.71, 90.33),
            new RouteCoordinate(23.72, 90.34)
        ], 200);

        Assert.True(JourneySimilarity.Calculate(nearA, nearB) >= 0.90);
        Assert.True(JourneySimilarity.Calculate(nearA, far) < 0.90);
    }

    [Fact]
    public async Task BusBoarding_ChargesStopAccessWalkingAndWait()
    {
        var graph = BasicGraph();
        graph.Nodes[0].TransitStopIds.Add("S1");
        graph.Nodes[0].TransitStopAccessMeters["S1"] = 240;
        graph.Nodes[1].TransitStopIds.Add("S2");
        graph.Nodes[1].TransitStopAccessMeters["S2"] = 0;
        graph.AddEdge(new BusMobilityEdge
        {
            Id = 7, FromNodeId = 0, ToNodeId = 1, CandidateKey = "C", DistanceMeters = 1000,
            Geometry = [A, D], TrafficRoadId = "R1", RoadClass = "secondary", TrafficRoadMatchMeters = 10,
            RouteId = "B1", RouteName = "Demo bus", FromStopId = "S1", ToStopId = "S2"
        });

        var request = Request(MobilityModes.Bus);
        request.MaxWalkingMeters = 500;
        var path = await Router().FindBestAsync(graph, request);

        Assert.NotNull(path);
        Assert.True(path!.WalkingMeters >= 239);
        var boarding = Assert.Single(path.Steps.Where(step => step.Edge is null));
        Assert.Equal(1d, boarding.ExpectedWaitMinutes, 3);
        Assert.True(boarding.TransferWalkingMeters >= 239);
        var movement = Assert.Single(path.Steps.Where(step => step.Edge is BusMobilityEdge));
        Assert.True(movement.DurationMinutes >= 2.0d); // road movement plus configured synthetic stop dwell
    }

    [Fact]
    public async Task DepartureWindow_SuggestsLaterOnlyWhenBenefitIsMaterial()
    {
        var service = new DepartureWindowService(new DepartureSensitiveTravel(), Options.Create(new IntelligentMobilityOptions
        {
            DepartureBenefitMinutes = 5,
            DepartureBenefitPercent = 0.08
        }));
        var start = new DateTimeOffset(2026, 9, 16, 9, 0, 0, TimeSpan.FromHours(6));
        var journey = new JourneyOptionDto
        {
            Id = "J", TotalDurationMinutes = 20, PredictedCongestionIndex = 85,
            Legs = [new JourneyLegDto { Sequence=1, Mode=MobilityModes.Car, Start=new(A.Latitude,A.Longitude), End=new(D.Latitude,D.Longitude), DistanceMeters=5000, Geometry=[A,D], TrafficRoadId="R1", RoadClass="secondary" }]
        };
        var request = Request(MobilityModes.Car); request.DepartureTime = start;
        var result = await service.EvaluateAsync(journey, request);
        Assert.True(result.HasSuggestion);
        Assert.NotNull(result.SuggestedDepartureTime);
    }


    [Fact]
    public async Task KBestPlanner_ExplicitlyExploresSelectedVehicleAndMultimodalProfiles()
    {
        var recorder = new RecordingJourneyRouter();
        var planner = new KBestJourneyPlanner(recorder, Options.Create(new IntelligentMobilityOptions
        {
            CandidatePoolSize = 20
        }));
        var request = Request(
            MobilityModes.Walk,
            MobilityModes.Rickshaw,
            MobilityModes.Bus,
            MobilityModes.Motorbike,
            MobilityModes.Car);

        await planner.FindCandidatesAsync(BasicGraph(), request);

        Assert.Contains("BUS", recorder.ModeSets);
        Assert.Contains("CAR", recorder.ModeSets);
        Assert.Contains("MOTORBIKE", recorder.ModeSets);
        Assert.Contains("RICKSHAW", recorder.ModeSets);
        Assert.Contains("WALK", recorder.ModeSets);
        Assert.Contains("BUS|WALK", recorder.ModeSets);
        Assert.Contains("BUS|RICKSHAW", recorder.ModeSets);
        Assert.Contains("BUS|RICKSHAW|WALK", recorder.ModeSets);
    }


    private static JourneyPath PathAlong(IReadOnlyList<RouteCoordinate> geometry, int edgeId)
    {
        var from = geometry[0];
        var to = geometry[^1];
        var edge = new RoadMobilityEdge
        {
            Id = edgeId, FromNodeId = 0, ToNodeId = 1, CandidateKey = "C",
            DistanceMeters = 1000, Geometry = geometry, TrafficRoadId = "R1", RoadClass = "secondary",
            TrafficRoadMatchMeters = 10, AllowedModes = new(StringComparer.OrdinalIgnoreCase) { MobilityModes.Car }
        };
        return new JourneyPath
        {
            Steps = [new JourneyPathStep(1, 0, 1, MobilityModes.Car, edge, 5, 0, null, null, null, null, null)],
            ArrivalTime = DateTimeOffset.Now.AddMinutes(5)
        };
    }

    private static TimeDependentDijkstraRouter Router() => new(new FixedTravel(), new ClearSafety(), new FakeTransit(), Opt());
    private static IOptions<IntelligentMobilityOptions> Opt() => Options.Create(new IntelligentMobilityOptions { GenericTransferMinutes=.1, BusTransferMinutes=.1 });
    private static IntelligentRouteRequest Request(params string[] modes) => new(){Start=new(A.Latitude,A.Longitude),Destination=new(D.Latitude,D.Longitude),DepartureTime=DateTimeOffset.Now,EnabledModes=modes.ToList(),Preference=JourneyPreferences.Balanced,MaxWalkingMeters=2000,MaxTransfers=2};
    private static MobilityGraph BasicGraph(){var g=new MobilityGraph{SourceNodeId=0,DestinationNodeId=1};g.AddNode(new(){Id=0,Coordinate=A});g.AddNode(new(){Id=1,Coordinate=D});return g;}
    private static MobilityGraph BuildMultimodalGraph(){
        var g=BasicGraph();g.AddNode(new(){Id=2,Coordinate=B});g.AddNode(new(){Id=3,Coordinate=C});
        g.Nodes[2].TransitStopIds.Add("S1"); g.Nodes[3].TransitStopIds.Add("S2");
        g.AddEdge(Road(1,0,2,700,MobilityModes.Rickshaw));
        g.AddEdge(new BusMobilityEdge{Id=2,FromNodeId=2,ToNodeId=3,CandidateKey="C",DistanceMeters=2500,Geometry=[B,C],TrafficRoadId="R1",RoadClass="secondary",TrafficRoadMatchMeters=10,RouteId="B1",RouteName="Demo bus"});
        g.AddEdge(Road(3,3,1,300,MobilityModes.Walk)); return g;
    }
    private static RoadMobilityEdge Road(int id,int from,int to,double meters,string mode,string road="R1") => new(){Id=id,FromNodeId=from,ToNodeId=to,CandidateKey="C",DistanceMeters=meters,Geometry=[from==0?A:B,to==1?D:C],TrafficRoadId=road,RoadClass="secondary",TrafficRoadMatchMeters=10,AllowedModes=new(StringComparer.OrdinalIgnoreCase){mode}};
    private static JourneyPath PathWithDurations(params double[] durations){var steps=durations.Select((d,i)=>new JourneyPathStep(i+1,i,i+1,MobilityModes.Car,Road(i+1,i,i+1,100,MobilityModes.Car),d,0,new TrafficPredictionResult(40,"MODERATE","X","HIGH","M","1","R1","Road","secondary",0),new SegmentSafetySnapshot(80,"LOWER",RouteIncidentStates.Clear,"X",1,0,false),null,null,null)).ToArray();return new JourneyPath{Steps=steps,ArrivalTime=DateTimeOffset.Now.AddMinutes(durations.Sum())};}
    private static JourneyOptionDto Option(string id,double min,double congestion,double exposure,int transfers,double resilience)=>new(){Id=id,TotalDurationMinutes=min,PredictedCongestionIndex=congestion,SafetyExposure=new(0,exposure,0,0),TransferCount=transfers,Resilience=resilience};

    private sealed class FixedTrafficPrediction(double index):ITrafficPredictionService{public bool ModelAvailable=>true;public string ModelName=>"Test";public string ModelVersion=>"1";public Task<TrafficPredictionResult> PredictAsync(TrafficPredictionRequest r,CancellationToken c=default)=>Task.FromResult(new TrafficPredictionResult(index,TrafficLevels.FromIndex(index),"ML_MODEL","HIGH","Test","1",r.TrafficRoadId,"Road",r.RoadClass,0));}
    private sealed class FixedTrafficData:ITrafficDataRepository{public int RowCount=>1;public int RoadCount=>1;public string DataStatus=>"SYNTHETIC_DEVELOPMENT";public TrafficRoadMatch? FindNearestRoad(double a,double b)=>null;public TrafficRoadProfile? GetRoad(string id)=>null;public TrafficFallbackResult GetFallback(string id,DateTimeOffset at,string rc)=>new(50,"GLOBAL","LOW");public double GetCalibratedSpeedKph(string mode,string rc,double c)=>mode switch{MobilityModes.Walk=>5,MobilityModes.Rickshaw=>12,MobilityModes.Motorbike=>30,MobilityModes.Bus=>15,MobilityModes.Car=>20,_=>10};public BusCalibration GetBusCalibration(string r,DateTimeOffset a)=>new(true,4);public double GetTrafficVolatility(string r,DateTimeOffset a)=>20;public ulong? GetMappedRoadSegmentId(string r)=>null;}
    private sealed class FakeTransit:ITransitNetworkService{private readonly TransitRouteInfo route=new("B1","Demo bus","B1",["S1","S2"]);public IReadOnlyList<TransitStopInfo> Stops=>[];public IReadOnlyList<TransitRouteInfo> Routes=>[route];public IReadOnlyList<TransitStopInfo> FindStopsNear(RouteCoordinate c,double m)=>[];public IReadOnlyList<TransitRouteInfo> GetRoutesServingStop(string id)=>id is "S1" or "S2"?[route]:[];public double GetExpectedWaitMinutes(string id,DateTimeOffset at)=>1;}
    private sealed class FixedTravel:IVehicleTravelTimeService{public Task<VehicleTravelTimeEstimate> EstimateAsync(string mode,double meters,TrafficPredictionRequest r,string? busRouteId=null,bool includeBusWait=false,CancellationToken c=default){var speed=mode switch{MobilityModes.Walk=>5d,MobilityModes.Rickshaw=>25d,MobilityModes.Bus=>40d,_=>30d};var mins=meters/1000d/speed*60;var t=new TrafficPredictionResult(40,"MODERATE","TEST","HIGH","T","1",r.TrafficRoadId,"R","secondary",0);return Task.FromResult(new VehicleTravelTimeEstimate(mode,meters,speed,mins,t,true));}}
    private sealed class RecordingTravel:IVehicleTravelTimeService{public List<DateTimeOffset> SeenTimes{get;}=[];public Task<VehicleTravelTimeEstimate> EstimateAsync(string mode,double meters,TrafficPredictionRequest r,string? busRouteId=null,bool includeBusWait=false,CancellationToken c=default){SeenTimes.Add(r.At);var t=new TrafficPredictionResult(50,"MODERATE","TEST","HIGH","T","1",r.TrafficRoadId,"R","secondary",0);return Task.FromResult(new VehicleTravelTimeEstimate(mode,meters,20,7,t,true));}}
    private sealed class ClearSafety:ISegmentSafetyProvider{public Task<SegmentSafetySnapshot> GetSafetyAsync(string? id,IReadOnlyList<RouteCoordinate> g,IReadOnlyList<RouteIncidentDto> incidents,CancellationToken c=default)=>Task.FromResult(new SegmentSafetySnapshot(85,"LOWER",RouteIncidentStates.Clear,"TEST",1,0,false));}
    private sealed class HardBlockSafety:ISegmentSafetyProvider{public Task<SegmentSafetySnapshot> GetSafetyAsync(string? id,IReadOnlyList<RouteCoordinate> g,IReadOnlyList<RouteIncidentDto> incidents,CancellationToken c=default)=>Task.FromResult(id=="BLOCKED"?new SegmentSafetySnapshot(10,"VERY_HIGH",RouteIncidentStates.Affected,"TEST",1,1,true):new SegmentSafetySnapshot(85,"LOWER",RouteIncidentStates.Clear,"TEST",1,0,false));}
    private sealed class RecordingJourneyRouter : ITimeDependentJourneyRouter
    {
        private int _nextEdge = 1000;
        public HashSet<string> ModeSets { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Task<JourneyPath?> FindBestAsync(
            MobilityGraph graph,
            IntelligentRouteRequest request,
            IReadOnlySet<int>? excludedEdgeIds = null,
            string? singleModeOnly = null,
            CancellationToken cancellationToken = default)
        {
            var modes = request.EnabledModes
                .Select(m => m.Trim().ToUpperInvariant())
                .OrderBy(m => m, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            ModeSets.Add(string.Join("|", modes));
            if (modes.Length == 0)
            {
                return Task.FromResult<JourneyPath?>(null);
            }

            var id = _nextEdge++;
            var mode = modes.Contains(MobilityModes.Bus, StringComparer.OrdinalIgnoreCase)
                ? MobilityModes.Bus
                : modes[0];
            MobilityEdge edge = mode == MobilityModes.Bus
                ? new BusMobilityEdge
                {
                    Id = id, FromNodeId = graph.SourceNodeId, ToNodeId = graph.DestinationNodeId,
                    CandidateKey = "TEST", DistanceMeters = 1000, Geometry = [A, D],
                    RouteId = "B1", RouteName = "Test bus", TrafficRoadMatchMeters = 10
                }
                : new RoadMobilityEdge
                {
                    Id = id, FromNodeId = graph.SourceNodeId, ToNodeId = graph.DestinationNodeId,
                    CandidateKey = "TEST", DistanceMeters = 1000, Geometry = [A, D],
                    TrafficRoadMatchMeters = 10,
                    AllowedModes = new(StringComparer.OrdinalIgnoreCase) { mode }
                };

            var path = new JourneyPath
            {
                Steps = [new JourneyPathStep(1, graph.SourceNodeId, graph.DestinationNodeId, mode, edge, 5, 0, null, null, null, null, null)],
                GeneralizedSearchCost = id,
                ArrivalTime = DateTimeOffset.Now.AddMinutes(5)
            };
            return Task.FromResult<JourneyPath?>(path);
        }
    }

    private sealed class DepartureSensitiveTravel:IVehicleTravelTimeService
    {
        public Task<VehicleTravelTimeEstimate> EstimateAsync(string mode,double meters,TrafficPredictionRequest r,string? busRouteId=null,bool includeBusWait=false,CancellationToken c=default)
        {
            var minutes = r.At.Minute >= 30 || r.At.Hour >= 10 ? 10d : 22d;
            var congestion = minutes > 15 ? 85d : 40d;
            var t=new TrafficPredictionResult(congestion,TrafficLevels.FromIndex(congestion),"TEST","HIGH","T","1",r.TrafficRoadId,"R","secondary",0);
            return Task.FromResult(new VehicleTravelTimeEstimate(mode,meters,20,minutes,t,true));
        }
    }
}

