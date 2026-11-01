using DevExpress.ExpressApp;
using XafStateless.Module.BusinessObjects;
using XafStateless.Module.Services;

namespace XafStateless.Demo.Blazor.Server;

/// <summary>Delivers the outbox: every 30 s, each Pending notification is logged and marked Sent.</summary>
// Kept simple: log sink; a host plugs its own mail delivery in here
public sealed class LogNotificationDispatcher(IServiceScopeFactory scopes, ILogger<LogNotificationDispatcher> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        do
        {
            try
            {
                using var scope = scopes.CreateScope();
                using var os = scope.ServiceProvider.GetRequiredService<INonSecuredObjectSpaceFactory>().CreateNonSecuredObjectSpace<WorkflowNotification>();
                var pending = os.GetObjectsQuery<WorkflowNotification>().Where(n => n.State == NotificationState.Pending).OrderBy(n => n.CreatedUtc).ToList();
                foreach (var n in pending)
                {
                    // A log, so unmasked (security: null). A mail renders per recipient from their secured space (docs/integrating.md step 9).
                    var body = WorkflowNotifications.Render(n, os, null);
                    if (n.UserId is { } user) log.LogInformation("Notification to user {UserName} ({UserId}): {Body}", n.UserName, user, body);
                    else log.LogInformation("Notification to role {Role}: {Body}", n.Role, body);
                    (n.State, n.Attempts) = (NotificationState.Sent, n.Attempts + 1);
                }
                os.CommitChanges();
                log.LogInformation("Notification dispatch tick: {Sent} sent", pending.Count);
            }
            catch (Exception ex) { log.LogError(ex, "Notification dispatch tick failed"); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
