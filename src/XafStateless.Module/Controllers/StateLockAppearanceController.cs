using DevExpress.ExpressApp;
using DevExpress.ExpressApp.ConditionalAppearance;
using Microsoft.Extensions.DependencyInjection;
using XafStateless.Module.BusinessObjects;
using XafStateless.Module.Services;

namespace XafStateless.Module.Controllers;

/// <summary>
/// Disables the editors of the members locked in the subject's current state (XAF StateMachine's StateAppearance, same hook).
/// Detail views only: a list holds rows in different states, and a rule built from CurrentObject would apply to all of them.
/// UI only; <see cref="StateLockGuard"/> refuses the save. The rule set depends on the current state, so every
/// <see cref="Reapply"/> resets the rules cache before refreshing (dxdocs 113374).
/// A rule exists for every member the pinned definition locks in ANY state, plus every member this view has emitted a rule
/// for since activation, and fails ("1 = 0") where the current state does not lock it. AppearanceController restores an
/// item only through a present rule whose criteria fail (ResetValue); an absent rule leaves the item as it was, so a member
/// unlocked by a state change, or by record navigation to an object on another (or no) definition, would stay disabled.
/// </summary>
public sealed class StateLockAppearanceController : ViewController<DetailView>
{
    AppearanceController? appearance;
    DefinitionStore? store;
    readonly HashSet<string> emitted = [];   // every member given a rule in this view: keeps getting a (reset) rule

    public StateLockAppearanceController() => TargetObjectType = typeof(IWorkflowSubject);

    protected override void OnActivated()
    {
        base.OnActivated();
        store = Application.ServiceProvider.GetRequiredService<DefinitionStore>();
        appearance = Frame.GetController<AppearanceController>();
        if (appearance != null) appearance.CollectAppearanceRules += Appearance_CollectAppearanceRules;
        View.CurrentObjectChanged += View_CurrentObjectChanged;
        Reapply();   // dxdocs 113374: subscribe, then reset the cache and refresh
    }

    protected override void OnDeactivated()
    {
        View.CurrentObjectChanged -= View_CurrentObjectChanged;
        if (appearance != null) appearance.CollectAppearanceRules -= Appearance_CollectAppearanceRules;
        (appearance, store) = (null, null);
        emitted.Clear();
        base.OnDeactivated();
    }

    void View_CurrentObjectChanged(object? sender, EventArgs e) => Reapply();

    /// <summary>Called on activation, on CurrentObjectChanged, and by the workflow actions after each command.</summary>
    internal void Reapply()
    {
        if (appearance == null) return;
        appearance.ResetRulesCache();
        appearance.Refresh();
    }

    void Appearance_CollectAppearanceRules(object? sender, CollectAppearanceRulesEventArgs e)
    {
        // CachedAppearanceRuleProperties: the public IAppearanceRuleProperties whose DeclaringType is settable. AppearanceRule
        // needs it (ValidateByCriteria), and AppearanceAttribute's is always null.
        var (lockable, locked) = Locks();
        emitted.UnionWith(lockable);
        foreach (var member in emitted)
            e.AppearanceRules.Add(new CachedAppearanceRuleProperties(new AppearanceAttribute($"XafStateless.StateLock.{member}")
            {
                TargetItems = member,
                AppearanceItemType = "ViewItem",
                Enabled = false,
                Context = "DetailView",
                Criteria = locked.Contains(member) ? null : "1 = 0",   // unlocked: the failing rule resets the editor
            }) { DeclaringType = View.ObjectTypeInfo.Type });
    }

    /// <summary>From the pinned definition's snapshot: every member any state locks, and those the current state locks.</summary>
    (IReadOnlyList<string> Lockable, IReadOnlyList<string> Locked) Locks() =>
        store == null ? ([], []) : Locks(store, View.ObjectSpace, View.CurrentObject);

    /// <remarks>A subject without a workflow is locked as in the initial state of every Published definition for its type
    /// (CARD-2713, as <see cref="StateLockGuard"/>). A user who can read the subject but not its instance sees the link as null,
    /// so gets those initial locks; the guard judges the persisted link.</remarks>
    internal static (IReadOnlyList<string> Lockable, IReadOnlyList<string> Locked) Locks(DefinitionStore store, IObjectSpace os, object? current)
    {
        switch (current)
        {
            case IWorkflowSubject { Workflow: { Definition: { } d } instance }:
                var states = store.Get(os, d.ID).States;
                return (states.SelectMany(s => s.LockedMembers).Distinct().ToList(),
                        states.FirstOrDefault(s => s.Key == instance.StateKey)?.LockedMembers ?? []);
            case IWorkflowSubject { Workflow: null } subject:
                var defs = store.PublishedFor(os, subject);
                return (defs.SelectMany(def => def.States).SelectMany(s => s.LockedMembers).Distinct().ToList(),
                        defs.SelectMany(def => def.State(def.InitialState).LockedMembers).Distinct().ToList());
            default:
                return ([], []);
        }
    }
}
