using System.ComponentModel;
using System.ComponentModel.DataAnnotations.Schema;
using System.Globalization;
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
    /// <summary>A user member: a Task state or a Notify can address it as <c>@Buyer</c> (CARD-2615).</summary>
    public virtual ApplicationUser? Buyer { get; set; }
    /// <summary>A second user member a workflow fills itself: a Set field <c>=[Buyer]</c> when a state is entered (CARD-2895).</summary>
    public virtual ApplicationUser? Approver { get; set; }
    public virtual WorkflowInstance? Workflow { get; set; }
    /// <summary>A calculated member: a Notify placeholder on it is filled when the notification is shown, from what the
    /// viewer's space holds (its getter's reads are invisible to a permission check, so no snapshot; CARD-2788). Kept out of
    /// the views, but browsable: a placeholder may not read a hidden ([Browsable(false)]) member (CARD-2909).</summary>
    [NotMapped, VisibleInListView(false), VisibleInDetailView(false), VisibleInLookupListView(false)] public string Summary => $"{Title} ({Amount.ToString(CultureInfo.InvariantCulture)})";
    /// <summary>A hidden member: no Set field expression or placeholder may read it (CARD-2895, CARD-2909); the tests use it.</summary>
    [NotMapped, Browsable(false)] public string SortKey => Title.ToUpperInvariant();
}
