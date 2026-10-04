using DevExpress.ExpressApp;
using XafStateless.Module.BusinessObjects;

namespace XafStateless.Module.Services;

/// <summary>
/// A host action a <see cref="Engine.EffectKind.RunAction"/> effect runs when its state is entered (XSM-032): the way a
/// workflow changes records other than its subject (release a student's lesson slots, refund). Registered in DI as a
/// singleton (<c>services.AddSingleton&lt;IWorkflowAction, ReleaseSlotsAction&gt;()</c>), picked by <see cref="Name"/> in
/// the designer, checked at publish.
/// <para><b>Actions are trusted host code.</b> <see cref="Run"/> executes inside the workflow command, with the runtime's
/// own write permit, and writes through <see cref="WorkflowActionContext.ObjectSpace"/>: the command's non-secured object
/// space, so its changes commit with the state change and roll back with it, and are not checked against the caller's
/// permissions. The runtime owns saving and the object space's lifetime: an action only stages changes (no CommitChanges,
/// Rollback, Refresh or Dispose, no writes on another connection), finishes before it returns (no async void, no background
/// use of the context), and does not touch workflow rows (instances, tasks, notifications, the subject's Workflow link);
/// the guards cannot tell its writes from the runtime's. A member the subject's current state locks is refused unless an
/// effect of the entered state writes it too. An exception aborts the attempt with nothing committed; a
/// UserFriendlyException reaches the user as the message. A command is retried on a lost race (up to
/// <see cref="WorkflowRuntime.MaxAttempts"/>), so the action can run more than once while only the last attempt commits:
/// external side effects belong in an outbox row the host delivers, like notifications.</para>
/// </summary>
public interface IWorkflowAction
{
    /// <summary>What the designer picks as the effect's Target; unique within the host.</summary>
    string Name { get; }

    void Run(WorkflowActionContext context);
}

/// <param name="ObjectSpace">The command's object space; writes made through it commit with the transition.</param>
/// <param name="Subject">The workflow's subject; its <see cref="IWorkflowSubject.Workflow"/> is the instance, already in the entered state.</param>
/// <param name="Argument">The effect's Value with its {Member} placeholders filled; "" when none.</param>
public sealed record WorkflowActionContext(IObjectSpace ObjectSpace, IWorkflowSubject Subject, string Argument);

/// <summary>The host's registered actions by name, shared by the runtime, the validator and the designer's lookup.
/// Built from DI (<c>IEnumerable&lt;IWorkflowAction&gt;</c>) when first resolved; a duplicate or empty name throws then.</summary>
public sealed class WorkflowActionCatalog
{
    public static readonly WorkflowActionCatalog Empty = new([]);

    readonly Dictionary<string, IWorkflowAction> actions = new(StringComparer.Ordinal);

    public WorkflowActionCatalog(IEnumerable<IWorkflowAction> actions)
    {
        foreach (var a in actions)
        {
            if (string.IsNullOrWhiteSpace(a.Name)) throw new ArgumentException($"Workflow action {a.GetType().Name} has no name.");
            if (!this.actions.TryAdd(a.Name, a))
                throw new ArgumentException($"Two workflow actions are named '{a.Name}': {this.actions[a.Name].GetType().Name} and {a.GetType().Name}.");
        }
    }

    public IReadOnlyList<string> Names => actions.Keys.Order(StringComparer.Ordinal).ToList();

    public IWorkflowAction? Find(string name) => actions.GetValueOrDefault(name);
}
