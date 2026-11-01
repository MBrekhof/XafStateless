using DevExpress.ExpressApp;
using DevExpress.ExpressApp.EFCore;
using DevExpress.Persistent.BaseImpl.EF;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using XafStateless.Module.BusinessObjects;

namespace XafStateless.Module.Services;

/// <summary>
/// Immutability at persistence, for every path (UI, Web API, services) and every role, Administrators too.
/// Published and Retired definitions are frozen; states, effects and transitions change only while their definition
/// is persisted as Draft, and each such write Touches the definition, so a Publish racing a draft edit collides on the
/// definition row. State changes happen only inside <see cref="AllowPublishing"/>, for the ids it names:
/// Draft -> Published for <c>publish</c>, Published -> Retired for <c>retire</c>.
/// Runtime-owned rows (instances' state, tasks, notification inserts) change only inside a <see cref="RuntimeScope"/> bound to the
/// saving context: see <see cref="CheckInstances"/>, <see cref="CheckTasks"/>, <see cref="CheckNotifications"/>.
/// "Persisted" is read from the database with raw SQL (<see cref="RawSql"/>: same connection and transaction, never
/// rewritten by XAF security, one query per entity type per save), never from the change tracker's original values,
/// which an attached entity can fake.
/// </summary>
public sealed class DefinitionGuard : SaveChangesInterceptor
{
    /// <summary>The one instance the DbContext adds to itself (OnConfiguring), whoever builds the context.</summary>
    public static readonly DefinitionGuard Instance = new();

    static readonly AsyncLocal<Scope?> Current = new();

    /// <summary>Permits exactly <paramref name="publish"/> Draft -> Published and <paramref name="retire"/> Published -> Retired
    /// until disposed. Disposal revokes the permit everywhere, including in an execution context captured inside the scope.</summary>
    public static IDisposable AllowPublishing(Guid publish, Guid? retire)
    {
        var scope = new Scope(publish, retire, Current.Value);
        Current.Value = scope;
        return scope;
    }

    sealed class Scope(Guid publish, Guid? retire, Scope? previous) : IDisposable
    {
        volatile bool disposed;
        public bool Allows(Guid id, DefinitionState target) =>
            !disposed && (target == DefinitionState.Published ? id == publish : target == DefinitionState.Retired && id == retire);
        public void Dispose() { disposed = true; Current.Value = previous; }
    }

    static bool Allowed(Guid id, DefinitionState target) => Current.Value?.Allows(id, target) == true;

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

    static UserFriendlyException Refuse(string message) => new($"Workflow definition: {message}");

    const string Id = nameof(BaseObject.ID);
    const string LockField = nameof(BaseObject.OptimisticLockField);
    const string GCRecord = EFCoreDeferredDeletionInterceptor.GCRecordFieldName;

    static List<EntityEntry<T>> Changed<T>(DbContext ctx) where T : class =>
        ctx.ChangeTracker.Entries<T>().Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted).ToList();

    static IEnumerable<Guid> Existing<T>(IEnumerable<EntityEntry<T>> entries) where T : BaseObject =>
        entries.Where(e => e.State != EntityState.Added).Select(e => e.Entity.ID);

    static void Check(DbContext? ctx)
    {
        if (ctx == null) return;
        ctx.ChangeTracker.DetectChanges();
        var instances = Changed<WorkflowInstance>(ctx);
        var (states, transitions, effects, definitions) = (Changed<WorkflowState>(ctx), Changed<WorkflowTransition>(ctx), Changed<WorkflowEffect>(ctx), Changed<WorkflowDefinition>(ctx));
        var (tasks, notifications) = (Changed<WorkflowTask>(ctx), Changed<WorkflowNotification>(ctx));
        var (ruleSets, ruleParams, rules) = (Changed<WorkflowRuleSet>(ctx), Changed<WorkflowRuleParam>(ctx), Changed<WorkflowRule>(ctx));
        if (instances.Count + states.Count + transitions.Count + effects.Count + definitions.Count + tasks.Count + notifications.Count
            + ruleSets.Count + ruleParams.Count + rules.Count == 0) return;

        var runtime = RuntimeScope.Allows(ctx);   // the scope counts only for the context it was opened for
        CheckInstances(ctx, instances, runtime);
        CheckTasks(ctx, tasks, runtime);
        CheckNotifications(ctx, notifications, runtime);

        // Persisted parents, one query per type. An effect's owner goes through its state, so effects are read first.
        var effectParents = PersistedParents<WorkflowEffect>(ctx, Existing(effects), nameof(WorkflowEffect.State));
        var effectStates = effects.Select(e => Parent(e, nameof(WorkflowEffect.State), effectParents)).ToList();
        var trackedStates = ctx.ChangeTracker.Entries<WorkflowState>().ToDictionary(e => e.Entity.ID);
        var stateParents = PersistedParents<WorkflowState>(ctx,
            Existing(states).Concat(effectStates.OfType<Guid>().Where(id => !(trackedStates.TryGetValue(id, out var s) && s.State == EntityState.Added))),
            nameof(WorkflowState.Definition));
        var transitionParents = PersistedParents<WorkflowTransition>(ctx, Existing(transitions), nameof(WorkflowTransition.Definition));
        var paramParents = PersistedParents<WorkflowRuleParam>(ctx, Existing(ruleParams), nameof(WorkflowRuleParam.RuleSet));
        var paramRuleSets = ruleParams.Select(p => Parent(p, nameof(WorkflowRuleParam.RuleSet), paramParents)).ToList();
        var ruleParents = PersistedParents<WorkflowRule>(ctx, Existing(rules), nameof(WorkflowRule.RuleSet));
        var ruleRuleSets = rules.Select(r => Parent(r, nameof(WorkflowRule.RuleSet), ruleParents)).ToList();
        var trackedRuleSets = ctx.ChangeTracker.Entries<WorkflowRuleSet>().ToDictionary(e => e.Entity.ID);
        var ruleSetParents = PersistedParents<WorkflowRuleSet>(ctx,
            Existing(ruleSets)
                .Concat(paramRuleSets.OfType<Guid>().Where(id => !(trackedRuleSets.TryGetValue(id, out var rs) && rs.State == EntityState.Added)))
                .Concat(ruleRuleSets.OfType<Guid>().Where(id => !(trackedRuleSets.TryGetValue(id, out var rs) && rs.State == EntityState.Added))),
            nameof(WorkflowRuleSet.Definition));

        Guid? RuleSetOwner(Guid? ruleSetId) => ruleSetId is not { } id ? null
            : trackedRuleSets.TryGetValue(id, out var rs) ? Parent(rs, nameof(WorkflowRuleSet.Definition), ruleSetParents)
            : ruleSetParents.GetValueOrDefault(id);

        Guid? StateOwner(Guid? stateId) => stateId is not { } id ? null
            : trackedStates.TryGetValue(id, out var s) ? Parent(s, nameof(WorkflowState.Definition), stateParents)
            : stateParents.GetValueOrDefault(id);

        var owners = states.Select(e => Parent(e, nameof(WorkflowState.Definition), stateParents))
            .Concat(transitions.Select(e => Parent(e, nameof(WorkflowTransition.Definition), transitionParents)))
            .Concat(effectStates.Select(StateOwner))
            .Concat(ruleSets.Select(e => Parent(e, nameof(WorkflowRuleSet.Definition), ruleSetParents)))
            .Concat(paramRuleSets.Select(RuleSetOwner))
            .Concat(ruleRuleSets.Select(RuleSetOwner))
            .ToList();
        if (owners.Any(o => o == null)) throw Refuse("a state, effect or transition must belong to a definition.");

        var trackedDefinitions = ctx.ChangeTracker.Entries<WorkflowDefinition>().ToDictionary(e => e.Entity.ID);
        var existingOwners = owners.Select(o => o!.Value).Distinct()
            .Where(id => !(trackedDefinitions.TryGetValue(id, out var d) && d.State == EntityState.Added)).ToList();   // a new owner: CheckDefinition requires Draft
        // Track the owners not loaded yet (one query) so they can be Touched. Only their lock version is used, and it is
        // compared against the raw read below, so a masked or stale value fails closed.
        var untracked = existingOwners.Where(id => !trackedDefinitions.ContainsKey(id)).ToList();
        if (untracked.Count > 0)
            foreach (var d in ctx.Set<WorkflowDefinition>().Where(x => untracked.Contains(x.ID)).ToList())
                trackedDefinitions[d.ID] = ctx.Entry(d);

        var persisted = RawSql.Rows<WorkflowDefinition>(ctx, Id, existingOwners.Concat(Existing(definitions)), Id, nameof(WorkflowDefinition.State), LockField)
            .ToDictionary(r => (Guid)r[0]!, r => (State: (DefinitionState)(int)r[1]!, Version: (int)r[2]!));

        foreach (var id in existingOwners)
        {
            if (!persisted.TryGetValue(id, out var row)) throw Refuse("a state, effect or transition must belong to a definition.");
            if (row.State != DefinitionState.Draft)
                throw Refuse($"the definition is {row.State}; its states, effects and transitions cannot change. Create a new draft.");
            if (!trackedDefinitions.TryGetValue(id, out var owner)) throw Refuse("the definition cannot be read.");
            // Touch writes original+1 with the original in the UPDATE's WHERE: the original must be the version whose State we just read.
            SameVersion(owner, row.Version);
            if (owner.State is EntityState.Unchanged or EntityState.Modified) Concurrency.Touch(owner);
        }

        foreach (var e in definitions) CheckDefinition(e, persisted);
    }

    static void CheckDefinition(EntityEntry<WorkflowDefinition> e, Dictionary<Guid, (DefinitionState State, int Version)> persistedRows)
    {
        var (d, target) = (e.Entity, e.Entity.State);
        if (e.State == EntityState.Added)
        {
            if (target != DefinitionState.Draft) throw Refuse("a new definition starts as Draft; only Publish makes it Published.");
            return;
        }
        var persisted = persistedRows.TryGetValue(d.ID, out var row) ? row.State : throw Refuse($"'{d.Name}' v{d.Version} does not exist.");
        SameVersion(e, row.Version);   // the State just read belongs to the version this entry's UPDATE is conditioned on
        var modified = e.Properties.Where(p => p.IsModified && p.Metadata.Name != LockField).Select(p => p.Metadata.Name).ToList();
        // A deletion: EF Deleted, or XAF deferred deletion's Modified with GCRecord going from unset to set.
        // A GCRecord merely marked modified is not a deletion; the lifecycle rules below run for every entry regardless.
        var gc = e.Property(GCRecord);
        if ((e.State == EntityState.Deleted || (gc.CurrentValue is int now && now != 0 && gc.OriginalValue is null or 0))
            && persisted != DefinitionState.Draft)
            throw Refuse($"'{d.Name}' v{d.Version} is {persisted} and cannot be deleted.");
        switch (persisted)
        {
            case DefinitionState.Draft when target == DefinitionState.Draft:
                if (modified.Any(n => n is nameof(WorkflowDefinition.PublishedBy) or nameof(WorkflowDefinition.PublishedUtc)))
                    throw Refuse("only Publish records who published and when.");
                break;
            case DefinitionState.Draft:
                if (!(target == DefinitionState.Published && Allowed(d.ID, target))) throw Refuse("only Publish moves a draft out of Draft.");
                break;
            case DefinitionState.Published:
                if (!(target == DefinitionState.Retired && Allowed(d.ID, target) && modified.All(n => n == nameof(WorkflowDefinition.State))))
                    throw Refuse($"'{d.Name}' v{d.Version} is Published and cannot change. Create a new draft.");
                break;
            default:
                throw Refuse($"'{d.Name}' v{d.Version} is {persisted} and cannot change.");
        }
    }

    static UserFriendlyException RefuseInstance(string message) => new($"Workflow instance: {message}");

    const string InstanceDeleted = "instances are deleted by the workflow runtime only.";

    static readonly string[] InstanceStateColumns =
        [nameof(WorkflowInstance.StateKey), nameof(WorkflowInstance.StateCaption), nameof(WorkflowInstance.EnteredUtc), nameof(WorkflowInstance.DueUtc)];

    /// <summary>The requester, fixed from insert on, even when it was null and even inside the runtime scope (XSM-014).</summary>
    static readonly string[] InstanceRequesterColumns = [nameof(WorkflowInstance.StartedById), nameof(WorkflowInstance.StartedBy)];

    /// <summary>Instances are created and deleted (hard, or soft through GCRecord) by the runtime only (inside its scope): a deleted
    /// instance would drop its subject's state locks. Definition, SubjectType and SubjectId are fixed once set; StateKey,
    /// StateCaption, EnteredUtc and DueUtc change only inside the runtime scope, for every role, Administrators too.
    /// Judged against the database rows (one query).</summary>
    static void CheckInstances(DbContext ctx, List<EntityEntry<WorkflowInstance>> instances, bool runtime)
    {
        if (!runtime && instances.Any(e => e.State == EntityState.Added)) throw RefuseInstance("instances are created by the workflow runtime only.");
        if (!runtime && instances.Any(e => e.State == EntityState.Deleted)) throw RefuseInstance(InstanceDeleted);
        var modified = instances.Where(e => e.State == EntityState.Modified).ToList();
        if (modified.Count == 0) return;
        var fk = RawSql.ForeignKey<WorkflowInstance>(ctx, nameof(WorkflowInstance.Definition));
        var rows = RawSql.Rows<WorkflowInstance>(ctx, Id, modified.Select(e => e.Entity.ID),
                [Id, fk, nameof(WorkflowInstance.SubjectType), nameof(WorkflowInstance.SubjectId), .. InstanceStateColumns, .. InstanceRequesterColumns])
            .ToDictionary(r => (Guid)r[0]!);
        foreach (var e in modified)
        {
            if (!rows.TryGetValue(e.Entity.ID, out var row)) throw Refuse("the workflow instance does not exist.");
            var now = new object?[] { (Guid?)e.Property(fk).CurrentValue, e.Entity.SubjectType, e.Entity.SubjectId };
            for (var i = 0; i < now.Length; i++)
                if (!IsUnset(row[i + 1]) && !Equals(row[i + 1], now[i]))
                    throw Refuse("a workflow instance's definition and subject are fixed once set.");
            for (var i = 0; i < InstanceRequesterColumns.Length; i++)
                if (!Same(e.Property(InstanceRequesterColumns[i]).CurrentValue, row[1 + now.Length + InstanceStateColumns.Length + i]))
                    throw RefuseInstance("its requester is fixed when it starts.");
            if (runtime) continue;
            // The row was read live (RawSql skips GCRecord != 0), so any GCRecord set now is a soft delete.
            if (e.Property(GCRecord).CurrentValue is int gc && gc != 0) throw RefuseInstance(InstanceDeleted);
            for (var i = 0; i < InstanceStateColumns.Length; i++)
                if (!Same(e.Property(InstanceStateColumns[i]).CurrentValue, row[1 + now.Length + i]))
                    throw RefuseInstance("its state changes only through the workflow.");
        }
    }

    /// <summary>Notifications are written into the outbox by the runtime only (inside its scope). After insert the recipient
    /// (Role, UserId, UserName), Body, IsTemplate (a cleared one would mail the raw template), the Snapshot shadow column, Instance and CreatedUtc are fixed, for every caller; delivery updates (State, Attempts,
    /// LastError) by a dispatcher, and deletes, are not restricted here. Judged against the database rows.</summary>
    static void CheckNotifications(DbContext ctx, List<EntityEntry<WorkflowNotification>> notifications, bool runtime)
    {
        if (!runtime && notifications.Any(e => e.State == EntityState.Added))
            throw new UserFriendlyException("Workflow notification: notifications are created by the workflow runtime only.");
        var modified = notifications.Where(e => e.State == EntityState.Modified).ToList();
        if (modified.Count == 0) return;
        var fk = RawSql.ForeignKey<WorkflowNotification>(ctx, nameof(WorkflowNotification.Instance));
        string[] fixedColumns = [nameof(WorkflowNotification.Role), nameof(WorkflowNotification.UserId), nameof(WorkflowNotification.UserName),
            nameof(WorkflowNotification.Body), nameof(WorkflowNotification.IsTemplate), fk, nameof(WorkflowNotification.CreatedUtc),
            .. WorkflowNotifications.HasSnapshot(ctx) ? [WorkflowNotifications.SnapshotColumn] : Array.Empty<string>()];
        var rows = RawSql.Rows<WorkflowNotification>(ctx, Id, modified.Select(e => e.Entity.ID), [Id, .. fixedColumns]).ToDictionary(r => (Guid)r[0]!);
        foreach (var e in modified)
        {
            if (!rows.TryGetValue(e.Entity.ID, out var row)) throw new UserFriendlyException("Workflow notification: the notification does not exist.");
            object? Now(string column) => column == fk
                ? (e.Reference(nameof(WorkflowNotification.Instance)).CurrentValue as BaseObject)?.ID ?? (Guid?)e.Property(fk).CurrentValue
                : e.Property(column).CurrentValue;
            for (var i = 0; i < fixedColumns.Length; i++)
                if (!Same(Now(fixedColumns[i]), row[1 + i]))
                    throw new UserFriendlyException("Workflow notification: its recipient and text are fixed once written; only delivery (State, Attempts, LastError) may change.");
        }
    }

    static UserFriendlyException RefuseTask(string message) => new($"Workflow task: {message}");

    static readonly string[] TaskRuntimeColumns =
        [nameof(WorkflowTask.State), nameof(WorkflowTask.DecidedBy), nameof(WorkflowTask.Outcome), nameof(WorkflowTask.DecidedUtc)];

    /// <summary>Tasks are written by the runtime only: inserts, and changes to State/DecidedBy/Outcome/DecidedUtc, only inside a
    /// <see cref="RuntimeScope"/> bound to the saving context. Instance, StateKey, Role, UserId, UserName and Subject are fixed after insert, even inside the scope; tasks are
    /// never deleted (hard or soft). Judged against the database rows (one query), never the change tracker's original values.</summary>
    static void CheckTasks(DbContext ctx, List<EntityEntry<WorkflowTask>> tasks, bool inScope)
    {
        if (tasks.Count == 0) return;
        if (tasks.Any(e => e.State == EntityState.Deleted)) throw RefuseTask("a task cannot be deleted.");
        if (!inScope && tasks.Any(e => e.State == EntityState.Added)) throw RefuseTask("tasks are created by the workflow runtime only.");
        var modified = tasks.Where(e => e.State == EntityState.Modified).ToList();
        if (modified.Count == 0) return;

        var fk = RawSql.ForeignKey<WorkflowTask>(ctx, nameof(WorkflowTask.Instance));
        string[] fixedColumns = [fk, nameof(WorkflowTask.StateKey), nameof(WorkflowTask.Role), nameof(WorkflowTask.UserId), nameof(WorkflowTask.UserName),
            nameof(WorkflowTask.Subject), GCRecord];
        var rows = RawSql.Rows<WorkflowTask>(ctx, Id, modified.Select(e => e.Entity.ID), [Id, .. fixedColumns, .. TaskRuntimeColumns])
            .ToDictionary(r => (Guid)r[0]!);
        foreach (var e in modified)
        {
            if (!rows.TryGetValue(e.Entity.ID, out var row)) throw RefuseTask("the task does not exist.");
            object? Now(string column) => column == fk
                ? (e.Reference(nameof(WorkflowTask.Instance)).CurrentValue as BaseObject)?.ID ?? (Guid?)e.Property(fk).CurrentValue
                : e.Property(column).CurrentValue;
            for (var i = 0; i < fixedColumns.Length; i++)
                if (!Same(Now(fixedColumns[i]), row[1 + i]))
                    throw RefuseTask("a task's instance, state, role, user and subject are fixed once created, and a task cannot be deleted.");
            if (inScope) continue;
            for (var i = 0; i < TaskRuntimeColumns.Length; i++)
                if (!Same(Now(TaskRuntimeColumns[i]), row[1 + fixedColumns.Length + i]))
                    throw RefuseTask("only the workflow runtime decides a task.");
        }
    }

    /// <summary>A tracked value against its raw-read column: enums compare by their stored int.</summary>
    static bool Same(object? current, object? stored) => Equals(current is Enum ? Convert.ToInt32(current) : current, stored);

    /// <summary>id -> parent id as stored (null: no parent). Rows missing from the result do not exist (or are soft-deleted).</summary>
    static Dictionary<Guid, Guid?> PersistedParents<T>(DbContext ctx, IEnumerable<Guid> ids, string navigation) where T : BaseObject =>
        RawSql.Rows<T>(ctx, Id, ids, Id, RawSql.ForeignKey<T>(ctx, navigation)).ToDictionary(r => (Guid)r[0]!, r => (Guid?)r[1]);

    /// <summary>An Added row's current parent; otherwise the parent as stored. Re-parenting is refused.</summary>
    static Guid? Parent<T>(EntityEntry<T> e, string navigation, Dictionary<Guid, Guid?> persisted) where T : BaseObject
    {
        var fk = e.Property(((Microsoft.EntityFrameworkCore.Metadata.INavigation)e.Reference(navigation).Metadata).ForeignKey.Properties[0].Name);
        var current = (Guid?)fk.CurrentValue ?? (e.Reference(navigation).CurrentValue as BaseObject)?.ID;
        if (e.State == EntityState.Added) return current;
        if (!persisted.TryGetValue(e.Entity.ID, out var stored)) throw Refuse("the row being changed does not exist.");
        if (e.State == EntityState.Modified && current != stored)
            throw Refuse("a state, effect or transition cannot move to another parent.");
        return stored;
    }

    public const string ChangedMeanwhile = "The workflow definition changed meanwhile.";

    /// <summary>The raw-read State is only meaningful for the version the entry was loaded at (its UPDATE's WHERE uses the
    /// original version); a different stored version means another writer got there first: a lost race, before any SQL runs.</summary>
    static void SameVersion(EntityEntry<WorkflowDefinition> e, int storedVersion)
    {
        if ((int)e.Property(LockField).OriginalValue! != storedVersion) throw new DbUpdateConcurrencyException(ChangedMeanwhile);
    }

    static bool IsUnset(object? v) => v is null or "" || (v is Guid g && g == Guid.Empty);
}
