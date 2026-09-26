using DevExpress.ExpressApp;
using DevExpress.ExpressApp.Actions;
using DevExpress.ExpressApp.Security;
using DevExpress.Persistent.Base;
using Microsoft.Extensions.DependencyInjection;
using XafStateless.Module.BusinessObjects;
using XafStateless.Module.Services;

namespace XafStateless.Module.Controllers;

/// <summary>Start a workflow on a subject, fire its manual transitions, and open its current task.</summary>
public sealed class SubjectWorkflowController : ViewController<DetailView>
{
    public const string StartId = "Workflow.Start", TransitionId = "Workflow.Transition", OpenTaskActionId = "Workflow.OpenTask";

    readonly SingleChoiceAction start, transition;
    readonly SimpleAction openTask;
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
        openTask = new SimpleAction(this, OpenTaskActionId, PredefinedCategory.Edit) { Caption = "Open task" };
        openTask.Execute += OpenTask_Execute;
    }

    IWorkflowSubject? Subject => View.CurrentObject as IWorkflowSubject;
    Type SubjectType => View.ObjectTypeInfo.Type;

    protected override void OnActivated()
    {
        base.OnActivated();
        runtime = Application.ServiceProvider.GetRequiredService<WorkflowRuntime>();
        View.CurrentObjectChanged += View_CurrentObjectChanged;
        View.ObjectSpace.Committed += ObjectSpace_Changed;
        View.ObjectSpace.Reloaded += ObjectSpace_Changed;   // Refresh() raises Reloaded (IObjectSpace has no Refreshed)
        Refill();
    }

    protected override void OnDeactivated()
    {
        View.CurrentObjectChanged -= View_CurrentObjectChanged;
        View.ObjectSpace.Committed -= ObjectSpace_Changed;
        View.ObjectSpace.Reloaded -= ObjectSpace_Changed;
        runtime = null;
        base.OnDeactivated();
    }

    void View_CurrentObjectChanged(object? sender, EventArgs e) => Refill();
    void ObjectSpace_Changed(object? sender, EventArgs e) => Refill();

    void Run(Func<WorkflowCaller, RunResult> command)
    {
        WorkflowCommand.Run(Application, View, command);
        Refill();
        Frame.GetController<StateLockAppearanceController>()?.Reapply();   // the state may have changed: its locks with it
    }

    /// <summary>The subject's Open task for its instance's current state, if <paramref name="secured"/> may read it. A database
    /// query (projected, so no cached entity answers it): a stale space still sees a task closed elsewhere.</summary>
    internal static Guid? OpenTaskId(IObjectSpace secured, IWorkflowSubject subject)
    {
        if (subject.Workflow is not { } instance) return null;
        var id = instance.ID;
        return secured.GetObjectsQuery<WorkflowTask>()
            .Where(t => t.State == TaskState.Open && t.Instance!.ID == id && t.StateKey == t.Instance.StateKey)
            .Select(t => (Guid?)t.ID).FirstOrDefault();
    }

    /// <summary>The execute path: a new secured space from <paramref name="newSecuredSpace"/>, the availability check re-run in it
    /// (the task may have closed since the button was shown), and the task loaded there. Returns the space and task (the caller
    /// hands the space to a root view, which owns it) or null, having disposed the space.</summary>
    internal static (IObjectSpace Space, WorkflowTask Task)? PrepareOpen(Func<IObjectSpace> newSecuredSpace, IWorkflowSubject subject)
    {
        var os = newSecuredSpace();
        try
        {
            if (OpenTaskId(os, subject) is { } id && os.GetObjectByKey<WorkflowTask>(id) is { } task) return (os, task);
        }
        catch
        {
            os.Dispose();
            throw;
        }
        os.Dispose();
        return null;
    }

    void OpenTask_Execute(object? sender, SimpleActionExecuteEventArgs e)
    {
        if (Subject is { } subject && PrepareOpen(() => Application.CreateObjectSpace(typeof(WorkflowTask)), subject) is var (os, task))
        {
            try { e.ShowViewParameters.CreatedView = Application.CreateDetailView(os, task, true); }
            catch { os.Dispose(); throw; }
            return;
        }
        Application.ShowViewStrategy.ShowMessage("This task is no longer open.", InformationType.Warning);
        Refill();
    }

    /// <summary>Start: the Published definitions for this subject type, while no workflow runs and the user may write the
    /// subject. Transition: the manual transitions the runtime says this user may fire now. Open task: see OpenTaskId.</summary>
    void Refill()
    {
        start.Items.Clear();
        transition.Items.Clear();
        if (Subject is not { } subject)
        {
            (start.Active["Subject"], transition.Active["Subject"], openTask.Active["Subject"]) = (false, false, false);
            return;
        }
        (start.Active["Subject"], transition.Active["Subject"], openTask.Active["Subject"]) = (true, true, true);
        openTask.Active["HasOpenTask"] = OpenTaskId(View.ObjectSpace, subject) != null;
        start.Active["NotStarted"] = subject.Workflow == null;
        // The runtime refuses Start without Write on the subject; Transition hides itself (Available is empty then).
        start.Active["Write"] = Application.Security is not IRequestSecurityStrategy security || security.CanWrite(View.ObjectSpace, subject);
        if (subject.Workflow == null)
        {
            var typeName = SubjectType.FullName;
            foreach (var name in View.ObjectSpace.GetObjectsQuery<WorkflowDefinition>()
                         .Where(d => d.State == DefinitionState.Published && d.TargetTypeName == typeName).Select(d => d.Name).OrderBy(n => n).ToList())
                start.Items.Add(new ChoiceActionItem(name, name));
            start.Active["HasItems"] = start.Items.Count > 0;
        }
        else
            foreach (var trigger in runtime!.Available(WorkflowCommand.Caller(Application, View.ObjectSpace), SubjectType, subject.ID).Select(t => t.Trigger).Distinct())
                transition.Items.Add(new ChoiceActionItem(trigger, trigger));
        transition.Active["HasItems"] = transition.Items.Count > 0;
    }
}
