namespace XafStateless.Engine;

public enum StateKind { Manual, Task, Wait, Decision, Final }

/// <summary>Notify: Target = role, <see cref="EffectDef.Requester"/> or a user member as <c>@Member</c>, Value = text with {Member} placeholders. SetField: Target = member,
/// Value = the new value as text. Increment: Target = a whole-number or decimal member, Value = the step as text ("" = 1), so a
/// workflow can count attempts and a Decision can limit them (XSM-031). RunAction: Target = the name of an action the host
/// registered, Value = its argument with {Member} placeholders; the action runs in the transition's commit and may change
/// other records than the subject (XSM-032). RunRules: Target = the Key of a <see cref="RuleSetDef"/> on the definition (XSM-035).</summary>
public enum EffectKind { Notify, SetField, Increment, RunAction, RunRules }

public sealed record EffectDef(EffectKind Kind, string Target, string Value)
{
    /// <summary>A Notify target that addresses the user who started the workflow, not a role (XSM-014).</summary>
    public const string Requester = "@requester";

    /// <summary>The user member a target <c>@Member</c> addresses (CARD-2615): the name after '@' for any target but
    /// <see cref="Requester"/>; null for a role, the requester or a bare '@'. A Task state's TaskRole and a Notify target use it.</summary>
    public static string? UserMember(string? target) =>
        target is { Length: > 1 } && target[0] == '@' && target != Requester ? target[1..] : null;
}

public sealed record RuleParamDef(string Name, string Expression);

public sealed record RuleItemDef(string Name, string Expression, string TargetMember, string OutputExpression, int Order = 0);

public sealed record RuleSetDef(string Key, IReadOnlyList<RuleParamDef> Params, IReadOnlyList<RuleItemDef> Rules);

/// <summary>TaskRole: the role that decides a Task state, or a user member of the subject as <c>@Member</c> (the user it holds
/// when the task opens decides it; CARD-2615).</summary>
public sealed record StateDef(string Key, string Caption, StateKind Kind,
    string? TaskRole = null, int? WaitMinutes = null, IReadOnlyList<EffectDef>? OnEntry = null, IReadOnlyList<string>? Locked = null)
{
    public IReadOnlyList<EffectDef> Effects => OnEntry ?? [];
    /// <summary>Subject members that cannot change while an instance is in this state (the runtime's own SetFields excepted).</summary>
    public IReadOnlyList<string> LockedMembers => Locked ?? [];
}

public sealed record TransitionDef(string From, string Trigger, string To,
    string? Criteria = null, string? RequiredRole = null, int Order = 0);

public sealed record WorkflowDef(string Name, int Version, string InitialState,
    IReadOnlyList<StateDef> States, IReadOnlyList<TransitionDef> Transitions,
    IReadOnlyList<RuleSetDef>? RuleSets = null)
{
    public IReadOnlyList<RuleSetDef> RuleSetList => RuleSets ?? [];
    public RuleSetDef? RuleSet(string key) => RuleSetList.FirstOrDefault(r => r.Key == key);
    public StateDef State(string key) => States.First(s => s.Key == key);
    public IEnumerable<TransitionDef> From(string key) => Transitions.Where(t => t.From == key).OrderBy(t => t.Order);
}
