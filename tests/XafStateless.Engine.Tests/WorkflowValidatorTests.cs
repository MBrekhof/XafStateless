using XafStateless.Engine;
namespace XafStateless.Engine.Tests;

sealed class FakeEnv(params string[] roles) : IWorkflowEnvironment
{
    public bool RoleExists(string r) => roles.Contains(r);
    public string? CheckCriteria(string c) => c.Contains("Nope") ? "unknown member Nope" : null;
    public string? CheckEffect(EffectDef e) => e.Target == "Nope" ? "unknown member Nope" : null;
    public string? CheckMember(string m) => m == "Nope" ? "unknown member Nope" : null;
}

public class WorkflowValidatorTests
{
    static readonly FakeEnv Env = new("Approvers");
    static IReadOnlyList<string> V(WorkflowDef d) => WorkflowValidator.Validate(d, Env);
    static WorkflowDef Pr => Fixtures.PurchaseRequest();

    [Fact] public void Fixture_is_valid() => Assert.Empty(V(Pr));

    [Fact] public void Duplicate_state()
    {
        var d = Pr with { States = [.. Pr.States, new StateDef("draft", "Duplicate", StateKind.Manual)] };
        Assert.Contains(V(d), e => e.Contains("duplicate state 'draft'"));
    }

    [Fact] public void Initial_state_missing()
    {
        var d = Pr with { InitialState = "nonexistent" };
        Assert.Contains(V(d), e => e.Contains("initial state 'nonexistent' does not exist"));
    }

    [Fact] public void Transition_refers_to_unknown_state()
    {
        var d = Pr with { Transitions = [.. Pr.Transitions, new TransitionDef("draft", "Go", "unknown")] };
        Assert.Contains(V(d), e => e.Contains("transition 'Go' refers to unknown state 'unknown'"));
    }

    [Fact] public void Duplicate_trigger()
    {
        var d = Pr with { Transitions = [.. Pr.Transitions, new TransitionDef("approval", "Approve", "rejected")] };
        Assert.Contains(V(d), e => e.Contains("duplicate trigger 'Approve' from 'approval'"));
    }

    [Fact] public void Self_loop_on_decision_is_refused()
    {
        var d = Pr with
        {
            States = Pr.States.Select(s => s.Key == "decide-amount" ? s : s).ToList(),
            Transitions = [.. Pr.Transitions, new TransitionDef("decide-amount", "Loop", "decide-amount")]
        };
        Assert.Contains(V(d), e => e.Contains("'decide-amount' cannot transition to itself"));
    }

    [Fact] public void Unreachable_state()
    {
        var d = Pr with { States = [.. Pr.States, new StateDef("orphan", "Orphan", StateKind.Final)] };
        Assert.Contains(V(d), e => e.Contains("'orphan' is unreachable"));
    }

    [Fact] public void Final_state_with_transitions_is_refused()
    {
        var d = Pr with { Transitions = [.. Pr.Transitions, new TransitionDef("ordered", "Restart", "draft")] };
        Assert.Contains(V(d), e => e.Contains("final state 'ordered' has transitions"));
    }

    [Fact] public void State_without_way_out()
    {
        var d = Pr with { Transitions = Pr.Transitions.Where(t => t.From != "wait-cooling").ToList() };
        Assert.Contains(V(d), e => e.Contains("'wait-cooling' has no way out"));
    }

    [Fact] public void Trap_cycle_can_never_finish()
    {
        var d = Pr with
        {
            States = [.. Pr.States, new StateDef("a", "A", StateKind.Manual), new StateDef("b", "B", StateKind.Manual)],
            Transitions = [.. Pr.Transitions, new TransitionDef("draft", "Park", "a"), new TransitionDef("a", "Go", "b"), new TransitionDef("b", "Back", "a")]
        };
        Assert.Contains(V(d), e => e.Contains("'a' can never finish"));
    }

    [Fact] public void Wait_without_duration()
    {
        var d = Pr with { States = Pr.States.Select(s => s.Key == "wait-cooling" ? s with { WaitMinutes = null } : s).ToList() };
        Assert.Contains(V(d), e => e.Contains("wait 'wait-cooling' needs a duration"));
    }

    [Fact] public void Guarded_wait_is_stuck()
    {
        var d = Pr with { Transitions = Pr.Transitions.Select(t => t.From == "wait-cooling" ? t with { Criteria = "Amount > 0" } : t).ToList() };
        Assert.Contains(V(d), e => e.Contains("wait 'wait-cooling' needs exactly one unguarded exit"));
    }

    [Fact] public void Task_without_role()
    {
        var d = Pr with { States = Pr.States.Select(s => s.Key == "approval" ? s with { TaskRole = null } : s).ToList() };
        Assert.Contains(V(d), e => e.Contains("task 'approval' needs a role"));
    }

    [Fact] public void Task_outcome_with_required_role()
    {
        var d = Pr with { Transitions = Pr.Transitions.Select(t => t.Trigger == "Approve" ? t with { RequiredRole = "Approvers" } : t).ToList() };
        Assert.Contains(V(d), e => e.Contains("task 'approval' outcome 'Approve' cannot require a role"));
    }

    [Fact] public void Decision_needs_else()
    {
        var d = Pr with { Transitions = Pr.Transitions.Select(t => t.Trigger == "Small" ? t with { Criteria = "Amount <= 1000" } : t).ToList() };
        Assert.Contains(V(d), e => e.Contains("decision 'decide-amount' needs a final else-branch without criteria"));
    }

    [Fact] public void Decision_branch_without_criteria()
    {
        var d = Pr with { Transitions = Pr.Transitions.Select(t => t.Trigger == "Large" ? t with { Criteria = null } : t).ToList() };
        Assert.Contains(V(d), e => e.Contains("decision 'decide-amount' branch 'Large' needs criteria"));
    }

    [Fact] public void Decision_duplicate_order_is_refused()
    {
        var d = Pr with { Transitions = Pr.Transitions.Select(t => t.Trigger == "Small" ? t with { Order = 0 } : t).ToList() };
        Assert.Contains(V(d), e => e.Contains("decision 'decide-amount' has duplicate order 0"));
    }

    [Fact] public void Decision_branch_role_is_refused_even_if_role_exists()
    {
        var d = Pr with { Transitions = Pr.Transitions.Select(t => t.Trigger == "Large" ? t with { RequiredRole = "Approvers" } : t).ToList() };
        Assert.Contains(V(d), e => e.Contains("decision 'decide-amount' branch 'Large' cannot require a role"));
    }

    [Fact] public void Shuffled_transitions_validate_and_route_the_same()
    {
        var shuffled = Pr with { Transitions = Pr.Transitions.Reverse().ToList() };
        Assert.Equal(V(Pr), V(shuffled));
    }

    [Fact] public void Decision_loop()
    {
        var d = Pr with
        {
            States = [.. Pr.States, new StateDef("d2", "D2", StateKind.Decision)],
            Transitions = [.. Pr.Transitions.Where(t => t.Trigger != "Small"),
                new TransitionDef("decide-amount", "Small", "d2", Order: 1), new TransitionDef("d2", "Back", "decide-amount")]
        };
        Assert.Contains(V(d), e => e.Contains("form a loop"));
    }

    [Fact] public void Task_role_must_exist()
    {
        var d = Pr with { States = Pr.States.Select(s => s.Key == "approval" ? s with { TaskRole = "Ghosts" } : s).ToList() };
        Assert.Contains(V(d), e => e.Contains("role 'Ghosts' does not exist"));
    }

    [Fact] public void Bad_criteria_and_effect_surface_env_errors()
    {
        var d = Pr with
        {
            Transitions = Pr.Transitions.Select(t => t.Trigger == "Large" ? t with { Criteria = "Nope > 1" } : t).ToList(),
            States = Pr.States.Select(s => s.Key == "ordered" ? s with { OnEntry = [new(EffectKind.SetField, "Nope", "1")] } : s).ToList()
        };
        var errs = V(d);
        Assert.Contains(errs, e => e.Contains("criteria on 'Large': unknown member Nope"));
        Assert.Contains(errs, e => e.Contains("effect on 'ordered': unknown member Nope"));
    }

    [Fact] public void Notify_role_must_exist()
    {
        var d = Pr with { States = Pr.States.Select(s => s.Key == "approval" ? s with { OnEntry = [new(EffectKind.Notify, "Ghosts", "msg")] } : s).ToList() };
        Assert.Contains(V(d), e => e.Contains("role 'Ghosts' does not exist"));
    }

    static WorkflowDef NotifyOnApproval(string target) =>
        Pr with { States = Pr.States.Select(s => s.Key == "approval" ? s with { OnEntry = [new(EffectKind.Notify, target, "msg")] } : s).ToList() };

    [Fact] public void Notify_may_address_the_requester() => Assert.Empty(V(NotifyOnApproval(EffectDef.Requester)));   // XSM-014

    [Fact] public void A_role_named_like_the_requester_token_conflicts()
    {
        var errs = WorkflowValidator.Validate(NotifyOnApproval(EffectDef.Requester), new FakeEnv("Approvers", EffectDef.Requester));
        Assert.Contains(errs, e => e.Contains($"a role named '{EffectDef.Requester}'"));
    }

    [Fact] public void The_requester_token_is_not_a_task_role()
    {
        var d = Pr with { States = Pr.States.Select(s => s.Key == "approval" ? s with { TaskRole = EffectDef.Requester } : s).ToList() };
        Assert.Contains(V(d), e => e.Contains($"role '{EffectDef.Requester}' does not exist"));
    }

    [Fact] public void Transition_role_must_exist()
    {
        var d = Pr with { Transitions = Pr.Transitions.Select(t => t.Trigger == "Submit" ? t with { RequiredRole = "Ghosts" } : t).ToList() };
        Assert.Contains(V(d), e => e.Contains("role 'Ghosts' does not exist"));
    }

    [Fact] public void Lock_on_unknown_member_is_refused()
    {
        var d = Pr with { States = Pr.States.Select(s => s.Key == "approval" ? s with { Locked = ["Amount", "Nope"] } : s).ToList() };
        var errs = V(d);
        Assert.Contains("lock on 'Nope': unknown member Nope", errs);
        Assert.DoesNotContain(errs, e => e.Contains("'Amount'"));
        Assert.Equal(["Amount", "Nope"], d.State("approval").LockedMembers);
        Assert.Empty(Pr.State("approval").LockedMembers);
    }
}
