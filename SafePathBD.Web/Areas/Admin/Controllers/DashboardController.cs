using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SafePathBD.Web.Common;
using SafePathBD.Web.Models.DTOs.Moderation;
using SafePathBD.Web.Security;
using SafePathBD.Web.Services.Interfaces;

namespace SafePathBD.Web.Areas.Admin.Controllers;

[Area("Admin")]
[Authorize(Roles = RoleNames.Admin)]
public class DashboardController : Controller
{
    private readonly IReportModerationService _moderation;

    public DashboardController(IReportModerationService moderation)
    {
        _moderation = moderation;
    }

    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        ViewData["AdminName"] = User.GetDisplayName();

        return View(new AdminOverviewViewModel
        {
            Counts = await _moderation.GetCountsAsync(cancellationToken),
            RecentActions = await _moderation.GetRecentActionsAsync(8, cancellationToken)
        });
    }
}

public sealed class AdminOverviewViewModel
{
    public ModerationCountsDto Counts { get; init; } = new(0, 0, 0, 0, 0, 0, 0);

    public IReadOnlyList<AdminActionEntryDto> RecentActions { get; init; } = Array.Empty<AdminActionEntryDto>();
}
