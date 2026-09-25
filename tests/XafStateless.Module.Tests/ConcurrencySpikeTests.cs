using DevExpress.EntityFrameworkCore.Security;
using DevExpress.ExpressApp;
using DevExpress.ExpressApp.DataLocking;
using DevExpress.ExpressApp.DC;
using DevExpress.ExpressApp.EFCore;
using DevExpress.ExpressApp.EFCore.DataLocking;
using DevExpress.ExpressApp.Localization;
using DevExpress.ExpressApp.Security;
using DevExpress.Persistent.BaseImpl.EF.PermissionPolicy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using XafStateless.Demo.Module.BusinessObjects;
using XafStateless.Module.Services;

namespace XafStateless.Module.Tests;

/// <summary>Pins how XAF 26.1.4's EF Core optimistic lock behaves (Task 4b). Findings: see Concurrency.cs.</summary>
public class ConcurrencySpikeTests : IClassFixture<TestDb>
{
    readonly TestDb db; public ConcurrencySpikeTests(TestDb db) => this.db = db;

    Guid Seed()
    {
        using var os = db.NewObjectSpace();
        var pr = os.CreateObject<PurchaseRequest>(); pr.Title = "t"; pr.Amount = 1;
        os.CommitChanges();
        return pr.ID;
    }

    static int Version(IObjectSpace os, Guid id) => ((IOptimisticLock)os.GetObjectByKey<PurchaseRequest>(id)).OptimisticLockField;

    /// <summary>The recorded lost-race shape: XAF's EFCoreObjectSpace.DoCommit wraps EF's exception.</summary>
    static void AssertLostRaceShape(Exception ex)
    {
        var ufe = Assert.IsType<UserFriendlyException>(ex);
        Assert.Equal(UserVisibleExceptionLocalizer.GetExceptionMessage(UserVisibleExceptionId.ObjectToSaveWasChanged), ufe.Message);
        Assert.IsType<DbUpdateConcurrencyException>(ufe.InnerException);
        Assert.True(LostRace.Is(ex), ex.ToString());
    }

    [Fact] public void Lost_race_in_non_ui_object_space_surfaces_as()
    {
        var id = Seed();
        using var a = db.NewObjectSpace();
        using var b = db.NewObjectSpace();
        var prA = a.GetObjectByKey<PurchaseRequest>(id);
        var prB = b.GetObjectByKey<PurchaseRequest>(id);

        prB.Title = "B"; b.CommitChanges();
        prA.Amount = 42;
        var ex = Record.Exception(() => a.CommitChanges());

        Assert.NotNull(ex);                  // no silent merge
        AssertLostRaceShape(ex);
        using var check = db.NewObjectSpace();
        var saved = check.GetObjectByKey<PurchaseRequest>(id);
        Assert.Equal(("B", 1m), (saved.Title, saved.Amount));   // A's write did not land
    }

    [Fact] public void Touch_advances_lock_version()
    {
        var id = Seed();
        using var stale = db.NewObjectSpace();
        var prStale = stale.GetObjectByKey<PurchaseRequest>(id);
        int before = Version(stale, id);

        using (var cmd = db.NewObjectSpace())
        {
            Concurrency.Touch(cmd, cmd.GetObjectByKey<PurchaseRequest>(id));
            cmd.CommitChanges();
        }
        using (var check = db.NewObjectSpace()) Assert.Equal(before + 1, Version(check, id));

        prStale.Amount = 7;
        AssertLostRaceShape(Record.Exception(() => stale.CommitChanges())!);
    }

    [Fact] public void Stale_user_save_behaviour()
    {
        var id = Seed();
        var userName = $"u{Guid.NewGuid():N}";
        using (var os = db.NewObjectSpace())
        {
            var role = os.CreateObject<PermissionPolicyRole>(); role.Name = userName; role.IsAdministrative = true;
            var user = os.CreateObject<ApplicationUser>(); user.UserName = userName; user.SetPassword(""); user.Roles.Add(role);
            os.CommitChanges();
            ((ISecurityUserWithLoginInfo)user).CreateUserLoginInfo(SecurityDefaults.PasswordAuthentication, os.GetKeyValueAsString(user));
            os.CommitChanges();
        }
        // Built as the demo builds a user's space: AddSecuredEFCore with no lock options (defaults AllFields + Merge),
        // the DbContext's UseOptimisticLock(), plus XAF's lock interceptor that AddSecuredEFCore wires in.
        var saves = new SaveCounter();
        var auth = new AuthenticationStandard();
        var security = new SecurityStrategyComplex(typeof(ApplicationUser), typeof(PermissionPolicyRole), auth);
        using var provider = new SecuredEFCoreObjectSpaceProvider<DemoEFCoreDbContext>(security, XafTypesInfo.Instance, db.ConnectionString,
            (builder, cs) => { builder.UseConnectionString(cs); builder.AddInterceptors(new EFCoreOptimisticLockInterceptor(), saves); });
        auth.SetLogonParameters(new AuthenticationStandardLogonParameters(userName, ""));
        using (var logon = ((INonsecuredObjectSpaceProvider)provider).CreateNonsecuredObjectSpace()) security.Logon(logon);

        // (1) Bare user object space (no UI frame): the stale save fails with the lost-race shape.
        using (var user = provider.CreateObjectSpace())
        {
            Assert.IsType<SecuredEFCoreObjectSpace>(user);
            var efUser = (EFCoreObjectSpace)user;
            Assert.Equal((OptimisticLockDetection.AllFields, OptimisticLockHandling.Merge), (efUser.OptimisticLockDetection, efUser.OptimisticLockHandling));
            var pr = user.GetObjectByKey<PurchaseRequest>(id);
            TouchInCommand(id);
            pr.Amount = 99;
            saves.Count = 0;
            AssertLostRaceShape(Record.Exception(() => user.CommitChanges())!);
            Assert.Equal(1, saves.Count);    // exactly one SaveChanges attempt, which the interceptor saw
        }

        // (2) The same save through the path a Blazor Save button takes: ProcessDataLockingInfoController runs
        // GetDataLockingInfo -> (CanAutoMerge) MergeData before CommitChanges. Reproduced here without a frame.
        using (var user = provider.CreateObjectSpace())
        {
            var pr = user.GetObjectByKey<PurchaseRequest>(id);
            TouchInCommand(id);
            pr.Amount = 77;
            var mgr = (IDataLockingManager)user;
            Assert.True(mgr.IsActive);
            var info = mgr.GetDataLockingInfo();
            Assert.True(info.IsLocked);
            Assert.True(info.CanAutoMerge);  // Touch changed no user-visible field: the UI would merge silently, no dialog
            mgr.MergeData(info);
            saves.Count = 0;
            user.CommitChanges();
            Assert.Equal(1, saves.Count);    // the merged save goes through one SaveChanges pass the interceptor sees
        }
        using var check = db.NewObjectSpace();
        Assert.Equal(77m, check.GetObjectByKey<PurchaseRequest>(id).Amount);
    }

    [Fact] public void Merge_is_disabled_for_commands()
    {
        // The first test shows no silent merge: XAF merges only in ProcessDataLockingInfoController (a UI-frame
        // controller hooked to the Save actions). A command's bare CommitChanges never consults OptimisticLockHandling,
        // so even an explicit Merge setting on the command's object space fails loudly.
        var id = Seed();
        using var a = db.NewObjectSpace();
        using var b = db.NewObjectSpace();
        ((EFCoreObjectSpace)a).OptimisticLockHandling = OptimisticLockHandling.Merge;
        ((EFCoreObjectSpace)a).OptimisticLockDetection = OptimisticLockDetection.AllFields;
        var prA = a.GetObjectByKey<PurchaseRequest>(id);
        b.GetObjectByKey<PurchaseRequest>(id).Title = "B"; b.CommitChanges();
        prA.Amount = 5;
        AssertLostRaceShape(Record.Exception(() => a.CommitChanges())!);
    }

    void TouchInCommand(Guid id)
    {
        using var cmd = db.NewObjectSpace();
        Concurrency.Touch(cmd, cmd.GetObjectByKey<PurchaseRequest>(id));
        cmd.CommitChanges();
    }

    sealed class SaveCounter : SaveChangesInterceptor
    {
        public int Count;
        public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
        { Count++; return result; }
    }
}
