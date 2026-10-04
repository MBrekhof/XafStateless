using DevExpress.ExpressApp;
using DevExpress.ExpressApp.EFCore;
using XafStateless.Engine;
using XafStateless.Module.BusinessObjects;

namespace XafStateless.Module.Services;

/// <summary>Findings carry the state they are about (XSM-027); null for results that are not validations.</summary>
public sealed record DefinitionResult(bool Ok, IReadOnlyList<string> Messages, IReadOnlyList<Finding>? Findings = null);

/// <summary>A view item that shows a definition's validation (the diagram); the definition's controller pushes each result.</summary>
public interface IDefinitionValidationListener
{
    void OnValidated(DefinitionResult? result);
}

/// <summary>The definition lifecycle: new draft, validate, publish, dry run. DefinitionGuard enforces immutability underneath.</summary>
public sealed class DefinitionService(DefinitionStore store, WorkflowActionCatalog? actions = null)
{
    readonly WorkflowActionCatalog catalog = actions ?? WorkflowActionCatalog.Empty;

    public const string PendingChangesMessage = "Save or cancel your pending changes before publishing.";
    public const string LostRaceMessage = "The definition changed meanwhile (another edit or publish committed first). Reload it and publish again.";

    /// <summary>max(Version)+1 over every live definition of <paramref name="name"/>, read past security: a caller who may not
    /// read the published versions must still number after them, or the (Name, Version) index refuses the save.</summary>
    public static int NextVersion(IObjectSpace os, string name)
    {
        var db = (os as EFCoreObjectSpace)?.DbContext ?? throw new NotSupportedException("Workflow definitions need an EF Core object space.");
        return (RawSql.Max<WorkflowDefinition>(db, nameof(WorkflowDefinition.Version), nameof(WorkflowDefinition.Name), name) ?? 0) + 1;
    }

    /// <summary>A Draft v(max+1): a deep copy of the Published version, or empty when none is published. Not committed.</summary>
    public WorkflowDefinition NewDraft(IObjectSpace os, string name)
    {
        if (os.FirstOrDefault<WorkflowDefinition>(d => d.Name == name && d.State == DefinitionState.Draft) is { } existing)
            throw new UserFriendlyException($"'{name}' already has a draft (v{existing.Version}); publish or delete it first.");
        var draft = os.CreateObject<WorkflowDefinition>();
        draft.Name = name;
        draft.State = DefinitionState.Draft;
        draft.Version = NextVersion(os, name);
        if (store.Published(os, name) is not { } src) return draft;

        (draft.TargetTypeName, draft.InitialStateKey) = (src.TargetTypeName, src.InitialStateKey);
        foreach (var s in src.States)
        {
            var state = os.CreateObject<WorkflowState>();
            (state.Key, state.Caption, state.Kind, state.TaskRole, state.WaitMinutes, state.LockedMembers) = (s.Key, s.Caption, s.Kind, s.TaskRole, s.WaitMinutes, s.LockedMembers);
            foreach (var e in s.Effects)
            {
                var effect = os.CreateObject<WorkflowEffect>();
                (effect.Order, effect.Kind, effect.Target, effect.Value) = (e.Order, e.Kind, e.Target, e.Value);
                state.Effects.Add(effect);
            }
            draft.States.Add(state);
        }
        foreach (var t in src.Transitions)
        {
            var copy = os.CreateObject<WorkflowTransition>();
            (copy.FromKey, copy.Trigger, copy.ToKey, copy.Criteria, copy.RequiredRole, copy.Order) = (t.FromKey, t.Trigger, t.ToKey, t.Criteria, t.RequiredRole, t.Order);
            draft.Transitions.Add(copy);
        }
        return draft;
    }

    /// <param name="roleNames">From <see cref="WorkflowRoleCatalog.Names"/> over a non-secured object space, fetched by the caller.</param>
    public DefinitionResult Validate(IObjectSpace os, WorkflowDefinition d, IReadOnlyList<string> roleNames)
    {
        var target = os.TypesInfo.FindTypeInfo(d.TargetTypeName);
        if (target?.Type == null || !target.Implements<IWorkflowSubject>())
        {
            var message = $"target type '{d.TargetTypeName}' is not a workflow subject";
            return new(false, [message], [new Finding(message)]);
        }
        var findings = WorkflowValidator.Check(DefinitionMapper.ToDef(d, os), new XafWorkflowEnvironment(roleNames, target, catalog.Names));
        return new(findings.Count == 0, findings.Select(f => f.Message).ToArray(), findings);
    }

    /// <summary>Validate, then in ONE transaction inside AllowPublishing: retire the current Published, publish <paramref name="d"/>.
    /// Retire is saved first so the one-Published index never sees two. A draft edit committed after <paramref name="d"/> was
    /// loaded advanced its lock version (DefinitionGuard's Touch), so the publish loses the race instead of publishing unvalidated rules.</summary>
    public DefinitionResult Publish(IObjectSpace os, WorkflowDefinition d, string caller, IReadOnlyList<string> roleNames)
    {
        // Publish commits the object space: anything else pending would ride along inside the permit, and a failed
        // publish refreshes the object space. So the caller saves (or cancels) first.
        if (os.IsModified) return new(false, [PendingChangesMessage]);
        if (d.State != DefinitionState.Draft) return new(false, [$"'{d.Name}' v{d.Version} is {d.State}; only a draft can be published."]);
        var check = Validate(os, d, roleNames);
        if (!check.Ok) return check;
        var db = ((EFCoreObjectSpace)os).DbContext;
        try
        {
            using var tx = db.Database.BeginTransaction();
            var old = os.GetObjectsQuery<WorkflowDefinition>().FirstOrDefault(x => x.Name == d.Name && x.State == DefinitionState.Published);
            using (DefinitionGuard.AllowPublishing(d.ID, old?.ID))
            {
                if (old != null) old.State = DefinitionState.Retired;
                os.CommitChanges();
                (d.State, d.PublishedBy, d.PublishedUtc) = (DefinitionState.Published, caller, DateTime.UtcNow);
                os.CommitChanges();
            }
            tx.Commit();
        }
        catch (Exception ex) when (LostRace.Is(ex) || ex is UserFriendlyException)
        {
            os.Refresh();   // the transaction rolled back: drop what the object space accepted inside it (nothing else was pending)
            return new(false, [LostRace.Is(ex) ? LostRaceMessage : ex.Message]);
        }
        return new(true, [$"'{d.Name}' v{d.Version} published."]);
    }

    /// <summary>Runs a path through the definition; refuses an invalid definition, as <see cref="Publish"/> does (XSM-027).</summary>
    public (IReadOnlyList<Step> Steps, string? Error) DryRun(IObjectSpace os, WorkflowDefinition d, IReadOnlyList<string> roleNames,
        IEnumerable<string> triggers, ISet<string> trueCriteria)
    {
        var check = Validate(os, d, roleNames);
        if (!check.Ok) return ([], $"the definition is not valid: {check.Messages[0]}");
        return WorkflowDryRun.Run(DefinitionMapper.ToDef(d, os), triggers, trueCriteria.Contains);
    }
}
