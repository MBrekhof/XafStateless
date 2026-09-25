using System.Collections.ObjectModel;
using DevExpress.ExpressApp.DC;
using DevExpress.Persistent.BaseImpl.EF;
using XafStateless.Engine;

namespace XafStateless.Module.BusinessObjects;

public class WorkflowState : BaseObject
{
    public virtual WorkflowDefinition? Definition { get; set; }
    public virtual string Key { get; set; } = "";
    public virtual string Caption { get; set; } = "";
    public virtual StateKind Kind { get; set; }
    public virtual string? TaskRole { get; set; }
    public virtual int? WaitMinutes { get; set; }

    [Aggregated] public virtual IList<WorkflowEffect> Effects { get; set; } = new ObservableCollection<WorkflowEffect>();
}
