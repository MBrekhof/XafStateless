using XafStateless.Engine;
namespace XafStateless.Engine.Tests;

public static class Fixtures
{
    public static WorkflowDef PurchaseRequest() => new("purchase-request", 1, "draft",
        [
            new("draft", "Draft", StateKind.Manual),
            new("approval", "Approval", StateKind.Task, TaskRole: "Approvers",
                OnEntry: [new(EffectKind.Notify, "Approvers", "Please approve {Title}")]),
            new("decide-amount", "Amount check", StateKind.Decision),
            new("wait-cooling", "Cooling-off", StateKind.Wait, WaitMinutes: 1),
            new("ordered", "Ordered", StateKind.Final, OnEntry: [new(EffectKind.SetField, "Ordered", "true")]),
            new("rejected", "Rejected", StateKind.Final),
        ],
        [
            new("draft", "Submit", "approval"),
            new("approval", "Approve", "decide-amount"),
            new("approval", "Reject", "rejected"),
            new("decide-amount", "Large", "wait-cooling", Criteria: "Amount > 1000", Order: 0),
            new("decide-amount", "Small", "ordered", Order: 1),
            new("wait-cooling", "Cooled", "ordered"),
        ]);
}
