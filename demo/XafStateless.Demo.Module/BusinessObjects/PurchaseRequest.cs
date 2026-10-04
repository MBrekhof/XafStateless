using DevExpress.Persistent.Base;
using DevExpress.Persistent.BaseImpl.EF;
using Microsoft.EntityFrameworkCore;
using XafStateless.Module.BusinessObjects;

namespace XafStateless.Demo.Module.BusinessObjects;

[DefaultClassOptions]
public class PurchaseRequest : BaseObject, IWorkflowSubject
{
    public virtual string Title { get; set; } = "";
    [Precision(18, 2)] public virtual decimal Amount { get; set; }
    public virtual bool Ordered { get; set; }
    /// <summary>How often the request was sent back; a counter an Increment step can raise and a Decision can limit (XSM-031).</summary>
    public virtual int? Revisions { get; set; }
    public virtual WorkflowInstance? Workflow { get; set; }
}
