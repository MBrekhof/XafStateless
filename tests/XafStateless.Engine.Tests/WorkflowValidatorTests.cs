using XafStateless.Engine;
namespace XafStateless.Engine.Tests;

sealed class FakeEnv(params string[] roles) : IWorkflowEnvironment
{
    public bool RoleExists(string r) => roles.Contains(r);
    public string? CheckCriteria(string c) => c.Contains("Nope") ? "unknown member Nope" : null;
    public string? CheckEffect(EffectDef e) => e.Target == "Nope" ? "unknown member Nope" : null;
    public string? CheckMember(string m) => m == "Nope" ? "unknown member Nope" : null;
    public string? CheckRuleSet(RuleSetDef rs) => rs.Key == "bad-env" ? "rule set 'bad-env' failed env check" : null;
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

    // XSM-024: the user's case. The cause (no Final state) as one definition-level finding, plus the dead end on the state
    // that should have been Final; no "can never finish" on the states before it.
    static readonly WorkflowDef NoFinal = new("chain", 1, "Start",
        [new("Start", "Starting", StateKind.Manual), new("Middle", "In the middle", StateKind.Manual), new("End", "The End", StateKind.Manual)],
        [new("Start", "Start", "Middle"), new("Middle", "Middle", "End")]);

    [Fact] public void No_final_state_is_reported_as_the_cause_not_as_symptoms()
    {
        var findings = WorkflowValidator.Check(NoFinal, Env);
        Assert.Contains(findings, f => f.State == null && f.Message.StartsWith("no state is Final"));
        Assert.Contains(findings, f => f.State == "End" && f.Message == "'End' has no way out: make it Final, or add a transition from it");
        Assert.DoesNotContain(findings, f => f.Message.Contains("can never finish"));
        Assert.Equal(2, findings.Count);
    }

    [Fact] public void States_leading_only_to_a_dead_end_are_not_blamed()
    {
        // draft -Park-> a -Go-> b (a dead end); the rest of the fixture still finishes.
        var d = Pr with
        {
            States = [.. Pr.States, new StateDef("a", "A", StateKind.Manual), new StateDef("b", "B", StateKind.Manual)],
            Transitions = [.. Pr.Transitions, new TransitionDef("draft", "Park", "a"), new TransitionDef("a", "Go", "b")]
        };
        var findings = WorkflowValidator.Check(d, Env);
        Assert.Contains(findings, f => f.State == "b" && f.Message.StartsWith("'b' has no way out"));
        Assert.DoesNotContain(findings, f => f.Message.Contains("'a' can never finish"));
    }

    static WorkflowDef Plus(IEnumerable<StateDef> states, IEnumerable<TransitionDef> transitions) =>
        Pr with { States = [.. Pr.States, .. states], Transitions = [.. Pr.Transitions, .. transitions] };

    [Fact] public void A_closed_loop_next_to_a_dead_end_blames_only_the_loop_and_the_dead_end()
    {
        // draft -Park-> a <-> b (closed loop), draft -Hold-> c (dead end)
        var d = Plus([new("a", "A", StateKind.Manual), new("b", "B", StateKind.Manual), new("c", "C", StateKind.Manual)],
            [new("draft", "Park", "a"), new("a", "Go", "b"), new("b", "Back", "a"), new("draft", "Hold", "c")]);
        var f = WorkflowValidator.Check(d, Env);
        Assert.Contains(f, x => x.State == "a" && x.Message.StartsWith("'a' can never finish"));
        Assert.Contains(f, x => x.State == "b" && x.Message.StartsWith("'b' can never finish"));
        Assert.Contains(f, x => x.State == "c" && x.Message.StartsWith("'c' has no way out"));
        Assert.DoesNotContain(f, x => x.Message.Contains("'draft' can never finish"));
    }

    [Fact] public void A_loop_whose_exit_is_a_dead_end_is_not_blamed()
    {
        // a <-> b, b -Leave-> c (dead end): the dead end is the cause
        var d = Plus([new("a", "A", StateKind.Manual), new("b", "B", StateKind.Manual), new("c", "C", StateKind.Manual)],
            [new("draft", "Park", "a"), new("a", "Go", "b"), new("b", "Back", "a"), new("b", "Leave", "c")]);
        var f = WorkflowValidator.Check(d, Env);
        Assert.Single(f, x => x.Message.StartsWith("'c' has no way out"));
        Assert.DoesNotContain(f, x => x.Message.Contains("can never finish"));
    }

    [Fact] public void A_wait_leading_into_a_dead_end_keeps_its_own_findings_but_is_not_blamed_for_finishing()
    {
        var d = Plus([new("w", "W", StateKind.Wait), new("c", "C", StateKind.Manual)], [new("draft", "Park", "w"), new("w", "Done", "c")]);
        var f = WorkflowValidator.Check(d, Env);
        Assert.Contains(f, x => x.State == "w" && x.Message == "wait 'w' needs a duration");
        Assert.DoesNotContain(f, x => x.Message.Contains("'w' can never finish"));
    }

    [Fact] public void Role_criteria_effect_and_lock_findings_carry_their_state()
    {
        var d = Pr with
        {
            States = Pr.States.Select(s => s.Key == "approval"
                ? s with { TaskRole = "Ghosts", OnEntry = [new(EffectKind.SetField, "Nope", "1")], Locked = ["Nope"] } : s).ToList(),
            Transitions = Pr.Transitions.Select(t => t.Trigger == "Large" ? t with { Criteria = "Nope > 1" } : t).ToList(),
        };
        var f = WorkflowValidator.Check(d, Env);
        Assert.Contains(f, x => x.State == "approval" && x.Message == "role 'Ghosts' does not exist");
        Assert.Contains(f, x => x.State == "approval" && x.Message.StartsWith("effect on 'approval'"));
        Assert.Contains(f, x => x.State == "approval" && x.Message.StartsWith("lock on 'Nope'"));
        Assert.Contains(f, x => x.State == "decide-amount" && x.Message.StartsWith("criteria on 'Large'"));
    }

    [Fact] public void Without_a_final_state_a_loop_is_not_blamed_either()
    {
        var loop = new WorkflowDef("loop", 1, "a", [new("a", "A", StateKind.Manual), new("b", "B", StateKind.Manual)],
            [new("a", "Go", "b"), new("b", "Back", "a")]);
        var f = WorkflowValidator.Check(loop, Env);
        Assert.Equal([null], f.Select(x => x.State));
        Assert.StartsWith("no state is Final", f[0].Message);
    }

    [Fact] public void A_duplicate_trigger_from_an_unknown_state_is_not_tagged_with_it()
    {
        var d = Pr with { Transitions = [.. Pr.Transitions, new TransitionDef("ghost", "X", "draft"), new TransitionDef("ghost", "X", "draft")] };
        Assert.Contains(WorkflowValidator.Check(d, Env), f => f.Message == "duplicate trigger 'X' from 'ghost'" && f.State == null);
    }

    [Fact] public void Blank_required_texts_are_findings()   // XSM-027: validation runs before XAF's required-field rules
    {
        var d = Pr with
        {
            Name = "",
            States = Pr.States.Select(s => s.Key == "approval" ? s with { Caption = " " } : s).ToList(),
            Transitions = Pr.Transitions.Select(t => t.Trigger == "Reject" ? t with { Trigger = "" } : t).ToList(),
        };
        var f = WorkflowValidator.Check(d, Env);
        Assert.Contains(f, x => x.Message == "the definition has no name" && x.State == null);
        Assert.Contains(f, x => x.Message == "state 'approval' has no caption" && x.State == "approval");
        Assert.Contains(f, x => x.Message == "a transition from 'approval' has no action name" && x.State == "approval");
    }

    [Fact] public void A_blank_initial_state_is_a_finding_and_still_draws()
    {
        var d = Pr with { InitialState = "" };
        Assert.Contains(WorkflowValidator.Check(d, Env), f => f.Message == "initial state '' does not exist");
        Assert.DoesNotContain("[*] -->", WorkflowMermaid.Format(d, new DiagramStyle(ByKind: true)));
    }

    [Fact] public void Validate_returns_the_finding_messages() =>
        Assert.Equal(WorkflowValidator.Check(NoFinal, Env).Select(f => f.Message), WorkflowValidator.Validate(NoFinal, Env));

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

    [Fact] public void RuleSet_and_RunRules_validation()
    {
        var validRule = new RuleItemDef("r1", "subject.Amount > 0", "Amount", "100m", Order: 0);
        var dValid = Pr with
        {
            RuleSets = [new RuleSetDef("pricing", [new RuleParamDef("disc", "10m")], [validRule])],
            States = Pr.States.Select(s => s.Key == "ordered" ? s with { OnEntry = [new(EffectKind.RunRules, "pricing", "")] } : s).ToList()
        };
        Assert.Empty(V(dValid));

        var dMissingRs = Pr with
        {
            States = Pr.States.Select(s => s.Key == "ordered" ? s with { OnEntry = [new(EffectKind.RunRules, "missing-rs", "")] } : s).ToList()
        };
        var fMissing = WorkflowValidator.Check(dMissingRs, Env);
        Assert.Contains(fMissing, x => x.State == "ordered" && x.Message == "effect on 'ordered': rule set 'missing-rs' does not exist");

        var dDupKey = Pr with
        {
            RuleSets = [new RuleSetDef("pricing", [], [validRule]), new RuleSetDef("pricing", [], [validRule])]
        };
        Assert.Contains(V(dDupKey), e => e == "duplicate rule set 'pricing'");

        var dEmptyRules = Pr with { RuleSets = [new RuleSetDef("pricing", [], [])] };
        Assert.Contains(V(dEmptyRules), e => e == "rule set 'pricing' has no rules");

        var dReservedParam = Pr with
        {
            RuleSets = [new RuleSetDef("pricing", [new RuleParamDef("subject", "1")], [validRule])]
        };
        Assert.Contains(V(dReservedParam), e => e == "rule set 'pricing' parameter 'subject' is reserved");

        var dDupParam = Pr with
        {
            RuleSets = [new RuleSetDef("pricing", [new RuleParamDef("p1", "1"), new RuleParamDef("p1", "2")], [validRule])]
        };
        Assert.Contains(V(dDupParam), e => e == "rule set 'pricing' has duplicate parameter 'p1'");

        var dDupRuleAndOrder = Pr with
        {
            RuleSets = [new RuleSetDef("pricing", [], [validRule, validRule])]
        };
        var errsDup = V(dDupRuleAndOrder);
        Assert.Contains(errsDup, e => e == "rule set 'pricing' has duplicate rule 'r1'");
        Assert.Contains(errsDup, e => e == "rule set 'pricing' has duplicate order 0");

        var dEnvErr = Pr with { RuleSets = [new RuleSetDef("bad-env", [], [validRule])] };
        Assert.Contains(V(dEnvErr), e => e == "rule set 'bad-env' failed env check");
    }

    [Fact] public void Mermaid_details_includes_run_rules()
    {
        var d = Pr with
        {
            States = Pr.States.Select(s => s.Key == "ordered" ? s with { OnEntry = [new(EffectKind.RunRules, "pricing", "")] } : s).ToList()
        };
        var mermaid = WorkflowMermaid.Format(d, new DiagramStyle(Details: true));
        Assert.Contains("rules pricing", mermaid);
    }
}