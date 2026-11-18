using System.Diagnostics;
using System.Drawing;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Windows.Forms;

namespace SingleAppRemote.Host;

public sealed class SettingsDialog : Form
{
    private readonly HostConfig _config;
    private readonly ComboBox _cmbListenUrl;
    private readonly TextBox _txtAccessToken;
    private readonly TextBox _txtTargetTitle;
    private readonly NumericUpDown _numFps;
    private readonly NumericUpDown _numQuality;
    private readonly CheckBox _chkCaptureCursor;
    private readonly ComboBox _cmbInputMode;
    private readonly CheckBox _chkBlockShellShortcuts;
    private readonly Button _btnCopyUrl;
    private readonly Button _btnSave;

    public bool ListenUrlChanged { get; private set; }

    private sealed record InputModeItem(InputMode Mode, string Label)
    {
        public override string ToString() => Label;
    }

    public SettingsDialog(HostConfig config, Icon appIcon)
    {
        _config = config;

        Text = "Single-App Remote 設定 (ホスト専用)";
        Icon = appIcon;
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = false;
        MinimizeBox = false;
        TopMost = true;
        Font = new Font("Yu Gothic UI", 9.5f);

        // 単一の2列フラット TableLayoutPanel で GroupBox の高DPI見切れバグを完全回避
        var formTable = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            Padding = new Padding(18, 14, 18, 8)
        };
        formTable.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        formTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));

        int row = 0;

        void AddSectionHeader(string title, bool isFirst = false)
        {
            var lbl = new Label
            {
                Text = title,
                AutoSize = true,
                Font = new Font(Font, FontStyle.Bold),
                ForeColor = Color.FromArgb(37, 99, 235),
                Margin = new Padding(0, isFirst ? 0 : 14, 0, 6)
            };
            formTable.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            formTable.Controls.Add(lbl, 0, row);
            formTable.SetColumnSpan(lbl, 2);
            row++;
        }

        void AddRow(string labelText, Control control)
        {
            formTable.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            if (!string.IsNullOrEmpty(labelText))
            {
                var lbl = new Label
                {
                    Text = labelText,
                    AutoSize = true,
                    Anchor = AnchorStyles.Left,
                    Margin = new Padding(6, 4, 12, 4)
                };
                formTable.Controls.Add(lbl, 0, row);
            }
            formTable.Controls.Add(control, 1, row);
            row++;
        }

        // =========================================================
        // 1. ネットワーク・認証設定
        // =========================================================
        AddSectionHeader("■ ネットワーク・認証設定", isFirst: true);

        _cmbListenUrl = new ComboBox
        {
            Dock = DockStyle.Fill,
            DropDownStyle = ComboBoxStyle.DropDown,
            Margin = new Padding(0, 3, 0, 2)
        };
        _cmbListenUrl.Items.AddRange(new object[]
        {
            "http://0.0.0.0:8000",
            "http://127.0.0.1:8000",
            "http://0.0.0.0:9000"
        });
        _cmbListenUrl.Text = _config.ListenUrl;
        AddRow("待受 URL:", _cmbListenUrl);

        var lblUrlNote = new Label
        {
            Text = "※待受URLの変更は次回起動時から反映されます (0.0.0.0 = LAN公開 / 127.0.0.1 = このPCのみ)",
            AutoSize = true,
            ForeColor = Color.FromArgb(100, 100, 110),
            Font = new Font(Font.FontFamily, 8.5f),
            Margin = new Padding(2, 0, 0, 6)
        };
        AddRow("", lblUrlNote);

        var tokenPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 3,
            RowCount = 1,
            Margin = new Padding(0, 2, 0, 4)
        };
        tokenPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        tokenPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        tokenPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        _txtAccessToken = new TextBox
        {
            Dock = DockStyle.Fill,
            Anchor = AnchorStyles.Left | AnchorStyles.Right,
            Text = _config.AccessToken,
            PlaceholderText = "空欄の場合はトークン認証なし",
            Margin = new Padding(0, 2, 6, 2)
        };

        var btnGenToken = new Button
        {
            Text = "ランダム生成",
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(8, 2, 8, 2),
            Margin = new Padding(0, 0, 6, 0)
        };
        btnGenToken.Click += (_, _) =>
        {
            _txtAccessToken.Text = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
        };

        var btnClearToken = new Button
        {
            Text = "クリア",
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(8, 2, 8, 2),
            Margin = new Padding(0)
        };
        btnClearToken.Click += (_, _) => _txtAccessToken.Text = "";

        tokenPanel.Controls.Add(_txtAccessToken, 0, 0);
        tokenPanel.Controls.Add(btnGenToken, 1, 0);
        tokenPanel.Controls.Add(btnClearToken, 2, 0);
        AddRow("アクセストークン:", tokenPanel);

        _btnCopyUrl = new Button
        {
            Text = "接続用 URL (LAN IP + トークン) をクリップボードにコピー",
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Anchor = AnchorStyles.Left,
            Padding = new Padding(10, 3, 10, 3),
            Margin = new Padding(0, 2, 0, 4)
        };
        _btnCopyUrl.Click += (_, _) => CopyConnectionUrlToClipboard();
        AddRow("", _btnCopyUrl);

        // =========================================================
        // 2. キャプチャ・映像品質設定
        // =========================================================
        AddSectionHeader("■ キャプチャ・映像品質設定");

        var targetPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            RowCount = 1,
            Margin = new Padding(0, 2, 0, 4)
        };
        targetPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        targetPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        _txtTargetTitle = new TextBox
        {
            Dock = DockStyle.Fill,
            Anchor = AnchorStyles.Left | AnchorStyles.Right,
            Text = _config.TargetWindowTitle,
            PlaceholderText = "起動時の対象タイトル（空欄で未選択起動）",
            Margin = new Padding(0, 2, 6, 2)
        };

        var btnClearTarget = new Button
        {
            Text = "未選択にする",
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(8, 2, 8, 2),
            Margin = new Padding(0)
        };
        btnClearTarget.Click += (_, _) => _txtTargetTitle.Text = "";

        targetPanel.Controls.Add(_txtTargetTitle, 0, 0);
        targetPanel.Controls.Add(btnClearTarget, 1, 0);
        AddRow("対象ウィンドウ名:", targetPanel);

        var fpsQualityFlow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = new Padding(0, 2, 0, 4)
        };

        _numFps = new NumericUpDown
        {
            Minimum = 1,
            Maximum = 60,
            Value = Math.Clamp(_config.TargetFps, 1, 60),
            Width = 75,
            Margin = new Padding(0, 2, 6, 2)
        };
        var lblFpsUnit = new Label
        {
            Text = "FPS (1〜60)     /     JPEG画質:",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 5, 6, 0)
        };
        _numQuality = new NumericUpDown
        {
            Minimum = 10,
            Maximum = 100,
            Increment = 5,
            Value = Math.Clamp(_config.JpegQuality, 10L, 100L),
            Width = 75,
            Margin = new Padding(0, 2, 6, 2)
        };
        var lblQualityUnit = new Label
        {
            Text = "% (10〜100)",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 5, 0, 0)
        };

        fpsQualityFlow.Controls.Add(_numFps);
        fpsQualityFlow.Controls.Add(lblFpsUnit);
        fpsQualityFlow.Controls.Add(_numQuality);
        fpsQualityFlow.Controls.Add(lblQualityUnit);
        AddRow("フレーム / 画質:", fpsQualityFlow);

        _chkCaptureCursor = new CheckBox
        {
            Text = "ホストPCのマウスカーソルをキャプチャ映像に含める",
            AutoSize = true,
            Checked = _config.CaptureCursor,
            Margin = new Padding(2, 4, 0, 4)
        };
        AddRow("", _chkCaptureCursor);

        // =========================================================
        // 3. 入力モード・Kiosk セキュリティ設定
        // =========================================================
        AddSectionHeader("■ 入力モード・Kiosk セキュリティ設定");

        _cmbInputMode = new ComboBox
        {
            Dock = DockStyle.Fill,
            DropDownStyle = ComboBoxStyle.DropDownList,
            Margin = new Padding(0, 3, 0, 6)
        };
        var modeItems = new[]
        {
            new InputModeItem(InputMode.SendInputRestoreCursor, "SendInput + カーソル位置即時復帰 (推奨・全アプリ対応)"),
            new InputModeItem(InputMode.SendInputDirect, "SendInput 直接移動 (ドラッグ重視・仮想ディスプレイ向け)"),
            new InputModeItem(InputMode.PostMessage, "PostMessage (完全バックグラウンド・従来のWin32アプリ向け)")
        };
        _cmbInputMode.Items.AddRange(modeItems);
        _cmbInputMode.SelectedItem = modeItems.FirstOrDefault(m => m.Mode == _config.CurrentInputMode) ?? modeItems[0];
        AddRow("入力注入モード:", _cmbInputMode);

        _chkBlockShellShortcuts = new CheckBox
        {
            Text = "Kiosk保護: ファイル開く/保存 (Ctrl+O/S/P) や新規ウィンドウ等のショートカットを遮断",
            AutoSize = true,
            Checked = _config.BlockShellShortcuts,
            Margin = new Padding(2, 4, 0, 8)
        };
        AddRow("", _chkBlockShellShortcuts);

        // =========================================================
        // 4. 下部ボタンバー（設定保存フォルダを開く / 保存 / キャンセル）
        // =========================================================
        var bottomBar = new TableLayoutPanel
        {
            Dock = DockStyle.Bottom,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 3,
            RowCount = 1,
            Padding = new Padding(18, 10, 18, 14)
        };
        bottomBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        bottomBar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottomBar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var btnOpenFolder = new Button
        {
            Text = "設定保存フォルダを開く...",
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Anchor = AnchorStyles.Left,
            Padding = new Padding(10, 4, 10, 4),
            Margin = new Padding(0)
        };
        var folderTip = new ToolTip();
        folderTip.SetToolTip(btnOpenFolder, HostConfig.SettingsFilePath);
        btnOpenFolder.Click += (_, _) =>
        {
            try
            {
                Directory.CreateDirectory(HostConfig.SettingsDirectory);
                Process.Start(new ProcessStartInfo(HostConfig.SettingsDirectory) { UseShellExecute = true });
            }
            catch { }
        };

        _btnSave = new Button
        {
            Text = "保存して適用 (OK)",
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(16, 4, 16, 4),
            Margin = new Padding(0, 0, 8, 0)
        };
        _btnSave.Click += (_, _) => ApplyAndSave();

        var btnCancel = new Button
        {
            Text = "キャンセル",
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(14, 4, 14, 4),
            Margin = new Padding(0),
            DialogResult = DialogResult.Cancel
        };

        bottomBar.Controls.Add(btnOpenFolder, 0, 0);
        bottomBar.Controls.Add(_btnSave, 1, 0);
        bottomBar.Controls.Add(btnCancel, 2, 0);

        Controls.Add(formTable);
        Controls.Add(bottomBar);

        AcceptButton = _btnSave;
        CancelButton = btnCancel;

        // 初期サイズ（Shown 時に実際の DPI スケーリング後の PreferredSize に合わせて自動拡張）
        ClientSize = new Size(760, 520);

        Shown += (_, _) =>
        {
            FitWindowToContent(formTable, bottomBar);
            // 起動時に ComboBox のテキストが全選択されて入力サジェストアイコンが重なるのを防ぐ
            _cmbListenUrl.SelectionLength = 0;
            _btnSave.Focus();
        };
    }

    private void FitWindowToContent(Control topContent, Control bottomContent)
    {
        PerformLayout();
        Size topPref = topContent.PreferredSize;
        Size botPref = bottomContent.PreferredSize;

        int targetWidth = Math.Max(ClientSize.Width, Math.Max(topPref.Width, botPref.Width) + 28);
        int targetHeight = Math.Max(topContent.Height, topPref.Height) + Math.Max(bottomContent.Height, botPref.Height) + 20;

        ClientSize = new Size(targetWidth, targetHeight);
        MinimumSize = Size;
    }

    private void CopyConnectionUrlToClipboard()
    {
        try
        {
            string rawUrl = string.IsNullOrWhiteSpace(_cmbListenUrl.Text) ? _config.ListenUrl : _cmbListenUrl.Text.Trim();
            string lanIp = GetLocalLanIp() ?? "localhost";
            string url = rawUrl
                .Replace("0.0.0.0", lanIp)
                .Replace("*", lanIp)
                .Replace("+", lanIp)
                .TrimEnd('/');

            string token = _txtAccessToken.Text.Trim();
            if (!string.IsNullOrEmpty(token))
            {
                url = $"{url}/?token={Uri.EscapeDataString(token)}";
            }

            Clipboard.SetText(url);
            _btnCopyUrl.Text = $"コピーしました: {url}";
        }
        catch { }
    }

    private static string? GetLocalLanIp()
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, 0);
            socket.Connect("8.8.8.8", 65530);
            if (socket.LocalEndPoint is IPEndPoint ep)
            {
                return ep.Address.ToString();
            }
        }
        catch { }

        try
        {
            foreach (var ip in Dns.GetHostEntry(Dns.GetHostName()).AddressList)
            {
                if (ip.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip))
                {
                    return ip.ToString();
                }
            }
        }
        catch { }

        return null;
    }

    private void ApplyAndSave()
    {
        string newListenUrl = string.IsNullOrWhiteSpace(_cmbListenUrl.Text)
            ? "http://0.0.0.0:8000"
            : _cmbListenUrl.Text.Trim();

        if (!Uri.TryCreate(
                newListenUrl.Replace("0.0.0.0", "localhost").Replace("*", "localhost").Replace("+", "localhost"),
                UriKind.Absolute,
                out var parsedUri) ||
            (parsedUri.Scheme != Uri.UriSchemeHttp && parsedUri.Scheme != Uri.UriSchemeHttps))
        {
            MessageBox.Show(
                this,
                "待受 URL の形式が正しくありません。\n例: http://0.0.0.0:8000 または http://127.0.0.1:8000",
                "入力エラー",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            _cmbListenUrl.Focus();
            return;
        }

        ListenUrlChanged = !string.Equals(_config.ListenUrl, newListenUrl, StringComparison.OrdinalIgnoreCase);
        _config.ListenUrl = newListenUrl;
        _config.AccessToken = _txtAccessToken.Text.Trim();
        _config.TargetFps = (int)_numFps.Value;
        _config.JpegQuality = (long)_numQuality.Value;
        _config.CaptureCursor = _chkCaptureCursor.Checked;
        _config.BlockShellShortcuts = _chkBlockShellShortcuts.Checked;

        if (_cmbInputMode.SelectedItem is InputModeItem selectedMode)
        {
            _config.CurrentInputMode = selectedMode.Mode;
        }

        string newTitle = _txtTargetTitle.Text.Trim();
        if (!string.Equals(_config.TargetWindowTitle, newTitle, StringComparison.Ordinal))
        {
            _config.TargetWindowTitle = newTitle;
        }

        _config.SaveUserSettings();

        if (ListenUrlChanged)
        {
            MessageBox.Show(
                this,
                $"設定を保存しました。\n({HostConfig.SettingsFilePath})\n\n※待受 URL の変更 ({newListenUrl}) は次回起動時から反映されます。",
                "設定保存完了",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }

        DialogResult = DialogResult.OK;
        Close();
    }
}
