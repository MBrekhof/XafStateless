using System.ComponentModel;
using System.ComponentModel.DataAnnotations.Schema;
using DevExpress.ExpressApp.Editors;
using DevExpress.Persistent.BaseImpl.EF;

namespace XafStateless.Module.BusinessObjects;

public class WorkflowTransition : BaseObject
{
    public virtual WorkflowDefinition? Definition { get; set; }
    public virtual string FromKey { get; set; } = "";
    public virtual string Trigger { get; set; } = "";
    public virtual string ToKey { get; set; } = "";
    [CriteriaOptions(nameof(TargetType))] public virtual string? Criteria { get; set; }
    public virtual string? RequiredRole { get; set; }
    public virtual int Order { get; set; }

    [NotMapped, Browsable(false)] public Type? TargetType => Definition?.TargetType;
}
