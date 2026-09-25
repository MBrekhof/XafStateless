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
/// definition row. State changes happen only inside <see cref="AllowPublishing"/>: Draft -> Published, Published -> Retired.
/// "Persisted" = the change tracker's original values (as loaded); a stale load loses on the definition's lock version.
/// </summary>
public sealed class DefinitionGuard : SaveChangesInterceptor
{
    /// <summary>The one instance the DbContext adds to itself (OnConfiguring), whoever builds the context.</summary>
    public static readonly DefinitionGuard Instance = new();

    static readonly AsyncLocal<bool> Publishing = new();

    public static IDisposable AllowPublishing()
    {
        var previous = Publishing.Value;
        Publishing.Value = true;
        return new Scope(previous);
    }

    sealed class Scope(bool previous) : IDisposable { public void Dispose() => Publishing.Value = previous; }

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

        foreach (var i in ctx.ChangeTracker.Entries<WorkflowInstance>().Where(e => e.State == EntityState.Modified))
            foreach (var p in new[] { ForeignKey(i, nameof(WorkflowInstance.Definition)), i.Property(x => x.SubjectType), i.Property(x => x.SubjectId) })
                if (!IsUnset(p.OriginalValue) && !Equals(p.OriginalValue, p.CurrentValue))
                    throw Refuse("a workflow instance's definition and subject are fixed once set.");

        // Children first: their Touch makes the owning definition Modified, which the definition rule below then allows (Draft).
        foreach (var e in ctx.ChangeTracker.Entries<WorkflowState>().Where(e => changed.Contains(e.State)).ToList())
            CheckChild(ctx, Parent(e, nameof(WorkflowState.Definition)));
        foreach (var e in ctx.ChangeTracker.Entries<WorkflowTransition>().Where(e => changed.Contains(e.State)).ToList())
            CheckChild(ctx, Parent(e, nameof(WorkflowTransition.Definition)));
        foreach (var e in ctx.ChangeTracker.Entries<WorkflowEffect>().Where(e => changed.Contains(e.State)).ToList())
        {
            var state = Parent(e, nameof(WorkflowEffect.State)) is { } stateId ? ctx.Find<WorkflowState>(stateId) : null;
            CheckChild(ctx, state == null ? null : Parent(ctx.Entry(state), nameof(WorkflowState.Definition)));
        }

        foreach (var e in ctx.ChangeTracker.Entries<WorkflowDefinition>().Where(e => changed.Contains(e.State)).ToList())
            CheckDefinition(e);
    }

    static void CheckDefinition(EntityEntry<WorkflowDefinition> e)
    {
        var (d, target) = (e.Entity, e.Entity.State);
        if (e.State == EntityState.Added)
        {
            if (target != DefinitionState.Draft) throw Refuse("a new definition starts as Draft; only Publish makes it Published.");
            return;
        }
        var persisted = e.Property(x => x.State).OriginalValue;
        var modified = e.Properties.Where(p => p.IsModified && p.Metadata.Name != LockField).Select(p => p.Metadata.Name).ToList();
        // XAF's deferred deletion turns a Deleted entry into Modified(GCRecord); depending on interceptor order we see either.
        if (e.State == EntityState.Deleted || modified.Contains(GCRecord))
        {
            if (persisted != DefinitionState.Draft) throw Refuse($"'{d.Name}' v{d.Version} is {persisted} and cannot be deleted.");
            return;
        }
        switch (persisted)
        {
            case DefinitionState.Draft when target == DefinitionState.Draft:
                if (modified.Any(n => n is nameof(WorkflowDefinition.PublishedBy) or nameof(WorkflowDefinition.PublishedUtc)))
                    throw Refuse("only Publish records who published and when.");
                break;
            case DefinitionState.Draft:
                if (!(Publishing.Value && target == DefinitionState.Published)) throw Refuse("only Publish moves a draft out of Draft.");
                break;
            case DefinitionState.Published:
                if (!(Publishing.Value && target == DefinitionState.Retired && modified.All(n => n == nameof(WorkflowDefinition.State))))
                    throw Refuse($"'{d.Name}' v{d.Version} is Published and cannot change. Create a new draft.");
                break;
            default:
                throw Refuse($"'{d.Name}' v{d.Version} is {persisted} and cannot change.");
        }
    }

    /// <summary>A state/effect/transition write: allowed only while the owning definition is persisted as Draft; Touches it.</summary>
    static void CheckChild(DbContext ctx, Guid? definitionId)
    {
        if (definitionId is not { } id || ctx.Find<WorkflowDefinition>(id) is not { } d)
            throw Refuse("a state, effect or transition must belong to a definition.");
        var owner = ctx.Entry(d);
        if (owner.State == EntityState.Added) return;   // a new definition: CheckDefinition requires it to be a Draft
        var persisted = owner.Property(x => x.State).OriginalValue;
        if (persisted != DefinitionState.Draft)
            throw Refuse($"'{d.Name}' v{d.Version} is {persisted}; its states, effects and transitions cannot change. Create a new draft.");
        if (owner.State is EntityState.Unchanged or EntityState.Modified) Concurrency.Touch(owner);
    }

    /// <summary>The parent id as persisted (original FK); an Added row's current parent. Re-parenting is refused.</summary>
    static Guid? Parent(EntityEntry e, string navigation)
    {
        var fk = ForeignKey(e, navigation);
        if (e.State == EntityState.Added) return (Guid?)fk.CurrentValue ?? (e.Reference(navigation).CurrentValue as BaseObject)?.ID;
        if (e.State == EntityState.Modified && !Equals(fk.OriginalValue, fk.CurrentValue))
            throw Refuse("a state, effect or transition cannot move to another parent.");
        return (Guid?)fk.OriginalValue;
    }

    static PropertyEntry ForeignKey(EntityEntry e, string navigation) =>
        e.Property(((INavigation)e.Reference(navigation).Metadata).ForeignKey.Properties[0].Name);

    static bool IsUnset(object? v) => v is null or "" || (v is Guid g && g == Guid.Empty);
}
