using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Threading;
using MongoDB.Bson;
using MongoDB.Driver;

namespace SqlResultExporter;

internal sealed class SearchColumn
{
    internal string Name, Type;
}

internal sealed class SearchTable
{
    internal string Schema, Name;
    internal readonly List<SearchColumn> Columns = new();
    public override string ToString() => string.IsNullOrEmpty(Schema) ? Name : Schema + "." + Name;
}

internal sealed class SearchHit
{
    internal SearchTable Table;
    internal string[] Fields, Values;
    internal string Document;

    internal string Details => Document ?? System.Text.Json.JsonSerializer.Serialize(
        Table.Columns.Select((column, index) => new { field = column.Name, type = column.Type, value = Values[index] }),
        new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Create(System.Text.Unicode.UnicodeRanges.All)
        });
}

internal sealed class SearchSummary
{
    internal int Rows, FinishedTables, FailedTables, LimitedTables;
    internal bool Cancelled, TotalLimit;
    internal readonly List<string> Messages = new();
}

internal sealed class KeywordSearch
{
    internal const int TotalRows = 1000, MongoScanLimit = 10000;
    private readonly Options options;

    internal KeywordSearch(Options options) => this.options = options;

    internal List<SearchTable> Tables(CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(options.Timeout));
        token = deadline.Token;
        if (options.Kind == DatabaseKind.MongoDB)
        {
            using var client = MongoClient();
            var database = client.GetDatabase(new MongoUrl(options.ConnectionString).DatabaseName);
            using var cursor = database.ListCollectionNames(new ListCollectionNamesOptions
            {
                AuthorizedCollections = true
            }, token);
            return cursor.ToEnumerable(token).Where(name => !name.StartsWith("system.", StringComparison.Ordinal))
                .OrderBy(name => name, StringComparer.Ordinal).Select(name => new SearchTable { Name = name }).ToList();
        }
        using var connection = Providers.Create(options);
        connection.OpenAsync(token).GetAwaiter().GetResult();
        using var command = connection.CreateCommand();
        command.CommandTimeout = options.Timeout;
        command.CommandText = MetadataSql(options.Kind);
        using var cancel = token.Register(() => Cancel(command));
        using var reader = command.ExecuteReader();
        var tables = new List<SearchTable>();
        while (reader.Read())
        {
            token.ThrowIfCancellationRequested();
            string schema = reader.GetString(0), name = reader.GetString(1);
            var table = tables.LastOrDefault();
            if (table == null || table.Schema != schema || table.Name != name)
            {
                table = new SearchTable { Schema = schema, Name = name };
                tables.Add(table);
            }
            table.Columns.Add(new SearchColumn { Name = reader.GetString(2), Type = reader.GetString(3) });
        }
        return tables;
    }

    private static string MetadataSql(DatabaseKind kind)
    {
        if (kind == DatabaseKind.Oracle)
            return @"SELECT c.OWNER,c.TABLE_NAME,c.COLUMN_NAME,c.DATA_TYPE FROM ALL_TAB_COLUMNS c
JOIN ALL_TABLES t ON t.OWNER=c.OWNER AND t.TABLE_NAME=c.TABLE_NAME
WHERE c.OWNER NOT IN ('SYS','SYSTEM','XDB','CTXSYS','MDSYS','WMSYS','OUTLN','DBSNMP')
ORDER BY c.OWNER,c.TABLE_NAME,c.COLUMN_ID";
        string filter = kind switch
        {
            DatabaseKind.SqlServer => "c.TABLE_CATALOG=DB_NAME() AND c.TABLE_SCHEMA NOT IN ('sys','INFORMATION_SCHEMA')",
            DatabaseKind.MySQL => "c.TABLE_SCHEMA=DATABASE()",
            DatabaseKind.PostgreSQL => "c.table_schema<>'information_schema' AND left(c.table_schema,3)<>'pg_'",
            _ => throw new NotSupportedException()
        };
        return @"SELECT c.TABLE_SCHEMA,c.TABLE_NAME,c.COLUMN_NAME,c.DATA_TYPE
FROM INFORMATION_SCHEMA.COLUMNS c JOIN INFORMATION_SCHEMA.TABLES t
ON t.TABLE_CATALOG=c.TABLE_CATALOG AND t.TABLE_SCHEMA=c.TABLE_SCHEMA AND t.TABLE_NAME=c.TABLE_NAME
WHERE t.TABLE_TYPE='BASE TABLE' AND " + filter + " ORDER BY c.TABLE_SCHEMA,c.TABLE_NAME,c.ORDINAL_POSITION";
    }

    internal SearchSummary Run(IReadOnlyList<SearchTable> tables, string keyword, bool exact,
        bool includeScalars, int perTable, CancellationToken token, Action<SearchHit> hit, Action<string> progress)
    {
        if (string.IsNullOrWhiteSpace(keyword) || keyword.Length > 512 || keyword.Contains('\0'))
            throw new InvalidOperationException("关键字须为 1 到 512 个字符，且不能只含空白或包含空字符。");
        if (tables.Count == 0 || perTable < 1 || perTable > 500)
            throw new InvalidOperationException("请选择表或集合，每表结果上限须为 1 到 500。");
        var summary = new SearchSummary();
        foreach (var table in tables)
        {
            if (token.IsCancellationRequested) { summary.Cancelled = true; break; }
            int limit = Math.Min(perTable, TotalRows - summary.Rows);
            if (limit == 0) { summary.TotalLimit = true; break; }
            progress("正在查找 " + table);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(options.Timeout));
            try
            {
                bool limited = options.Kind == DatabaseKind.MongoDB
                    ? SearchMongo(table, keyword, exact, includeScalars, limit, timeout.Token, Add)
                    : SearchSql(table, keyword, exact, includeScalars, limit, timeout.Token, Add, summary);
                summary.FinishedTables++;
                if (limited)
                {
                    summary.LimitedTables++;
                    summary.Messages.Add(table + "：已达到记录或扫描上限，结果可能不完整。");
                }
            }
            catch (Exception) when (token.IsCancellationRequested)
            {
                summary.Cancelled = true;
                break;
            }
            catch (Exception) when (timeout.IsCancellationRequested)
            {
                summary.FailedTables++;
                summary.Messages.Add(table + "：超时，保留已找到的记录。");
            }
            catch (Exception ex) when (ex is DbException or MongoException or NotSupportedException or InvalidCastException)
            {
                summary.FailedTables++;
                summary.Messages.Add(table + "：读取失败，请核对权限、字段类型与连接。错误类型 " + ex.GetType().Name);
            }
        }
        return summary;

        void Add(SearchHit row) { hit(row); summary.Rows++; }
    }

    internal static bool Searchable(DatabaseKind kind, string type, bool includeScalars)
    {
        type = type.ToUpperInvariant();
        string[] textTypes = { "CHAR", "NCHAR", "VARCHAR", "NVARCHAR", "VARCHAR2", "NVARCHAR2", "TEXT",
            "NTEXT", "TINYTEXT", "MEDIUMTEXT", "LONGTEXT", "CLOB", "NCLOB", "CHARACTER",
            "CHARACTER VARYING", "NAME", "CITEXT", "JSON", "JSONB", "XML" };
        if (textTypes.Contains(type)) return kind != DatabaseKind.Oracle || type != "XML";
        if (!includeScalars) return false;
        string[] scalars = { "INT", "INTEGER", "BIGINT", "SMALLINT", "TINYINT", "MEDIUMINT",
            "DECIMAL", "NUMERIC", "NUMBER", "MONEY", "SMALLMONEY", "FLOAT", "REAL", "DOUBLE",
            "DOUBLE PRECISION", "BINARY_FLOAT", "BINARY_DOUBLE", "DATE", "DATETIME", "DATETIME2",
            "SMALLDATETIME", "DATETIMEOFFSET", "TIME", "YEAR", "TIMESTAMP", "BOOLEAN", "BOOL",
            "UUID", "UNIQUEIDENTIFIER", "ENUM", "SET", "ARRAY" };
        // SQL Server timestamp/rowversion and bit are binary types for this purpose.
        if (kind == DatabaseKind.SqlServer && type == "TIMESTAMP") return false;
        if (kind == DatabaseKind.SqlServer && type == "BIT") return true;
        return scalars.Contains(type) || type.StartsWith("TIMESTAMP(", StringComparison.Ordinal)
            || type.StartsWith("TIMESTAMP ", StringComparison.Ordinal)
            || type.StartsWith("TIME ", StringComparison.Ordinal)
            || kind == DatabaseKind.Oracle && type.StartsWith("INTERVAL ", StringComparison.Ordinal);
    }

    internal static string Quote(DatabaseKind kind, string name) => kind switch
    {
        DatabaseKind.SqlServer => "[" + name.Replace("]", "]]") + "]",
        DatabaseKind.MySQL => "`" + name.Replace("`", "``") + "`",
        _ => "\"" + name.Replace("\"", "\"\"") + "\""
    };

    internal static DbCommand Query(DbConnection connection, DatabaseKind kind, SearchTable table,
        string keyword, bool exact, bool includeScalars, int limit)
    {
        var command = connection.CreateCommand();
        try
        {
            var searchable = table.Columns.Where(column => Searchable(kind, column.Type, includeScalars)).ToArray();
            if (searchable.Length == 0) throw new InvalidOperationException("没有可检索字段。");
            string parameter = kind == DatabaseKind.Oracle ? ":kw" : "@kw";
            var predicates = searchable.Select(column =>
            {
                string name = Quote(kind, column.Name);
                string text = kind switch
                {
                    DatabaseKind.SqlServer => "CONVERT(nvarchar(max)," + name + ")",
                    DatabaseKind.MySQL => "CAST(" + name + " AS CHAR)",
                    DatabaseKind.PostgreSQL => "CAST(" + name + " AS text)",
                    _ => name
                };
                string contains = kind switch
                {
                    DatabaseKind.SqlServer => "CHARINDEX(" + parameter + "," + text + ")",
                    DatabaseKind.MySQL => "LOCATE(" + parameter + "," + text + ")",
                    DatabaseKind.PostgreSQL => "strpos(" + text + "," + parameter + ")",
                    _ => "INSTR(" + text + "," + parameter + ")"
                };
                return !exact ? contains + ">0"
                    : kind == DatabaseKind.Oracle
                        ? contains + "=1 AND LENGTH(" + text + ")=LENGTH(" + parameter + ")"
                        : text + "=" + parameter;
            }).ToArray();
            string columns = string.Join(",", table.Columns.Select(column => Quote(kind, column.Name)));
            string flags = string.Join(",", predicates.Select((predicate, index) =>
                "CASE WHEN (" + predicate + ") THEN 1 ELSE 0 END AS " + Quote(kind, "__match_" + index)));
            string target = Quote(kind, table.Schema) + "." + Quote(kind, table.Name);
            string select = "SELECT " + (kind == DatabaseKind.SqlServer ? "TOP (" + (limit + 1) + ") " : "");
            command.CommandText = select + columns + "," + flags + " FROM " + target
                + " WHERE ((" + string.Join(") OR (", predicates) + "))"
                + (kind == DatabaseKind.Oracle ? " AND ROWNUM<=" + (limit + 1) : "")
                + (kind is DatabaseKind.MySQL or DatabaseKind.PostgreSQL ? " LIMIT " + (limit + 1) : "");
            var value = command.CreateParameter();
            value.ParameterName = "kw";
            value.DbType = DbType.String;
            value.Value = keyword;
            command.Parameters.Add(value);
            Providers.Configure(command);
            return command;
        }
        catch { command.Dispose(); throw; }
    }

    private bool SearchSql(SearchTable table, string keyword, bool exact, bool scalars, int limit,
        CancellationToken token, Action<SearchHit> add, SearchSummary summary)
    {
        var searchable = table.Columns.Where(column => Searchable(options.Kind, column.Type, scalars)).ToArray();
        int skipped = table.Columns.Count - searchable.Length;
        if (skipped > 0) summary.Messages.Add(table + "：" + skipped + " 个字段未参与匹配（类型或范围限制）。");
        if (searchable.Length == 0) return false;
        using var connection = Providers.Create(options);
        connection.OpenAsync(token).GetAwaiter().GetResult();
        using var command = Query(connection, options.Kind, table, keyword, exact, scalars, limit);
        command.CommandTimeout = options.Timeout;
        using var cancel = token.Register(() => Cancel(command));
        using var reader = command.ExecuteReader();
        return ReadRows(reader, table, searchable, limit, token, add);
    }

    internal static bool ReadRows(DbDataReader reader, SearchTable table, SearchColumn[] searchable, int limit,
        CancellationToken token, Action<SearchHit> add)
    {
        int count = 0;
        while (reader.Read())
        {
            token.ThrowIfCancellationRequested();
            if (count == limit) return true;
            var raw = new object[reader.FieldCount];
            Providers.ReadValues(reader, raw);
            var fields = searchable.Where((column, index) => Util.Value(raw[table.Columns.Count + index]) == "1")
                .Select(column => column.Name).ToArray();
            add(new SearchHit { Table = table, Fields = fields,
                Values = raw.Take(table.Columns.Count).Select(Util.Value).ToArray() });
            count++;
        }
        return false;
    }

    private MongoClient MongoClient()
    {
        var settings = MongoClientSettings.FromConnectionString(options.ConnectionString);
        settings.ApplicationName = "DatabaseResultExporter";
        settings.RetryWrites = false;
        return new MongoClient(settings);
    }

    private bool SearchMongo(SearchTable table, string keyword, bool exact, bool scalars, int limit,
        CancellationToken token, Action<SearchHit> add)
    {
        using var client = MongoClient();
        var database = client.GetDatabase(new MongoUrl(options.ConnectionString).DatabaseName);
        using var cursor = database.GetCollection<BsonDocument>(table.Name).FindSync(
            FilterDefinition<BsonDocument>.Empty, new FindOptions<BsonDocument>
            {
                BatchSize = 100, Limit = MongoScanLimit + 1, MaxTime = TimeSpan.FromSeconds(options.Timeout)
            }, token);
        int scanned = 0, found = 0;
        while (cursor.MoveNext(token))
        {
            foreach (var document in cursor.Current)
            {
                token.ThrowIfCancellationRequested();
                if (scanned++ == MongoScanLimit) return true;
                var fields = MongoFields(document, keyword, exact, scalars).ToArray();
                if (fields.Length == 0) continue;
                if (found++ == limit) return true;
                add(new SearchHit { Table = table, Fields = fields, Document = MongoExport.Json(document) });
            }
        }
        return false;
    }

    internal static IEnumerable<string> MongoFields(BsonValue value, string keyword, bool exact,
        bool scalars, string path = "")
    {
        if (value.IsBsonDocument)
        {
            foreach (var field in value.AsBsonDocument)
                foreach (string match in MongoFields(field.Value, keyword, exact, scalars,
                    path + "[" + System.Text.Json.JsonSerializer.Serialize(field.Name) + "]"))
                    yield return match;
        }
        else if (value.IsBsonArray)
        {
            for (int index = 0; index < value.AsBsonArray.Count; index++)
                foreach (string match in MongoFields(value[index], keyword, exact, scalars, path + "[" + index + "]"))
                    yield return match;
        }
        else if (value.IsString || scalars && (value.IsNumeric || value.IsObjectId || value.IsBoolean || value.BsonType == BsonType.DateTime))
        {
            string text = value.IsString ? value.AsString : value.ToString();
            if (exact ? text == keyword : text.Contains(keyword, StringComparison.Ordinal)) yield return path;
        }
    }

    private static void Cancel(DbCommand command)
    {
        try { command.Cancel(); }
        catch (DbException) { }
        catch (InvalidOperationException) { }
    }
}
