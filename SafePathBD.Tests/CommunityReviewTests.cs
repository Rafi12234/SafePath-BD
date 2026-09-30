using SafePathBD.Web.Common;
using SafePathBD.Web.Models.DTOs.Reports;
using SafePathBD.Web.Services.Interfaces;

namespace SafePathBD.Tests;

/// <summary>
/// The centralised visibility policy and the community review listing that depends on it.
/// </summary>
public class CommunityReviewTests
{
    private const ulong Owner = 7;
    private const ulong Member = 8;

    private static readonly ReportLocationInput Point =
        new(23.7465, 90.3760, "Dhanmondi 27", null, "Dhanmondi", "Dhaka", "Dhaka", "OSM");

    private static async Task<ulong> AddHazardAsync(ReportTestContext ctx, ulong userId, string title = "Pothole")
    {
        var result = await ctx.Hazards.CreateAsync(
            new CreateHazardReportRequest(userId, title, null, Point, 1, HazardRiskLevels.High, DateTime.Now, null),
            Array.Empty<StoredImage>());

        return result.ReportId;
    }

    // ------------------------------------------------------- the policy itself

    [Theory]
    [InlineData(ReportStatusCodes.Pending, true, true)]
    [InlineData(ReportStatusCodes.UnderReview, true, true)]
    [InlineData(ReportStatusCodes.NeedsInfo, true, true)]
    [InlineData(ReportStatusCodes.Verified, true, false)]
    [InlineData(ReportStatusCodes.Rejected, true, false)]
    [InlineData(ReportStatusCodes.Pending, false, false)]
    public void CommunityReviewableCoversOnlyOpenPublicReports(string statusCode, bool isPublic, bool expected)
    {
        Assert.Equal(expected, ReportVisibility.IsCommunityReviewable(statusCode, isPublic));
    }

    [Theory]
    [InlineData(ReportStatusCodes.Verified, true, true)]
    [InlineData(ReportStatusCodes.Verified, false, false)]
    [InlineData(ReportStatusCodes.Pending, true, false)]
    public void AnonymousVisibilityStaysStrict(string statusCode, bool isPublic, bool expected)
    {
        Assert.Equal(expected, ReportVisibility.IsPubliclyVisible(statusCode, isPublic));

        // An anonymous viewer is never granted community access, whatever the status.
        Assert.Equal(expected, ReportVisibility.CanView(statusCode, isPublic, false, false, isAuthenticated: false));
    }

    [Fact]
    public void ASignedInMemberSeesOpenPublicReportsThatAnonymousVisitorsCannot()
    {
        Assert.False(ReportVisibility.CanView(ReportStatusCodes.Pending, true, false, false, isAuthenticated: false));
        Assert.True(ReportVisibility.CanView(ReportStatusCodes.Pending, true, false, false, isAuthenticated: true));
    }

    [Fact]
    public void StaffAndOwnersSeeEverythingAboutTheirReports()
    {
        Assert.True(ReportVisibility.CanView(ReportStatusCodes.Rejected, false, isOwner: true, isStaff: false, isAuthenticated: true));
        Assert.True(ReportVisibility.CanView(ReportStatusCodes.Rejected, false, isOwner: false, isStaff: true, isAuthenticated: true));
    }

    [Fact]
    public void OnlyOrdinaryMembersMayVote()
    {
        Assert.True(ReportVisibility.CanVote(ReportStatusCodes.Pending, true, false, false, true));
        Assert.False(ReportVisibility.CanVote(ReportStatusCodes.Pending, true, isOwner: true, isStaff: false, isAuthenticated: true));
        Assert.False(ReportVisibility.CanVote(ReportStatusCodes.Pending, true, isOwner: false, isStaff: true, isAuthenticated: true));
        Assert.False(ReportVisibility.CanVote(ReportStatusCodes.Pending, true, false, false, isAuthenticated: false));
    }

    // ------------------------------------------------------------- the listing

    [Fact]
    public async Task TheListingShowsOpenPublicReportsFromOtherPeople()
    {
        using var ctx = new ReportTestContext();
        await AddHazardAsync(ctx, Owner, "Someone else's");

        var page = await ctx.Reports.GetCommunityReviewAsync(new CommunityReviewQuery(Member));

        Assert.Single(page.Items);
        Assert.Equal("Someone else's", page.Items[0].Title);
        Assert.Equal(ReportStatusCodes.Pending, page.Items[0].StatusCode);
    }

    [Fact]
    public async Task TheListingHidesTheViewersOwnReports()
    {
        using var ctx = new ReportTestContext();
        await AddHazardAsync(ctx, Member, "Mine");
        await AddHazardAsync(ctx, Owner, "Theirs");

        var page = await ctx.Reports.GetCommunityReviewAsync(new CommunityReviewQuery(Member));

        Assert.Single(page.Items);
        Assert.Equal("Theirs", page.Items[0].Title);
    }

    [Fact]
    public async Task TheListingHidesVerifiedAndClosedReports()
    {
        using var ctx = new ReportTestContext();
        var verified = await AddHazardAsync(ctx, Owner, "Verified");
        var rejected = await AddHazardAsync(ctx, Owner, "Rejected");
        await AddHazardAsync(ctx, Owner, "Still open");

        ctx.SetStatus(verified, ReportStatusCodes.Verified);
        ctx.SetStatus(rejected, ReportStatusCodes.Rejected);

        var page = await ctx.Reports.GetCommunityReviewAsync(new CommunityReviewQuery(Member));

        Assert.Single(page.Items);
        Assert.Equal("Still open", page.Items[0].Title);
    }

    [Fact]
    public async Task TheListingHidesPrivateReports()
    {
        using var ctx = new ReportTestContext();
        var priv = await AddHazardAsync(ctx, Owner, "Private");
        ctx.SetStatus(priv, ReportStatusCodes.Pending, isPublic: false);

        var page = await ctx.Reports.GetCommunityReviewAsync(new CommunityReviewQuery(Member));

        Assert.Empty(page.Items);
    }

    [Fact]
    public async Task TheListingCarriesCommunityCountsAndTheViewersOwnVote()
    {
        using var ctx = new ReportTestContext();
        var reportId = await AddHazardAsync(ctx, Owner);
        await ctx.Community.CastVoteAsync(reportId, ReportTestContext.Member(Member), ReportVoteTypes.Confirm);
        await ctx.Community.AddCommentAsync(reportId, ReportTestContext.Member(Member), "Still blocked.", null);

        var item = (await ctx.Reports.GetCommunityReviewAsync(new CommunityReviewQuery(Member))).Items.Single();

        Assert.Equal(1, item.ConfirmCount);
        Assert.Equal(0, item.DisputeCount);
        Assert.Equal(1, item.CommentCount);
        Assert.Equal(ReportVoteTypes.Confirm, item.MyVote);
    }

    [Fact]
    public async Task TheListingCarriesNoReporterIdentity()
    {
        using var ctx = new ReportTestContext();
        await AddHazardAsync(ctx, Owner);

        Assert.DoesNotContain(
            typeof(CommunityReportSummaryDto).GetProperties(),
            p => p.Name.Contains("Reporter") || p.Name.Contains("Email") || p.Name == "UserId");
    }

    [Fact]
    public async Task TheListingFiltersByTypeAndStatus()
    {
        using var ctx = new ReportTestContext();
        await AddHazardAsync(ctx, Owner, "A hazard");
        var underReview = await AddHazardAsync(ctx, Owner, "Being reviewed");
        ctx.SetStatus(underReview, ReportStatusCodes.UnderReview);

        var hazards = await ctx.Reports.GetCommunityReviewAsync(
            new CommunityReviewQuery(Member, ReportTypes.Hazard));
        var reviewing = await ctx.Reports.GetCommunityReviewAsync(
            new CommunityReviewQuery(Member, null, ReportStatusCodes.UnderReview));

        Assert.Equal(2, hazards.TotalCount);
        Assert.Single(reviewing.Items);
        Assert.Equal("Being reviewed", reviewing.Items[0].Title);
    }

    [Fact]
    public async Task TheCountMatchesTheListing()
    {
        using var ctx = new ReportTestContext();
        await AddHazardAsync(ctx, Owner);
        await AddHazardAsync(ctx, Member, "Mine, excluded");

        Assert.Equal(1, await ctx.Reports.GetCommunityReviewCountAsync(Member));
    }

    [Fact]
    public async Task TheListingIsPaged()
    {
        using var ctx = new ReportTestContext();
        for (var i = 0; i < 25; i++)
        {
            await AddHazardAsync(ctx, Owner, "Report " + i);
        }

        var page = await ctx.Reports.GetCommunityReviewAsync(new CommunityReviewQuery(Member, PageSize: 20));

        Assert.Equal(25, page.TotalCount);
        Assert.Equal(20, page.Items.Count);
        Assert.True(page.HasNext);
    }

    // ------------------------------------------------------ details + evidence

    [Fact]
    public async Task AMemberCanOpenAPendingReportButStillDoesNotSeeTheReportersName()
    {
        using var ctx = new ReportTestContext();
        var reportId = await AddHazardAsync(ctx, Owner);

        var details = await ctx.Reports.GetDetailsAsync(reportId, Member, viewerIsStaff: false);

        Assert.NotNull(details);
        Assert.Equal("Community reporter", details!.ReporterName);
    }

    [Fact]
    public async Task AnAnonymousVisitorStillCannotOpenAPendingReport()
    {
        using var ctx = new ReportTestContext();
        var reportId = await AddHazardAsync(ctx, Owner);

        Assert.Null(await ctx.Reports.GetDetailsAsync(reportId, null, viewerIsStaff: false));
    }
}
