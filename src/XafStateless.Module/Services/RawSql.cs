using DevExpress.ExpressApp.EFCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;

namespace XafStateless.Module.Services;

/// <summary>
/// Persisted-value reads for the guard and the store: plain ADO on the context's own connection and current transaction.
/// Not LINQ, so a secured context's SecurityQueryCompiler never rewrites them (a member-level Read deny would otherwise
/// substitute default values, e.g. State = Draft). Table and column names come from the EF model; soft-deleted rows
/// (GCRecord != 0) are skipped. SQL Server syntax, like the module's filtered indexes.
/// </summary>
internal static class RawSql
{
    const int ChunkSize = 2000;   // SQL Server allows 2100 parameters per command

    /// <summary>The given <paramref name="columns"/> (property names) of every live row whose <paramref name="keyProperty"/> is in <paramref name="keys"/>.</summary>
    public static List<object?[]> Rows<T>(DbContext ctx, string keyProperty, IEnumerable<Guid> keys, params string[] columns) =>
        Rows(ctx, typeof(T), keyProperty, keys, columns);

    /// <summary>The same for an entity type known only at run time (a host's subject type).</summary>
    public static List<object?[]> Rows(DbContext ctx, Type entityType, string keyProperty, IEnumerable<Guid> keys, params string[] columns)
    {
        var rows = new List<object?[]>();
        var list = keys.Distinct().ToList();
        if (list.Count == 0) return rows;
        var et = ctx.Model.FindEntityType(entityType) ?? throw new InvalidOperationException($"{entityType.Name} is not mapped.");
        var table = StoreObjectIdentifier.Table(et.GetTableName()!, et.GetSchema());
        string Col(string property) => $"[{et.FindProperty(property)!.GetColumnName(table)}]";
        var from = (et.GetSchema() is { } schema ? $"[{schema}]." : "") + $"[{et.GetTableName()}]";
        var live = et.FindProperty(EFCoreDeferredDeletionInterceptor.GCRecordFieldName) != null
            ? $" AND {Col(EFCoreDeferredDeletionInterceptor.GCRecordFieldName)} = 0" : "";

        ctx.Database.OpenConnection();
        try
        {
            foreach (var chunk in list.Chunk(ChunkSize))
            {
                using var cmd = ctx.Database.GetDbConnection().CreateCommand();
                cmd.Transaction = ctx.Database.CurrentTransaction?.GetDbTransaction();
                var names = new List<string>();
                foreach (var key in chunk)
                {
                    var p = cmd.CreateParameter();
                    p.ParameterName = $"@p{names.Count}";
                    p.Value = key;
                    cmd.Parameters.Add(p);
                    names.Add(p.ParameterName);
                }
                cmd.CommandText = $"SELECT {string.Join(", ", columns.Select(Col))} FROM {from} WHERE {Col(keyProperty)} IN ({string.Join(", ", names)}){live}";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var row = new object?[reader.FieldCount];
                    reader.GetValues(row!);
                    for (var i = 0; i < row.Length; i++) if (row[i] is DBNull) row[i] = null;
                    rows.Add(row);
                }
            }
        }
        finally { ctx.Database.CloseConnection(); }
        return rows;
    }

    /// <summary>The foreign-key property behind <typeparamref name="T"/>'s reference <paramref name="navigation"/>.</summary>
    public static string ForeignKey<T>(DbContext ctx, string navigation) =>
        ctx.Model.FindEntityType(typeof(T))!.FindNavigation(navigation)!.ForeignKey.Properties[0].Name;
}
