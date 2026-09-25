using XafStateless.Engine;
namespace XafStateless.Engine.Tests;

public class WorkflowMachineTests
{
    static (WorkflowMachine m, Func<string> state) Make(decimal amount)
    {
        var s = "draft";
        var m = new WorkflowMachine(Fixtures.PurchaseRequest(), () => s, v => s = v, c => c == "Amount > 1000" && amount > 1000);
        return (m, () => s);
    }

    [Fact] public void Small_amount_auto_routes_through_decision_to_ordered()
    {
        var (m, st) = Make(50);
        m.Fire("Submit");
        var steps = m.Fire("Approve");
        Assert.Equal(["decide-amount", "ordered"], steps.Select(x => x.To));
        Assert.Equal("ordered", st());
    }

    [Fact] public void Large_amount_waits_then_timer_orders()
    {
        var (m, st) = Make(5000);
        m.Fire("Submit"); m.Fire("Approve");
        Assert.Equal("wait-cooling", st());
        Assert.Empty(m.Available());
        m.FireTimer();
        Assert.Equal("ordered", st());
    }

    [Fact] public void Impossible_trigger_throws_readable()
    {
        var (m, _) = Make(1);
        var ex = Assert.Throws<InvalidOperationException>(() => m.Fire("Approve"));
        Assert.Contains("'Approve' is not possible from 'draft'", ex.Message);
    }

    [Fact] public void Available_lists_task_outcomes()
    {
        var (m, _) = Make(1);
        m.Fire("Submit");
        Assert.Equal(["Approve", "Reject"], m.Available().Select(t => t.Trigger));
    }

    [Fact] public void Mermaid_is_exact_and_escapes_captions()
    {
        var def = new WorkflowDef("t", 1, "a",
            [new("a", "Draft \"new\"", StateKind.Manual), new("b", "Done: ok", StateKind.Final)],
            [new("a", "Send back", "b"), new("a", "Ghost", "nope")]);
        Assert.Equal(
            "stateDiagram-v2\n    state \"Draft 'new'\" as s0\n    state \"Done  ok\" as s1\n    [*] --> s0\n    s0 --> s1 : Send back\n    s1 --> [*]\n",
            WorkflowMermaid.Format(def));
    }

    [Fact] public void Self_loop_on_task_reenters_guarded_and_unguarded()
    {
        foreach (var crit in new string?[] { null, "Amount > 1000" })
        {
            var baseDef = Fixtures.PurchaseRequest();
            var def = baseDef with { Transitions = [.. baseDef.Transitions, new("approval", "Remind", "approval", Criteria: crit)] };
            var s = "draft"; var entered = new List<string>();
            var m = new WorkflowMachine(def, () => s, v => s = v, c => c == "Amount > 1000", onEnter: entered.Add);
            m.Fire("Submit"); m.Fire("Remind");
            Assert.Equal("approval", s);
            Assert.Equal(["approval", "approval"], entered);
        }
    }

    [Fact] public void Entry_effect_changes_decision_branch()
    {
        // onEnter("decide-amount") flips amount to 5000 before the decision is evaluated → the route goes to wait-cooling, not ordered
        var amount = 50m; var s = "draft";
        var m = new WorkflowMachine(Fixtures.PurchaseRequest(), () => s, v => s = v,
            c => c == "Amount > 1000" && amount > 1000, onEnter: k => { if (k == "decide-amount") amount = 5000; });
        m.Fire("Submit"); m.Fire("Approve");
        Assert.Equal("wait-cooling", s);
    }

    [Fact] public void Dry_run_reports_refusal_without_state()
    {
        var (steps, err) = WorkflowDryRun.Run(Fixtures.PurchaseRequest(), ["Submit", "Cooled"], _ => false);
        Assert.Single(steps);
        Assert.Contains("'Cooled' is not possible from 'approval'", err);
    }

    // 33 Decision states chained a0 -> a1 -> ... -> a33 (each one unguarded transition; a33 Final).
    // MaxDecisionDepth is 32, so entering the initial state alone (a0..a32, 33 decisions) overruns it.
    static WorkflowDef DeepDecisionChain()
    {
        var states = Enumerable.Range(0, 33).Select(i => new StateDef($"a{i}", $"a{i}", StateKind.Decision))
            .Append(new StateDef("a33", "a33", StateKind.Final)).ToList();
        var transitions = Enumerable.Range(0, 33).Select(i => new TransitionDef($"a{i}", "Next", $"a{i + 1}")).ToList();
        return new WorkflowDef("deep", 1, "a0", states, transitions);
    }

    [Fact] public void Dry_run_does_not_throw_past_decision_depth_cap()
    {
        var (_, err) = WorkflowDryRun.Run(DeepDecisionChain(), [], _ => true);
        Assert.NotNull(err);
    }

    [Fact] public void Decision_chain_past_max_depth_throws()
    {
        var s = "a0";
        var m = new WorkflowMachine(DeepDecisionChain(), () => s, v => s = v, _ => true);
        var ex = Assert.Throws<InvalidOperationException>(() => m.EnterInitial());
        Assert.Contains($"exceeded {WorkflowMachine.MaxDecisionDepth}", ex.Message);
    }

    [Fact] public void Mermaid_tolerates_duplicate_keys_and_missing_initial_state()
    {
        var def = new WorkflowDef("broken", 1, "nope",
            [new("a", "First", StateKind.Manual), new("a", "Second", StateKind.Final)], []);

        var mermaid = WorkflowMermaid.Format(def);

        Assert.Equal(1, mermaid.Split('\n').Count(l => l.StartsWith("    state \"")));
        Assert.DoesNotContain("[*] -->", mermaid);
    }
}
