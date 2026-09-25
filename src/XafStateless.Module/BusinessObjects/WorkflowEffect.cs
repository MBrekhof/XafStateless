using DevExpress.Persistent.BaseImpl.EF;
using XafStateless.Engine;

namespace XafStateless.Module.BusinessObjects;

public class WorkflowEffect : BaseObject
{
    public virtual WorkflowState? State { get; set; }
    public virtual int Order { get; set; }
    public virtual EffectKind Kind { get; set; }
    public virtual string Target { get; set; } = "";
    public virtual string Value { get; set; } = "";
}
