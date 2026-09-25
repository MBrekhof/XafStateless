using Stateless;

namespace XafStateless.Engine;

public sealed record Step(string From, string Trigger, string To);

public sealed class WorkflowMachine
{
    public const int MaxDecisionDepth = 32;

    readonly WorkflowDef _def;
    readonly Func<string> _getState;
    readonly Action<string> _setState;
    readonly Func<string, bool> _criteria;
    readonly Action<string>? _onEnter;
    readonly StateMachine<string, string> _machine;

    public WorkflowMachine(WorkflowDef def, Func<string> getState, Action<string> setState, Func<string, bool> criteria,
                            Action<string>? onEnter = null)
    {
        _def = def;
        _getState = getState;
        _setState = setState;
        _criteria = criteria;
        _onEnter = onEnter;
        _machine = new StateMachine<string, string>(getState, setState);
        foreach (var s in def.States)
        {
            var cfg = _machine.Configure(s.Key);
            foreach (var t in def.From(s.Key))
            {
                Func<bool>? guard = t.Criteria is null ? null : () => criteria(t.Criteria);
                if (t.From == t.To)
                {
                    if (guard is null) cfg.PermitReentry(t.Trigger);
                    else cfg.PermitReentryIf(t.Trigger, guard);
                }
                else
                {
                    if (guard is null) cfg.Permit(t.Trigger, t.To);
                    else cfg.PermitIf(t.Trigger, t.To, guard);
                }
            }
        }
    }

    public IReadOnlyList<TransitionDef> Available()
    {
        var state = _getState();
        if (_def.State(state).Kind is not (StateKind.Manual or StateKind.Task)) return [];
        return _def.From(state).Where(t => t.Criteria is null || _criteria(t.Criteria)).ToList();
    }

    public IReadOnlyList<Step> Fire(string trigger)
    {
        var state = _getState();
        if (!_machine.CanFire(trigger))
            throw new InvalidOperationException($"'{trigger}' is not possible from '{state}'");
        var steps = new List<Step>();
        FireOne(trigger, steps);
        RunDecisions(steps);
        return steps;
    }

    public IReadOnlyList<Step> EnterInitial()
    {
        _setState(_def.InitialState);
        _onEnter?.Invoke(_def.InitialState);
        var steps = new List<Step>();
        RunDecisions(steps);
        return steps;
    }

    public IReadOnlyList<Step> FireTimer()
    {
        var state = _getState();
        if (_def.State(state).Kind != StateKind.Wait)
            throw new InvalidOperationException($"'{state}' is not a Wait state");
        var steps = new List<Step>();
        FireOne(_def.From(state).Single().Trigger, steps);
        RunDecisions(steps);
        return steps;
    }

    void RunDecisions(List<Step> steps)
    {
        var rounds = 0;
        while (_def.State(_getState()).Kind == StateKind.Decision)
        {
            if (rounds++ == MaxDecisionDepth)
                throw new InvalidOperationException($"decision chain exceeded {MaxDecisionDepth} rounds");
            var state = _getState();
            var t = _def.From(state).First(x => x.Criteria is null || _criteria(x.Criteria));
            FireOne(t.Trigger, steps);
        }
    }

    void FireOne(string trigger, List<Step> steps)
    {
        var from = _getState();
        _machine.Fire(trigger);
        var to = _getState();
        steps.Add(new Step(from, trigger, to));
        _onEnter?.Invoke(to);
    }
}
