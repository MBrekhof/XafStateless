using DevExpress.ExpressApp;
using XafStateless.Engine;
using XafStateless.Module.BusinessObjects;

namespace XafStateless.Module.Services;

public static class DefinitionMapper
{
    /// <summary>Entities -> the engine's definition. Blank optional texts (a cleared editor) become null. With
    /// <paramref name="os"/>, children marked for deletion in it are left out (a live, unsaved editing session).</summary>
    public static WorkflowDef ToDef(WorkflowDefinition d, IObjectSpace? os = null)
    {
        bool Live(object o) => os == null || !(os.IsObjectToDelete(o) || os.IsDeletedObject(o));   // before a commit only IsObjectToDelete is set
        // A cleared editor leaves null in a required text; the engine gets "" and reports it, never a null (XSM-027).
        static string Req(string? s) => s ?? "";
        return new(Req(d.Name), d.Version, Req(d.InitialStateKey),
            d.States.Where(Live).Select(s => new StateDef(Req(s.Key), Req(s.Caption), s.Kind, Opt(s.TaskRole), s.WaitMinutes,
                s.Effects.Where(Live).OrderBy(e => e.Order).Select(e => new EffectDef(e.Kind, e.Target, e.Value)).ToList(), Members(s.LockedMembers))).ToList(),
            d.Transitions.Where(Live).Select(t => new TransitionDef(Req(t.FromKey), Req(t.Trigger), Req(t.ToKey), Opt(t.Criteria), Opt(t.RequiredRole), t.Order)).ToList());
    }

    /// <summary>The engine's definition -> a new Draft graph in <paramref name="os"/> (not committed), for seeding a host
    /// or a test; publish it through <see cref="DefinitionService.Publish"/>.</summary>
    public static WorkflowDefinition ToDraft(IObjectSpace os, WorkflowDef def, string targetTypeName)
    {
        var d = os.CreateObject<WorkflowDefinition>();
        (d.Name, d.TargetTypeName, d.Version, d.State, d.InitialStateKey) = (def.Name, targetTypeName, def.Version, DefinitionState.Draft, def.InitialState);
        foreach (var s in def.States)
        {
            var state = os.CreateObject<WorkflowState>();
            (state.Key, state.Caption, state.Kind, state.TaskRole, state.WaitMinutes) = (s.Key, s.Caption, s.Kind, s.TaskRole, s.WaitMinutes);
            state.LockedMembers = s.LockedMembers.Count == 0 ? null : string.Join(StringLookup.Separator, s.LockedMembers);
            foreach (var e in s.Effects)
            {
                var effect = os.CreateObject<WorkflowEffect>();
                (effect.Order, effect.Kind, effect.Target, effect.Value) = (state.Effects.Count, e.Kind, e.Target, e.Value);
                state.Effects.Add(effect);
            }
            d.States.Add(state);
        }
        foreach (var t in def.Transitions)
        {
            var tr = os.CreateObject<WorkflowTransition>();
            (tr.FromKey, tr.Trigger, tr.ToKey, tr.Criteria, tr.RequiredRole, tr.Order) = (t.From, t.Trigger, t.To, t.Criteria, t.RequiredRole, t.Order);
            d.Transitions.Add(tr);
        }
        return d;
    }

    internal static string? Opt(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    /// <summary>A <see cref="StringLookup.Separator"/>-joined member list -> its names (blanks dropped).</summary>
    internal static IReadOnlyList<string> Members(string? joined) =>
        (joined ?? "").Split(StringLookup.Separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
