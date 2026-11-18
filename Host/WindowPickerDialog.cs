using System.Drawing;
using System.Windows.Forms;

namespace SingleAppRemote.Host;

public sealed class WindowPickerDialog : Form
{
    private readonly TextBox _searchBox;
    private readonly ListView _listView;
    private readonly Button _btnSelect;
    private readonly Button _btnRefresh;
    private readonly Button _btnCancel;
    private List<WindowEntry> _allWindows = new();
    private readonly IntPtr _currentHwnd;

    public WindowEntry? SelectedWindow { get; private set; }

    public WindowPickerDialog(IntPtr currentHwnd, Icon appIcon)
    {
        _currentHwnd = currentHwnd;

        Text = "キャプチャ対象ウィンドウの選択 (ホスト専用)";
        Icon = appIcon;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(760, 500);
        MinimumSize = new Size(560, 380);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = false;
        MinimizeBox = false;
        TopMost = true;
        KeyPreview = true;
        Font = new Font("Yu Gothic UI", 9.5f);

        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.F5)
            {
                ReloadWindows();
                e.Handled = true;
            }
        };

        // DPI スケーリングでも絶対にはみ出さない 3列 TableLayoutPanel (ラベル / 検索入力欄 100%伸縮 / 更新ボタン)
        var topLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 3,
            RowCount = 1,
            Padding = new Padding(10, 10, 10, 8)
        };
        topLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        topLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        topLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var lblSearch = new Label
        {
            Text = "検索 (タイトル / プロセス名):",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 0, 8, 0)
        };

        _searchBox = new TextBox
        {
            Dock = DockStyle.Fill,
            Anchor = AnchorStyles.Left | AnchorStyles.Right,
            Margin = new Padding(0, 2, 8, 2),
            PlaceholderText = "例: 電卓, chrome, notepad..."
        };

        _btnRefresh = new Button
        {
            Text = "",
            Size = new Size(32, 30),
            Anchor = AnchorStyles.Right,
            Margin = new Padding(0)
        };
        _btnRefresh.Paint += (_, e) =>
        {
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            float cx = _btnRefresh.ClientSize.Width / 2f;
            float cy = _btnRefresh.ClientSize.Height / 2f;
            float r = Math.Min(cx, cy) * 0.46f;

            using var pen = new Pen(Color.FromArgb(45, 45, 48), 2.0f)
            {
                StartCap = System.Drawing.Drawing2D.LineCap.Round,
                EndCap = System.Drawing.Drawing2D.LineCap.Round
            };
            // 円弧 (40度〜330度)
            g.DrawArc(pen, cx - r, cy - r, r * 2, r * 2, 40, 290);

            // 矢印の先端 (330度付近 = 右上)
            double rad = 330.0 * Math.PI / 180.0;
            float ax = cx + (float)(r * Math.Cos(rad));
            float ay = cy + (float)(r * Math.Sin(rad));
            var arrowPts = new PointF[]
            {
                new(ax + 3.5f, ay + 1.5f),
                new(ax - 3.5f, ay + 2.5f),
                new(ax + 0.5f, ay - 4.0f)
            };
            using var brush = new SolidBrush(Color.FromArgb(45, 45, 48));
            g.FillPolygon(brush, arrowPts);
        };
        var toolTip = new ToolTip();
        toolTip.SetToolTip(_btnRefresh, "一覧を再読み込み (F5)");
        _btnRefresh.Click += (_, _) => ReloadWindows();

        topLayout.Controls.Add(lblSearch, 0, 0);
        topLayout.Controls.Add(_searchBox, 1, 0);
        topLayout.Controls.Add(_btnRefresh, 2, 0);

        _listView = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            MultiSelect = false,
            GridLines = true,
            HideSelection = false
        };
        _listView.Columns.Add("ウィンドウタイトル", 460);
        _listView.Columns.Add("プロセス名", 160);
        _listView.Columns.Add("PID", 90);

        _listView.Resize += (_, _) => AdjustListViewColumns();

        _btnSelect = new Button
        {
            Text = "このウィンドウを選択",
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(14, 4, 14, 4),
            Margin = new Padding(8, 0, 0, 0),
            Enabled = false
        };
        _btnSelect.Click += (_, _) => ConfirmSelection();

        _btnCancel = new Button
        {
            Text = "キャンセル",
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(14, 4, 14, 4),
            Margin = new Padding(8, 0, 0, 0),
            DialogResult = DialogResult.Cancel
        };

        _searchBox.TextChanged += (_, _) => ApplyFilter();
        _searchBox.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Down && _listView.Items.Count > 0)
            {
                _listView.Focus();
                if (_listView.SelectedItems.Count == 0) _listView.Items[0].Selected = true;
                e.Handled = true;
            }
        };

        _listView.SelectedIndexChanged += (_, _) =>
        {
            _btnSelect.Enabled = _listView.SelectedItems.Count > 0;
        };
        _listView.DoubleClick += (_, _) => ConfirmSelection();

        var bottomPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(10, 10, 10, 10)
        };

        bottomPanel.Controls.Add(_btnCancel);
        bottomPanel.Controls.Add(_btnSelect);

        Controls.Add(_listView);
        Controls.Add(topLayout);
        Controls.Add(bottomPanel);

        AcceptButton = _btnSelect;
        CancelButton = _btnCancel;

        Shown += (_, _) =>
        {
            AdjustListViewColumns();
            ReloadWindows();
            _searchBox.Focus();
        };
    }

    private void AdjustListViewColumns()
    {
        if (_listView.Columns.Count < 3) return;
        int totalWidth = _listView.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 6;
        if (totalWidth <= 200) return;
        int procWidth = Math.Max(130, (int)(totalWidth * 0.22));
        int pidWidth = Math.Max(75, (int)(totalWidth * 0.11));
        int titleWidth = Math.Max(200, totalWidth - procWidth - pidWidth);

        _listView.Columns[0].Width = titleWidth;
        _listView.Columns[1].Width = procWidth;
        _listView.Columns[2].Width = pidWidth;
    }

    private void ReloadWindows()
    {
        _allWindows = HostConfig.GetVisibleWindows();
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        string query = _searchBox.Text.Trim();
        var filtered = string.IsNullOrEmpty(query)
            ? _allWindows
            : _allWindows.Where(w =>
                w.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                w.ProcessName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                w.ProcessId.ToString().Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();

        _listView.BeginUpdate();
        _listView.Items.Clear();

        foreach (var win in filtered)
        {
            var item = new ListViewItem(win.Title)
            {
                Tag = win
            };
            item.SubItems.Add(win.ProcessName);
            item.SubItems.Add(win.ProcessId.ToString());

            if (win.Hwnd == _currentHwnd)
            {
                item.Selected = true;
                item.Font = new Font(_listView.Font, FontStyle.Bold);
            }
            _listView.Items.Add(item);
        }

        if (_listView.SelectedItems.Count == 0 && _listView.Items.Count > 0)
        {
            _listView.Items[0].Selected = true;
        }

        _listView.EndUpdate();
        _btnSelect.Enabled = _listView.SelectedItems.Count > 0;
    }

    private void ConfirmSelection()
    {
        if (_listView.SelectedItems.Count == 0) return;
        if (_listView.SelectedItems[0].Tag is WindowEntry entry)
        {
            SelectedWindow = entry;
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
