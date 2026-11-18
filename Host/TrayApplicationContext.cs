using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace SingleAppRemote.Host;

public sealed class TrayApplicationContext : ApplicationContext
{
    private readonly HostConfig _config;
    private readonly string _listenUrl;
    private readonly Action _onExitRequested;
    private readonly Icon _appIcon;
    private readonly NotifyIcon _notifyIcon;
    private readonly ContextMenuStrip _contextMenu;
    private readonly ToolStripMenuItem _modeMenu;
    private readonly ToolStripMenuItem _qualityMenu;
    private readonly ToolStripMenuItem _fpsMenu;
    private readonly ToolStripMenuItem _statusItem;
    private readonly SynchronizationContext? _uiSyncContext;
    private readonly Action _targetChangedHandler;
    private Form? _activeDialog;

    public TrayApplicationContext(HostConfig config, string listenUrl, Action onExitRequested)
    {
        _config = config;
        _listenUrl = listenUrl;
        _onExitRequested = onExitRequested;
        _uiSyncContext = SynchronizationContext.Current;
        _appIcon = AppIconGenerator.CreateAppIcon(64);

        _contextMenu = new ContextMenuStrip();

        _statusItem = new ToolStripMenuItem($"対象: {_config.TargetWindowTitle}")
        {
            Enabled = false
        };
        _contextMenu.Items.Add(_statusItem);
        _contextMenu.Items.Add(new ToolStripSeparator());

        // 対象ウィンドウの選択（検索付きモーダルダイアログ・ホストPC側専用）
        var openPickerItem = new ToolStripMenuItem("キャプチャ対象ウィンドウの選択 (検索ダイアログ)...");
        openPickerItem.Font = new Font(openPickerItem.Font, FontStyle.Bold);
        openPickerItem.Click += (_, _) => ShowWindowPickerDialog();
        _contextMenu.Items.Add(openPickerItem);

        // 入力モード切替（ホストPC側専用）
        _modeMenu = new ToolStripMenuItem("入力モード切替");
        RebuildModeMenu();
        _contextMenu.Items.Add(_modeMenu);

        // 画質切替
        _qualityMenu = new ToolStripMenuItem("JPEG画質 (Quality)");
        RebuildQualityMenu();
        _contextMenu.Items.Add(_qualityMenu);

        // FPS切替
        _fpsMenu = new ToolStripMenuItem("最大フレームレート (FPS)");
        RebuildFpsMenu();
        _contextMenu.Items.Add(_fpsMenu);

        // カーソルキャプチャ切替
        var cursorItem = new ToolStripMenuItem("ホストのマウスカーソルを映像に含める")
        {
            Checked = _config.CaptureCursor,
            CheckOnClick = true
        };
        cursorItem.CheckedChanged += (_, _) =>
        {
            _config.CaptureCursor = cursorItem.Checked;
            _config.SaveUserSettings();
        };
        _contextMenu.Items.Add(cursorItem);

        // Kiosk保護（シェル・ファイルダイアログ系ショートカット遮断）切替
        var shellGuardItem = new ToolStripMenuItem("Kiosk保護: ファイル開く/保存等のショートカット遮断")
        {
            Checked = _config.BlockShellShortcuts,
            CheckOnClick = true
        };
        shellGuardItem.CheckedChanged += (_, _) =>
        {
            _config.BlockShellShortcuts = shellGuardItem.Checked;
            _config.SaveUserSettings();
        };
        _contextMenu.Items.Add(shellGuardItem);

        _contextMenu.Items.Add(new ToolStripSeparator());

        // 設定ダイアログを開く
        var openSettingsItem = new ToolStripMenuItem("設定 (ネットワーク・認証・Kiosk保護)...");
        openSettingsItem.Click += (_, _) => ShowSettingsDialog();
        _contextMenu.Items.Add(openSettingsItem);

        var openBrowserItem = new ToolStripMenuItem($"ローカルブラウザで開く ({GetLocalBrowserDisplayUrl()})");
        openBrowserItem.Click += (_, _) =>
        {
            try
            {
                Process.Start(new ProcessStartInfo(GetLocalBrowserUrl()) { UseShellExecute = true });
            }
            catch { }
        };
        _contextMenu.Items.Add(openBrowserItem);

        _contextMenu.Items.Add(new ToolStripSeparator());

        var exitItem = new ToolStripMenuItem("終了 (Exit)");
        exitItem.Click += (_, _) => ExitServer();
        _contextMenu.Items.Add(exitItem);

        _contextMenu.Opening += (_, _) =>
        {
            var (state, title) = _config.GetTargetStatus();
            string stateJa = state switch
            {
                "active" => "キャプチャ中",
                "minimized" => "最小化中",
                "unselected" => "未選択",
                _ => "未検出/クローズ"
            };
            string dispTitle = string.IsNullOrWhiteSpace(title) ? "(未選択)" : title;
            _statusItem.Text = $"現在の対象: {dispTitle} ({stateJa})";
            cursorItem.Checked = _config.CaptureCursor;
            shellGuardItem.Checked = _config.BlockShellShortcuts;
            RebuildModeMenu();
            RebuildQualityMenu();
            RebuildFpsMenu();
        };

        _notifyIcon = new NotifyIcon
        {
            Icon = _appIcon,
            Text = FormatNotifyIconText(_config.TargetWindowTitle),
            ContextMenuStrip = _contextMenu,
            Visible = true
        };

        // タスクトレイアイコンの左クリック（またはダブルクリック）でウィンドウ選択ダイアログを開く
        _notifyIcon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                ShowWindowPickerDialog();
            }
        };

        _targetChangedHandler = () =>
        {
            void UpdateText()
            {
                if (_notifyIcon.Visible)
                {
                    _notifyIcon.Text = FormatNotifyIconText(_config.TargetWindowTitle);
                }
            }

            if (_uiSyncContext != null && SynchronizationContext.Current != _uiSyncContext)
            {
                _uiSyncContext.Post(_ => UpdateText(), null);
            }
            else
            {
                UpdateText();
            }
        };
        _config.TargetChanged += _targetChangedHandler;
    }

    private static string FormatNotifyIconText(string? targetTitle)
    {
        string t = string.IsNullOrWhiteSpace(targetTitle) ? "未選択" : targetTitle;
        string txt = $"Single-App Remote ({t})";
        if (txt.Length <= 63) return txt;
        int cut = char.IsHighSurrogate(txt[62]) ? 62 : 63;
        return txt[..cut];
    }

    private string GetLocalBrowserDisplayUrl()
        => _listenUrl.Replace("0.0.0.0", "localhost").Replace("*", "localhost").Replace("+", "localhost");

    private string GetLocalBrowserUrl()
    {
        string baseUrl = GetLocalBrowserDisplayUrl().TrimEnd('/');
        string token = _config.AccessToken;
        if (!string.IsNullOrEmpty(token))
        {
            return $"{baseUrl}/?token={Uri.EscapeDataString(token)}";
        }
        return baseUrl;
    }

    private void ShowWindowPickerDialog()
    {
        if (_activeDialog != null && !_activeDialog.IsDisposed)
        {
            _activeDialog.Activate();
            return;
        }

        using var dlg = new WindowPickerDialog(_config.TargetHwnd, _appIcon);
        _activeDialog = dlg;
        _config.IsHostDialogOpen = true;
        try
        {
            if (dlg.ShowDialog() == DialogResult.OK && dlg.SelectedWindow is { } selected)
            {
                _config.SetTargetWindow(selected.Hwnd, selected.Title, selected.ProcessName);
            }
        }
        finally
        {
            _config.IsHostDialogOpen = false;
            _activeDialog = null;
        }
    }

    private void ShowSettingsDialog()
    {
        if (_activeDialog != null && !_activeDialog.IsDisposed)
        {
            _activeDialog.Activate();
            return;
        }

        using var dlg = new SettingsDialog(_config, _appIcon);
        _activeDialog = dlg;
        _config.IsHostDialogOpen = true;
        try
        {
            dlg.ShowDialog();
        }
        finally
        {
            _config.IsHostDialogOpen = false;
            _activeDialog = null;
        }
    }

    private void RebuildModeMenu()
    {
        _modeMenu.DropDownItems.Clear();
        AddModeItem("SendInput + カーソル位置即時復帰 (推奨・汎用)", InputMode.SendInputRestoreCursor);
        AddModeItem("SendInput 直接移動 (ドラッグ重視・仮想モニタ向け)", InputMode.SendInputDirect);
        AddModeItem("PostMessage (完全バックグラウンド・Win32アプリ向け)", InputMode.PostMessage);
    }

    private void AddModeItem(string label, InputMode mode)
    {
        var item = new ToolStripMenuItem(label)
        {
            Checked = _config.CurrentInputMode == mode
        };
        item.Click += (_, _) =>
        {
            _config.CurrentInputMode = mode;
            _config.SaveUserSettings();
        };
        _modeMenu.DropDownItems.Add(item);
    }

    private void RebuildQualityMenu()
    {
        _qualityMenu.DropDownItems.Clear();
        foreach (long q in new[] { 50L, 75L, 85L, 95L })
        {
            var item = new ToolStripMenuItem($"{q}%")
            {
                Checked = _config.JpegQuality == q
            };
            item.Click += (_, _) =>
            {
                _config.JpegQuality = q;
                _config.SaveUserSettings();
            };
            _qualityMenu.DropDownItems.Add(item);
        }
    }

    private void RebuildFpsMenu()
    {
        _fpsMenu.DropDownItems.Clear();
        foreach (int fps in new[] { 15, 30, 60 })
        {
            var item = new ToolStripMenuItem($"{fps} FPS")
            {
                Checked = _config.TargetFps == fps
            };
            item.Click += (_, _) =>
            {
                _config.TargetFps = fps;
                _config.SaveUserSettings();
            };
            _fpsMenu.DropDownItems.Add(item);
        }
    }

    private void ExitServer()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _onExitRequested();
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _config.TargetChanged -= _targetChangedHandler;
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _contextMenu.Dispose();
            _appIcon.Dispose();
        }
        base.Dispose(disposing);
    }
}
