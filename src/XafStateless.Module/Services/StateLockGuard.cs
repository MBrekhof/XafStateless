using System.Globalization;
using DevExpress.ExpressApp;
using DevExpress.ExpressApp.EFCore;
using DevExpress.Persistent.BaseImpl.EF;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using XafStateless.Engine;
using XafStateless.Module.BusinessObjects;

namespace XafStateless.Module.Services;

/// <summary>
/// Per-state field locks at persistence, for every path (UI, Web API, imports, code) and every role. The UI rule
/// (StateLockAppearanceController) only disables editors; this interceptor refuses the save.
/// <list type="bullet">
/// <item>A Modified <see cref="IWorkflowSubject"/> may not change a member locked in its instance's <b>persisted</b> state:
/// the subject's Workflow link, the instance's StateKey and the state's locks are all read from the database at SavingChanges
/// (<see cref="RawSql"/>: same connection and transaction, never rewritten by XAF security), never from tracked values.</item>
/// <item>Inside a <see cref="RuntimeScope"/> bound to the saving context, only the (subject type, subject, member) entries the runtime recorded
/// with <see cref="RuntimeScope.Allow"/> (one per SetField) are exempt; every other modified member is still checked.</item>
/// <item>Outside a runtime scope the Workflow link cannot change, clearing included; it is checked first, so clearing it in
/// the same save cannot dodge a lock.</item>
/// <item>Outside a runtime scope a subject whose persisted instance is in a non-Final state cannot be deleted, hard
/// (EntityState.Deleted) or soft (GCRecord set on a row that is live in the database): that would orphan its instance and tasks, and a Wait
/// state's timer would fail on every tick. Subjects in a Final state, or without a workflow, delete normally.</item>
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
    const string GCRecord = EFCoreDeferredDeletionInterceptor.GCRecordFieldName;

    /// <summary>EF Deleted, or XAF deferred deletion's Modified with a nonzero GCRecord on a row that is live in the database.
    /// <paramref name="live"/> holds the rows RawSql read (it skips GCRecord != 0); never OriginalValue, which calling code can forge.</summary>
    static bool IsDelete(EntityEntry e, Dictionary<(Type, Guid), Guid?> live) =>
        e.State == EntityState.Deleted
        || (e.State == EntityState.Modified && e.Metadata.FindProperty(GCRecord) != null
            && e.Property(GCRecord).CurrentValue is int now && now != 0 && live.ContainsKey(Key(e)));

    static void Check(DbContext? ctx)
    {
        if (ctx == null) return;
        ctx.ChangeTracker.DetectChanges();
        var subjects = ctx.ChangeTracker.Entries()
            .Where(e => e.Entity is IWorkflowSubject && e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted).ToList();
        if (subjects.Count == 0) return;
        var scope = RuntimeScope.Allows(ctx) ? RuntimeScope.Current : null;

        // The persisted link of every existing subject, keyed by (type, ID): subject types in different tables may share IDs.
        var persistedLink = new Dictionary<(Type, Guid), Guid?>();
        foreach (var group in subjects.Where(e => e.State != EntityState.Added).GroupBy(e => e.Metadata))
            foreach (var row in RawSql.Rows(ctx, group.Key.ClrType, Id, group.Select(e => ((IWorkflowSubject)e.Entity).ID), Id, ForeignKey(group.Key)))
                persistedLink[(group.Key.ClrType, (Guid)row[0]!)] = (Guid?)row[1];

        var defFk = RawSql.ForeignKey<WorkflowInstance>(ctx, nameof(WorkflowInstance.Definition));
        var stateFk = RawSql.ForeignKey<WorkflowState>(ctx, nameof(WorkflowState.Definition));
        if (scope == null) CheckDeletes(ctx, subjects.Where(e => IsDelete(e, persistedLink)).ToList(), persistedLink, defFk, stateFk);
        subjects.RemoveAll(e => e.State == EntityState.Deleted);

        foreach (var e in subjects)
        {
            var link = (e.Reference(Link).CurrentValue as BaseObject)?.ID ?? (Guid?)e.Property(ForeignKey(e.Metadata)).CurrentValue;
            var stored = e.State == EntityState.Added ? null : persistedLink.GetValueOrDefault(Key(e));
            if (scope == null && link != stored) throw new UserFriendlyException(LinkManaged);
        }

        var modified = subjects.Where(e => e.State == EntityState.Modified && persistedLink.GetValueOrDefault(Key(e)) != null).ToList();
        if (modified.Count == 0) return;

        // Persisted instance state, then that state's locks and caption (definitions are immutable once Published).
        var instances = RawSql.Rows<WorkflowInstance>(ctx, Id, modified.Select(e => persistedLink[Key(e)]!.Value),
                Id, defFk, nameof(WorkflowInstance.StateKey))
            .ToDictionary(r => (Guid)r[0]!, r => (Definition: (Guid?)r[1], StateKey: (string)r[2]!));
        var locks = RawSql.Rows<WorkflowState>(ctx, stateFk, instances.Values.Select(i => i.Definition).OfType<Guid>(),
                stateFk, nameof(WorkflowState.Key), nameof(WorkflowState.Caption), nameof(WorkflowState.LockedMembers))
            .ToDictionary(r => ((Guid)r[0]!, (string)r[1]!), r => (Caption: (string)r[2]!, Members: DefinitionMapper.Members((string?)r[3])));

        // Each subject's locked, modified, non-exempt members. No live instance or state row means nothing is locked; instance
        // deletes are refused outside the runtime (DefinitionGuard), so that happens only by writes that bypass EF.
        var candidates = new List<(EntityEntry Entry, string Member, string Caption)>();
        foreach (var e in modified)
        {
            var (type, id) = Key(e);
            if (!instances.TryGetValue(persistedLink[Key(e)]!.Value, out var instance) || instance.Definition is not { } def
                || !locks.TryGetValue((def, instance.StateKey), out var state))
                continue;
            foreach (var member in state.Members)
                if (e.Metadata.FindProperty(member) != null && e.Property(member).IsModified && scope?.IsAllowed(type, id, member) != true)
                    candidates.Add((e, member, state.Caption));
        }
        if (candidates.Count == 0) return;

        // "Changed" is judged against the database, never OriginalValue: a disconnected ctx.Update or a forged OriginalValue
        // makes original == current while the UPDATE still writes the column. One query per subject type, locked columns only.
        foreach (var group in candidates.GroupBy(c => c.Entry.Metadata))
        {
            var columns = group.Select(c => c.Member).Distinct().ToList();
            var stored = RawSql.Rows(ctx, group.Key.ClrType, Id, group.Select(c => ((IWorkflowSubject)c.Entry.Entity).ID), [Id, .. columns])
                .ToDictionary(r => (Guid)r[0]!);
            foreach (var (e, member, caption) in group)
            {
                var row = stored.GetValueOrDefault(((IWorkflowSubject)e.Entity).ID);
                if (row == null || !Same(e.Property(member), row[1 + columns.IndexOf(member)]))
                    throw new UserFriendlyException($"'{member}' is locked in state '{caption}'");
            }
        }
    }

    /// <summary>Refuses deleting a subject whose persisted instance is in a non-Final persisted state. No instance or state row
    /// (a write that bypassed EF) means nothing to orphan.</summary>
    static void CheckDeletes(DbContext ctx, List<EntityEntry> deletes, Dictionary<(Type, Guid), Guid?> persistedLink, string defFk, string stateFk)
    {
        var linked = deletes.Where(e => persistedLink.GetValueOrDefault(Key(e)) != null).ToList();
        if (linked.Count == 0) return;
        var instances = RawSql.Rows<WorkflowInstance>(ctx, Id, linked.Select(e => persistedLink[Key(e)]!.Value),
                Id, defFk, nameof(WorkflowInstance.StateKey))
            .ToDictionary(r => (Guid)r[0]!, r => (Definition: (Guid?)r[1], StateKey: (string)r[2]!));
        var states = RawSql.Rows<WorkflowState>(ctx, stateFk, instances.Values.Select(i => i.Definition).OfType<Guid>(),
                stateFk, nameof(WorkflowState.Key), nameof(WorkflowState.Caption), nameof(WorkflowState.Kind))
            .ToDictionary(r => ((Guid)r[0]!, (string)r[1]!), r => (Caption: (string)r[2]!, Kind: (StateKind)(int)r[3]!));
        foreach (var e in linked)
        {
            if (!instances.TryGetValue(persistedLink[Key(e)]!.Value, out var instance) || instance.Definition is not { } def
                || !states.TryGetValue((def, instance.StateKey), out var state) || state.Kind == StateKind.Final)
                continue;
            var caption = Convert.ToString(XafTypesInfo.Instance.FindTypeInfo(e.Metadata.ClrType)?.DefaultMember?.GetValue(e.Entity), CultureInfo.InvariantCulture);
            throw new UserFriendlyException($"'{(string.IsNullOrEmpty(caption) ? e.Metadata.ClrType.Name : caption)}' cannot be deleted while its workflow is in '{state.Caption}'");
        }
    }

    /// <summary>A tracked value against its raw-read column, both in provider form: the property's mapped converter (value
    /// converters and enums alike) turns the current value into what EF would write, and the provider comparer compares it.
    /// A column the reader returns as another CLR type than the provider type compares unequal: refused, never allowed.</summary>
    static bool Same(PropertyEntry current, object? stored)
    {
        var p = current.Metadata;
        var value = p.GetTypeMapping().Converter is { } c ? c.ConvertToProvider(current.CurrentValue) : current.CurrentValue;
        if (value == null || stored == null) return value == null && stored == null;
        return value.GetType() == stored.GetType() && p.GetProviderValueComparer().Equals(value, stored);
    }

    /// <summary>A subject entry's identity across subject types: its CLR type and ID.</summary>
    static (Type, Guid) Key(EntityEntry e) => (e.Metadata.ClrType, ((IWorkflowSubject)e.Entity).ID);

    static string ForeignKey(Microsoft.EntityFrameworkCore.Metadata.IEntityType type) =>
        type.FindNavigation(Link)?.ForeignKey.Properties[0].Name
        ?? throw new InvalidOperationException($"{type.ClrType.Name}.{Link} is not a mapped reference.");
}
