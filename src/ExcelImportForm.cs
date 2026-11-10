using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Data.SqlClient;

namespace SqlResultExporter;

internal sealed class ExcelImportForm : Form
{
    private readonly SqlTableImport importer;
    private readonly string server, database;
    private readonly Action<string> log;
    private readonly Label connection = new() { Dock = DockStyle.Fill, AutoEllipsis = true };
    private readonly Label result = new() { Dock = DockStyle.Fill, AutoEllipsis = true };
    private readonly ComboBox tables = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox file = new() { Dock = DockStyle.Fill, ReadOnly = true };
    private readonly Button reload = Button("加载表", 100), template = Button("下载 Excel 模板", 145),
        browse = Button("选择 Excel…", 114), validate = Button("校验并预览", 120),
        commit = Button("确认导入", 112, primary: true), report = Button("保存错误报告", 124), cancel = Button("取消操作", 110);
    private readonly DataGridView grid = new()
    {
        Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false,
        AllowUserToDeleteRows = false, BackgroundColor = Color.White, RowHeadersVisible = false,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None, SelectionMode = DataGridViewSelectionMode.FullRowSelect
    };
    private ImportSchema checkedSchema;
    private ImportPlan plan;
    private CancellationTokenSource running;

    internal ExcelImportForm(string connectionString, int timeout, Action<string> log, bool loadOnShow = true)
    {
        importer = new SqlTableImport(connectionString, timeout);
        var settings = new SqlConnectionStringBuilder(connectionString);
        server = settings.DataSource;
        database = settings.InitialCatalog;
        this.log = log;
        Text = "Excel 导入 · SQL Server";
        Font = new Font("Microsoft YaHei UI", 9F);
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(1010, 655);
        MinimumSize = new Size(820, 520);
        BackColor = Color.FromArgb(246, 248, 251);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 8, Padding = new Padding(16) };
        foreach (int height in new[] { 48, 31, 42, 42, 42, 46 }) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        Controls.Add(layout);
        layout.Controls.Add(new Label
        {
            Text = "Excel 导入   /   SQL Server", Dock = DockStyle.Fill, ForeColor = Color.FromArgb(26, 53, 85),
            Font = new Font(Font.FontFamily, 16, FontStyle.Bold)
        }, 0, 0);
        layout.Controls.Add(connection, 0, 1);
        layout.Controls.Add(Field("目标表", tables, reload), 0, 2);
        layout.Controls.Add(Field("Excel 文件", file, browse), 0, 3);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        actions.Controls.AddRange(new Control[] { template, validate, commit, report, cancel });
        layout.Controls.Add(actions, 0, 4);
        layout.Controls.Add(result, 0, 5);
        layout.Controls.Add(grid, 0, 6);
        layout.Controls.Add(new Label
        {
            Dock = DockStyle.Fill, AutoEllipsis = true, ForeColor = Color.DimGray,
            Text = $"单文件、单表、仅新增 · 每次最多 {ExcelImportWorkbook.MaxRows} 行 · 写入使用一个事务"
        }, 0, 7);
        connection.Text = $"待加载   {server} / {database}";
        result.Text = "选择目标表并下载模板。填写后选择文件，点击“校验并预览”。";

        reload.Click += async (sender, args) => await LoadTables();
        template.Click += async (sender, args) => await Download();
        browse.Click += (sender, args) => Browse();
        validate.Click += async (sender, args) => await ValidateFile();
        commit.Click += async (sender, args) => await Confirm();
        report.Click += (sender, args) => SaveReport();
        cancel.Click += (sender, args) =>
        {
            running?.Cancel();
            result.Text = "已请求取消，正在等待数据库或文件操作结束。";
        };
        tables.SelectedIndexChanged += (sender, args) => ResetPreview();
        FormClosing += (sender, args) =>
        {
            if (running == null) return;
            args.Cancel = true;
            result.Text = "操作进行中。请等待完成，或点击取消操作。";
        };
        if (loadOnShow) Shown += async (sender, args) => await LoadTables();
        RefreshActions();
    }

    private static Button Button(string caption, int width, bool primary = false)
        => new AppButton(primary) { Text = caption, Width = width, Height = 30 };

    private static TableLayoutPanel Field(string caption, Control input, Control action)
    {
        var field = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1 };
        field.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 84));
        field.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        field.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 125));
        field.Controls.Add(new Label { Text = caption, Dock = DockStyle.Fill, Padding = new Padding(0, 6, 0, 0) }, 0, 0);
        field.Controls.Add(input, 1, 0);
        field.Controls.Add(action, 2, 0);
        return field;
    }

    private ImportTable Target => tables.SelectedItem as ImportTable;

    private void RefreshActions()
    {
        bool idle = running == null;
        reload.Enabled = tables.Enabled = idle;
        browse.Enabled = template.Enabled = idle && Target != null;
        validate.Enabled = idle && Target != null && file.Text.Length > 0;
        report.Enabled = idle && plan?.Issues.Count > 0;
        commit.Enabled = idle && plan?.Valid == true && checkedSchema != null && checkedSchema.WriteBlock == null;
        cancel.Enabled = !idle;
    }

    private void ResetPreview()
    {
        plan = null;
        checkedSchema = null;
        grid.Rows.Clear();
        grid.Columns.Clear();
        RefreshActions();
    }

    private async Task Operate(string message, Func<CancellationToken, Task> action)
    {
        if (running != null) return;
        using var cancellation = new CancellationTokenSource();
        running = cancellation;
        RefreshActions();
        result.Text = message;
        try { await action(cancellation.Token); }
        catch (OperationCanceledException) { result.Text = "操作已取消。"; }
        catch (SqlException ex)
        {
            result.Text = $"SQL Server 错误 {ex.Number}。请核对连接、目标表权限和数据库版本。";
            log($"Excel 导入操作遇到 SQL Server 错误 {ex.Number}。");
        }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or ImportWriteException)
        {
            result.Text = ex.Message;
            log("Excel 导入操作未完成。请查看导入窗口提示。");
        }
        catch (Exception)
        {
            result.Text = "操作失败。请核对文件格式、目录权限或数据库连接。";
            log("Excel 导入操作未完成。请查看导入窗口提示。");
        }
        finally
        {
            running = null;
            RefreshActions();
        }
    }

    private async Task LoadTables()
    {
        ResetPreview();
        tables.Items.Clear();
        connection.ForeColor = Color.DimGray;
        connection.Text = $"连接中   {server} / {database}";
        await Operate("正在读取 SQL Server 数据表…", async token =>
        {
            List<ImportTable> list = await importer.Tables(token);
            tables.Items.AddRange(list.Cast<object>().ToArray());
            connection.ForeColor = Color.DarkGreen;
            connection.Text = $"已连接   {server} / {database}   ·   {list.Count} 张可见表";
            result.Text = list.Count == 0 ? "当前账号没有可见的用户表。" : "请选择目标表。";
        });
        if (connection.ForeColor == Color.DimGray)
        {
            connection.ForeColor = Color.Firebrick;
            connection.Text = $"未加载表   {server} / {database}";
        }
    }

    private async Task Download()
    {
        ImportTable table = Target;
        if (table == null) return;
        using var dialog = new SaveFileDialog
        {
            Filter = "Excel 工作簿 (*.xlsx)|*.xlsx", DefaultExt = "xlsx", AddExtension = true,
            FileName = Util.Safe(table.Schema + "_" + table.Name) + "_导入模板.xlsx"
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        await Operate("正在读取列结构并生成模板…", async token =>
        {
            var schema = await importer.Schema(table, token);
            await Task.Run(() => ExcelImportWorkbook.Write(dialog.FileName, schema), token);
            result.Text = "模板已保存。请在“填写数据”页录入，再选择该文件校验。";
            log("已生成 SQL Server Excel 导入模板。");
        });
    }

    private void Browse()
    {
        using var dialog = new OpenFileDialog { Filter = "Excel 工作簿 (*.xlsx)|*.xlsx", CheckFileExists = true };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        file.Text = dialog.FileName;
        ResetPreview();
        result.Text = "文件已选。点击“校验并预览”。";
    }

    private async Task ValidateFile()
    {
        ImportTable table = Target;
        string path = file.Text;
        ResetPreview();
        await Operate("正在校验工作簿、字段和可检查的唯一键…", async token =>
        {
            var schema = await importer.Schema(table, token);
            var checkedPlan = await Task.Run(() => ExcelImportWorkbook.Read(path, schema, token), token);
            await importer.CheckExisting(schema, checkedPlan, token);
            checkedSchema = schema;
            plan = checkedPlan;
            ShowPreview();
        });
    }

    private void ShowPreview()
    {
        if (!plan.Valid)
        {
            grid.Columns.Add("row", "Excel 行");
            grid.Columns.Add("field", "字段 / 索引");
            grid.Columns.Add("reason", "原因");
            grid.Columns[1].Width = 200;
            grid.Columns[2].AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
            foreach (var issue in plan.Issues.Take(2000))
                grid.Rows.Add(issue.Row == 0 ? "" : issue.Row.ToString(), issue.Column, issue.Reason);
            result.Text = plan.Issues.Count >= 2000 ? "已列出前 2000 项错误。请修正后重新校验。"
                : $"发现 {plan.Issues.Count} 项错误。请修正后重新校验。";
            return;
        }
        var columns = checkedSchema.Writable.Take(20).ToArray();
        grid.Columns.Add("row", "Excel 行");
        grid.Columns[0].Width = 76;
        for (int index = 0; index < columns.Length; index++)
        {
            grid.Columns.Add("c" + index, columns[index].Name);
            grid.Columns[index + 1].Width = columns[index].Type switch
            {
                "decimal" or "numeric" => Math.Clamp((columns[index].Precision + 3) * 8 + 12, 130, 342),
                "datetime" or "smalldatetime" or "datetime2" or "datetimeoffset" => 238,
                "char" or "varchar" or "nchar" or "nvarchar" => 160,
                _ => 115
            };
        }
        foreach (var row in plan.Rows.Take(50))
        {
            var cells = new List<object> { row.Number };
            foreach (var column in columns)
            {
                if (!row.Values.TryGetValue(column.Id, out var value)) cells.Add("〔数据库默认〕");
                else
                {
                    string formatted = value is DateTime date && column.Type == "date"
                        ? date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                        : Util.Value(value) ?? "〔NULL〕";
                    cells.Add(formatted.Length > 180 ? formatted[..180] + "…" : formatted);
                }
            }
            grid.Rows.Add(cells.ToArray());
        }
        result.Text = $"校验通过，共 {plan.DataRows} 行。下方预览最多显示 50 行、20 列。";
        if (checkedSchema.WriteBlock != null) result.Text = "校验通过。 " + checkedSchema.WriteBlock;
    }

    private async Task Confirm()
    {
        var schema = checkedSchema;
        var validated = plan;
        if (validated?.Valid != true || schema == null || schema.WriteBlock != null) return;
        string message = $"服务器：{schema.Server}\n数据库：{schema.Database}\n目标表：{schema.Table.SqlName}\n"
            + $"文件：{Path.GetFileName(file.Text)}\n预计新增：{validated.DataRows} 行\n\n确认提交本次导入？";
        if (MessageBox.Show(this, message, "确认新增数据", MessageBoxButtons.YesNo,
            MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        // A completed or interrupted write consumes the preview. A retry always starts with validation.
        plan = null;
        checkedSchema = null;
        await Operate("正在写入事务，请等待提交结果…", async token =>
        {
            await Task.Run(() => ExcelImportWorkbook.CheckFileUnchanged(file.Text, validated), token);
            int written = await importer.Insert(schema, validated, token);
            result.Text = $"导入完成，已提交 {written} 行。";
            log($"SQL Server Excel 导入已提交 {written} 行。");
        });
    }

    private void SaveReport()
    {
        if (plan?.Issues.Count is not > 0) return;
        using var dialog = new SaveFileDialog
        {
            Filter = "CSV (*.csv)|*.csv", DefaultExt = "csv", AddExtension = true,
            FileName = "Excel导入错误报告.csv"
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            using var writer = new StreamWriter(dialog.FileName, false, new System.Text.UTF8Encoding(true));
            writer.WriteLine("Excel行,字段或索引,原因");
            foreach (var issue in plan.Issues)
                writer.WriteLine($"{issue.Row},{Util.Csv(issue.Column, issue.Column)},{Util.Csv(issue.Reason, issue.Reason)}");
            result.Text = "错误报告已保存。";
        }
        catch (Exception) { result.Text = "无法保存错误报告，请检查目录权限。"; }
    }
}

internal sealed partial class MainForm
{
    private void AddHeader(TableLayoutPanel root, Label title)
    {
        var heading = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 1, Margin = Padding.Empty };
        heading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        heading.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        heading.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 188));
        heading.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80));
        var launch = new AppButton { Text = "Excel 导入（SQL Server）", Dock = DockStyle.Top, Height = 33 };
        launch.Click += (sender, args) =>
        {
            if (cancellation != null) return;
            if (Kind != DatabaseKind.SqlServer)
            {
                MessageBox.Show(this, "请先选择 SQL Server 连接配置。", "Excel 导入");
                return;
            }
            string settings;
            try { settings = Connection(); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "请检查连接信息"); return; }
            using var dialog = new ExcelImportForm(settings, (int)timeout.Value, Log);
            dialog.ShowDialog(this);
        };
        var about = new AppButton { Text = "关于", Dock = DockStyle.Top, Height = 33 };
        about.Click += (sender, args) => ShowAbout();
        var find = new AppButton { Text = "关键字查找", Dock = DockStyle.Top, Height = 33 };
        find.Click += (sender, args) =>
        {
            if (cancellation != null) return;
            string settings;
            try { settings = Connection(); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "请检查连接信息"); return; }
            using var dialog = new KeywordSearchForm(new Options
            {
                Kind = Kind, ConnectionString = settings, Timeout = (int)timeout.Value
            }, Log);
            dialog.ShowDialog(this);
        };
        heading.Controls.Add(title, 0, 0);
        heading.Controls.Add(find, 1, 0);
        heading.Controls.Add(launch, 2, 0);
        heading.Controls.Add(about, 3, 0);
        root.Controls.Add(heading);
    }

    private void ShowAbout()
    {
        const string website = "https://cd5239.cn/";
        string company = typeof(MainForm).Assembly.GetCustomAttribute<AssemblyCompanyAttribute>().Company;
        using var dialog = new Form
        {
            Text = "关于数据库查询结果导出工具", Font = Font,
            ClientSize = new Size(390, 160), StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false,
            MinimizeBox = false, ShowInTaskbar = false, AutoScaleMode = AutoScaleMode.Font
        };
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(18)
        };
        foreach (int height in new[] { 30, 24, 28 }) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(new Label { Text = Text, AutoSize = true, Font = new Font(Font, FontStyle.Bold) }, 0, 0);
        layout.Controls.Add(new Label { Text = $"版本 v{Util.ToolVersion}", AutoSize = true }, 0, 1);

        var companyLink = new LinkLabel
        {
            Text = company, AutoSize = true, TabStop = true, LinkArea = new LinkArea(0, company.Length)
        };
        companyLink.LinkClicked += (sender, args) =>
        {
            try { Process.Start(new ProcessStartInfo(website) { UseShellExecute = true }); }
            catch (System.ComponentModel.Win32Exception)
            {
                MessageBox.Show(dialog, $"请在浏览器中打开 {website}", "无法打开官网");
            }
        };
        layout.Controls.Add(companyLink, 0, 2);
        var close = new AppButton { Text = "关闭", Width = 82, Height = 28,
            Anchor = AnchorStyles.Right | AnchorStyles.Bottom, DialogResult = DialogResult.OK };
        layout.Controls.Add(close, 0, 3);
        dialog.AcceptButton = dialog.CancelButton = close;
        dialog.Controls.Add(layout);
        dialog.ShowDialog(this);
    }
}
