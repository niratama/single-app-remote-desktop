using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using SingleAppRemote.Interop;

namespace SingleAppRemote.Host;

public enum InputMode
{
    /// <summary>
    /// 元のカーソル位置を保存し、対象ウィンドウをクリック後に即座にカーソル位置を復元する方式（全アプリ対応・推奨）
    /// </summary>
    SendInputRestoreCursor,

    /// <summary>
    /// カーソルを移動したままにする通常の SendInput 方式（ドラッグ操作や専用仮想ディスプレイ運用向け）
    /// </summary>
    SendInputDirect,

    /// <summary>
    /// カーソルやフォーカスを一切奪わずにウィンドウメッセージを直接送信する方式（従来のWin32アプリ向け）
    /// </summary>
    PostMessage
}

public sealed record WindowEntry(IntPtr Hwnd, string Title, string ProcessName, uint ProcessId)
{
    public override string ToString() => $"{Title} [{ProcessName} / PID: {ProcessId}]";
}

public sealed class HostConfig
{
    public static string SettingsDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SingleAppRemote");

    public static string SettingsFilePath =>
        Path.Combine(SettingsDirectory, "settings.json");

    private readonly object _lock = new();
    private string _listenUrl = "http://0.0.0.0:8000";
    private IntPtr _targetHwnd = IntPtr.Zero;
    private string _targetWindowTitle = "";
    private string _targetProcessName = "";
    private int _targetFps = 30;
    private long _jpegQuality = 75L;
    private InputMode _inputMode = InputMode.SendInputRestoreCursor;
    private bool _captureCursor = false;
    private bool _blockShellShortcuts = true;
    private string _accessToken = "";
    private volatile bool _isHostDialogOpen = false;

    public event Action? TargetChanged;
    public event Action? QualityChanged;

    public string ListenUrl
    {
        get { lock (_lock) return _listenUrl; }
        set { lock (_lock) _listenUrl = string.IsNullOrWhiteSpace(value) ? "http://0.0.0.0:8000" : value.Trim(); }
    }

    public bool IsHostDialogOpen
    {
        get => _isHostDialogOpen;
        set => _isHostDialogOpen = value;
    }

    public bool BlockShellShortcuts
    {
        get { lock (_lock) return _blockShellShortcuts; }
        set { lock (_lock) _blockShellShortcuts = value; }
    }

    public string AccessToken
    {
        get { lock (_lock) return _accessToken; }
        set { lock (_lock) _accessToken = value ?? ""; }
    }

    public string TargetWindowTitle
    {
        get { lock (_lock) return _targetWindowTitle; }
        set
        {
            lock (_lock)
            {
                _targetWindowTitle = value;
                _targetProcessName = "";
                _targetHwnd = IntPtr.Zero;
            }
            TargetChanged?.Invoke();
        }
    }

    public IntPtr TargetHwnd
    {
        get
        {
            lock (_lock)
            {
                if (_targetHwnd != IntPtr.Zero && NativeMethods.IsWindow(_targetHwnd) && NativeMethods.IsWindowVisible(_targetHwnd))
                {
                    return _targetHwnd;
                }
                _targetHwnd = FindWindowByTitleSubstring(_targetWindowTitle, _targetProcessName, out string matchedProc);
                if (_targetHwnd != IntPtr.Zero && string.IsNullOrEmpty(_targetProcessName) && !string.IsNullOrEmpty(matchedProc))
                {
                    _targetProcessName = matchedProc;
                }
                return _targetHwnd;
            }
        }
    }

    /// <summary>
    /// クライアント通知用の現在ステータス ("active" | "minimized" | "closed" | "unselected")
    /// </summary>
    public (string state, string title) GetTargetStatus()
    {
        var title = TargetWindowTitle;
        if (string.IsNullOrWhiteSpace(title) && _targetHwnd == IntPtr.Zero)
        {
            return ("unselected", "");
        }
        var hwnd = TargetHwnd;
        if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd) || !NativeMethods.IsWindowVisible(hwnd))
        {
            return ("closed", title);
        }
        if (NativeMethods.IsIconic(hwnd))
        {
            return ("minimized", title);
        }
        return ("active", title);
    }

    public void SetTargetWindow(IntPtr hwnd, string title, string processName = "")
    {
        lock (_lock)
        {
            _targetHwnd = hwnd;
            _targetWindowTitle = title;
            _targetProcessName = processName;
        }
        TargetChanged?.Invoke();
    }

    public void ForceRefreshTarget()
    {
        lock (_lock)
        {
            if (_targetHwnd == IntPtr.Zero || !NativeMethods.IsWindow(_targetHwnd) || !NativeMethods.IsWindowVisible(_targetHwnd))
            {
                _targetHwnd = FindWindowByTitleSubstring(_targetWindowTitle, _targetProcessName, out string matchedProc);
                if (_targetHwnd != IntPtr.Zero && string.IsNullOrEmpty(_targetProcessName) && !string.IsNullOrEmpty(matchedProc))
                {
                    _targetProcessName = matchedProc;
                }
            }
            if (_targetHwnd != IntPtr.Zero && NativeMethods.IsIconic(_targetHwnd))
            {
                NativeMethods.ShowWindow(_targetHwnd, NativeMethods.SW_RESTORE);
            }
        }
        TargetChanged?.Invoke();
    }

    public int TargetFps
    {
        get { lock (_lock) return _targetFps; }
        set { lock (_lock) _targetFps = Math.Clamp(value, 1, 60); }
    }

    public long JpegQuality
    {
        get { lock (_lock) return _jpegQuality; }
        set
        {
            lock (_lock) _jpegQuality = Math.Clamp(value, 10L, 100L);
            QualityChanged?.Invoke();
        }
    }

    public InputMode CurrentInputMode
    {
        get { lock (_lock) return _inputMode; }
        set { lock (_lock) _inputMode = value; }
    }

    public bool CaptureCursor
    {
        get { lock (_lock) return _captureCursor; }
        set
        {
            lock (_lock) _captureCursor = value;
            TargetChanged?.Invoke();
        }
    }

    /// <summary>
    /// 現在の設定を %LocalAppData%\SingleAppRemote\settings.json へ保存する
    /// </summary>
    public bool SaveUserSettings()
    {
        try
        {
            Directory.CreateDirectory(SettingsDirectory);

            object payload;
            lock (_lock)
            {
                payload = new
                {
                    RemoteSettings = new
                    {
                        ListenUrl = _listenUrl,
                        TargetWindowTitle = _targetWindowTitle,
                        TargetFps = _targetFps,
                        JpegQuality = _jpegQuality,
                        InputMode = _inputMode.ToString(),
                        CaptureCursor = _captureCursor,
                        BlockShellShortcuts = _blockShellShortcuts,
                        AccessToken = _accessToken
                    }
                };
            }

            var jsonOptions = new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            };
            string json = JsonSerializer.Serialize(payload, jsonOptions);
            File.WriteAllText(SettingsFilePath, json, Encoding.UTF8);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static IntPtr FindWindowByTitleSubstring(string titleSubstring)
        => FindWindowByTitleSubstring(titleSubstring, "", out _);

    public static IntPtr FindWindowByTitleSubstring(string titleSubstring, string requiredProcessName, out string matchedProcessName)
    {
        matchedProcessName = "";
        if (string.IsNullOrWhiteSpace(titleSubstring)) return IntPtr.Zero;
        IntPtr found = IntPtr.Zero;
        string foundProcName = "";
        uint currentPid = (uint)Environment.ProcessId;

        NativeMethods.EnumWindows((hwnd, _) =>
        {
            if (!NativeMethods.IsWindowVisible(hwnd)) return true;
            if (NativeMethods.DwmGetWindowAttribute(hwnd, NativeMethods.DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0)
            {
                return true;
            }

            NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == currentPid) return true;

            int len = NativeMethods.GetWindowTextLength(hwnd);
            if (len == 0) return true;
            var sb = new StringBuilder(len + 1);
            NativeMethods.GetWindowText(hwnd, sb, sb.Capacity);
            if (sb.ToString().Contains(titleSubstring, StringComparison.OrdinalIgnoreCase))
            {
                if (!NativeMethods.IsIconic(hwnd) &&
                    NativeMethods.GetWindowRect(hwnd, out var rect) &&
                    (rect.Width < 16 || rect.Height < 16))
                {
                    return true;
                }

                string procName = "";
                try
                {
                    using var proc = Process.GetProcessById((int)pid);
                    procName = proc.ProcessName;
                }
                catch { }

                if (!string.IsNullOrEmpty(requiredProcessName) &&
                    !string.Equals(procName, requiredProcessName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                found = hwnd;
                foundProcName = procName;
                return false;
            }
            return true;
        }, IntPtr.Zero);

        matchedProcessName = foundProcName;
        return found;
    }

    public static List<WindowEntry> GetVisibleWindows()
    {
        var list = new List<WindowEntry>();
        uint currentPid = (uint)Environment.ProcessId;
        var procNameCache = new Dictionary<uint, string>();

        NativeMethods.EnumWindows((hwnd, _) =>
        {
            if (!NativeMethods.IsWindowVisible(hwnd)) return true;

            if (NativeMethods.DwmGetWindowAttribute(hwnd, NativeMethods.DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0)
            {
                return true;
            }

            NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == currentPid) return true;

            int len = NativeMethods.GetWindowTextLength(hwnd);
            if (len == 0) return true;

            var sb = new StringBuilder(len + 1);
            NativeMethods.GetWindowText(hwnd, sb, sb.Capacity);
            var title = sb.ToString().Trim();
            if (!string.IsNullOrEmpty(title) &&
                title != "Program Manager" &&
                !title.StartsWith("_SoftwareUpdateNotificationService", StringComparison.OrdinalIgnoreCase))
            {
                // サイズが 0 または極小の非表示ダミーウィンドウ（最小化中を除く）は一覧から除外
                if (!NativeMethods.IsIconic(hwnd) &&
                    NativeMethods.GetWindowRect(hwnd, out var rect) &&
                    (rect.Width < 16 || rect.Height < 16))
                {
                    return true;
                }

                if (!procNameCache.TryGetValue(pid, out var procName))
                {
                    try
                    {
                        using var proc = Process.GetProcessById((int)pid);
                        procName = proc.ProcessName;
                    }
                    catch
                    {
                        procName = "unknown";
                    }
                    procNameCache[pid] = procName;
                }
                list.Add(new WindowEntry(hwnd, title, procName, pid));
            }
            return true;
        }, IntPtr.Zero);

        return list.OrderBy(w => w.Title).ToList();
    }
}
