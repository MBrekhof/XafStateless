using System.Collections.ObjectModel;
using System.ComponentModel.DataAnnotations.Schema;
using DevExpress.ExpressApp;
using DevExpress.ExpressApp.ConditionalAppearance;
using DevExpress.ExpressApp.DC;
using DevExpress.ExpressApp.Editors;
using DevExpress.Persistent.Base;
using DevExpress.Persistent.BaseImpl.EF;
using DevExpress.Persistent.Validation;
using XafStateless.Engine;
using XafStateless.Module.Services;

namespace XafStateless.Module.BusinessObjects;

[Appearance("WorkflowState.TaskRole", AppearanceItemType = "ViewItem", TargetItems = nameof(TaskRole), Context = "DetailView",
    Criteria = "Kind != ##Enum#XafStateless.Engine.StateKind,Task#", Visibility = ViewItemVisibility.Hide)]
[Appearance("WorkflowState.WaitMinutes", AppearanceItemType = "ViewItem", TargetItems = nameof(WaitMinutes), Context = "DetailView",
    Criteria = "Kind != ##Enum#XafStateless.Engine.StateKind,Wait#", Visibility = ViewItemVisibility.Hide)]
// XSM-027: a state with a validation problem is colored in the definition's States list.
[Appearance("WorkflowState.Problem", AppearanceItemType = "ViewItem", TargetItems = "*", Context = "ListView",
    Criteria = "Not IsNullOrEmpty([Problem])", FontColor = "#7f0000", BackColor = "#fde2e2")]
[ImageName("BO_State")]
public class WorkflowState : BaseObject, IStringLookupSource
{
    public virtual WorkflowDefinition? Definition { get; set; }
    [RuleRequiredField] public virtual string Key { get; set; } = "";
    [RuleRequiredField] public virtual string Caption { get; set; } = "";
    [ImmediatePostData] public virtual StateKind Kind { get; set; }
    [EditorAlias(StringLookup.Single)] public virtual string? TaskRole { get; set; }
    public virtual int? WaitMinutes { get; set; }
    /// <summary>Subject members locked in this state, joined with <see cref="StringLookup.Separator"/> (member names cannot contain it).</summary>
    [EditorAlias(StringLookup.Multi)] public virtual string? LockedMembers { get; set; }

    [Aggregated] public virtual IList<WorkflowEffect> Effects { get; set; } = new ObservableCollection<WorkflowEffect>();

    /// <summary>This state's findings from the latest validation of its definition in this editing session; never stored.</summary>
    [NotMapped, VisibleInDetailView(false)]
    public string? Problem => StateProblems.Get(this);

    /// <summary>TaskRole: role names and the subject's user members (<c>@Member</c>). LockedMembers: the subject's members a
    /// state may lock, references included (CARD-2735).</summary>
    public IEnumerable<string> LookupValues(string memberName, Func<IReadOnlyList<string>> roleNames) => memberName switch
    {
        nameof(TaskRole) => [.. UserTargets(Definition), .. roleNames()],
        nameof(LockedMembers) => XafTypesInfo.Instance.FindTypeInfo(Definition?.TargetTypeName ?? "") is { } target
            ? WorkflowMembers.Lockable(target).Select(m => m.Name) : [],
        _ => [],
    };

    /// <summary>The definition's subject's user members as recipients: <c>@Member</c>.</summary>
    internal static IEnumerable<string> UserTargets(WorkflowDefinition? definition) =>
        XafTypesInfo.Instance.FindTypeInfo(definition?.TargetTypeName ?? "") is { } target ? WorkflowMembers.Users(target).Select(m => "@" + m.Name) : [];
}
