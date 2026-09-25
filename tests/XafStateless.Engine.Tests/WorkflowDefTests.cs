using XafStateless.Engine;
namespace XafStateless.Engine.Tests;

public class WorkflowDefTests
{
    [Fact]
    public void From_returns_transitions_in_order()
    {
        var def = Fixtures.PurchaseRequest();
        Assert.Equal(["Large", "Small"], def.From("decide-amount").Select(t => t.Trigger));
    }
}
