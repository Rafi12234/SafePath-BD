using SafePathBD.Web.Common;
using SafePathBD.Web.Models.Entities;
using SafePathBD.Web.Models.DTOs.Moderation;
using SafePathBD.Web.Models.DTOs.Reports;
using SafePathBD.Web.Services.Interfaces;

namespace SafePathBD.Tests;

public class DashboardServiceTests
{
    private const ulong Reporter = 7;
    private const ulong OtherUser = 8;
    private const ulong Moderator = 9;

    private static readonly ReportLocationInput Point =
        new(23.7465, 90.3760, "Dhanmondi 27", null, "Dhanmondi", "Dhaka", "Dhaka", "OSM");

    private static Task<CreateReportResult> AddHazardAsync(ReportTestContext ctx, ulong userId, string title) =>
        ctx.Hazards.CreateAsync(
            new CreateHazardReportRequest(userId, title, null, Point, 1, HazardRiskLevels.High, DateTime.Now, null),
            Array.Empty<StoredImage>());

    [Fact]
    public async Task UserDashboardCountsOnlyAuthenticatedUsersReports()
    {
        using var ctx = new ReportTestContext();
        var pending = await AddHazardAsync(ctx, Reporter, "Mine pending");
        var verified = await AddHazardAsync(ctx, Reporter, "Mine verified");
        await AddHazardAsync(ctx, OtherUser, "Not mine");
        ctx.SetStatus(verified.ReportId, ReportStatusCodes.Verified);

        var dashboard = await ctx.Dashboards.GetUserDashboardAsync(Reporter);

        Assert.Equal(2, dashboard.TotalReports);
        Assert.Equal(1, dashboard.Pending);
        Assert.Equal(1, dashboard.Verified);
        Assert.Equal(2, dashboard.RecentReports.Count);
        Assert.All(dashboard.RecentReports, report => Assert.NotEqual("Not mine", report.Title));
    }

    [Fact]
    public async Task NeedsInfoIsSurfacedWithReviewerRequest()
    {
        using var ctx = new ReportTestContext();
        var report = await AddHazardAsync(ctx, Reporter, "Needs evidence");

        await ctx.Moderation.ApplyDecisionAsync(new ModerationDecision(
            report.ReportId,
            Moderator,
            ReportStatusCodes.NeedsInfo,
            "Please upload a clearer road photo.",
            ReportStatusCodes.Pending));

        var dashboard = await ctx.Dashboards.GetUserDashboardAsync(Reporter);

        Assert.Equal(1, dashboard.NeedsInfo);
        Assert.Single(dashboard.ActionRequired);
        Assert.Contains("clearer road photo", dashboard.ActionRequired[0].ReviewerNote, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UserDashboardUnreadCountComesFromNotificationsTable()
    {
        using var ctx = new ReportTestContext();
        var systemTypeId = ctx.Db.NotificationTypes.Single(t => t.TypeCode == NotificationTypeCodes.System).NotificationTypeId;
        ctx.Db.Notifications.AddRange(
            new Notifications { UserId = Reporter, NotificationTypeId = systemTypeId, Title = "Unread", Message = "A", IsRead = false, CreatedAt = DateTime.Now },
            new Notifications { UserId = Reporter, NotificationTypeId = systemTypeId, Title = "Read", Message = "B", IsRead = true, ReadAt = DateTime.Now, CreatedAt = DateTime.Now },
            new Notifications { UserId = OtherUser, NotificationTypeId = systemTypeId, Title = "Other", Message = "C", IsRead = false, CreatedAt = DateTime.Now });
        await ctx.Db.SaveChangesAsync();

        var dashboard = await ctx.Dashboards.GetUserDashboardAsync(Reporter);

        Assert.Equal(1, dashboard.UnreadNotifications);
        Assert.Equal(2, dashboard.RecentNotifications.Count);
    }

    [Fact]
    public async Task ModeratorDashboardUsesRealQueueAndVerificationHistory()
    {
        using var ctx = new ReportTestContext();
        await AddHazardAsync(ctx, Reporter, "Still pending");
        var verified = await AddHazardAsync(ctx, Reporter, "Will verify");
        await ctx.Moderation.ApplyDecisionAsync(new ModerationDecision(
            verified.ReportId, Moderator, ReportStatusCodes.Verified, null, ReportStatusCodes.Pending));

        var dashboard = await ctx.Dashboards.GetModeratorDashboardAsync();

        Assert.Equal(1, dashboard.Counts.Pending);
        Assert.Equal(1, dashboard.Counts.Verified);
        Assert.NotEmpty(dashboard.QueuePreview);
        Assert.Contains(dashboard.RecentActivity, item => item.ReportId == verified.ReportId && item.StatusCode == ReportStatusCodes.Verified);
    }

    [Fact]
    public async Task AdminDashboardUsesRealUserAndReportCounts()
    {
        using var ctx = new ReportTestContext();
        await AddHazardAsync(ctx, Reporter, "Hazard");
        await ctx.Accidents.CreateAsync(
            new CreateAccidentReportRequest(Reporter, "Accident", null, Point, 1, 2, DateTime.Now, null, 0, 0, null),
            Array.Empty<StoredImage>());

        var dashboard = await ctx.Dashboards.GetAdminDashboardAsync();

        Assert.Equal(3, dashboard.TotalUsers);
        Assert.Equal(3, dashboard.ActiveUsers);
        Assert.Equal(1, dashboard.AccidentReports);
        Assert.Equal(1, dashboard.HazardReports);
        Assert.Equal(2, dashboard.ReportCounts.Pending);
    }
}

