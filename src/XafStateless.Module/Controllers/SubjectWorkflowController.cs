using DevExpress.ExpressApp;
using DevExpress.ExpressApp.Actions;
using DevExpress.ExpressApp.Security;
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
        Frame.GetController<StateLockAppearanceController>()?.Reapply();   // the state may have changed: its locks with it
    }

    /// <summary>Start: the Published definitions for this subject type, while no workflow runs and the user may write the
    /// subject. Transition: the manual transitions the runtime says this user may fire now.</summary>
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
