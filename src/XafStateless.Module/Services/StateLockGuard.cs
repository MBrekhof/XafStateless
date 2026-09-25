using DevExpress.ExpressApp;
using DevExpress.Persistent.BaseImpl.EF;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using XafStateless.Module.BusinessObjects;

namespace XafStateless.Module.Services;

/// <summary>
/// Per-state field locks at persistence, for every path (UI, Web API, imports, code) and every role. The UI rule
/// (StateLockAppearanceController) only disables editors; this interceptor refuses the save.
/// <list type="bullet">
/// <item>A Modified <see cref="IWorkflowSubject"/> may not change a member locked in its instance's <b>persisted</b> state:
/// the subject's Workflow link, the instance's StateKey and the state's locks are all read from the database at SavingChanges
/// (<see cref="RawSql"/>: same connection and transaction, never rewritten by XAF security), never from tracked values.</item>
/// <item>Inside a <see cref="RuntimeScope"/> bound to the saving context, only the (subject, member) pairs the runtime recorded
/// with <see cref="RuntimeScope.Allow"/> (one per SetField) are exempt; every other modified member is still checked.</item>
/// <item>Outside a runtime scope the Workflow link cannot change, clearing included; it is checked first, so clearing it in
/// the same save cannot dodge a lock.</item>
/// </list>
/// A user save racing a transition collides on the subject's lock version (every command Touches the subject); a merged
/// retry is a new SavingChanges pass, which this guard judges against the new persisted state.
/// </summary>
public sealed class StateLockGuard : SaveChangesInterceptor
{
    /// <summary>The one instance the DbContext adds to itself (OnConfiguring), next to <see cref="DefinitionGuard.Instance"/>.</summary>
    public static readonly StateLockGuard Instance = new();

    public const string LinkManaged = "the workflow link is managed by the workflow";

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Check(eventData.Context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Check(eventData.Context);
        return ValueTask.FromResult(result);
    }

    const string Id = nameof(BaseObject.ID);
    const string Link = nameof(IWorkflowSubject.Workflow);

    static void Check(DbContext? ctx)
    {
        if (ctx == null) return;
        ctx.ChangeTracker.DetectChanges();
        var subjects = ctx.ChangeTracker.Entries()
            .Where(e => e.Entity is IWorkflowSubject && e.State is EntityState.Added or EntityState.Modified).ToList();
        if (subjects.Count == 0) return;
        var scope = RuntimeScope.Allows(ctx) ? RuntimeScope.Current : null;

        // The persisted link of every modified subject: one query per subject type.
        var persistedLink = new Dictionary<Guid, Guid?>();
        foreach (var group in subjects.Where(e => e.State == EntityState.Modified).GroupBy(e => e.Metadata))
            foreach (var row in RawSql.Rows(ctx, group.Key.ClrType, Id, group.Select(e => ((IWorkflowSubject)e.Entity).ID), Id, ForeignKey(group.Key)))
                persistedLink[(Guid)row[0]!] = (Guid?)row[1];

        foreach (var e in subjects)
        {
            var subject = (IWorkflowSubject)e.Entity;
            var link = (e.Reference(Link).CurrentValue as BaseObject)?.ID ?? (Guid?)e.Property(ForeignKey(e.Metadata)).CurrentValue;
            var stored = e.State == EntityState.Added ? null : persistedLink.GetValueOrDefault(subject.ID);
            if (scope == null && link != stored) throw new UserFriendlyException(LinkManaged);
        }

        var modified = subjects.Where(e => e.State == EntityState.Modified && persistedLink.GetValueOrDefault(((IWorkflowSubject)e.Entity).ID) != null).ToList();
        if (modified.Count == 0) return;

        // Persisted instance state, then that state's locks and caption (definitions are immutable once Published).
        var defFk = RawSql.ForeignKey<WorkflowInstance>(ctx, nameof(WorkflowInstance.Definition));
        var instances = RawSql.Rows<WorkflowInstance>(ctx, Id, modified.Select(e => persistedLink[((IWorkflowSubject)e.Entity).ID]!.Value),
                Id, defFk, nameof(WorkflowInstance.StateKey))
            .ToDictionary(r => (Guid)r[0]!, r => (Definition: (Guid?)r[1], StateKey: (string)r[2]!));
        var stateFk = RawSql.ForeignKey<WorkflowState>(ctx, nameof(WorkflowState.Definition));
        var locks = RawSql.Rows<WorkflowState>(ctx, stateFk, instances.Values.Select(i => i.Definition).OfType<Guid>(),
                stateFk, nameof(WorkflowState.Key), nameof(WorkflowState.Caption), nameof(WorkflowState.LockedMembers))
            .ToDictionary(r => ((Guid)r[0]!, (string)r[1]!), r => (Caption: (string)r[2]!, Members: DefinitionMapper.Members((string?)r[3])));

        foreach (var e in modified)
        {
            var subject = (IWorkflowSubject)e.Entity;
            if (!instances.TryGetValue(persistedLink[subject.ID]!.Value, out var instance) || instance.Definition is not { } def
                || !locks.TryGetValue((def, instance.StateKey), out var state))
                continue;   // no live instance or state row: nothing is locked
            foreach (var member in state.Members)
            {
                if (e.Metadata.FindProperty(member) is null) continue;
                var p = e.Property(member);
                if (!p.IsModified || Equals(p.OriginalValue, p.CurrentValue) || scope?.IsAllowed(subject.ID, member) == true) continue;
                throw new UserFriendlyException($"'{member}' is locked in state '{state.Caption}'");
            }
        }
    }

    static string ForeignKey(Microsoft.EntityFrameworkCore.Metadata.IEntityType type) =>
        type.FindNavigation(Link)?.ForeignKey.Properties[0].Name
        ?? throw new InvalidOperationException($"{type.ClrType.Name}.{Link} is not a mapped reference.");
}
