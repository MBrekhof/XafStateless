namespace XafStateless.Engine;

public enum StateKind { Manual, Task, Wait, Decision, Final }

public enum EffectKind { Notify, SetField }

public sealed record EffectDef(EffectKind Kind, string Target, string Value);

public sealed record StateDef(string Key, string Caption, StateKind Kind,
    string? TaskRole = null, int? WaitMinutes = null, IReadOnlyList<EffectDef>? OnEntry = null)
{
    public IReadOnlyList<EffectDef> Effects => OnEntry ?? [];
}

public sealed record TransitionDef(string From, string Trigger, string To,
    string? Criteria = null, string? RequiredRole = null, int Order = 0);

public sealed record WorkflowDef(string Name, int Version, string InitialState,
    IReadOnlyList<StateDef> States, IReadOnlyList<TransitionDef> Transitions)
{
    public StateDef State(string key) => States.First(s => s.Key == key);
    public IEnumerable<TransitionDef> From(string key) => Transitions.Where(t => t.From == key).OrderBy(t => t.Order);
}
