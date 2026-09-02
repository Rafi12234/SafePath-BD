namespace SafePathBD.Web.Common;

/// <summary>
/// Values that must match the <c>report_votes.vote_type</c> enum exactly.
/// </summary>
public static class ReportVoteTypes
{
    public const string Confirm = "CONFIRM";
    public const string Dispute = "DISPUTE";

    public static readonly IReadOnlyList<string> All = new[] { Confirm, Dispute };

    public static bool IsValid(string? value) => value is not null && All.Contains(value);

    /// <summary>Normalises client input; returns null when the value is not a supported vote.</summary>
    public static string? Normalize(string? value)
    {
        var upper = value?.Trim().ToUpperInvariant();
        return IsValid(upper) ? upper : null;
    }
}

/// <summary>
/// <c>admin_actions.action_type</c> values written by the moderation module.
/// </summary>
public static class AdminActionTypes
{
    public const string ReportVerified = "REPORT_VERIFIED";
    public const string ReportRejected = "REPORT_REJECTED";
    public const string ReportMarkedDuplicate = "REPORT_MARKED_DUPLICATE";
    public const string ReportNeedsInfo = "REPORT_NEEDS_INFO";
    public const string ReportResolved = "REPORT_RESOLVED";
    public const string ReportUnderReview = "REPORT_UNDER_REVIEW";
    public const string AccidentPromoted = "ACCIDENT_PROMOTED";

    public const string ReportEntity = "reports";
    public const string AccidentEntity = "accidents";

    /// <summary>Maps a target status code to the audit action recorded for it.</summary>
    public static string ForStatus(string statusCode) => statusCode switch
    {
        ReportStatusCodes.Verified => ReportVerified,
        ReportStatusCodes.Rejected => ReportRejected,
        ReportStatusCodes.Duplicate => ReportMarkedDuplicate,
        ReportStatusCodes.NeedsInfo => ReportNeedsInfo,
        ReportStatusCodes.Resolved => ReportResolved,
        ReportStatusCodes.UnderReview => ReportUnderReview,
        _ => "REPORT_STATUS_CHANGED"
    };
}

/// <summary>
/// The single source of truth for which report status changes a moderator may perform.
/// Closed outcomes (REJECTED, DUPLICATE, RESOLVED) are terminal so history cannot be rewritten.
/// </summary>
public static class ReportStatusTransitions
{
    private static readonly IReadOnlyDictionary<string, string[]> Allowed =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            [ReportStatusCodes.Pending] = new[]
            {
                ReportStatusCodes.UnderReview,
                ReportStatusCodes.Verified,
                ReportStatusCodes.Rejected,
                ReportStatusCodes.Duplicate,
                ReportStatusCodes.NeedsInfo
            },
            [ReportStatusCodes.UnderReview] = new[]
            {
                ReportStatusCodes.Verified,
                ReportStatusCodes.Rejected,
                ReportStatusCodes.Duplicate,
                ReportStatusCodes.NeedsInfo
            },
            [ReportStatusCodes.NeedsInfo] = new[]
            {
                ReportStatusCodes.UnderReview,
                ReportStatusCodes.Verified,
                ReportStatusCodes.Rejected
            },
            [ReportStatusCodes.Verified] = new[]
            {
                ReportStatusCodes.Resolved
            },
            [ReportStatusCodes.Rejected] = Array.Empty<string>(),
            [ReportStatusCodes.Duplicate] = Array.Empty<string>(),
            [ReportStatusCodes.Resolved] = Array.Empty<string>()
        };

    /// <summary>Target statuses reachable from <paramref name="fromStatusCode"/>.</summary>
    public static IReadOnlyList<string> From(string fromStatusCode) =>
        Allowed.TryGetValue(fromStatusCode, out var targets) ? targets : Array.Empty<string>();

    public static bool IsAllowed(string fromStatusCode, string toStatusCode) =>
        From(fromStatusCode).Contains(toStatusCode, StringComparer.Ordinal);

    /// <summary>Statuses that require the reviewer to explain the decision.</summary>
    public static bool RequiresNote(string toStatusCode) =>
        toStatusCode is ReportStatusCodes.Rejected
            or ReportStatusCodes.Duplicate
            or ReportStatusCodes.NeedsInfo;
}
