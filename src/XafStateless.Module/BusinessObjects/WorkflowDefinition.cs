using System.Collections.ObjectModel;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations.Schema;
using DevExpress.ExpressApp;
using DevExpress.ExpressApp.DC;
using DevExpress.Persistent.Base;
using DevExpress.Persistent.BaseImpl.EF;

namespace XafStateless.Module.BusinessObjects;

public enum DefinitionState { Draft, Published, Retired }

[DefaultClassOptions]
[DefaultProperty(nameof(Name))]
public class WorkflowDefinition : BaseObject
{
    public virtual string Name { get; set; } = "";            // stable key
    public virtual string TargetTypeName { get; set; } = "";  // full CLR name of an IWorkflowSubject class
    public virtual int Version { get; set; }
    public virtual DefinitionState State { get; set; }
    public virtual string InitialStateKey { get; set; } = "";
    public virtual string? PublishedBy { get; set; }
    public virtual DateTime? PublishedUtc { get; set; }

    [Aggregated] public virtual IList<WorkflowState> States { get; set; } = new ObservableCollection<WorkflowState>();
    [Aggregated] public virtual IList<WorkflowTransition> Transitions { get; set; } = new ObservableCollection<WorkflowTransition>();

    [NotMapped, Browsable(false)] public Type? TargetType => XafTypesInfo.Instance.FindTypeInfo(TargetTypeName)?.Type;
}
