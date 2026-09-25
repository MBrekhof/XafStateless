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
/// </summary>
public sealed class StateLockAppearanceController : ViewController<DetailView>
{
    AppearanceController? appearance;
    DefinitionStore? store;

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
        foreach (var member in LockedMembers())
            e.AppearanceRules.Add(new CachedAppearanceRuleProperties(new AppearanceAttribute($"XafStateless.StateLock.{member}")
            {
                TargetItems = member,
                AppearanceItemType = "ViewItem",
                Enabled = false,
                Context = "DetailView",
            }) { DeclaringType = View.ObjectTypeInfo.Type });
    }

    /// <summary>The locks of the current object's state, from the pinned definition's snapshot.</summary>
    IReadOnlyList<string> LockedMembers() =>
        View.CurrentObject is IWorkflowSubject { Workflow: { Definition: { } d } instance } && store != null
            ? store.Get(View.ObjectSpace, d.ID).States.FirstOrDefault(s => s.Key == instance.StateKey)?.LockedMembers ?? []
            : [];
}
