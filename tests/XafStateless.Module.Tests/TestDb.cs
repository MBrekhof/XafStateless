using DevExpress.ExpressApp;
using DevExpress.ExpressApp.EFCore;
using DevExpress.ExpressApp.EFCore.DataLocking;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using XafStateless.Demo.Module.BusinessObjects;
using XafStateless.Module.Services;

namespace XafStateless.Module.Tests;

/// <summary>A fresh LocalDB database per fixture, built from the demo DbContext; dropped on Dispose.</summary>
public sealed class TestDb : IDisposable
{
    const string Server = @"Data Source=(localdb)\mssqllocaldb;Integrated Security=SSPI;MultipleActiveResultSets=True";

    public string DatabaseName { get; } = $"XafStateless_Test_{Guid.NewGuid():N}";
    public string ConnectionString { get; }
    readonly EFCoreObjectSpaceProvider<DemoEFCoreDbContext> provider;

    public TestDb()
    {
        ConnectionString = $"{Server};Initial Catalog={DatabaseName}";
        // Same options as the demo's WithDbContext: UseConnectionString adds the SQL Server provider plus
        // change-tracking, object-space-link and lazy-loading proxies. AddEFCore/AddSecuredEFCore also add XAF's
        // optimistic-lock interceptor (it bumps OptimisticLockField on save); this ctor does not, so add it here --
        // without it a lost race is a silent last-writer-wins (Task 4b). Lock options: the demo sets none, so the
        // provider defaults (AllFields + Merge) apply here too.
        provider = new EFCoreObjectSpaceProvider<DemoEFCoreDbContext>((builder, _) =>
        {
            builder.UseConnectionString(ConnectionString);
            builder.AddInterceptors(new EFCoreOptimisticLockInterceptor());
        });
        using var ctx = NewSubjectsContext();   // the demo schema plus the test-only subject tables
        ctx.Database.EnsureCreated();
    }

    public IObjectSpace NewObjectSpace() => provider.CreateObjectSpace();

    /// <summary>A second provider on the same database, with extra interceptors; <paramref name="maxBatchSize1"/> makes every
    /// DML statement its own DbCommand. The caller disposes it.</summary>
    public EFCoreObjectSpaceProvider<DemoEFCoreDbContext> NewProvider(bool maxBatchSize1 = false, params IInterceptor[] extra) =>
        new((builder, _) =>
        {
            builder.UseConnectionString(ConnectionString);
            if (maxBatchSize1) builder.UseSqlServer(ConnectionString, o => o.MaxBatchSize(1));
            builder.AddInterceptors(new EFCoreOptimisticLockInterceptor());
            builder.AddInterceptors(extra);
        });

    /// <summary>A caller whose Secured and command spaces are both plain (non-secured) object spaces; security is Task 7's.</summary>
    public WorkflowCaller Caller(string name, params string[] roles) => new(name, roles.Contains, NewObjectSpace(), NewObjectSpace);

    public DemoEFCoreDbContext NewDbContext()
    {
        var options = new DbContextOptionsBuilder<DemoEFCoreDbContext>();
        options.UseConnectionString(ConnectionString);
        return new DemoEFCoreDbContext(options.Options);
    }

    /// <summary>A context that also maps <see cref="ConvertedSubject"/> and <see cref="OtherSubject"/>.</summary>
    public TestSubjectsDbContext NewSubjectsContext()
    {
        var options = new DbContextOptionsBuilder<DemoEFCoreDbContext>();
        options.UseConnectionString(ConnectionString);
        return new TestSubjectsDbContext(options.Options);
    }

    public void Dispose()
    {
        provider.Dispose();
        SqlConnection.ClearAllPools();
        using var conn = new SqlConnection($"{Server};Initial Catalog=master");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"IF DB_ID('{DatabaseName}') IS NOT NULL BEGIN ALTER DATABASE [{DatabaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{DatabaseName}]; END";
        cmd.ExecuteNonQuery();
    }
}
