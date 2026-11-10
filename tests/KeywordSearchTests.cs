using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlTypes;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using MongoDB.Bson;

namespace SqlResultExporter;

internal static class KeywordSearchTests
{
    internal static int Run(string root)
    {
        int count = 0;
        void Check(bool success, string name)
        {
            if (!success) throw new Exception("Keyword search: " + name);
            count++;
        }

        var table = new SearchTable { Schema = "test]schema\"x`", Name = "records]\";`" };
        table.Columns.AddRange(new[]
        {
            new SearchColumn { Name = "text]\"`", Type = "varchar" },
            new SearchColumn { Name = "amount", Type = "decimal" },
            new SearchColumn { Name = "binary", Type = "varbinary" }
        });
        string keyword = "'; DROP TABLE example; -- %_[]";
        foreach (DatabaseKind kind in new[] { DatabaseKind.SqlServer, DatabaseKind.MySQL, DatabaseKind.PostgreSQL, DatabaseKind.Oracle })
        {
            var options = new Options
            {
                Kind = kind, ConnectionString = Providers.Connection(kind, "localhost", "1234", "example", "example",
                    "synthetic", false, false, false, "")
            };
            using var connection = Providers.Create(options);
            using var command = KeywordSearch.Query(connection, kind, table, keyword, false, true, 100);
            Check(!command.CommandText.Contains(keyword) && command.Parameters.Count == 1
                && (string)command.Parameters[0].Value == keyword, kind + " literal parameter");
            Check(command.CommandText.Contains(KeywordSearch.Quote(kind, table.Schema) + "."
                + KeywordSearch.Quote(kind, table.Name)), kind + " quoted target");
            Check(command.CommandText.Contains("__match_1") && !command.CommandText.Contains("__match_2"),
                kind + " searchable scalar and excluded binary");
            Check(command.CommandText.Contains("101"), kind + " one extra row detects truncation");
            using var exact = KeywordSearch.Query(connection, kind, table, "00123", true, false, 2);
            Check(!exact.CommandText.Contains("__match_1"), kind + " text-only selection");
            if (kind == DatabaseKind.Oracle)
                Check(command.CommandText.Contains(")) AND ROWNUM<=101")
                    && exact.CommandText.Contains("LENGTH("), "Oracle all predicates capped and CLOB exact syntax");
        }
        Check(!KeywordSearch.Searchable(DatabaseKind.SqlServer, "timestamp", true), "SQL Server timestamp excluded");
        Check(KeywordSearch.Searchable(DatabaseKind.Oracle, "TIMESTAMP(6) WITH TIME ZONE", true), "Oracle timestamp scalar");

        var recordTable = new SearchTable { Schema = "dbo", Name = "records" };
        recordTable.Columns.AddRange(new[]
        {
            new SearchColumn { Name = "text", Type = "nvarchar" },
            new SearchColumn { Name = "amount", Type = "decimal" },
            new SearchColumn { Name = "empty", Type = "nvarchar" }
        });
        var data = new DataTable();
        data.Columns.Add("text", typeof(string));
        data.Columns.Add("amount", typeof(SqlDecimal));
        data.Columns.Add("empty", typeof(string));
        data.Columns.Add("flag0", typeof(int));
        data.Columns.Add("flag1", typeof(int));
        string longText = new string('中', 70000);
        string precise = "12345678901234567890.123456789012345678";
        data.Rows.Add(longText, SqlDecimal.Parse(precise), DBNull.Value, 1, 1);
        data.Rows.Add("next", SqlDecimal.Parse("1"), DBNull.Value, 1, 0);
        var rows = new List<SearchHit>();
        using (var reader = data.CreateDataReader())
            Check(KeywordSearch.ReadRows(reader, recordTable, recordTable.Columns.Take(2).ToArray(), 1,
                CancellationToken.None, rows.Add), "extra matching row flags partial results");
        Check(rows.Count == 1 && rows[0].Fields.SequenceEqual(new[] { "text", "amount" }), "one record with multiple matched fields");
        Check(rows[0].Values[0] == longText && rows[0].Values[1] == precise && rows[0].Values[2] == null,
            "long text, 38 digits and NULL preserved");
        Check(rows[0].Details.Contains(precise), "full record representation");
        rows.Clear();
        using (var reader = data.CreateDataReader())
            Check(!KeywordSearch.ReadRows(reader, recordTable, recordTable.Columns.Take(2).ToArray(), 2,
                CancellationToken.None, rows.Add) && rows.Count == 2, "exactly at limit remains complete");
        using (var reader = data.CreateDataReader())
        {
            bool cancelled = false;
            try { KeywordSearch.ReadRows(reader, recordTable, recordTable.Columns.Take(2).ToArray(), 2,
                new CancellationToken(true), rows.Add); }
            catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled && rows.Count == 2, "cancelled reader adds no records");
        }
        rows.Clear();
        using (var cancellation = new CancellationTokenSource())
        using (var reader = data.CreateDataReader())
        {
            try
            {
                KeywordSearch.ReadRows(reader, recordTable, recordTable.Columns.Take(2).ToArray(), 2,
                    cancellation.Token, row => { rows.Add(row); cancellation.Cancel(); });
            }
            catch (OperationCanceledException) { }
            Check(rows.Count == 1 && rows[0].Values[0] == longText, "cancel after a hit retains its complete record");
        }

        var document = new BsonDocument
        {
            { "a.b", new BsonArray { new BsonDocument("name", "客户%_[]"), 12345 } },
            { "null", BsonNull.Value }, { "other", "客户" }
        };
        Check(KeywordSearch.MongoFields(document, "%_[]", false, true).Single() == "[\"a.b\"][0][\"name\"]",
            "nested field path preserves literal field names and metacharacters");
        Check(KeywordSearch.MongoFields(document, "12345", true, true).Single() == "[\"a.b\"][1]",
            "Mongo numeric scalar match");
        Check(!KeywordSearch.MongoFields(document, "12345", true, false).Any(), "Mongo text-only excludes number");
        Check(KeywordSearch.MongoFields(document, "客户", true, false).Single() == "[\"other\"]", "Mongo exact matching");

        using var form = new KeywordSearchForm(new Options
        {
            Kind = DatabaseKind.SqlServer, Timeout = 1,
            ConnectionString = Providers.Connection(DatabaseKind.SqlServer, "localhost", "1433", "example", "example",
                "synthetic", false, false, false, "")
        }, message => { }, loadOnShow: false);
        form.Show();
        Application.DoEvents();
        var formType = typeof(KeywordSearchForm);
        object Field(string name) => formType.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(form);
        var selectedTables = (CheckedListBox)Field("tables");
        selectedTables.Items.Add(recordTable, true);
        formType.GetField("loaded", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(form, true);
        formType.GetMethod("RefreshActions", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(form, null);
        ((TextBox)Field("keyword")).Text = "客户";
        var grid = (DataGridView)Field("grid");
        int hitIndex = grid.Rows.Add(recordTable.ToString(), "text, amount", "text=客户资料 | amount=" + precise);
        grid.Rows[hitIndex].Tag = rows[0];
        formType.GetMethod("ShowRecord", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(form, null);
        Check(((TextBox)Field("detail")).Text.Contains(precise), "selected result displays precise complete record");
        Application.DoEvents();
        using var bitmap = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
        bitmap.Save(Path.Combine(root, "keyword-search.png"));
        form.Hide();
        return count;
    }
}
