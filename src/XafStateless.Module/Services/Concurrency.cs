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

namespace XafStateless.Module.Services;

public static class Concurrency
{
    /// <summary>Advances <paramref name="obj"/>'s optimistic-lock version on the next commit of <paramref name="os"/> without changing any other property.</summary>
    public static void Touch(IObjectSpace os, object obj) => ((IOptimisticLock)obj).OptimisticLockField++;
}
