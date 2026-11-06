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
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using DocumentFormat.OpenXml.Validation;

namespace SqlResultExporter;

internal static class ExcelImportTests
{
    private static int count;
    private static void Check(bool passed, string description)
    {
        if (!passed) throw new Exception("Excel import: " + description);
        count++;
    }

    private static void Reject(Action action, string description)
    {
        try { action(); }
        catch (Exception ex) when (ex is FormatException or InvalidDataException or InvalidOperationException)
        {
            count++;
            return;
        }
        throw new Exception("Expected rejection: " + description);
    }

    internal static int Run(string root)
    {
        count = 0;
        var table = new ImportTable(321, "test]schema", "导入测试");
        var id = new ImportColumn(1, "ID", "int", 4, 10, 0, false, false, "", 0, "", true, false);
        var code = new ImportColumn(2, "编码", "varchar", 12, 0, 0, false, false, "", 936, "Chinese_PRC_CI_AS", false, false);
        var amount = new ImportColumn(3, "金额", "decimal", 17, 38, 18, false, false, "", 0, "", false, false);
        var date = new ImportColumn(4, "登记日期", "date", 3, 10, 0, false, false, "", 0, "", false, false);
        var enabled = new ImportColumn(5, "启用", "bit", 1, 1, 0, false, true, "((1))", 0, "", false, false);
        var note = new ImportColumn(6, "备注", "nvarchar", 64, 0, 0, true, false, "", 0, "", false, false);
        var rowVersion = new ImportColumn(7, "RV", "timestamp", 8, 0, 0, false, false, "", 0, "", true, false);
        var schema = new ImportSchema(table, "local-test", "Fixture", "fixture-1",
            new[] { id, code, amount, date, enabled, note, rowVersion }, new[] { new ImportKey("UX_编码", new[] { code.Id }) },
            true, true, true, false, false);
        string template = Path.Combine(root, "导入模板测试.xlsx");
        ExcelImportWorkbook.Write(template, schema);
        using (var document = SpreadsheetDocument.Open(template, false))
        {
            var errors = new OpenXmlValidator().Validate(document).ToList();
            Check(errors.Count == 0, "Open XML validation: " + string.Join("; ", errors.Select(error => error.Description)));
            var rows = Data(document).Elements<Row>().ToArray();
            Check(rows[0].Elements<Cell>().Select(c => c.InnerText).SequenceEqual(schema.Writable.Select(c => c.Name)),
                "identity and rowversion excluded, real headers");
            Check(rows[1].Elements<Cell>().All(cell => cell.StyleIndex?.Value == 1), "entry row text formatting");
        }
        var blank = ExcelImportWorkbook.Read(template, schema, CancellationToken.None);
        Check(!blank.Valid && blank.DataRows == 0, "blank template excluded");
        Reject(() => ExcelImportWorkbook.Read(template, schema with { Database = "DifferentDb" }, CancellationToken.None),
            "wrong database rejected");
        Reject(() => ExcelImportWorkbook.Read(template, schema with { Columns = schema.Columns.Select(c => c.Id == note.Id ? c with { Length = 128 } : c).ToArray() }, CancellationToken.None),
            "schema drift rejected");
        string filled = Path.Combine(root, "导入已填写测试.xlsx");
        File.Copy(template, filled, true);
        using (var document = SpreadsheetDocument.Open(filled, true))
        {
            var data = Data(document);
            data.Elements<Row>().Last().Remove();
            var row = new Row { RowIndex = 2 };
            row.Append(TextCell("A2", "00123"), TextCell("B2", "12345678901234567890.123456789012345678"),
                TextCell("C2", "2026-11-05"), TextCell("D2", ""), TextCell("E2", ""));
            data.Append(row);
        }
        var plan = ExcelImportWorkbook.Read(filled, schema, CancellationToken.None);
        Check(plan.Valid && plan.DataRows == 1, "valid workbook");
        ExcelImportWorkbook.CheckFileUnchanged(filled, plan);
        Reject(() => ExcelImportWorkbook.CheckFileUnchanged(template, plan), "file changes invalidate preview");
        Check((string)plan.Rows[0].Values[code.Id] == "00123", "leading zero roundtrip");
        Check(((SqlDecimal)plan.Rows[0].Values[amount.Id]).ToString() == "12345678901234567890.123456789012345678",
            "exact decimal38 roundtrip");
        Check((DateTime)plan.Rows[0].Values[date.Id] == new DateTime(2026, 11, 5), "ISO date parsed");
        Check(!plan.Rows[0].Values.ContainsKey(enabled.Id) && plan.Rows[0].Values[note.Id] == DBNull.Value,
            "default omission and null distinguished");
        string sharedPath = Path.Combine(root, "Excel共享字符串测试.xlsx");
        File.Copy(filled, sharedPath, true);
        using (var document = SpreadsheetDocument.Open(sharedPath, true))
        {
            var part = document.WorkbookPart.AddNewPart<SharedStringTablePart>();
            part.SharedStringTable = new SharedStringTable(new SharedStringItem(new Text("编码")),
                new SharedStringItem(new Text("00123")));
            foreach (var row in Data(document).Elements<Row>())
            {
                var cell = row.GetFirstChild<Cell>();
                cell.RemoveAllChildren();
                cell.DataType = CellValues.SharedString;
                cell.CellValue = new CellValue((row.RowIndex.Value - 1).ToString());
            }
        }
        var sharedPlan = ExcelImportWorkbook.Read(sharedPath, schema, CancellationToken.None);
        Check(sharedPlan.Valid && (string)sharedPlan.Rows[0].Values[code.Id] == "00123", "Excel shared text roundtrip");

        var batch = SqlTableImport.Batches(schema, plan.Rows).Single();
        using (var command = SqlTableImport.InsertCommand(null, null, table, batch.Columns, batch.Rows))
        {
            Check(command.CommandText.StartsWith("INSERT INTO [test]]schema].[导入测试]"), "quoted table identifiers");
            Check(!command.CommandText.Contains("[启用]") && !command.CommandText.Contains("00123")
                && command.CommandText.EndsWith("SET @written = @@ROWCOUNT;"), "parameter values, default omission and row count");
            var decimalParameter = command.Parameters.Cast<Microsoft.Data.SqlClient.SqlParameter>()
                .Single(parameter => parameter.SqlDbType == SqlDbType.Decimal);
            Check(decimalParameter.Precision == 38 && decimalParameter.Scale == 18 && decimalParameter.Value is SqlDecimal,
                "wide decimal SQL parameter");
        }
        var many = Enumerable.Range(0, 1001).Select(index => new ImportRow(index + 2, plan.Rows[0].Values)).ToList();
        Check(SqlTableImport.Batches(schema, many).All(b => b.Rows.Length * b.Columns.Length <= 1800),
            "batch stays below parameter limit");
        Check((schema with { HasInsertTrigger = true }).WriteBlock != null && (schema with { CanInspect = false }).WriteBlock != null,
            "unsafe trigger and metadata permission block");

        string errorsPath = Path.Combine(root, "导入错误测试.xlsx");
        File.Copy(filled, errorsPath, true);
        using (var document = SpreadsheetDocument.Open(errorsPath, true))
        {
            var data = Data(document);
            var repeated = new Row { RowIndex = 3 };
            repeated.Append(TextCell("A3", "00123"), TextCell("B3", "1.1"), TextCell("C3", "2026-11-05"));
            data.Append(repeated);
            var wrong = new Row { RowIndex = 5 };
            wrong.Append(new Cell(new CellValue("123")) { CellReference = "A5", DataType = CellValues.Number },
                new Cell(new CellFormula("1+2"), new CellValue("3")) { CellReference = "B5" },
                TextCell("C5", "not-a-date"), TextCell("F5", "unknown"));
            data.Append(wrong);
        }
        var errorsPlan = ExcelImportWorkbook.Read(errorsPath, schema, CancellationToken.None);
        Check(!errorsPlan.Valid && errorsPlan.DataRows == 3, "any invalid row blocks whole plan");
        Check(errorsPlan.Issues.Any(issue => issue.Row == 3 && issue.Reason.Contains("第 2 行")), "duplicate row identified");
        Check(errorsPlan.Issues.Count(issue => issue.Row == 5) == 4, "numeric code, formula, date and unknown cell diagnosed");
        Reject(() => ExcelImportValues.Parse(amount, "1234567890123456", true, false), "Excel rounded exact number rejected");
        Reject(() => ExcelImportValues.Parse(code, "😀", false, false), "ANSI loss rejected");
        Reject(() => ExcelImportValues.Parse(amount, "0.1234567890123456789", false, false), "scale overflow rejected");
        Check((DateTime)ExcelImportValues.Parse(date, "61", true, false) == new DateTime(1900, 3, 1), "Excel date serial");
        DrawWindow(root, schema, plan);
        return count;
    }

    private static SheetData Data(SpreadsheetDocument document)
    {
        var workbook = document.WorkbookPart;
        var sheet = workbook.Workbook.GetFirstChild<Sheets>().Elements<Sheet>().First();
        return ((WorksheetPart)workbook.GetPartById(sheet.Id.Value)).Worksheet.GetFirstChild<SheetData>();
    }

    private static Cell TextCell(string reference, string value) => new(new InlineString(new Text(value)))
    { CellReference = reference, DataType = CellValues.InlineString };

    private static void DrawWindow(string root, ImportSchema schema, ImportPlan plan)
    {
        using var form = new ExcelImportForm("Server=localhost;Database=Fixture;Integrated Security=true", 2, _ => { }, false);
        var type = typeof(ExcelImportForm);
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var commit = (Button)type.GetField("commit", flags).GetValue(form);
        Check(!commit.Enabled, "normal entry starts with write disabled");
        Check(commit.BackColor.GetBrightness() > .8f, "disabled import action uses a light background");
        form.Show();
        Application.DoEvents();
        using (var disabled = new Bitmap(form.Width, form.Height))
        {
            form.DrawToBitmap(disabled, new Rectangle(Point.Empty, disabled.Size));
            disabled.Save(Path.Combine(root, "excel-import-disabled.png"));
        }
        var tables = (ComboBox)type.GetField("tables", flags).GetValue(form);
        tables.Items.Add(schema.Table);
        tables.SelectedIndex = 0;
        ((Label)type.GetField("connection", flags).GetValue(form)).Text = "已连接   local-test / Fixture   ·   1 张可见表";
        ((TextBox)type.GetField("file", flags).GetValue(form)).Text = "导入已填写测试.xlsx";
        type.GetField("checkedSchema", flags).SetValue(form, schema);
        type.GetField("plan", flags).SetValue(form, plan);
        type.GetMethod("ShowPreview", flags).Invoke(form, null);
        type.GetMethod("RefreshActions", flags).Invoke(form, null);
        Check(commit.Enabled, "validated and permitted plan enables confirmation");
        Check(commit.ForeColor == System.Drawing.Color.White && commit.BackColor.GetBrightness() < .5f,
            "active import action uses white text on dark blue");
        Application.DoEvents();
        using var bitmap = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
        bitmap.Save(Path.Combine(root, "excel-import-window.png"));
        form.Hide();
    }
}
