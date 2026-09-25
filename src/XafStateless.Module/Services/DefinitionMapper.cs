using XafStateless.Engine;
using XafStateless.Module.BusinessObjects;

namespace XafStateless.Module.Services;

public static class DefinitionMapper
{
    /// <summary>Entities -> the engine's definition. Blank optional texts (a cleared editor) become null.</summary>
    public static WorkflowDef ToDef(WorkflowDefinition d) => new(d.Name, d.Version, d.InitialStateKey,
        d.States.Select(s => new StateDef(s.Key, s.Caption, s.Kind, Opt(s.TaskRole), s.WaitMinutes,
            s.Effects.OrderBy(e => e.Order).Select(e => new EffectDef(e.Kind, e.Target, e.Value)).ToList())).ToList(),
        d.Transitions.Select(t => new TransitionDef(t.FromKey, t.Trigger, t.ToKey, Opt(t.Criteria), Opt(t.RequiredRole), t.Order)).ToList());

    internal static string? Opt(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}
