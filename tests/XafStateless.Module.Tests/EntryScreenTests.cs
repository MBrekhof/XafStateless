using DevExpress.ExpressApp;
using DevExpress.ExpressApp.DC;
using XafStateless.Demo.Module.BusinessObjects;
using XafStateless.Module.Controllers;

namespace XafStateless.Module.Tests;

/// <summary>The object space an entry screen's command carries (XSM-033): the persistent one behind the non-persistent space.</summary>
public class EntryScreenTests(TestDb db) : IClassFixture<TestDb>
{
    [Fact] public void SpaceFor_picks_the_additional_space_that_knows_the_subject()
    {
        using var ef = db.NewObjectSpace();
        // The entity store the application's non-persistent provider fills from the model: here, the one screen type.
        var store = new NonPersistentTypeInfoSource(XafTypesInfo.Instance, [typeof(StartRequestsScreen)]);
        store.RegisterEntity(typeof(StartRequestsScreen));
        using var screenSpace = new NonPersistentObjectSpace(XafTypesInfo.Instance, store);
        screenSpace.AdditionalObjectSpaces.Add(ef);

        Assert.True(screenSpace.CanInstantiate(typeof(PurchaseRequest)));   // the trap: a composite answers through its additional spaces
        Assert.Same(ef, WorkflowCommand.SpaceFor(screenSpace, typeof(PurchaseRequest), securityConfigured: false));
        Assert.Same(screenSpace, WorkflowCommand.SpaceFor(screenSpace, typeof(StartRequestsScreen), securityConfigured: false));
        Assert.Same(ef, WorkflowCommand.SpaceFor(ef, typeof(PurchaseRequest), securityConfigured: false));
    }

    [Fact] public void SpaceFor_requires_a_secured_space_when_security_is_configured()
    {
        using var ef = db.NewObjectSpace();   // TestDb's spaces are plain, not secured
        using var screenSpace = new NonPersistentObjectSpace(XafTypesInfo.Instance);
        screenSpace.AdditionalObjectSpaces.Add(ef);

        Assert.Contains("not secured", Assert.Throws<InvalidOperationException>(() => WorkflowCommand.SpaceFor(screenSpace, typeof(PurchaseRequest), securityConfigured: true)).Message);
        Assert.Same(ef, WorkflowCommand.SpaceFor(screenSpace, typeof(PurchaseRequest), securityConfigured: false));   // no security: plain is fine
    }

    [Fact] public void SpaceFor_without_a_space_for_the_subject_says_what_the_host_forgot()
    {
        using var screenSpace = new NonPersistentObjectSpace(XafTypesInfo.Instance);   // the host did not populate the additional spaces
        Assert.Contains("integrating.md", Assert.Throws<InvalidOperationException>(() => WorkflowCommand.SpaceFor(screenSpace, typeof(PurchaseRequest), securityConfigured: false)).Message);
    }
}
