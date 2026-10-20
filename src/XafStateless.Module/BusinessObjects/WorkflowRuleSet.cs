using System.Collections.ObjectModel;
using System.ComponentModel;
using DevExpress.ExpressApp.DC;
using DevExpress.Persistent.Base;
using DevExpress.Persistent.BaseImpl.EF;
using DevExpress.Persistent.Validation;

namespace XafStateless.Module.BusinessObjects;

[DefaultProperty(nameof(Key)), ImageName("BO_Rules")]
public class WorkflowRuleSet : BaseObject
{
    public virtual WorkflowDefinition? Definition { get; set; }
    [RuleRequiredField] public virtual string Key { get; set; } = "";

    [Aggregated] public virtual IList<WorkflowRuleParam> Params { get; set; } = new ObservableCollection<WorkflowRuleParam>();
    [Aggregated] public virtual IList<WorkflowRule> Rules { get; set; } = new ObservableCollection<WorkflowRule>();
}
