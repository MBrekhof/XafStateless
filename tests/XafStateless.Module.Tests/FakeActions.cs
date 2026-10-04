using XafStateless.Demo.Module.BusinessObjects;
using XafStateless.Module.Services;

namespace XafStateless.Module.Tests;

/// <summary>A host action "refund": creates a second PurchaseRequest titled with its argument through the command's object
/// space (a related record), then runs <see cref="Also"/>, a per-test hook (throw, touch a locked member). Counts its calls.</summary>
public sealed class RefundAction : IWorkflowAction
{
    public const string Title = "Refund for ";

    public string Name => "refund";
    public int Calls;
    public readonly List<(Guid Subject, string? State)> Seen = [];
    public Action<WorkflowActionContext>? Also;

    public void Run(WorkflowActionContext context)
    {
        Calls++;
        Seen.Add((context.Subject.ID, context.Subject.Workflow?.StateKey));
        context.ObjectSpace.CreateObject<PurchaseRequest>().Title = context.Argument;
        Also?.Invoke(context);
    }
}
