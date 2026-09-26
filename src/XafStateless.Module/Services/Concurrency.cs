// Task 4b findings -- XAF 26.1.4 EF Core optimistic lock, pinned by ConcurrencySpikeTests:
// - Lock member: BaseObject.OptimisticLockField (int, IOptimisticLock, [ConcurrencyCheck]); mapped by
//   modelBuilder.UseOptimisticLock() (the demo DbContext calls it) and bumped to original+1 on save by
//   EFCoreOptimisticLockInterceptor, which AddEFCore/AddSecuredEFCore add. A provider built via the bare
//   EFCoreObjectSpaceProvider ctor lacks the interceptor, and then a lost race is a SILENT last-writer-wins.
// - Lost race in a non-UI object space (secured or not): CommitChanges throws
//   UserFriendlyException("The object you are trying to save was changed by another user. Please refresh data.")
//   with InnerException DbUpdateConcurrencyException (EFCoreObjectSpace.DoCommit wraps it). No merge.
// - Merge setting: the demo sets no lock options -> defaults OptimisticLockDetection.AllFields +
//   OptimisticLockHandling.Merge. Merging is done only by ProcessDataLockingInfoController on the Save
//   actions of a UI frame; a bare CommitChanges never consults OptimisticLockHandling, so commands need no
//   extra setting to fail loudly.
// - Touch: increment OptimisticLockField (EF entry -> Modified, the interceptor writes original+1).
//   os.SetModified(obj) alone does NOT advance the version (entry stays Unchanged, no UPDATE).
// - Stale user save after a command Touch: bare user object space -> case (i), fails with the lost-race shape.
//   Through the Blazor Save path -> case (ii): CanAutoMerge (Touch changed no visible field), MergeData, then
//   ONE SaveChanges pass that an EF SaveChangesInterceptor sees -- so Task 7b's guard gets its pass on the final save.
using DevExpress.ExpressApp;
using DevExpress.ExpressApp.DataLocking;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace XafStateless.Module.Services;

public static class Concurrency
{
    /// <summary>Advances <paramref name="obj"/>'s optimistic-lock version on the next commit of <paramref name="os"/> without changing any other property.</summary>
    public static void Touch(IObjectSpace os, object obj) => ((IOptimisticLock)obj).OptimisticLockField++;

    /// <summary>The DbContext form, for SaveChanges interceptors: writes original+1, exactly what XAF's
    /// EFCoreOptimisticLockInterceptor writes for a Modified entry, so running before or after it neither loses
    /// nor double-bumps the version. The UPDATE's concurrency check still uses the original value.</summary>
    public static void Touch(EntityEntry entry)
    {
        var version = entry.Property(nameof(IOptimisticLock.OptimisticLockField));
        version.CurrentValue = (int)version.OriginalValue! + 1;
        version.IsModified = true;
    }
}

/// <summary>
/// The runtime's write permit: open while a command runs its machine AND commits (guards run in SavingChanges), one per
/// attempt, and bound to the command's own DbContext. Modelled on DefinitionGuard.AllowPublishing: an AsyncLocal holding the
/// scope, and disposal revokes it everywhere, including in an execution context captured inside it. The AsyncLocal also flows
/// into Task.Run and threads started inside the scope, so a guard honours it only for the bound context (<see cref="Allows"/>):
/// a save of any other context, on any thread, is judged as outside the scope. Internal: only the runtime opens it.
/// It also carries the member-precise exemptions StateLockGuard honours: one (subject, member) pair per SetField.
/// </summary>
internal sealed class RuntimeScope : IDisposable
{
    static readonly AsyncLocal<RuntimeScope?> current = new();
    readonly RuntimeScope? previous;
    readonly HashSet<(Type, Guid, string)> allowed = [];
    volatile bool disposed;

    RuntimeScope(DbContext context, RuntimeScope? previous) => (Context, this.previous) = (context, previous);

    /// <summary>The only context whose saves this scope permits.</summary>
    public DbContext Context { get; }

    /// <summary>The open scope of this flow, or null (none, or disposed).</summary>
    public static RuntimeScope? Current => current.Value is { disposed: false } s ? s : null;

    /// <summary>True when an open scope of this flow is bound to exactly <paramref name="context"/>.</summary>
    public static bool Allows(DbContext? context) => context != null && ReferenceEquals(Current?.Context, context);

    public static RuntimeScope Open(DbContext context) => current.Value = new RuntimeScope(context, current.Value);

    /// <summary>Exempts exactly this subject member from StateLockGuard for this scope's saves (one per SetField). The subject
    /// is identified like the guard does: its EF entity CLR type and ID, since subject types in different tables may share IDs.</summary>
    public void Allow(Type subjectType, Guid subjectId, string member) { lock (allowed) allowed.Add((subjectType, subjectId, member)); }

    /// <summary>True when <see cref="Allow"/> recorded this (type, subject, member) and the scope is still open.</summary>
    public bool IsAllowed(Type subjectType, Guid subjectId, string member) { lock (allowed) return !disposed && allowed.Contains((subjectType, subjectId, member)); }

    public void Dispose()
    {
        disposed = true;
        current.Value = previous;
    }
}
