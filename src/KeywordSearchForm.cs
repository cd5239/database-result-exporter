using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SqlResultExporter;

internal sealed class KeywordSearchForm : Form
{
    private readonly KeywordSearch search;
    private readonly Action<string> log;
    private readonly TextBox keyword = new() { Dock = DockStyle.Fill, MaxLength = 512 };
    private readonly CheckBox exact = new() { Text = "整值匹配", AutoSize = true };
    private readonly CheckBox scalars = new() { Text = "含数字、日期等字段", Checked = true, AutoSize = true };
    private readonly NumericUpDown limit = new() { Minimum = 1, Maximum = 500, Value = 100, Width = 65 };
    private readonly CheckedListBox tables = new() { Dock = DockStyle.Fill, CheckOnClick = true, HorizontalScrollbar = true };
    private readonly AppButton reload = Button("加载表 / 集合", 125), all = Button("全选", 64),
        none = Button("全不选", 74), start = Button("开始查找", 105, true), cancel = Button("取消查找", 105);
    private readonly Label connection = new() { Dock = DockStyle.Fill, AutoEllipsis = true };
    private readonly TextBox status = new()
    {
        Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
        BackColor = Color.White, BorderStyle = BorderStyle.FixedSingle
    };
    private readonly TextBox detail = new()
    {
        Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both,
        WordWrap = false, BackColor = Color.White, MaxLength = int.MaxValue
    };
    private readonly DataGridView grid = new()
    {
        Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
        RowHeadersVisible = false, BackgroundColor = Color.White, MultiSelect = false,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill
    };
    private CancellationTokenSource running;
    private bool loaded;

    internal KeywordSearchForm(Options options, Action<string> log, bool loadOnShow = true)
    {
        search = new KeywordSearch(options);
        this.log = log;
        Font = new Font("Microsoft YaHei UI", 9F);
        Text = "关键字查找 · " + options.Kind;
        ClientSize = new Size(1100, 740);
        MinimumSize = new Size(900, 620);
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Color.FromArgb(246, 248, 251);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), RowCount = 6, ColumnCount = 1 };
        foreach (int height in new[] { 30, 40, 40 }) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 84));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        Controls.Add(layout);
        string[] target = options.Kind == DatabaseKind.MongoDB
            ? new[] { new MongoDB.Driver.MongoUrl(options.ConnectionString).Server.ToString(),
                new MongoDB.Driver.MongoUrl(options.ConnectionString).DatabaseName }
            : Providers.Target(options);
        connection.Text = $"待连接   {options.Kind}   ·   {target[0]} / {target[1]}";
        layout.Controls.Add(connection, 0, 0);

        var input = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        input.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70));
        input.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        input.Controls.Add(new Label { Text = "关键字", Dock = DockStyle.Fill, Padding = new Padding(0, 5, 0, 0) }, 0, 0);
        input.Controls.Add(keyword, 1, 0);
        layout.Controls.Add(input, 0, 1);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        actions.Controls.AddRange(new Control[] { start, cancel, exact, scalars,
            new Label { Text = "每表上限", AutoSize = true, Padding = new Padding(5, 5, 0, 0) }, limit });
        layout.Controls.Add(actions, 0, 2);

        var body = new SplitContainer
        {
            Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel1, Size = new Size(1060, 450), SplitterDistance = 240
        };
        body.Panel1MinSize = 180;
        body.Panel2MinSize = 450;
        var selection = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        selection.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        selection.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        selection.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var selectionActions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        selectionActions.Controls.AddRange(new Control[] { all, none });
        selection.Controls.Add(reload, 0, 0);
        selection.Controls.Add(selectionActions, 0, 1);
        selection.Controls.Add(tables, 0, 2);
        body.Panel1.Controls.Add(selection);
        var records = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal };
        grid.Columns.Add("table", "表 / 集合");
        grid.Columns.Add("fields", "命中字段 / 路径");
        grid.Columns.Add("preview", "记录摘要");
        records.Panel1.Controls.Add(grid);
        records.Panel2.Controls.Add(detail);
        body.Panel2.Controls.Add(records);
        layout.Controls.Add(body, 0, 3);
        layout.Controls.Add(status, 0, 4);
        layout.Controls.Add(new Label
        {
            Text = $"双击结果查看完整记录，也可在下方选中复制。总计最多 {KeywordSearch.TotalRows:N0} 条；MongoDB 每集合最多扫描 {KeywordSearch.MongoScanLimit:N0} 份文档。",
            Dock = DockStyle.Fill, ForeColor = Color.DimGray, AutoEllipsis = true
        }, 0, 5);
        status.Text = "正在等待加载表或集合。选择范围后输入关键字。匹配大小写与日期格式遵循数据库规则，MongoDB 按大小写匹配。";
        reload.Click += async (sender, args) => await LoadTables();
        start.Click += async (sender, args) => await Find();
        cancel.Click += (sender, args) => { running?.Cancel(); status.Text = "正在取消，请等待当前读取结束…"; };
        all.Click += (sender, args) => CheckTables(true);
        none.Click += (sender, args) => CheckTables(false);
        grid.SelectionChanged += (sender, args) => ShowRecord();
        grid.CellDoubleClick += (sender, args) => ExpandRecord();
        FormClosing += (sender, args) =>
        {
            if (running == null) return;
            args.Cancel = true;
            status.Text = "请先取消查找并等待结束，再关闭窗口。";
        };
        Shown += async (sender, args) =>
        {
            body.SplitterDistance = 240;
            records.SplitterDistance = records.Height / 2;
            if (loadOnShow) await LoadTables();
        };
        RefreshActions();
    }

    private static AppButton Button(string text, int width, bool primary = false)
        => new(primary) { Text = text, Width = width, Height = 30 };

    private void RefreshActions()
    {
        bool idle = running == null;
        reload.Enabled = keyword.Enabled = exact.Enabled = scalars.Enabled = limit.Enabled = idle;
        tables.Enabled = all.Enabled = none.Enabled = start.Enabled = idle && loaded;
        cancel.Enabled = !idle;
    }

    private void CheckTables(bool check)
    {
        tables.BeginUpdate();
        try { for (int index = 0; index < tables.Items.Count; index++) tables.SetItemChecked(index, check); }
        finally { tables.EndUpdate(); }
    }

    private async Task LoadTables()
    {
        if (running != null) return;
        using var cancellation = new CancellationTokenSource();
        running = cancellation;
        loaded = false;
        tables.Items.Clear();
        connection.Text = connection.Text.Replace("待连接", "连接中").Replace("已连接", "连接中")
            .Replace("连接失败", "连接中").Replace("已取消", "连接中");
        connection.ForeColor = Color.DimGray;
        RefreshActions();
        status.Text = "正在连接并加载可见表或集合…";
        try
        {
            var list = await Task.Run(() => search.Tables(cancellation.Token));
            tables.Items.AddRange(list.Cast<object>().ToArray());
            loaded = true;
            connection.Text = connection.Text.Replace("连接中", "已连接");
            connection.ForeColor = Color.DarkGreen;
            status.Text = $"连接成功，共 {list.Count} 个表或集合。请勾选查找范围。";
            log($"关键字查找已加载 {list.Count} 个表或集合。");
        }
        catch (Exception) when (cancellation.IsCancellationRequested)
        {
            connection.Text = connection.Text.Replace("连接中", "已取消");
            status.Text = "已取消加载。";
        }
        catch (Exception)
        {
            connection.ForeColor = Color.Firebrick;
            connection.Text = connection.Text.Replace("连接中", "连接失败");
            status.Text = "加载失败。请核对当前连接、网络和读取表结构的权限。";
            log("关键字查找加载表或集合失败。");
        }
        finally { running = null; RefreshActions(); }
    }

    private async Task Find()
    {
        if (running != null) return;
        var selected = tables.CheckedItems.Cast<SearchTable>().ToArray();
        string value = keyword.Text;
        if (selected.Length == 0 || string.IsNullOrWhiteSpace(value) || value.Contains('\0'))
        {
            status.Text = "请勾选至少一个表或集合，并输入有效关键字。";
            return;
        }
        grid.Rows.Clear();
        detail.Clear();
        using var cancellation = new CancellationTokenSource();
        running = cancellation;
        RefreshActions();
        var rows = new List<SearchHit>();
        var progress = new Progress<string>(message => { if (running == cancellation) status.Text = message; });
        bool matchExact = exact.Checked, includeScalars = scalars.Checked;
        int perTable = (int)limit.Value;
        try
        {
            var summary = await Task.Run(() => search.Run(selected, value, matchExact, includeScalars,
                perTable, cancellation.Token, rows.Add, ((IProgress<string>)progress).Report));
            status.Text = $"{(summary.Cancelled ? "已取消" : "查找结束")}。找到 {summary.Rows} 条记录；"
                + $"已检索 {summary.FinishedTables} 个表 / 集合，{summary.FailedTables} 个失败，{summary.LimitedTables} 个达到上限。"
                + (summary.TotalLimit ? " 已达到总计 1,000 条上限，后续表未查找。" : "")
                + (summary.Cancelled || summary.TotalLimit || summary.FailedTables > 0 || summary.LimitedTables > 0
                    ? " 结果不完整。" : "")
                + Environment.NewLine + string.Join(Environment.NewLine, summary.Messages);
            log($"关键字查找结束，找到 {summary.Rows} 条记录，失败 {summary.FailedTables} 个，取消 {summary.Cancelled}。");
        }
        catch (Exception)
        {
            status.Text = "查找未完成，请核对连接、数据库权限与字段类型。已读取的结果保留在下方。";
            log("关键字查找未完成。");
        }
        finally
        {
            foreach (var row in rows)
            {
                string preview = row.Document ?? string.Join(" | ", row.Table.Columns.Select((column, index) =>
                    column.Name + "=" + Preview(row.Values[index])));
                int index = grid.Rows.Add(row.Table.ToString(), string.Join(", ", row.Fields), Preview(preview));
                grid.Rows[index].Tag = row;
            }
            running = null;
            RefreshActions();
            ShowRecord();
        }
    }

    private static string Preview(string text) => text == null ? "NULL"
        : text.Length > 240 ? text.Substring(0, 240) + "…" : text;

    private void ShowRecord()
    {
        detail.Text = (grid.CurrentRow?.Tag as SearchHit)?.Details ?? "";
    }

    private void ExpandRecord()
    {
        if (grid.CurrentRow?.Tag is not SearchHit row) return;
        using var dialog = new Form
        {
            Text = "完整记录 · " + row.Table, ClientSize = new Size(850, 600),
            StartPosition = FormStartPosition.CenterParent, Font = Font, MinimizeBox = false
        };
        dialog.Controls.Add(new TextBox
        {
            Text = row.Details, Multiline = true, ReadOnly = true, Dock = DockStyle.Fill,
            WordWrap = false, ScrollBars = ScrollBars.Both, MaxLength = int.MaxValue
        });
        dialog.ShowDialog(this);
    }
}
