using System.ComponentModel;
using System.Runtime.CompilerServices;
using DevExpress.Data.Filtering;
using DevExpress.ExpressApp;
using DevExpress.ExpressApp.DC;
using DevExpress.Persistent.Base;
using XafStateless.Module.BusinessObjects;
using XafStateless.Module.Controllers;
using XafStateless.Module.Services;

namespace XafStateless.Demo.Module.BusinessObjects;

/// <summary>The demo's entry screen (XSM-033): opened from the navigation ("Start requests"), it lists the purchase requests
/// without a workflow, lets the user pick a published definition and a few of them, and starts the workflow for each
/// (StartRequestsController). Non-persistent: a fresh screen per visit, nothing to save.</summary>
[DomainComponent, XafDisplayName("Start requests")]
public class StartRequestsScreen : IEntryScreen, IObjectSpaceLink, INotifyPropertyChanged, IStringLookupSource
{
    IObjectSpace? objectSpace;
    string? definition;
    bool onlyLarge, openAfterStart = true;
    readonly BindingList<PurchaseRequest> requests = [];

    [Browsable(false), DevExpress.ExpressApp.Data.Key] public Guid Oid { get; set; } = Guid.NewGuid();

    /// <summary>A published definition for PurchaseRequest, picked from the lookup.</summary>
    [EditorAlias(StringLookup.Single), XafDisplayName("Workflow")]
    public string? Definition { get => definition; set => Set(ref definition, value); }

    /// <summary>List only the requests over 1000 (the ones that will wait in Cooling-off).</summary>
    [ImmediatePostData, XafDisplayName("Only large amounts")]
    public bool OnlyLarge { get => onlyLarge; set { if (Set(ref onlyLarge, value)) Reload(); } }

    [XafDisplayName("Open the request after starting")]
    public bool OpenAfterStart { get => openAfterStart; set => Set(ref openAfterStart, value); }

    /// <summary>The requests without a workflow; the user selects the ones to start. Shown as a nested list (Client mode).</summary>
    public IList<PurchaseRequest> Requests => requests;

    /// <summary>Set by the non-persistent object space that created the screen; its additional object spaces hold the persistent types.</summary>
    [Browsable(false)]
    public IObjectSpace ObjectSpace
    {
        get => objectSpace!;
        set { objectSpace = value; if (value != null) Reload(); }
    }

    /// <summary>The one reload path: fresh rows from the persistent object space(s), then the same list refilled, so the nested
    /// list sees list-changed events. Called on link, when OnlyLarge changes, after Start and on the view's Refresh.</summary>
    public void Reload()
    {
        if (objectSpace == null) return;
        if (objectSpace is CompositeObjectSpace composite)
            foreach (var additional in composite.AdditionalObjectSpaces) additional.Refresh();   // a Start committed elsewhere: reread the links
        var criteria = CriteriaOperator.Parse(onlyLarge ? "Workflow Is Null And Amount > 1000" : "Workflow Is Null");
        var fresh = objectSpace.GetObjects<PurchaseRequest>(criteria).OrderBy(r => r.Title).ToList();
        requests.RaiseListChangedEvents = false;
        requests.Clear();
        foreach (var r in fresh) requests.Add(r);
        requests.RaiseListChangedEvents = true;
        requests.ResetBindings();
    }

    public IEnumerable<string> LookupValues(string memberName, Func<IReadOnlyList<string>> roleNames) =>
        memberName == nameof(Definition) && objectSpace != null
            ? objectSpace.GetObjects<WorkflowDefinition>(CriteriaOperator.Parse("State = ? And TargetTypeName = ?", DefinitionState.Published, typeof(PurchaseRequest).FullName))
                .Select(d => d.Name).Order()
            : [];

    public event PropertyChangedEventHandler? PropertyChanged;

    bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }
}
