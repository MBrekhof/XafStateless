using DevExpress.ExpressApp;
using XafStateless.Module.BusinessObjects;
using XafStateless.Module.Services;

namespace XafStateless.Demo.Blazor.Server;

/// <summary>Host-owned timer: every 30 s (first tick at startup) fires the due Wait states as caller "system".
/// INonSecuredObjectSpaceFactory is registered scoped (XAF's TryAddScoped), so each tick runs in its own DI scope.</summary>
public sealed class WorkflowTimerService(IServiceScopeFactory scopes, WorkflowRuntime runtime, ILogger<WorkflowTimerService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        do
        {
            try
            {
                using var scope = scopes.CreateScope();
                var factory = scope.ServiceProvider.GetRequiredService<INonSecuredObjectSpaceFactory>();
                var fired = runtime.RunDueTimers(() => factory.CreateNonSecuredObjectSpace<WorkflowInstance>());
                log.LogInformation("Workflow timer tick: {Fired} fired", fired);
            }
            catch (Exception ex) { log.LogError(ex, "Workflow timer tick failed"); }   // keep the host alive; the next tick retries
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
