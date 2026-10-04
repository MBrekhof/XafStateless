using DevExpress.ExpressApp;
using DevExpress.ExpressApp.SystemModule;

namespace XafStateless.Module.Controllers;

/// <summary>
/// Marks a non-persistent (<c>[DomainComponent]</c>) class as an entry screen: a page opened straight from a navigation item
/// (View = its DetailView, no ObjectKey) where a user picks a workflow, subjects and options and runs an action that starts
/// work (XSM-033). <see cref="EntryScreenController"/> creates the screen object when the view opens and removes the Save
/// actions; the host's screen controller adds the actions. The host populates the non-persistent object space's additional
/// object spaces (docs 116106) so the screen reaches persistent objects; see integrating.md step 11.
/// </summary>
public interface IEntryScreen;

/// <summary>The activator of docs 113471 for every <see cref="IEntryScreen"/>: a navigation item without ObjectKey, or a deep
/// link on a fresh circuit, opens the DetailView with no object; this creates one and marks it unmodified, so closing the
/// view never asks to save. Save, Save and Close and Save and New are hidden: a screen has nothing to save.</summary>
public sealed class EntryScreenController : ViewController<DetailView>
{
    const string Reason = "EntryScreen";

    public EntryScreenController() => TargetObjectType = typeof(IEntryScreen);

    protected override void OnActivated()
    {
        base.OnActivated();
        if (ObjectSpace is NonPersistentObjectSpace npos && View.CurrentObject == null)
        {
            var screen = ObjectSpace.CreateObject(View.ObjectTypeInfo.Type);
            npos.RemoveFromModifiedObjects(screen);   // CreateObject marks it new; new would mean "Save changes?" on close
            View.CurrentObject = screen;
        }
        foreach (var a in SaveActions()) a.Active[Reason] = false;
    }

    protected override void OnDeactivated()
    {
        foreach (var a in SaveActions()) a.Active.RemoveItem(Reason);
        base.OnDeactivated();
    }

    IEnumerable<DevExpress.ExpressApp.Actions.ActionBase> SaveActions() =>
        Frame.GetController<ModificationsController>() is { } m ? [m.SaveAction, m.SaveAndCloseAction, m.SaveAndNewAction] : [];
}
