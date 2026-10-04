using DevExpress.ExpressApp;
using DevExpress.ExpressApp.Actions;
using DevExpress.ExpressApp.Editors;
using DevExpress.Persistent.Base;
using Microsoft.Extensions.DependencyInjection;
using XafStateless.Demo.Module.BusinessObjects;
using XafStateless.Module.Controllers;
using XafStateless.Module.Services;

namespace XafStateless.Demo.Module.Controllers;

/// <summary>Start on the entry screen (XSM-033): the picked definition on every selected request, each its own command. The
/// caller carries the user's secured object space (WorkflowCommand.Caller with the subject type), so a user without Write
/// on PurchaseRequest is refused exactly as on the request's own view.</summary>
public sealed class StartRequestsController : ObjectViewController<DetailView, StartRequestsScreen>
{
    public const string StartId = "StartRequests.Start";

    readonly SimpleAction start;

    public StartRequestsController()
    {
        start = new SimpleAction(this, StartId, PredefinedCategory.Edit) { Caption = "Start", ImageName = "Action_Start" };
        start.Execute += Start_Execute;
    }

    protected override void OnActivated()
    {
        base.OnActivated();
        View.ObjectSpace.Reloaded += ObjectSpace_Reloaded;   // the view's Refresh action
    }

    protected override void OnDeactivated()
    {
        View.ObjectSpace.Reloaded -= ObjectSpace_Reloaded;
        base.OnDeactivated();
    }

    void ObjectSpace_Reloaded(object? sender, EventArgs e) => ViewCurrentObject?.Reload();

    void Start_Execute(object? sender, SimpleActionExecuteEventArgs e)
    {
        var screen = ViewCurrentObject;
        if (string.IsNullOrEmpty(screen.Definition)) { Show("Pick a workflow.", InformationType.Warning); return; }
        // Selected ids first: the reload below replaces the rows.
        var ids = View.GetItems<ListPropertyEditor>().Single(x => x.PropertyName == nameof(StartRequestsScreen.Requests))
            .ListView?.SelectedObjects.OfType<PurchaseRequest>().Select(r => r.ID).ToList() ?? [];
        if (ids.Count == 0) { Show("Select at least one request.", InformationType.Warning); return; }

        var runtime = Application.ServiceProvider.GetRequiredService<WorkflowRuntime>();
        var caller = WorkflowCommand.Caller(Application, View.ObjectSpace, typeof(PurchaseRequest));
        // One command per request; a refusal or a user-facing failure of one (an effect past its range, a guard) is reported with
        // the rest and never hides a start that already committed. Other exceptions propagate.
        var results = ids.Select(id =>
        {
            try { return runtime.Start(caller, typeof(PurchaseRequest), id, screen.Definition); }
            catch (UserFriendlyException ex) { return new RunResult(false, ex.Message, []); }
        }).ToList();
        var started = results.Count(r => r.Ok);
        var message = $"Started {started} of {ids.Count}." + (results.FirstOrDefault(r => !r.Ok) is { } refused ? " " + refused.Message : "");
        Show(message, started == ids.Count ? InformationType.Success : InformationType.Warning);
        screen.Reload();

        if (screen.OpenAfterStart && ids.Count == 1 && started == 1) OpenRequest(e, ids[0]);
    }

    /// <summary>The started request in the screen's own tab (the screen is done): a new tab would be skipped when the request is
    /// already open in a stale tab, which would still show it without its workflow. The view owns the object space once created;
    /// until then this method does (the request may be gone by now).</summary>
    void OpenRequest(SimpleActionExecuteEventArgs e, Guid id)
    {
        var os = Application.CreateObjectSpace(typeof(PurchaseRequest));
        try
        {
            if (os.GetObjectByKey<PurchaseRequest>(id) is { } request)
            {
                e.ShowViewParameters.CreatedView = Application.CreateDetailView(os, request);
                e.ShowViewParameters.TargetWindow = TargetWindow.Current;
                return;
            }
        }
        catch { os.Dispose(); throw; }
        os.Dispose();
        Show("The request is no longer there.", InformationType.Warning);
    }

    void Show(string message, InformationType type) => Application.ShowViewStrategy.ShowMessage(message, type);
}
