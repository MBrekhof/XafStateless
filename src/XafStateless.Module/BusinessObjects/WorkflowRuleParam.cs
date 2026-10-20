using System.ComponentModel;
using DevExpress.Persistent.BaseImpl.EF;
using DevExpress.Persistent.Validation;

namespace XafStateless.Module.BusinessObjects;

[DefaultProperty(nameof(Name))]
public class WorkflowRuleParam : BaseObject
{
    public virtual WorkflowRuleSet? RuleSet { get; set; }
    public virtual int Order { get; set; }
    [RuleRequiredField] public virtual string Name { get; set; } = "";
    [RuleRequiredField] public virtual string Expression { get; set; } = "";
}
