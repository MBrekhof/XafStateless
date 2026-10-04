using XafStateless.Module.Services;

namespace XafStateless.Demo.Blazor.Server.Services;

/// <summary>The demo's one workflow action, "log": writes its argument to the log. A real host's actions change related
/// records through <see cref="WorkflowActionContext.ObjectSpace"/> (release slots, refund); see IWorkflowAction (XSM-032).</summary>
public sealed class LogWorkflowAction(ILogger<LogWorkflowAction> log) : IWorkflowAction
{
    public string Name => "log";

    public void Run(WorkflowActionContext context) =>
        log.LogInformation("Workflow action 'log': {Type} {Id} entered '{State}': {Argument}",
            context.Subject.GetType().Name, context.Subject.ID, context.Subject.Workflow?.StateCaption, context.Argument);
}
