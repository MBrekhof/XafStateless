using DevExpress.Data.Filtering;
using DevExpress.ExpressApp;
using DevExpress.ExpressApp.Actions;
using DevExpress.Persistent.Base;
using Microsoft.Extensions.DependencyInjection;
using XafStateless.Module.BusinessObjects;
using XafStateless.Module.Services;

namespace XafStateless.Module.Controllers;

/// <summary>Decide an open task. The outcomes come from the instance's pinned definition, never from code.</summary>
public sealed class WorkflowTaskController : ObjectViewController<DetailView, WorkflowTask>
{
    public const string DecideId = "Workflow.Decide";

    readonly SingleChoiceAction decide;
    WorkflowRuntime? runtime;

    public WorkflowTaskController()
    {
        decide = new SingleChoiceAction(this, DecideId, PredefinedCategory.Edit)
        { Caption = "Decide", ItemType = SingleChoiceActionItemType.ItemIsOperation, ShowItemsOnClick = true };
        decide.Execute += (_, e) =>
        {
            WorkflowCommand.Run(Application, View, c => runtime!.Decide(c, ViewCurrentObject.ID, (string)e.SelectedChoiceActionItem.Data));
            Refill();
        };
    }

    protected override void OnActivated()
    {
        base.OnActivated();
        runtime = Application.ServiceProvider.GetRequiredService<WorkflowRuntime>();
        View.AllowEdit["RuntimeOwned"] = false;   // tasks change only through Decide (the guard refuses any other write, for every role)
        View.CurrentObjectChanged += View_CurrentObjectChanged;
        Refill();
    }

    protected override void OnDeactivated()
    {
        View.CurrentObjectChanged -= View_CurrentObjectChanged;
        View.AllowEdit.RemoveItem("RuntimeOwned");
        runtime = null;
        base.OnDeactivated();
    }

    void View_CurrentObjectChanged(object? sender, EventArgs e) => Refill();

    /// <summary>Outcomes is empty unless the task is Open, current, and the user is in its role: exactly when Decide is active.</summary>
    void Refill()
    {
        decide.Items.Clear();
        if (ViewCurrentObject is { } task)
            foreach (var outcome in runtime!.Outcomes(WorkflowCommand.Caller(Application, View.ObjectSpace), task.ID))
                decide.Items.Add(new ChoiceActionItem(outcome, outcome));
        decide.Active["CanDecide"] = decide.Items.Count > 0;
    }
}

/// <summary>The Tasks navigation list: open tasks of the logged-on user's roles (the roles are read from the user security
/// logged on, so no secured role read is needed).</summary>
public sealed class MyTasksController : ObjectViewController<ListView, WorkflowTask>
{
    const string Key = "XafStateless.MyOpenTasks";

    public MyTasksController()
    {
        TargetViewId = "WorkflowTask_ListView";
        TargetViewNesting = Nesting.Root;
    }

    protected override void OnActivated()
    {
        base.OnActivated();
        var roles = WorkflowCommand.RoleNames(Application);
        View.CollectionSource.Criteria[Key] = roles.Count == 0
            ? CriteriaOperator.Parse("1 = 0")
            : CriteriaOperator.And(new BinaryOperator(nameof(WorkflowTask.State), TaskState.Open), new InOperator(nameof(WorkflowTask.Role), roles));
    }

    protected override void OnDeactivated()
    {
        View.CollectionSource.Criteria.Remove(Key);
        base.OnDeactivated();
    }
}
