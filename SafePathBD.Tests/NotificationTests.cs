using Microsoft.EntityFrameworkCore;
using SafePathBD.Web.Common;
using SafePathBD.Web.Models.Entities;
using SafePathBD.Web.Models.DTOs.Moderation;
using SafePathBD.Web.Models.DTOs.Reports;
using SafePathBD.Web.Services.Interfaces;

namespace SafePathBD.Tests;

public class NotificationTests
{
    private const ulong Reporter = 7;
    private const ulong OtherUser = 8;
    private const ulong Moderator = 9;

    private static readonly ReportLocationInput Point =
        new(23.7465, 90.3760, "Dhanmondi 27", null, "Dhanmondi", "Dhaka", "Dhaka", "OSM");

    private static Task<CreateReportResult> AddHazardAsync(ReportTestContext ctx, ulong userId = Reporter) =>
        ctx.Hazards.CreateAsync(
            new CreateHazardReportRequest(userId, "Dangerous pothole", "Deep pothole.", Point, 1, HazardRiskLevels.High, DateTime.Now, null),
            Array.Empty<StoredImage>());

    private static ModerationDecision Decide(ulong reportId, string target, string? note = null) =>
        new(reportId, Moderator, target, note, null);

    [Fact]
    public async Task ReporterReceivesVerifiedNotification()
    {
        using var ctx = new ReportTestContext();
        var report = await AddHazardAsync(ctx);

        await ctx.Moderation.ApplyDecisionAsync(Decide(report.ReportId, ReportStatusCodes.Verified));

        var notification = await ctx.Db.Notifications.Include(n => n.NotificationType).SingleAsync();
        Assert.Equal(Reporter, notification.UserId);
        Assert.Equal(report.ReportId, notification.ReportId);
        Assert.Equal(NotificationTypeCodes.ReportVerified, notification.NotificationType.TypeCode);
        Assert.False(notification.IsRead);
    }

    [Fact]
    public async Task ReporterReceivesRejectedNotification()
    {
        using var ctx = new ReportTestContext();
        var report = await AddHazardAsync(ctx);

        await ctx.Moderation.ApplyDecisionAsync(Decide(report.ReportId, ReportStatusCodes.Rejected, "The location could not be confirmed."));

        var notification = await ctx.Db.Notifications.Include(n => n.NotificationType).SingleAsync();
        Assert.Equal(NotificationTypeCodes.ReportRejected, notification.NotificationType.TypeCode);
        Assert.Equal("Report rejected", notification.Title);
        Assert.False(notification.Message.Contains("location could not be confirmed", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task NeedsInfoUsesExistingSystemNotificationType()
    {
        using var ctx = new ReportTestContext();
        var report = await AddHazardAsync(ctx);

        await ctx.Moderation.ApplyDecisionAsync(Decide(report.ReportId, ReportStatusCodes.NeedsInfo, "Please add a clearer photo."));

        var notification = await ctx.Db.Notifications.Include(n => n.NotificationType).SingleAsync();
        Assert.Equal(NotificationTypeCodes.System, notification.NotificationType.TypeCode);
        Assert.Equal("More information needed", notification.Title);
        Assert.Contains("clearer photo", notification.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DuplicateUsesExistingSystemNotificationType()
    {
        using var ctx = new ReportTestContext();
        var report = await AddHazardAsync(ctx);

        await ctx.Moderation.ApplyDecisionAsync(Decide(report.ReportId, ReportStatusCodes.Duplicate, "Already reported nearby."));

        var notification = await ctx.Db.Notifications.Include(n => n.NotificationType).SingleAsync();
        Assert.Equal(NotificationTypeCodes.System, notification.NotificationType.TypeCode);
        Assert.Equal("Report marked duplicate", notification.Title);
    }

    [Fact]
    public async Task ReporterReceivesResolvedNotification()
    {
        using var ctx = new ReportTestContext();
        var report = await AddHazardAsync(ctx);

        await ctx.Moderation.ApplyDecisionAsync(Decide(report.ReportId, ReportStatusCodes.Verified));
        ctx.Db.ChangeTracker.Clear(); // emulate the next HTTP request / scoped DbContext
        await ctx.Moderation.ApplyDecisionAsync(Decide(report.ReportId, ReportStatusCodes.Resolved));

        var types = await ctx.Db.Notifications
            .Include(n => n.NotificationType)
            .OrderBy(n => n.NotificationId)
            .Select(n => n.NotificationType.TypeCode)
            .ToListAsync();

        Assert.Equal(new[] { NotificationTypeCodes.ReportVerified, NotificationTypeCodes.ReportResolved }, types);
    }

    [Fact]
    public async Task RetryingSameSuccessfulStateDoesNotCreateDuplicateNotification()
    {
        using var ctx = new ReportTestContext();
        var report = await AddHazardAsync(ctx);

        var first = await ctx.Moderation.ApplyDecisionAsync(Decide(report.ReportId, ReportStatusCodes.Verified));
        ctx.Db.ChangeTracker.Clear(); // retry arrives as a separate request in production
        var retry = await ctx.Moderation.ApplyDecisionAsync(Decide(report.ReportId, ReportStatusCodes.Verified));

        Assert.True(first.Succeeded);
        Assert.False(retry.Succeeded);
        Assert.Equal(1, await ctx.Db.Notifications.CountAsync());
    }

    [Fact]
    public async Task UnderReviewDoesNotCreateNotificationNoise()
    {
        using var ctx = new ReportTestContext();
        var report = await AddHazardAsync(ctx);

        await ctx.Moderation.ApplyDecisionAsync(Decide(report.ReportId, ReportStatusCodes.UnderReview));

        Assert.Empty(ctx.Db.Notifications);
    }

    [Fact]
    public async Task OtherUserCannotReadNotificationByGuessingId()
    {
        using var ctx = new ReportTestContext();
        var report = await AddHazardAsync(ctx);
        await ctx.Moderation.ApplyDecisionAsync(Decide(report.ReportId, ReportStatusCodes.Verified));
        var id = await ctx.Db.Notifications.Select(n => n.NotificationId).SingleAsync();

        var result = await ctx.Notifications.MarkReadAsync(OtherUser, id);

        Assert.False(result);
        Assert.False((await ctx.Db.Notifications.SingleAsync()).IsRead);
    }

    [Fact]
    public async Task UnreadCountAndMarkOneReadAreUserScoped()
    {
        using var ctx = new ReportTestContext();
        SeedNotification(ctx, Reporter, "A");
        SeedNotification(ctx, Reporter, "B");
        SeedNotification(ctx, OtherUser, "C");
        await ctx.Db.SaveChangesAsync();

        var first = await ctx.Db.Notifications.Where(n => n.UserId == Reporter).OrderBy(n => n.NotificationId).FirstAsync();
        Assert.Equal(2, await ctx.Notifications.GetUnreadCountAsync(Reporter));

        Assert.True(await ctx.Notifications.MarkReadAsync(Reporter, first.NotificationId));
        Assert.Equal(1, await ctx.Notifications.GetUnreadCountAsync(Reporter));
        Assert.Equal(1, await ctx.Notifications.GetUnreadCountAsync(OtherUser));
        Assert.NotNull((await ctx.Db.Notifications.FindAsync(first.NotificationId))!.ReadAt);
    }

    [Fact]
    public async Task MarkAllReadOnlyTouchesCurrentUser()
    {
        using var ctx = new ReportTestContext();
        SeedNotification(ctx, Reporter, "A");
        SeedNotification(ctx, Reporter, "B");
        SeedNotification(ctx, OtherUser, "C");
        await ctx.Db.SaveChangesAsync();

        var updated = await ctx.Notifications.MarkAllReadAsync(Reporter);

        Assert.Equal(2, updated);
        Assert.Equal(0, await ctx.Notifications.GetUnreadCountAsync(Reporter));
        Assert.Equal(1, await ctx.Notifications.GetUnreadCountAsync(OtherUser));
    }

    [Fact]
    public async Task ReadNotificationRemainsReadWhenMarkedAgain()
    {
        using var ctx = new ReportTestContext();
        SeedNotification(ctx, Reporter, "A");
        await ctx.Db.SaveChangesAsync();
        var notification = await ctx.Db.Notifications.SingleAsync();

        Assert.True(await ctx.Notifications.MarkReadAsync(Reporter, notification.NotificationId));
        var firstReadAt = (await ctx.Db.Notifications.SingleAsync()).ReadAt;
        Assert.True(await ctx.Notifications.MarkReadAsync(Reporter, notification.NotificationId));
        var secondReadAt = (await ctx.Db.Notifications.SingleAsync()).ReadAt;

        Assert.Equal(firstReadAt, secondReadAt);
    }

    private static void SeedNotification(ReportTestContext ctx, ulong userId, string title)
    {
        ctx.Db.Notifications.Add(new Notifications
        {
            UserId = userId,
            NotificationTypeId = ctx.Db.NotificationTypes.Single(t => t.TypeCode == NotificationTypeCodes.System).NotificationTypeId,
            Title = title,
            Message = "Test message",
            IsRead = false,
            CreatedAt = DateTime.Now
        });
    }
}

