using DevExpress.ExpressApp;
using DevExpress.ExpressApp.EFCore;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using XafStateless.Demo.Module.BusinessObjects;

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
        // change-tracking, object-space-link and lazy-loading proxies.
        provider = new EFCoreObjectSpaceProvider<DemoEFCoreDbContext>((builder, _) => builder.UseConnectionString(ConnectionString));
        using var ctx = NewDbContext();
        ctx.Database.EnsureCreated();
    }

    public IObjectSpace NewObjectSpace() => provider.CreateObjectSpace();

    public DemoEFCoreDbContext NewDbContext()
    {
        var options = new DbContextOptionsBuilder<DemoEFCoreDbContext>();
        options.UseConnectionString(ConnectionString);
        return new DemoEFCoreDbContext(options.Options);
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
