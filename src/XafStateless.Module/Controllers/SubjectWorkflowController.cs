using DevExpress.ExpressApp;
using DevExpress.ExpressApp.Actions;
using DevExpress.Persistent.Base;
using Microsoft.Extensions.DependencyInjection;
using XafStateless.Module.BusinessObjects;
using XafStateless.Module.Services;

namespace XafStateless.Module.Controllers;

/// <summary>Start a workflow on a subject, and fire its manual transitions.</summary>
public sealed class SubjectWorkflowController : ViewController<DetailView>
{
    public const string StartId = "Workflow.Start", TransitionId = "Workflow.Transition";

    readonly SingleChoiceAction start, transition;
    WorkflowRuntime? runtime;

    public SubjectWorkflowController()
    {
        TargetObjectType = typeof(IWorkflowSubject);
        start = new SingleChoiceAction(this, StartId, PredefinedCategory.Edit)
        { Caption = "Start workflow", ItemType = SingleChoiceActionItemType.ItemIsOperation };
        start.Execute += (_, e) => Run(c => runtime!.Start(c, SubjectType, Subject!.ID, (string)e.SelectedChoiceActionItem.Data));
        transition = new SingleChoiceAction(this, TransitionId, PredefinedCategory.Edit)
        { Caption = "Transition", ItemType = SingleChoiceActionItemType.ItemIsOperation, ShowItemsOnClick = true };
        transition.Execute += (_, e) => Run(c => runtime!.Fire(c, SubjectType, Subject!.ID, (string)e.SelectedChoiceActionItem.Data));
    }

    IWorkflowSubject? Subject => View.CurrentObject as IWorkflowSubject;
    Type SubjectType => View.ObjectTypeInfo.Type;

    protected override void OnActivated()
    {
        base.OnActivated();
        runtime = Application.ServiceProvider.GetRequiredService<WorkflowRuntime>();
        View.CurrentObjectChanged += View_CurrentObjectChanged;
        Refill();
    }

    protected override void OnDeactivated()
    {
        View.CurrentObjectChanged -= View_CurrentObjectChanged;
        runtime = null;
        base.OnDeactivated();
    }

    void View_CurrentObjectChanged(object? sender, EventArgs e) => Refill();

    void Run(Func<WorkflowCaller, RunResult> command)
    {
        WorkflowCommand.Run(Application, View, command);
        Refill();
    }

    /// <summary>Start: the Published definitions for this subject type, while no workflow runs. Transition: the manual
    /// transitions the runtime says this user may fire now.</summary>
    void Refill()
    {
        start.Items.Clear();
        transition.Items.Clear();
        if (Subject is not { } subject)
        {
            (start.Active["Subject"], transition.Active["Subject"]) = (false, false);
            return;
        }
        (start.Active["Subject"], transition.Active["Subject"]) = (true, true);
        start.Active["NotStarted"] = subject.Workflow == null;
        if (subject.Workflow == null)
        {
            var typeName = SubjectType.FullName;
            foreach (var name in View.ObjectSpace.GetObjectsQuery<WorkflowDefinition>()
                         .Where(d => d.State == DefinitionState.Published && d.TargetTypeName == typeName).Select(d => d.Name).OrderBy(n => n).ToList())
                start.Items.Add(new ChoiceActionItem(name, name));
        }
        else
            foreach (var trigger in runtime!.Available(WorkflowCommand.Caller(Application, View.ObjectSpace), SubjectType, subject.ID).Select(t => t.Trigger).Distinct())
                transition.Items.Add(new ChoiceActionItem(trigger, trigger));
        transition.Active["HasItems"] = transition.Items.Count > 0;
    }
}

/// <summary>The command path every workflow action follows: save the view, act as the current user, refresh, report.</summary>
public static class WorkflowCommand
{
    /// <summary>The logged-on user's role names (none without security).</summary>
    public static IReadOnlyList<string> RoleNames(XafApplication app) =>
        (app.Security?.User as IPermissionPolicyUser)?.Roles.Select(r => r.Name).ToList() ?? [];

    /// <summary>The current user; reads through <paramref name="secured"/>, writes through non-secured command spaces.</summary>
    public static WorkflowCaller Caller(XafApplication app, IObjectSpace secured)
    {
        var (roles, services) = (RoleNames(app), app.ServiceProvider);
        return new(app.Security?.UserName ?? "", roles.Contains, secured,
            () => services.GetRequiredService<INonSecuredObjectSpaceFactory>().CreateNonSecuredObjectSpace(typeof(WorkflowInstance)));
    }

    /// <summary>Commits pending changes of <paramref name="os"/>. A refused save (validation, security, a guard) shows its
    /// message and returns false; the pending changes stay for the user to fix.</summary>
    public static bool Save(XafApplication app, IObjectSpace os)
    {
        if (!os.IsModified) return true;
        try
        {
            os.CommitChanges();
            return true;
        }
        catch (Exception ex)
        {
            app.ShowViewStrategy.ShowMessage(ex.Message, InformationType.Error);
            return false;
        }
    }

    /// <summary>Save, run <paramref name="command"/> as the current user, refresh the view to the committed result, and show
    /// the runtime's message when it refused.</summary>
    public static void Run(XafApplication app, ObjectView view, Func<WorkflowCaller, RunResult> command)
    {
        if (!Save(app, view.ObjectSpace)) return;
        var result = command(Caller(app, view.ObjectSpace));
        view.ObjectSpace.Refresh();
        if (!result.Ok) app.ShowViewStrategy.ShowMessage(result.Message, InformationType.Error);
    }
}
