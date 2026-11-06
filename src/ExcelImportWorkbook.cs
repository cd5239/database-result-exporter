using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace SqlResultExporter;

internal static class ExcelImportWorkbook
{
    internal const int MaxRows = 5000;
    private const string DataSheet = "填写数据";
    private const string FieldSheet = "字段说明";
    private const string MetadataSheet = "_导入信息";
    private const string Format = "DatabaseResultExporter.Import.1";

    internal static ImportColumn[] Columns(ImportSchema schema)
    {
        var columns = schema.Writable;
        if (columns.Length is 0 or > 1024)
            throw new InvalidOperationException("目标表的可填写列数不在 1 到 1024 范围内。");
        var unsupported = columns.FirstOrDefault(column => !ExcelImportValues.Supported(column));
        if (unsupported != null)
            throw new InvalidOperationException($"列 {unsupported.Name} 的类型或加密方式暂不支持导入（{unsupported.Type}）。");
        return columns;
    }

    internal static void Write(string path, ImportSchema schema)
    {
        var columns = Columns(schema);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var document = SpreadsheetDocument.Create(temporary, SpreadsheetDocumentType.Workbook))
            {
                var workbook = document.AddWorkbookPart();
                workbook.Workbook = new Workbook(new BookViews(new WorkbookView { ActiveTab = 0 }), new Sheets());
                var styles = workbook.AddNewPart<WorkbookStylesPart>();
                styles.Stylesheet = Styles();
                styles.Stylesheet.Save();
                var data = AddSheet(workbook, DataSheet, false);
                data.Append(TextRow(1, columns.Select(column => column.Name), 2));
                // A blank, explicitly text-formatted row also survives editors that ignore column styles.
                data.Append(TextRow(2, columns.Select(_ => ""), 1));
                var fields = AddSheet(workbook, FieldSheet, false);
                fields.Append(TextRow(1, new[] { "数据库列名", "SQL 类型", "填写规则", "示例" }, 2));
                uint rowIndex = 2;
                foreach (var column in columns)
                {
                    string rule = column.Required ? "必填" : column.HasDefault ? "留空使用数据库默认值" : "留空写入 NULL";
                    fields.Append(TextRow(rowIndex++, new[] { column.Name, column.Description, rule, ExcelImportValues.Example(column) }, 1));
                }
                fields.Append(TextRow(rowIndex++, new[] { "目标", schema.Database + "." + schema.Table.SqlName }, 1));
                fields.Append(TextRow(rowIndex++, new[] { "操作", "在填写数据页从第 2 行输入，保留表头，不使用公式。" }, 1));
                fields.Append(TextRow(rowIndex++, new[] { "精度", "编码、长整数和高精度数字按文本粘贴原值。" }, 1));
                fields.Append(TextRow(rowIndex, new[] { "范围", $"最多 {MaxRows} 行，忽略完全空白行。" }, 1));
                var metadata = AddSheet(workbook, MetadataSheet, true);
                metadata.Append(TextRow(1, new[] { Format, schema.Stamp }, 1));
                workbook.Workbook.Save();
            }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static SheetData AddSheet(WorkbookPart workbook, string name, bool hidden)
    {
        var part = workbook.AddNewPart<WorksheetPart>();
        var data = new SheetData();
        var widths = new Columns();
        if (name == FieldSheet)
        {
            widths.Append(new Column { Min = 1, Max = 1, Width = 28, CustomWidth = true, Style = 1 });
            widths.Append(new Column { Min = 2, Max = 2, Width = 61, CustomWidth = true, Style = 1 });
            widths.Append(new Column { Min = 3, Max = 3, Width = 30, CustomWidth = true, Style = 1 });
            widths.Append(new Column { Min = 4, Max = 4, Width = 43, CustomWidth = true, Style = 1 });
        }
        else widths.Append(new Column { Min = 1, Max = 1024, Width = 23, CustomWidth = true, Style = 1 });
        part.Worksheet = new Worksheet(
            new SheetViews(new SheetView(new Pane { VerticalSplit = 1, TopLeftCell = "A2",
                ActivePane = PaneValues.BottomLeft, State = PaneStateValues.Frozen }) { WorkbookViewId = 0 }),
            widths, data);
        var sheets = workbook.Workbook.GetFirstChild<Sheets>();
        sheets.Append(new Sheet { Name = name, Id = workbook.GetIdOfPart(part),
            SheetId = (uint)sheets.ChildElements.Count + 1,
            State = hidden ? SheetStateValues.VeryHidden : SheetStateValues.Visible });
        return data;
    }

    private static Stylesheet Styles()
    {
        var fonts = new Fonts(new Font(), new Font(new Bold()));
        var fills = new Fills(new Fill(new PatternFill { PatternType = PatternValues.None }),
            new Fill(new PatternFill { PatternType = PatternValues.Gray125 }),
            new Fill(new PatternFill(new ForegroundColor { Rgb = "FFE0EDF8" }, new BackgroundColor { Indexed = 64 })
                { PatternType = PatternValues.Solid }));
        return new Stylesheet(fonts, fills, new Borders(new Border()),
            new CellStyleFormats(new CellFormat()),
            new CellFormats(new CellFormat(), new CellFormat { NumberFormatId = 49, ApplyNumberFormat = true },
                new CellFormat { NumberFormatId = 49, FontId = 1, FillId = 2,
                    ApplyNumberFormat = true, ApplyFont = true, ApplyFill = true }),
            new CellStyles(new CellStyle { Name = "Normal", FormatId = 0, BuiltinId = 0 }));
    }

    private static Row TextRow(uint index, IEnumerable<string> values, uint style)
    {
        var row = new Row { RowIndex = index };
        int column = 0;
        foreach (string value in values)
            row.Append(new Cell(new InlineString(new Text(value) { Space = SpaceProcessingModeValues.Preserve }))
            { CellReference = ColumnName(++column) + index, DataType = CellValues.InlineString, StyleIndex = style });
        return row;
    }

    private static string ColumnName(int column)
    {
        string result = "";
        for (; column > 0; column = (column - 1) / 26)
            result = (char)('A' + (column - 1) % 26) + result;
        return result;
    }

    internal static ImportPlan Read(string path, ImportSchema schema, CancellationToken token)
    {
        var columns = Columns(schema).ToDictionary(column => column.Name, StringComparer.Ordinal);
        if (!Path.GetExtension(path).Equals(".xlsx", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("请选择 .xlsx 文件。");
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length > 20L * 1024 * 1024) throw new InvalidDataException("Excel 文件超过 20 MB。");
        string sourceHash = Convert.ToHexString(SHA256.HashData(file));
        file.Position = 0;
        using (var zip = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: true))
            if (zip.Entries.Count > 1024 || zip.Entries.Sum(entry => entry.Length) > 64L * 1024 * 1024)
                throw new InvalidDataException("Excel 文件展开后的内容超出导入范围。");
        file.Position = 0;
        using var document = SpreadsheetDocument.Open(file, false);
        if (document.DocumentType != SpreadsheetDocumentType.Workbook)
            throw new InvalidDataException("请选择普通 .xlsx 工作簿。");
        var workbook = document.WorkbookPart ?? throw new InvalidDataException("文件缺少工作簿。");
        var shared = workbook.SharedStringTablePart?.SharedStringTable?.Elements<SharedStringItem>().ToArray()
            ?? Array.Empty<SharedStringItem>();
        var stampRow = GetRows(workbook, MetadataSheet).FirstOrDefault();
        var stamp = stampRow == null ? new Dictionary<int, CellContent>() : ReadRow(stampRow, shared);
        if (!stamp.TryGetValue(1, out var format) || format.Invalid || format.Text != Format
            || !stamp.TryGetValue(2, out var identity) || identity.Invalid || identity.Text != schema.Stamp)
            throw new InvalidDataException("模板与当前目标表或表结构不匹配。请重新下载模板。");

        var rows = GetRows(workbook, DataSheet);
        using var iterator = rows.GetEnumerator();
        if (!iterator.MoveNext() || iterator.Current.RowIndex?.Value != 1)
            throw new InvalidDataException("填写数据页缺少第 1 行表头。");
        var header = ReadRow(iterator.Current, shared);
        var mapping = new Dictionary<int, ImportColumn>();
        var matched = new HashSet<int>();
        foreach (var cell in header)
        {
            if (cell.Value.Text.Length == 0 && !cell.Value.Invalid) continue;
            if (cell.Value.Invalid || !columns.TryGetValue(cell.Value.Text, out var column) || !matched.Add(column.Id))
                throw new InvalidDataException($"第 1 行 {ColumnName(cell.Key)} 列表头无效、重复或与目标列名不匹配。");
            mapping.Add(cell.Key, column);
        }
        if (mapping.Count != columns.Count) throw new InvalidDataException("模板表头不完整。请保留所有列名。");
        bool date1904 = workbook.Workbook.WorkbookProperties?.Date1904?.Value == true;
        var plan = new ImportPlan { SourceHash = sourceHash };
        int previous = 1;
        while (iterator.MoveNext())
        {
            token.ThrowIfCancellationRequested();
            int row = checked((int)(iterator.Current.RowIndex?.Value ?? 0));
            if (row <= previous || row > 1048576) throw new InvalidDataException("Excel 行编号无效或重复。");
            previous = row;
            var cells = ReadRow(iterator.Current, shared);
            if (cells.Values.All(cell => cell.Text.Length == 0 && !cell.Invalid)) continue;
            if (++plan.DataRows > MaxRows) throw new InvalidDataException($"单次最多导入 {MaxRows} 个非空数据行。");
            int previousIssues = plan.Issues.Count;
            var values = new Dictionary<int, object>();
            foreach (var entry in cells.Where(cell => !mapping.ContainsKey(cell.Key) && (cell.Value.Invalid || cell.Value.Text.Length > 0)))
                plan.Issues.Add(new ImportIssue(row, ColumnName(entry.Key), "此列不在模板中。"));
            foreach (var entry in mapping)
            {
                var column = entry.Value;
                if (!cells.TryGetValue(entry.Key, out var value) || (!value.Invalid && value.Text.Length == 0))
                {
                    if (column.Required) plan.Issues.Add(new ImportIssue(row, column.Name, "必填。"));
                    else if (!column.HasDefault) values.Add(column.Id, DBNull.Value);
                }
                else if (value.Invalid)
                    plan.Issues.Add(new ImportIssue(row, column.Name, "请填写常量，公式和错误单元格暂不支持。"));
                else
                {
                    try { values.Add(column.Id, ExcelImportValues.Parse(column, value.Text, value.Numeric, date1904)); }
                    catch (FormatException ex) { plan.Issues.Add(new ImportIssue(row, column.Name, ex.Message)); }
                    catch (Exception ex) when (ex is ArgumentException or OverflowException or System.Data.SqlTypes.SqlTypeException)
                    { plan.Issues.Add(new ImportIssue(row, column.Name, "值超出目标列可保存的范围。")); }
                }
            }
            if (plan.Issues.Count == previousIssues) plan.Rows.Add(new ImportRow(row, values));
            if (plan.Issues.Count >= 2000) throw new InvalidDataException("工作簿错误超过 2000 项。请先核对模板格式和必填字段。");
        }
        if (plan.DataRows == 0) plan.Issues.Add(new ImportIssue(0, "", "填写数据页没有可导入数据。"));
        CheckDuplicates(schema, plan);
        return plan;
    }

    internal static void CheckDuplicates(ImportSchema schema, ImportPlan plan)
    {
        foreach (var key in schema.Keys)
        {
            var seen = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var row in plan.Rows.Where(row => key.Columns.All(row.Values.ContainsKey)))
            {
                string values = Util.Json(key.Columns.Select(id => Util.Value(row.Values[id])).ToArray());
                if (!seen.TryAdd(values, row.Number))
                    plan.Issues.Add(new ImportIssue(row.Number, key.Name, $"与 Excel 第 {seen[values]} 行的唯一键重复。"));
                if (plan.Issues.Count >= 2000) return;
            }
        }
    }

    internal static void CheckFileUnchanged(string path, ImportPlan plan)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length > 20L * 1024 * 1024 || Convert.ToHexString(SHA256.HashData(file)) != plan.SourceHash)
            throw new InvalidDataException("文件在校验后已变更，请重新校验。");
    }

    private static IEnumerable<Row> GetRows(WorkbookPart workbook, string name)
    {
        var sheet = workbook.Workbook.GetFirstChild<Sheets>()?.Elements<Sheet>()
            .SingleOrDefault(candidate => candidate.Name?.Value == name);
        if (sheet?.Id?.Value == null || workbook.GetPartById(sheet.Id.Value) is not WorksheetPart part)
            throw new InvalidDataException("工作簿缺少模板工作表 " + name + "。");
        if (name == DataSheet && part.Worksheet.GetFirstChild<MergeCells>()?.HasChildren == true)
            throw new InvalidDataException("填写数据页包含合并单元格，请取消合并后校验。");
        return part.Worksheet.GetFirstChild<SheetData>()?.Elements<Row>()
            ?? throw new InvalidDataException("工作表内容无效。");
    }

    private sealed record CellContent(string Text, bool Numeric, bool Invalid);

    private static Dictionary<int, CellContent> ReadRow(Row row, SharedStringItem[] shared)
    {
        var values = new Dictionary<int, CellContent>();
        foreach (var cell in row.Elements<Cell>())
        {
            string address = cell.CellReference?.Value ?? "";
            int column = 0, offset = 0;
            while (offset < address.Length && address[offset] is >= 'A' and <= 'Z' && offset < 3)
                column = column * 26 + address[offset++] - 'A' + 1;
            if (column is < 1 or > 16384 || !uint.TryParse(address.AsSpan(offset), NumberStyles.None,
                CultureInfo.InvariantCulture, out uint rowNumber) || rowNumber != row.RowIndex?.Value)
                throw new InvalidDataException("存在无效的单元格地址。");
            var type = cell.DataType?.Value;
            string value = cell.CellValue?.Text ?? "";
            bool invalid = cell.CellFormula != null || type == CellValues.Error;
            if (type == CellValues.SharedString)
            {
                if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int id) || id >= shared.Length)
                    throw new InvalidDataException("Excel 共享文本索引无效。");
                value = RichText(shared[id]);
            }
            else if (type == CellValues.InlineString) value = RichText(cell.InlineString);
            bool numeric = value.Length > 0 && (type == null || type == CellValues.Number);
            if (!values.TryAdd(column, new CellContent(value, numeric, invalid)))
                throw new InvalidDataException("Excel 存在重复单元格地址。");
        }
        return values;
    }

    private static string RichText(OpenXmlElement element)
    {
        if (element == null) return "";
        var text = new StringBuilder();
        foreach (var child in element.ChildElements)
        {
            if (child is Text direct) text.Append(direct.Text);
            else if (child is Run run) text.Append(run.Text?.Text);
        }
        return text.ToString();
    }
}
