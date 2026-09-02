using Microsoft.EntityFrameworkCore;

namespace SafePathBD.Web.Data;

public static class DatabaseConnectionCheck
{
    // Read-only startup probe. It must never create, migrate or alter the existing schema.
    public static async Task VerifyAsync(IServiceProvider services, ILogger logger, CancellationToken cancellationToken = default)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SafePathDbContext>();

        try
        {
            if (!await db.Database.CanConnectAsync(cancellationToken))
            {
                logger.LogError(
                    "Cannot connect to the SafePath BD database. Verify that MySQL is running and that ConnectionStrings:DefaultConnection is set in User Secrets.");
                return;
            }

            var roleCount = await db.Roles.AsNoTracking().CountAsync(cancellationToken);
            var statusCount = await db.ReportStatuses.AsNoTracking().CountAsync(cancellationToken);

            logger.LogInformation(
                "SafePath BD database connection verified. roles={RoleCount}, report_statuses={ReportStatusCount}",
                roleCount,
                statusCount);
        }
        catch (Exception ex)
        {
            // Message is logged without the connection string so credentials never reach the log sink.
            logger.LogError(ex, "SafePath BD database verification failed.");
        }
    }
}
