using DevExpress.ExpressApp;
using DevExpress.ExpressApp.Actions;
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
        UpdateState();
    }

    protected override void OnDeactivated()
    {
        View.CurrentObjectChanged -= View_CurrentObjectChanged;
        foreach (var a in Actions) a.Active.RemoveItem("Designer");
        View.AllowEdit.RemoveItem("NotDraft");
        View.AllowDelete.RemoveItem("NotDraft");
        svc = null;
        base.OnDeactivated();
    }

    void View_CurrentObjectChanged(object? sender, EventArgs e) => UpdateState();

    void UpdateState()
    {
        var draft = ViewCurrentObject?.State is null or DefinitionState.Draft;
        View.AllowEdit["NotDraft"] = draft;
        View.AllowDelete["NotDraft"] = draft;
        publish.Enabled["Draft"] = draft;
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

    void Validate_Execute(object? sender, SimpleActionExecuteEventArgs e)
    {
        if (!WorkflowCommand.Save(Application, ObjectSpace)) return;
        var r = svc!.Validate(ObjectSpace, ViewCurrentObject, Roles());
        Show(r.Ok ? new(true, [$"'{ViewCurrentObject.Name}' v{ViewCurrentObject.Version} is valid."]) : r);
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
        var (steps, error) = svc!.DryRun(ViewCurrentObject, Split(p.Path), Split(p.TrueCriteria).ToHashSet());
        p.Result = string.Join(Environment.NewLine, steps.Select(s => $"{s.From} --{s.Trigger}--> {s.To}")
            .Append(error == null ? "(end of path)" : $"refused: {error}"));
    }

    static string[] Split(string? s) => (s ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
