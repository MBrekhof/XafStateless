using System.Collections.ObjectModel;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations.Schema;
using DevExpress.ExpressApp;
using DevExpress.ExpressApp.DC;
using DevExpress.ExpressApp.Model;
using DevExpress.Persistent.Base;
using DevExpress.Persistent.BaseImpl.EF;
using DevExpress.Persistent.Validation;
using XafStateless.Module.Services;

namespace XafStateless.Module.BusinessObjects;

public enum DefinitionState { Draft, Published, Retired }

[NavigationItem("Workflow")]
[DefaultProperty(nameof(Name))]
public class WorkflowDefinition : BaseObject, IStringLookupSource
{
    [RuleRequiredField] public virtual string Name { get; set; } = "";            // stable key
    [RuleRequiredField] public virtual string TargetTypeName { get; set; } = "";  // full CLR name of an IWorkflowSubject class
    public virtual int Version { get; set; }
    // Lifecycle fields: only NewDraft and Publish set them (DefinitionGuard enforces it); read-only on screen.
    [ModelDefault("AllowEdit", "False")] public virtual DefinitionState State { get; set; }
    [EditorAlias(StringLookup.Single)] public virtual string InitialStateKey { get; set; } = "";
    [ModelDefault("AllowEdit", "False")] public virtual string? PublishedBy { get; set; }
    [ModelDefault("AllowEdit", "False")] public virtual DateTime? PublishedUtc { get; set; }

    [Aggregated] public virtual IList<WorkflowState> States { get; set; } = new ObservableCollection<WorkflowState>();
    [Aggregated] public virtual IList<WorkflowTransition> Transitions { get; set; } = new ObservableCollection<WorkflowTransition>();

    [NotMapped, Browsable(false)] public Type? TargetType => XafTypesInfo.Instance.FindTypeInfo(TargetTypeName)?.Type;

    public IEnumerable<string> LookupValues(string memberName, Func<IReadOnlyList<string>> roleNames) =>
        memberName == nameof(InitialStateKey) ? States.Select(s => s.Key) : [];
}
