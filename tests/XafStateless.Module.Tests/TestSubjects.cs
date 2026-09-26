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
    public virtual WorkflowInstance? Workflow { get; set; }
}

/// <summary>The demo context plus the test-only subject types; the TestDb database is created from it.</summary>
public class TestSubjectsDbContext(DbContextOptions<DemoEFCoreDbContext> options) : DemoEFCoreDbContext(options)
{
    public DbSet<ConvertedSubject> ConvertedSubjects { get; set; }
    public DbSet<OtherSubject> OtherSubjects { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.Entity<ConvertedSubject>().Property(s => s.Ordered).HasConversion(v => !v, v => !v);
    }
}
