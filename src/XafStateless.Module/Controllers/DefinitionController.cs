using DevExpress.ExpressApp;
using DevExpress.ExpressApp.Actions;
using DevExpress.ExpressApp.Editors;
using DevExpress.Persistent.Base;
using Microsoft.Extensions.DependencyInjection;
using XafStateless.Module.BusinessObjects;
using XafStateless.Module.Services;

namespace XafStateless.Module.Controllers;

/// <summary>The definition lifecycle for designers: new version, validate, publish, dry run. A non-Draft definition's view
/// is read-only, so a Published definition is refused on screen, not first at Save (the guard still refuses underneath).</summary>
public sealed class DefinitionController : ObjectViewController<DetailView, WorkflowDefinition>
{
    public const string NewVersionId = "Workflow.NewVersion", ValidateId = "Workflow.Validate", PublishId = "Workflow.Publish", DryRunId = "Workflow.DryRun";
    static readonly string[] DesignerRoles = ["Administrators", "WorkflowDesigners"];

    readonly SimpleAction newVersion, validate, publish;
    readonly PopupWindowShowAction dryRun;
    DefinitionService? svc;
    IReadOnlyList<string> roles = [];
    DefinitionResult? last;        // the latest validation of the current definition in this view (XSM-027)
    bool validating, again;        // reentrancy: a change raised while validating reruns once afterwards

    public DefinitionController()
    {
        newVersion = new SimpleAction(this, NewVersionId, PredefinedCategory.Edit) { Caption = "New version" };
        newVersion.Execute += NewVersion_Execute;
        validate = new SimpleAction(this, ValidateId, PredefinedCategory.Edit) { Caption = "Validate" };
        validate.Execute += Validate_Execute;
        publish = new SimpleAction(this, PublishId, PredefinedCategory.Edit) { Caption = "Publish" };
        publish.Execute += Publish_Execute;
        dryRun = new PopupWindowShowAction(this, DryRunId, PredefinedCategory.Edit) { Caption = "Dry run" };
        dryRun.CustomizePopupWindowParams += DryRun_CustomizePopupWindowParams;
        dryRun.Execute += DryRun_Execute;
    }

    protected override void OnActivated()
    {
        base.OnActivated();
        svc = Application.ServiceProvider.GetRequiredService<DefinitionService>();
        var designer = WorkflowCommand.RoleNames(Application).Intersect(DesignerRoles).Any();
        foreach (var a in Actions) a.Active["Designer"] = designer;
        View.CurrentObjectChanged += View_CurrentObjectChanged;
        ObjectSpace.ObjectChanged += ObjectSpace_Changed;
        ObjectSpace.ObjectDeleted += ObjectSpace_Changed;
        ObjectSpace.Committed += ObjectSpace_Reset;
        ObjectSpace.ModifiedChanged += ObjectSpace_Reset;   // a rollback clears Modified
        ObjectSpace.Reloaded += ObjectSpace_Reset;
        roles = Roles();
        UpdateState();
    }

    protected override void OnDeactivated()
    {
        View.CurrentObjectChanged -= View_CurrentObjectChanged;
        ObjectSpace.ObjectChanged -= ObjectSpace_Changed;
        ObjectSpace.ObjectDeleted -= ObjectSpace_Changed;
        ObjectSpace.Committed -= ObjectSpace_Reset;
        ObjectSpace.ModifiedChanged -= ObjectSpace_Reset;
        ObjectSpace.Reloaded -= ObjectSpace_Reset;
        foreach (var a in Actions) a.Active.RemoveItem("Designer");
        View.AllowEdit.RemoveItem("NotDraft");
        View.AllowDelete.RemoveItem("NotDraft");
        svc = null;
        base.OnDeactivated();
    }

    void View_CurrentObjectChanged(object? sender, EventArgs e) => UpdateState();
    void ObjectSpace_Changed(object? sender, EventArgs e) => Revalidate();
    void ObjectSpace_Reset(object? sender, EventArgs e) { roles = Roles(); UpdateState(); }

    void UpdateState()
    {
        var draft = ViewCurrentObject?.State is null or DefinitionState.Draft;
        View.AllowEdit["NotDraft"] = draft;
        View.AllowDelete["NotDraft"] = draft;
        publish.Enabled["Draft"] = draft;
        Revalidate();
    }

    /// <summary>Validates the definition as it is on screen, saved or not, and shows the one result everywhere (XSM-027):
    /// Publish and Dry run are enabled only when it is valid (the tooltip names the first finding), each state's problem
    /// colors its row in the States list, and the diagram marks the problem states. The service re-validates on Publish and
    /// Dry run regardless. Edits saved in a separate state or transition tab reach this view when it refreshes.</summary>
    void Revalidate()
    {
        if (validating) { again = true; return; }
        validating = true;
        try
        {
            do
            {
                again = false;
                var d = ViewCurrentObject;
                last = d == null || svc == null ? null : svc.Validate(ObjectSpace, d, roles);
                Show(d);
            } while (again);
        }
        finally { validating = false; }
    }

    void Show(WorkflowDefinition? d)
    {
        var valid = last?.Ok == true;
        var reason = valid || last == null ? null : $"Fix first: {last.Messages[0]}";
        foreach (var a in new ActionBase[] { publish, dryRun })
        {
            a.Enabled["Valid"] = valid;
            a.ToolTip = reason;
        }
        if (d != null)
            foreach (var s in d.States)
            {
                var mine = last?.Findings?.Where(f => f.State == s.Key).Select(f => f.Message).ToList() ?? [];
                StateProblems.Set(s, mine.Count == 0 ? null : string.Join("; ", mine));
            }
        foreach (var listener in View.Items.OfType<IDefinitionValidationListener>()) listener.OnValidated(last);
        (View.FindItem(nameof(WorkflowDefinition.States)) as ListPropertyEditor)?.ListView?.Editor?.Refresh();
    }

    IReadOnlyList<string> Roles() => StringLookup.RoleNames(Application.ServiceProvider)();

    void Show(DefinitionResult r) =>
        Application.ShowViewStrategy.ShowMessage(string.Join("; ", r.Messages), r.Ok ? InformationType.Success : InformationType.Error);

    void NewVersion_Execute(object? sender, SimpleActionExecuteEventArgs e)
    {
        if (!WorkflowCommand.Save(Application, ObjectSpace)) return;
        var os = Application.CreateObjectSpace(typeof(WorkflowDefinition));
        try
        {
            var draft = svc!.NewDraft(os, ViewCurrentObject.Name);
            os.CommitChanges();
            e.ShowViewParameters.CreatedView = Application.CreateDetailView(os, draft);
        }
        catch (Exception ex)
        {
            os.Dispose();
            Application.ShowViewStrategy.ShowMessage(ex.Message, InformationType.Error);
        }
    }

    /// <summary>Validates what is on screen; no save needed (XSM-027).</summary>
    void Validate_Execute(object? sender, SimpleActionExecuteEventArgs e)
    {
        Revalidate();
        Show(last is { Ok: true } ? new(true, [$"'{ViewCurrentObject.Name}' v{ViewCurrentObject.Version} is valid."]) : last!);
    }

    void Publish_Execute(object? sender, SimpleActionExecuteEventArgs e)
    {
        if (!WorkflowCommand.Save(Application, ObjectSpace)) return;
        Show(svc!.Publish(ObjectSpace, ViewCurrentObject, Application.Security?.UserName ?? "", Roles()));
        UpdateState();
    }

    void DryRun_CustomizePopupWindowParams(object? sender, CustomizePopupWindowParamsEventArgs e)
    {
        var os = Application.CreateObjectSpace(typeof(DryRunParameters));
        e.View = Application.CreateDetailView(os, os.CreateObject<DryRunParameters>());
        e.DialogController.SaveOnAccept = false;
    }

    /// <summary>Runs the path and writes the steps into Result; the popup stays open so the result can be read.</summary>
    void DryRun_Execute(object? sender, PopupWindowShowActionExecuteEventArgs e)
    {
        e.CanCloseWindow = false;
        var p = (DryRunParameters)e.PopupWindowViewCurrentObject;
        if (!WorkflowCommand.Save(Application, ObjectSpace)) return;
        var (steps, error) = svc!.DryRun(ObjectSpace, ViewCurrentObject, roles, Split(p.Path), Split(p.TrueCriteria).ToHashSet());
        p.Result = string.Join(Environment.NewLine, steps.Select(s => $"{s.From} --{s.Trigger}--> {s.To}")
            .Append(error == null ? "(end of path)" : $"refused: {error}"));
    }

    static string[] Split(string? s) => (s ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
