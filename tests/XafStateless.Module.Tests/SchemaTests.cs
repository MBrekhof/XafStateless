using XafStateless.Demo.Module.BusinessObjects;
using XafStateless.Module.BusinessObjects;
using XafStateless.Module.Services;

namespace XafStateless.Module.Tests;

public class SchemaTests : IClassFixture<TestDb>
{
    readonly TestDb db; public SchemaTests(TestDb db) => this.db = db;
    [Fact] public void Second_draft_for_same_name_is_refused()
    {
        using var os = db.NewObjectSpace();
        foreach (var _ in new[] { 1, 2 }) { var d = os.CreateObject<WorkflowDefinition>(); d.Name = "x"; d.Version = _; d.State = DefinitionState.Draft; d.TargetTypeName = typeof(PurchaseRequest).FullName!; }
        var ex = Assert.ThrowsAny<Exception>(() => os.CommitChanges());
        Assert.True(LostRace.IsUniqueViolation(ex), ex.ToString());   // SqlException 2601/2627 somewhere in the chain, not just any failure
    }
}
