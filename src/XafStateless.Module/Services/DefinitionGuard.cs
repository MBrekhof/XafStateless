using DevExpress.ExpressApp;
using DevExpress.ExpressApp.EFCore;
using DevExpress.Persistent.BaseImpl.EF;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Metadata;
using XafStateless.Module.BusinessObjects;

namespace XafStateless.Module.Services;

/// <summary>
/// Immutability at persistence, for every path (UI, Web API, services) and every role, Administrators too.
/// Published and Retired definitions are frozen; states, effects and transitions change only while their definition
/// is persisted as Draft, and each such write Touches the definition, so a Publish racing a draft edit collides on the
/// definition row. State changes happen only inside <see cref="AllowPublishing"/>, for the ids it names:
/// Draft -> Published for <c>publish</c>, Published -> Retired for <c>retire</c>.
/// "Persisted" is read from the database (no-tracking, same context and connection, so inside the caller's transaction),
/// never from the change tracker's original values, which an attached entity can fake.
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

    const string LockField = nameof(BaseObject.OptimisticLockField);
    const string GCRecord = EFCoreDeferredDeletionInterceptor.GCRecordFieldName;

    static void Check(DbContext? ctx)
    {
        if (ctx == null) return;
        ctx.ChangeTracker.DetectChanges();
        var changed = new[] { EntityState.Added, EntityState.Modified, EntityState.Deleted };

        foreach (var i in ctx.ChangeTracker.Entries<WorkflowInstance>().Where(e => e.State == EntityState.Modified).ToList())
            CheckInstance(ctx, i);

        // Children first: their Touch makes the owning definition Modified, which the definition rule below then allows (Draft).
        foreach (var e in ctx.ChangeTracker.Entries<WorkflowState>().Where(e => changed.Contains(e.State)).ToList())
            CheckChild(ctx, Parent(ctx, e, nameof(WorkflowState.Definition)));
        foreach (var e in ctx.ChangeTracker.Entries<WorkflowTransition>().Where(e => changed.Contains(e.State)).ToList())
            CheckChild(ctx, Parent(ctx, e, nameof(WorkflowTransition.Definition)));
        foreach (var e in ctx.ChangeTracker.Entries<WorkflowEffect>().Where(e => changed.Contains(e.State)).ToList())
            CheckChild(ctx, Parent(ctx, e, nameof(WorkflowEffect.State)) is { } stateId ? StateOwner(ctx, stateId) : null);

        foreach (var e in ctx.ChangeTracker.Entries<WorkflowDefinition>().Where(e => changed.Contains(e.State)).ToList())
            CheckDefinition(ctx, e);
    }

    static void CheckDefinition(DbContext ctx, EntityEntry<WorkflowDefinition> e)
    {
        var (d, target) = (e.Entity, e.Entity.State);
        if (e.State == EntityState.Added)
        {
            if (target != DefinitionState.Draft) throw Refuse("a new definition starts as Draft; only Publish makes it Published.");
            return;
        }
        var persisted = PersistedState(ctx, d.ID) ?? throw Refuse($"'{d.Name}' v{d.Version} does not exist.");
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

    /// <summary>A state/effect/transition write: allowed only while the owning definition is persisted as Draft; Touches it.</summary>
    static void CheckChild(DbContext ctx, Guid? definitionId)
    {
        if (definitionId is not { } id) throw Refuse("a state, effect or transition must belong to a definition.");
        var tracked = ctx.ChangeTracker.Entries<WorkflowDefinition>().FirstOrDefault(x => x.Entity.ID == id);
        if (tracked?.State == EntityState.Added) return;   // a new definition: CheckDefinition requires it to be a Draft
        var persisted = PersistedState(ctx, id) ?? throw Refuse("a state, effect or transition must belong to a definition.");
        if (persisted != DefinitionState.Draft)
            throw Refuse($"the definition is {persisted}; its states, effects and transitions cannot change. Create a new draft.");
        var owner = tracked ?? (ctx.Find<WorkflowDefinition>(id) is { } d ? ctx.Entry(d) : null);
        if (owner?.State is EntityState.Unchanged or EntityState.Modified) Concurrency.Touch(owner);
    }

    /// <summary>Definition, SubjectType and SubjectId are fixed once set, judged against the database row.</summary>
    static void CheckInstance(DbContext ctx, EntityEntry<WorkflowInstance> e)
    {
        var fk = ForeignKey(e, nameof(WorkflowInstance.Definition)).Metadata.Name;
        var row = ctx.Set<WorkflowInstance>().AsNoTracking().Where(x => x.ID == e.Entity.ID)
            .Select(x => new { Definition = EF.Property<Guid?>(x, fk), x.SubjectType, x.SubjectId }).FirstOrDefault()
            ?? throw Refuse("the workflow instance does not exist.");
        var now = (Definition: (Guid?)e.Property(fk).CurrentValue, e.Entity.SubjectType, e.Entity.SubjectId);
        if ((!IsUnset(row.Definition) && row.Definition != now.Definition)
            || (!IsUnset(row.SubjectType) && row.SubjectType != now.SubjectType)
            || (!IsUnset(row.SubjectId) && row.SubjectId != now.SubjectId))
            throw Refuse("a workflow instance's definition and subject are fixed once set.");
    }

    /// <summary>An Added row's current parent; otherwise the parent as stored in the database. Re-parenting is refused.</summary>
    static Guid? Parent<T>(DbContext ctx, EntityEntry<T> e, string navigation) where T : BaseObject
    {
        var fk = ForeignKey(e, navigation);
        var current = (Guid?)fk.CurrentValue ?? (e.Reference(navigation).CurrentValue as BaseObject)?.ID;
        if (e.State == EntityState.Added) return current;
        var persisted = PersistedParent<T>(ctx, e.Entity.ID, fk.Metadata.Name);
        if (e.State == EntityState.Modified && current != persisted)
            throw Refuse("a state, effect or transition cannot move to another parent.");
        return persisted;
    }

    /// <summary>The definition an effect's state belongs to: tracked (Added = current, else persisted), or read from the database.</summary>
    static Guid? StateOwner(DbContext ctx, Guid stateId) =>
        ctx.ChangeTracker.Entries<WorkflowState>().FirstOrDefault(x => x.Entity.ID == stateId) is { } s
            ? Parent(ctx, s, nameof(WorkflowState.Definition))
            : PersistedParent<WorkflowState>(ctx, stateId,
                ((INavigation)ctx.Model.FindEntityType(typeof(WorkflowState))!.FindNavigation(nameof(WorkflowState.Definition))!).ForeignKey.Properties[0].Name);

    // ponytail: one query per changed row; batch by id if designers ever save hundreds of rows at once.
    static DefinitionState? PersistedState(DbContext ctx, Guid id) =>
        ctx.Set<WorkflowDefinition>().AsNoTracking().Where(x => x.ID == id).Select(x => (DefinitionState?)x.State).FirstOrDefault();

    static Guid? PersistedParent<T>(DbContext ctx, Guid id, string fk) where T : BaseObject =>
        ctx.Set<T>().AsNoTracking().Where(x => x.ID == id).Select(x => EF.Property<Guid?>(x, fk)).FirstOrDefault();

    static PropertyEntry ForeignKey(EntityEntry e, string navigation) =>
        e.Property(((INavigation)e.Reference(navigation).Metadata).ForeignKey.Properties[0].Name);

    static bool IsUnset(object? v) => v is null or "" || (v is Guid g && g == Guid.Empty);
}
