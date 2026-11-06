using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;

namespace SqlResultExporter;

internal sealed record ImportTable(int Id, string Schema, string Name)
{
    internal string SqlName => Quote(Schema) + "." + Quote(Name);
    internal static string Quote(string identifier) => "[" + identifier.Replace("]", "]]") + "]";
    public override string ToString() => SqlName;
}

internal sealed record ImportColumn(int Id, string Name, string Type, short Length, byte Precision,
    byte Scale, bool Nullable, bool HasDefault, string DefaultDefinition, int CodePage,
    string Collation, bool Generated, bool Encrypted)
{
    internal bool Required => !Nullable && !HasDefault;
    internal int CharacterLength => Length < 0 ? -1 : Type.StartsWith("n", StringComparison.Ordinal) ? Length / 2 : Length;
    internal string Description => Type switch
    {
        "decimal" or "numeric" => $"{Type}({Precision},{Scale})",
        "datetime2" or "datetimeoffset" or "time" => $"{Type}({Scale})",
        "char" or "varchar" or "nchar" or "nvarchar" => $"{Type}({(Length < 0 ? "max" : CharacterLength)})",
        _ => Type
    };
}

internal sealed record ImportKey(string Name, int[] Columns);
internal sealed record ImportSchema(ImportTable Table, string Server, string Database, string Identity,
    ImportColumn[] Columns, ImportKey[] Keys, bool CanSelect, bool CanInsert, bool CanInspect,
    bool HasInsertTrigger, bool RestrictedTable)
{
    internal ImportColumn[] Writable => Columns.Where(column => !column.Generated).ToArray();
    internal string Stamp => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
        new { Server, Database, Table, Identity, Columns, Keys }))));

    internal string WriteBlock => !CanSelect || !CanInsert || !CanInspect
        ? "正式导入需要目标表的 SELECT、INSERT 和 VIEW DEFINITION 权限。"
        : HasInsertTrigger ? "目标表有启用的 INSERT 触发器，首版暂停此表导入。"
        : RestrictedTable ? "首版仅支持普通 SQL Server 表。"
        : null;
}

internal sealed record ImportIssue(int Row, string Column, string Reason);
internal sealed record ImportRow(int Number, Dictionary<int, object> Values);
internal sealed class ImportPlan
{
    internal readonly List<ImportRow> Rows = new();
    internal readonly List<ImportIssue> Issues = new();
    internal int DataRows;
    internal string SourceHash;
    internal bool Valid => DataRows > 0 && Rows.Count == DataRows && Issues.Count == 0;
}

internal sealed class ImportWriteException : Exception
{
    internal ImportWriteException(string message, Exception inner = null) : base(message, inner) { }
}

internal sealed class SqlTableImport
{
    private readonly string connectionString;
    private readonly int timeout;

    internal SqlTableImport(string connectionString, int timeout)
    {
        this.connectionString = connectionString;
        this.timeout = timeout;
    }

    private SqlCommand Command(SqlConnection connection, string sql, SqlTransaction transaction = null)
        => new(sql, connection, transaction) { CommandTimeout = timeout };

    internal async Task<List<ImportTable>> Tables(CancellationToken token)
    {
        using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(token);
        using var command = Command(connection, """
            SELECT t.object_id,s.name,t.name
            FROM sys.tables t JOIN sys.schemas s ON s.schema_id=t.schema_id
            WHERE t.is_ms_shipped=0 ORDER BY s.name,t.name;
            """);
        using var reader = await command.ExecuteReaderAsync(token);
        var tables = new List<ImportTable>();
        while (await reader.ReadAsync(token))
            tables.Add(new ImportTable(reader.GetInt32(0), reader.GetString(1), reader.GetString(2)));
        return tables;
    }

    internal async Task<ImportSchema> Schema(ImportTable table, CancellationToken token)
    {
        using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(token);
        return await ReadSchema(connection, null, table, token);
    }

    private async Task<ImportSchema> ReadSchema(SqlConnection connection, SqlTransaction transaction,
        ImportTable table, CancellationToken token)
    {
        using var command = Command(connection, """
            SELECT CONCAT(CONVERT(nvarchar(30),t.create_date,126), N'/', DB_ID()),
              ISNULL(HAS_PERMS_BY_NAME(@qualified, 'OBJECT', 'SELECT'),0),
              ISNULL(HAS_PERMS_BY_NAME(@qualified, 'OBJECT', 'INSERT'),0),
              ISNULL(HAS_PERMS_BY_NAME(@qualified, 'OBJECT', 'VIEW DEFINITION'),0),
              CASE WHEN EXISTS (SELECT 1 FROM sys.triggers tr
                JOIN sys.trigger_events ev ON ev.object_id=tr.object_id
                WHERE tr.parent_id=t.object_id AND tr.is_disabled=0 AND ev.type_desc='INSERT') THEN 1 ELSE 0 END,
              CASE WHEN t.is_memory_optimized=1 OR t.is_external=1 OR t.is_filetable=1
                OR t.temporal_type=1 THEN 1 ELSE 0 END
            FROM sys.tables t JOIN sys.schemas s ON s.schema_id=t.schema_id
            WHERE t.object_id=@id AND s.name=@schema AND t.name=@table AND t.is_ms_shipped=0;

            SELECT c.column_id,c.name,ISNULL(TYPE_NAME(c.system_type_id),N'unsupported'),
              c.max_length,c.precision,c.scale,c.is_nullable,c.default_object_id,
              ISNULL(OBJECT_DEFINITION(c.default_object_id),N''),
              ISNULL(CONVERT(int,COLLATIONPROPERTY(c.collation_name,'CodePage')),0),
              ISNULL(c.collation_name,N''),
              CASE WHEN c.is_identity=1 OR c.is_computed=1 OR c.generated_always_type<>0
                OR c.is_hidden=1 OR c.system_type_id=189 THEN 1 ELSE 0 END,
              CASE WHEN c.encryption_type IS NULL THEN 0 ELSE 1 END
            FROM sys.columns c WHERE c.object_id=@id ORDER BY c.column_id;

            SELECT i.name,ic.column_id
            FROM sys.indexes i JOIN sys.index_columns ic
              ON ic.object_id=i.object_id AND ic.index_id=i.index_id
            WHERE i.object_id=@id AND i.is_unique=1 AND i.is_disabled=0 AND i.has_filter=0
              AND i.is_hypothetical=0 AND ic.key_ordinal>0 ORDER BY i.index_id,ic.key_ordinal;
            """, transaction);
        command.Parameters.Add("@id", SqlDbType.Int).Value = table.Id;
        command.Parameters.Add("@qualified", SqlDbType.NVarChar, 520).Value = table.SqlName;
        command.Parameters.Add("@schema", SqlDbType.NVarChar, 128).Value = table.Schema;
        command.Parameters.Add("@table", SqlDbType.NVarChar, 128).Value = table.Name;
        using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token))
            throw new InvalidOperationException("目标表已变更或当前账号无法查看。请重新加载表列表。");
        string identity = reader.GetString(0);
        bool canSelect = reader.GetInt32(1) == 1, canInsert = reader.GetInt32(2) == 1;
        bool canInspect = reader.GetInt32(3) == 1, trigger = reader.GetInt32(4) == 1, restricted = reader.GetInt32(5) == 1;
        var columns = new List<ImportColumn>();
        await reader.NextResultAsync(token);
        while (await reader.ReadAsync(token))
        {
            columns.Add(new ImportColumn(reader.GetInt32(0), reader.GetString(1), reader.GetString(2),
                reader.GetInt16(3), reader.GetByte(4), reader.GetByte(5), reader.GetBoolean(6),
                reader.GetInt32(7) != 0, reader.GetString(8), reader.GetInt32(9), reader.GetString(10),
                reader.GetInt32(11) == 1, reader.GetInt32(12) == 1));
        }
        var keyColumns = new List<(string Name, int Column)>();
        await reader.NextResultAsync(token);
        while (await reader.ReadAsync(token))
            keyColumns.Add((reader.GetString(0), reader.GetInt32(1)));
        var keys = keyColumns.GroupBy(entry => entry.Name)
            .Select(group => new ImportKey(group.Key, group.Select(entry => entry.Column).ToArray())).ToArray();
        return new ImportSchema(table, connection.DataSource, connection.Database, identity,
            columns.ToArray(), keys, canSelect, canInsert, canInspect, trigger, restricted);
    }

    internal async Task CheckExisting(ImportSchema schema, ImportPlan plan, CancellationToken token)
    {
        if (!schema.CanSelect || !plan.Valid || schema.Keys.Length == 0) return;
        using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(token);
        var current = await ReadSchema(connection, null, schema.Table, token);
        if (current.Stamp != schema.Stamp) throw new InvalidOperationException("表结构已变更，请重新下载模板。");
        var columns = schema.Writable.ToDictionary(column => column.Id);
        foreach (var key in schema.Keys.Where(key => key.Columns.All(columns.ContainsKey)))
        {
            var candidates = plan.Rows.Where(row => key.Columns.All(row.Values.ContainsKey));
            int batchSize = Math.Min(100, 1800 / key.Columns.Length);
            foreach (var batch in candidates.Chunk(batchSize))
            {
                using var command = Command(connection, "");
                var checks = new List<string>();
                for (int index = 0; index < batch.Length; index++)
                {
                    var row = batch[index];
                    var conditions = new List<string>();
                    foreach (int id in key.Columns)
                    {
                        var column = columns[id];
                        string name = ImportTable.Quote(column.Name);
                        object value = row.Values[id];
                        if (value == DBNull.Value) conditions.Add(name + " IS NULL");
                        else
                        {
                            string parameter = AddValue(command, column, value);
                            if (!string.IsNullOrEmpty(column.Collation))
                                parameter += " COLLATE " + ImportTable.Quote(column.Collation);
                            conditions.Add(name + "=" + parameter);
                        }
                    }
                    checks.Add($"SELECT {row.Number} AS ExcelRow WHERE EXISTS (SELECT 1 FROM {schema.Table.SqlName} WHERE {string.Join(" AND ", conditions)})");
                }
                command.CommandText = string.Join(" UNION ALL ", checks);
                using var reader = await command.ExecuteReaderAsync(token);
                while (await reader.ReadAsync(token))
                {
                    plan.Issues.Add(new ImportIssue(reader.GetInt32(0), key.Name, "目标表已有相同唯一键。"));
                    if (plan.Issues.Count >= 2000) return;
                }
            }
        }
    }

    internal async Task<int> Insert(ImportSchema schema, ImportPlan plan, CancellationToken token)
    {
        if (!plan.Valid) throw new InvalidOperationException("请先完成校验。");
        if (schema.WriteBlock != null) throw new InvalidOperationException(schema.WriteBlock);
        using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(token);
        using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
        bool committing = false;
        try
        {
            // Keep table metadata and trigger state stable until the confirmed import commits.
            using (var tableLock = Command(connection, $"SELECT TOP (1) 1 FROM {schema.Table.SqlName} WITH (TABLOCKX,HOLDLOCK);", transaction))
                await tableLock.ExecuteScalarAsync(token);
            var current = await ReadSchema(connection, transaction, schema.Table, token);
            if (current.Stamp != schema.Stamp) throw new InvalidOperationException("表结构已变更，请重新下载模板。");
            if (current.WriteBlock != null) throw new InvalidOperationException(current.WriteBlock);

            int written = 0;
            foreach (var batch in Batches(current, plan.Rows))
            {
                using var command = InsertCommand(connection, transaction, current.Table, batch.Columns, batch.Rows);
                command.CommandTimeout = timeout;
                try { await command.ExecuteNonQueryAsync(token); }
                catch (SqlException ex)
                {
                    throw new ImportWriteException($"Excel 第 {batch.Rows[0].Number} 至 {batch.Rows[^1].Number} 行写入失败（SQL Server 错误 {ex.Number}）。", ex);
                }
                if (command.Parameters["@written"].Value is not int count || count != batch.Rows.Length)
                    throw new ImportWriteException($"Excel 第 {batch.Rows[0].Number} 至 {batch.Rows[^1].Number} 行实际写入数不符，可能有唯一键冲突。");
                written += count;
            }
            token.ThrowIfCancellationRequested();
            committing = true;
            await transaction.CommitAsync(CancellationToken.None);
            return written;
        }
        catch (Exception ex)
        {
            if (committing) throw new ImportWriteException("提交状态未知。请核对目标表后再决定是否重新导入。", ex);
            try { if (transaction.Connection != null) await transaction.RollbackAsync(CancellationToken.None); }
            catch (Exception rollback)
            {
                throw new ImportWriteException("未能确认回滚状态。请先核对目标表。", new AggregateException(ex, rollback));
            }
            if (ex is OperationCanceledException) throw;
            token.ThrowIfCancellationRequested();
            throw new ImportWriteException(ex is SqlException sql
                ? $"SQL Server 错误 {sql.Number}，本次导入已回滚。"
                : ex.Message + " 本次导入已回滚。", ex);
        }
    }

    internal static IEnumerable<(ImportColumn[] Columns, ImportRow[] Rows)> Batches(
        ImportSchema schema, List<ImportRow> rows)
    {
        var columns = schema.Writable;
        int offset = 0;
        while (offset < rows.Count)
        {
            var first = rows[offset];
            var shape = columns.Where(column => first.Values.ContainsKey(column.Id)).ToArray();
            int limit = shape.Length == 0 ? 1 : Math.Min(500, 1800 / shape.Length);
            int end = offset + 1;
            while (end < rows.Count && end - offset < limit && rows[end].Values.Count == shape.Length
                && shape.All(column => rows[end].Values.ContainsKey(column.Id))) end++;
            yield return (shape, rows.GetRange(offset, end - offset).ToArray());
            offset = end;
        }
    }

    internal static SqlCommand InsertCommand(SqlConnection connection, SqlTransaction transaction,
        ImportTable table, ImportColumn[] columns, ImportRow[] rows)
    {
        var command = new SqlCommand("", connection, transaction);
        string insert = "INSERT INTO " + table.SqlName;
        if (columns.Length == 0) insert += " DEFAULT VALUES";
        else
        {
            string names = string.Join(",", columns.Select(column => ImportTable.Quote(column.Name)));
            var values = rows.Select(row => "(" + string.Join(",", columns.Select(column =>
                AddValue(command, column, row.Values[column.Id]))) + ")").ToArray();
            insert += " (" + names + ") VALUES " + string.Join(",", values);
        }
        command.Parameters.Add("@written", SqlDbType.Int).Direction = ParameterDirection.Output;
        command.CommandText = insert + "; SET @written = @@ROWCOUNT;";
        return command;
    }

    private static string AddValue(SqlCommand command, ImportColumn column, object value)
    {
        string name = "@v" + command.Parameters.Count.ToString(CultureInfo.InvariantCulture);
        SqlDbType type = ExcelImportValues.ParameterType(column.Type);
        var parameter = command.Parameters.Add(name, type);
        if (type == SqlDbType.NVarChar)
            parameter.Size = value is string text && text.Length > 4000 ? -1 : Math.Max(1, (value as string)?.Length ?? 0);
        if (type == SqlDbType.VarBinary)
            parameter.Size = value is byte[] bytes && bytes.Length > 8000 ? -1 : Math.Max(1, (value as byte[])?.Length ?? 0);
        if (type == SqlDbType.Decimal) parameter.Precision = column.Precision;
        if (type is SqlDbType.Decimal or SqlDbType.DateTime2 or SqlDbType.DateTimeOffset or SqlDbType.Time)
            parameter.Scale = column.Scale;
        parameter.Value = value;
        return name;
    }
}
