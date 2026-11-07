using System.ComponentModel;
using System.ComponentModel.DataAnnotations.Schema;
using DevExpress.Persistent.BaseImpl.EF;
using Microsoft.EntityFrameworkCore;
using XafStateless.Demo.Module.BusinessObjects;
using XafStateless.Module.BusinessObjects;

namespace XafStateless.Module.Tests;

/// <summary>A subject whose <see cref="Ordered"/> is stored inverted (value converter v => !v both ways).</summary>
public class ConvertedSubject : BaseObject, IWorkflowSubject
{
    public virtual string Name { get; set; } = "";
    public virtual bool Ordered { get; set; }
    public virtual WorkflowInstance? Workflow { get; set; }
}

/// <summary>A second subject type in its own table: its IDs can collide with <see cref="PurchaseRequest"/> IDs.</summary>
public class OtherSubject : BaseObject, IWorkflowSubject
{
    public virtual string Name { get; set; } = "";
    [Precision(18, 2)] public virtual decimal Amount { get; set; }
    /// <summary>A reference with a CLR foreign key (PurchaseRequest.Buyer's is a shadow one): CARD-2735 locks judge both.</summary>
    public virtual Guid? BuyerId { get; set; }
    [ForeignKey(nameof(BuyerId))] public virtual ApplicationUser? Buyer { get; set; }
    public virtual WorkflowInstance? Workflow { get; set; }
    // CARD-2811: references whose default member is calculated, a reference, or missing
    public virtual Contact? Contact { get; set; }
    public virtual Desk? Desk { get; set; }
    public virtual Bare? Bare { get; set; }
    public virtual Node? Node { get; set; }
    public virtual int? TicketId { get; set; }
    [ForeignKey(nameof(TicketId))] public virtual Ticket? Ticket { get; set; }
    public virtual Agent? Agent { get; set; }
    public virtual Vault? Vault { get; set; }
}

/// <summary>Shown as a hidden member (CARD-2909): {Vault} must not show it.</summary>
[DefaultProperty(nameof(Secret))]
public class Vault : BaseObject
{
    [Browsable(false)] public virtual string Secret { get; set; } = "";
}

/// <summary>A base class that is no user but declares a user's secret (CARD-2909): XAF reports it as the member's owner.</summary>
public abstract class AccountBase : BaseObject
{
    public virtual string StoredPassword { get; set; } = "";
}

/// <summary>A user class shown as its own secret, declared on a non-user base (CARD-2909): {Agent} and
/// {Agent.StoredPassword} must not show it.</summary>
[DefaultProperty(nameof(StoredPassword))]
public class Agent : AccountBase, DevExpress.ExpressApp.Security.ISecurityUser
{
    public virtual string UserName { get; set; } = "";
    public virtual bool IsActive { get; set; } = true;
}

/// <summary>A record with a store-generated (IDENTITY) key: unsaved, it has only a temporary key (CARD-2809).</summary>
public class Ticket
{
    [System.ComponentModel.DataAnnotations.Key] public virtual int ID { get; set; }
    public virtual string Number { get; set; } = "";
}

/// <summary>Shows as a calculated member: its getter reads members no permission check names.</summary>
[DefaultProperty(nameof(FullName))]
public class Contact : BaseObject
{
    public virtual string First { get; set; } = "";
    public virtual string Last { get; set; } = "";
    [NotMapped] public string FullName => First + " " + Last;
}

/// <summary>Shows as a reference (its owner, who shows as UserName).</summary>
[DefaultProperty(nameof(Owner))]
public class Desk : BaseObject
{
    public virtual string Code { get; set; } = "";
    public virtual ApplicationUser? Owner { get; set; }
    /// <summary>Hidden: a placeholder may not read it (CARD-2909).</summary>
    [Browsable(false)] public virtual string ApiKey { get; set; } = "";
}

/// <summary>No declared default member: XAF falls back to the key, so it shows as its ID.</summary>
public class Bare : BaseObject
{
    public virtual int Number { get; set; }
}

/// <summary>Shows as the next node: a node that points at itself is a loop with nothing to show.</summary>
[DefaultProperty(nameof(Next))]
public class Node : BaseObject
{
    public virtual Node? Next { get; set; }
}

/// <summary>The demo context plus the test-only subject types; the TestDb database is created from it.</summary>
public class TestSubjectsDbContext(DbContextOptions<TestSubjectsDbContext> options) : DemoEFCoreDbContext(options)   // an XAF provider builds it too
{
    public DbSet<ConvertedSubject> ConvertedSubjects { get; set; }
    public DbSet<OtherSubject> OtherSubjects { get; set; }
    public DbSet<Contact> Contacts { get; set; }
    public DbSet<Desk> Desks { get; set; }
    public DbSet<Bare> Bares { get; set; }
    public DbSet<Node> Nodes { get; set; }
    public DbSet<Ticket> Tickets { get; set; }
    public DbSet<Agent> Agents { get; set; }
    public DbSet<Vault> Vaults { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.Entity<ConvertedSubject>().Property(s => s.Ordered).HasConversion(v => !v, v => !v);
    }
}
