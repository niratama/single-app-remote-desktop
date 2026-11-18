using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Channels;
using SingleAppRemote.Host;
using SingleAppRemote.Interop;

namespace SingleAppRemote.Input;

public static class WindowInputHelper
{
    private const int MaxQueuedInputs = 64;
    private const int MaxTextLength = 256;

    private sealed record QueuedInput(HostConfig Config, JsonElement Payload);

    private static readonly Channel<QueuedInput> InputChannel = Channel.CreateBounded<QueuedInput>(
        new BoundedChannelOptions(MaxQueuedInputs)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });

    private static readonly object StateLock = new();
    private static NativeMethods.POINT? _savedHostCursor;
    private static CancellationTokenSource? _restoreCursorCts;
    private static bool _isDragging;
    private static int _dragButton;
    private static InputMode _lastDragMode = InputMode.SendInputRestoreCursor;

    static WindowInputHelper()
    {
        // 入力イベントを順序通りに高速消化する専用バックグラウンドワーカー
        _ = Task.Run(ProcessInputQueueAsync);
    }

    public static void HandleClientMessage(HostConfig config, JsonElement root)
    {
        if (config.IsHostDialogOpen) return;

        // JsonDocument が Dispose されても安全なように Clone して非同期キューへ即座に積む（WebSocket受信を一切止めない）
        InputChannel.Writer.TryWrite(new QueuedInput(config, root.Clone()));
    }

    /// <summary>
    /// クライアント切断時またはターゲット変更時に滞留キューを破棄し、ドラッグ押しっぱなし状態を安全に解除する
    /// </summary>
    public static void OnClientDisconnected(InputMode currentMode)
    {
        while (InputChannel.Reader.TryRead(out _)) { }
        ReleaseDragIfActive(currentMode);
    }

    public static void ReleaseDragIfActive(InputMode mode)
    {
        bool wasDragging = false;
        int btn = 0;
        InputMode activeMode = mode;

        lock (StateLock)
        {
            if (_isDragging)
            {
                wasDragging = true;
                btn = _dragButton;
                activeMode = _lastDragMode;
                _isDragging = false;
                _dragButton = 0;
            }
        }

        if (wasDragging && activeMode != InputMode.PostMessage)
        {
            uint upFlag = btn == 2 ? NativeMethods.MOUSEEVENTF_RIGHTUP : NativeMethods.MOUSEEVENTF_LEFTUP;
            NativeMethods.mouse_event(upFlag, 0, 0, 0, UIntPtr.Zero);
            ScheduleCursorRestore(activeMode, 50);
        }
    }

    private static async Task ProcessInputQueueAsync()
    {
        await foreach (var item in InputChannel.Reader.ReadAllAsync())
        {
            try
            {
                ProcessSingleMessage(item.Config, item.Payload);
            }
            catch
            {
                // 個別の入力エラーでワーカーを止めない
            }
        }
    }

    private static void ProcessSingleMessage(HostConfig config, JsonElement root)
    {
        if (config.IsHostDialogOpen) return;
        if (!root.TryGetProperty("type", out var typeProp) || typeProp.ValueKind != JsonValueKind.String) return;
        var type = typeProp.GetString();
        if (string.IsNullOrEmpty(type)) return;

        var hwnd = config.TargetHwnd;
        if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd)) return;

        var mode = config.CurrentInputMode;

        switch (type)
        {
            case "click":
            case "rightclick":
            {
                if (!TryGetClampedCoords(hwnd, root, out int absX, out int absY)) return;
                ExecutePointerClick(hwnd, mode, absX, absY, type);
                break;
            }
            case "pointerdown":
            case "pointermove":
            case "pointerup":
            {
                if (!TryGetClampedCoords(hwnd, root, out int absX, out int absY)) return;
                int button = root.TryGetProperty("button", out var btnProp) &&
                             btnProp.ValueKind == JsonValueKind.Number &&
                             btnProp.TryGetInt32(out int b) && b == 2 ? 2 : 0;
                ExecutePointerDrag(hwnd, mode, absX, absY, type, button);
                break;
            }
            case "wheel":
            {
                if (!TryGetClampedCoords(hwnd, root, out int absX, out int absY)) return;
                int deltaY = root.TryGetProperty("deltaY", out var dyProp) &&
                             dyProp.ValueKind == JsonValueKind.Number &&
                             dyProp.TryGetDouble(out double rawDy) &&
                             double.IsFinite(rawDy)
                    ? Math.Sign(rawDy)
                    : 0;
                ExecuteMouseWheel(hwnd, mode, absX, absY, deltaY);
                break;
            }
            case "key":
            {
                string key = root.TryGetProperty("key", out var kProp) && kProp.ValueKind == JsonValueKind.String ? kProp.GetString() ?? "" : "";
                string code = root.TryGetProperty("code", out var cProp) && cProp.ValueKind == JsonValueKind.String ? cProp.GetString() ?? "" : "";
                bool alt = root.TryGetProperty("altKey", out var aProp) && aProp.ValueKind is JsonValueKind.True or JsonValueKind.False && aProp.GetBoolean();
                bool ctrl = root.TryGetProperty("ctrlKey", out var ctrlProp) && ctrlProp.ValueKind is JsonValueKind.True or JsonValueKind.False && ctrlProp.GetBoolean();
                bool shift = root.TryGetProperty("shiftKey", out var sProp) && sProp.ValueKind is JsonValueKind.True or JsonValueKind.False && sProp.GetBoolean();
                bool meta = root.TryGetProperty("metaKey", out var mProp) && mProp.ValueKind is JsonValueKind.True or JsonValueKind.False && mProp.GetBoolean();
                ExecuteKeyPress(hwnd, mode, key, code, ctrl, alt, shift, meta, config.BlockShellShortcuts);
                break;
            }
            case "text":
            {
                string text = root.TryGetProperty("text", out var tProp) && tProp.ValueKind == JsonValueKind.String ? tProp.GetString() ?? "" : "";
                if (!string.IsNullOrEmpty(text))
                {
                    ExecuteTextInput(hwnd, mode, text);
                }
                break;
            }
        }
    }

    /// <summary>
    /// 連続操作の最初にだけホストのカーソル座標を保存し、復帰タイマーをキャンセルする
    /// </summary>
    private static void SaveCursorBeforeAction(InputMode mode)
    {
        if (mode != InputMode.SendInputRestoreCursor) return;

        lock (StateLock)
        {
            _restoreCursorCts?.Cancel();
            _restoreCursorCts?.Dispose();
            _restoreCursorCts = null;

            if (_savedHostCursor == null && NativeMethods.GetCursorPos(out var cur))
            {
                _savedHostCursor = cur;
            }
        }
    }

    /// <summary>
    /// 連続クリックが途切れてから一定時間後（かつ後続キューが空の時）にだけ元のカーソル位置へ戻す。
    /// これにより連打中に物理マウスが引き戻されて座標が狂う現象を防ぐ。
    /// </summary>
    private static void ScheduleCursorRestore(InputMode mode, int delayMs = 220)
    {
        if (mode != InputMode.SendInputRestoreCursor) return;

        // すでに次のクリックがキューに溜まっている場合はカーソルを戻さず即座に次の処理へ進む
        if (InputChannel.Reader.Count > 0) return;

        lock (StateLock)
        {
            _restoreCursorCts?.Cancel();
            _restoreCursorCts?.Dispose();
            var cts = new CancellationTokenSource();
            _restoreCursorCts = cts;

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(delayMs, cts.Token);
                    lock (StateLock)
                    {
                        if (!cts.Token.IsCancellationRequested && _savedHostCursor is { } pos && !_isDragging)
                        {
                            NativeMethods.SetCursorPos(pos.X, pos.Y);
                            _savedHostCursor = null;
                        }
                    }
                }
                catch (OperationCanceledException) { }
            });
        }
    }

    private static bool TryGetClampedCoords(IntPtr hwnd, JsonElement root, out int absX, out int absY)
    {
        absX = 0;
        absY = 0;
        if (!root.TryGetProperty("x", out var xProp) || !root.TryGetProperty("y", out var yProp))
            return false;
        if (xProp.ValueKind != JsonValueKind.Number || yProp.ValueKind != JsonValueKind.Number)
            return false;
        if (!xProp.TryGetDouble(out double rawX) || !yProp.TryGetDouble(out double rawY))
            return false;
        if (!double.IsFinite(rawX) || !double.IsFinite(rawY))
            return false;

        // 【重要】最小化中のウィンドウは座標が (-32000, -32000) またはタスクバー左端（スタートボタン付近）になるため、
        // 座標計算をする前に必ず最小化を解除し、DWM アニメーションが終わって正常な画面内矩形に戻るまで待機する。
        if (NativeMethods.IsIconic(hwnd))
        {
            NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);
            for (int i = 0; i < 20; i++)
            {
                Thread.Sleep(15);
                if (!NativeMethods.IsIconic(hwnd) &&
                    NativeMethods.GetEffectiveWindowRect(hwnd, out var restoredRect) &&
                    IsValidOnScreenRect(restoredRect))
                {
                    break;
                }
            }
            // 復帰直後の描画安定待ち
            Thread.Sleep(30);
        }

        if (!NativeMethods.GetEffectiveWindowRect(hwnd, out var rect) || !IsValidOnScreenRect(rect))
        {
            // まだ最小化中・オフスクリーン異常座標の場合はクリックを破棄してスタートメニュー誤爆を防ぐ
            return false;
        }

        double relX = Math.Clamp(rawX, 0.0, 1.0);
        double relY = Math.Clamp(rawY, 0.0, 1.0);

        int width = Math.Max(1, rect.Width);
        int height = Math.Max(1, rect.Height);

        // ウィンドウ境界外へのクリックを完全に防ぐため1px内側にクランプ
        absX = Math.Clamp(rect.Left + (int)(width * relX), rect.Left, Math.Max(rect.Left, rect.Right - 1));
        absY = Math.Clamp(rect.Top + (int)(height * relY), rect.Top, Math.Max(rect.Top, rect.Bottom - 1));
        return true;
    }

    private static bool IsValidOnScreenRect(NativeMethods.RECT rect)
    {
        // 最小化時の特殊座標 (-32000, -32000) や極小矩形を弾く
        return rect.Left > -10000 && rect.Top > -10000 && rect.Width >= 32 && rect.Height >= 32;
    }

    /// <summary>
    /// 指定したスクリーン座標の最前面ウィンドウが対象ウィンドウ（またはその子/所有ポップアップ）であるかを検証し、
    /// 前面に重なった別ウィンドウや自身のダイアログへのクリック貫通を防ぐ。
    /// </summary>
    private static bool IsPointOnTargetWindow(IntPtr targetHwnd, int screenX, int screenY)
    {
        IntPtr hit = NativeMethods.WindowFromPoint(new NativeMethods.POINT { X = screenX, Y = screenY });
        if (hit == IntPtr.Zero) return false;

        NativeMethods.GetWindowThreadProcessId(hit, out uint hitPid);
        if (hitPid == (uint)Environment.ProcessId) return false;

        if (hit == targetHwnd) return true;
        if (NativeMethods.GetAncestor(hit, NativeMethods.GA_ROOT) == targetHwnd) return true;
        if (NativeMethods.GetAncestor(hit, NativeMethods.GA_ROOTOWNER) == targetHwnd) return true;

        NativeMethods.GetWindowThreadProcessId(targetHwnd, out uint targetPid);
        return hitPid != 0 && hitPid == targetPid;
    }

    /// <summary>
    /// キーボード入力注入時にフォアグラウンドウィンドウが対象ウィンドウであることを検証する。
    /// </summary>
    private static bool IsTargetWindowForeground(IntPtr targetHwnd)
    {
        IntPtr fg = NativeMethods.GetForegroundWindow();
        if (fg == IntPtr.Zero) return false;

        NativeMethods.GetWindowThreadProcessId(fg, out uint fgPid);
        if (fgPid == (uint)Environment.ProcessId) return false;

        if (fg == targetHwnd) return true;
        if (NativeMethods.GetAncestor(fg, NativeMethods.GA_ROOT) == targetHwnd) return true;
        if (NativeMethods.GetAncestor(fg, NativeMethods.GA_ROOTOWNER) == targetHwnd) return true;

        NativeMethods.GetWindowThreadProcessId(targetHwnd, out uint targetPid);
        return fgPid != 0 && fgPid == targetPid;
    }

    private static void EnsureWindowActive(IntPtr hwnd)
    {
        if (NativeMethods.IsIconic(hwnd))
        {
            NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);
        }

        IntPtr fg = NativeMethods.GetForegroundWindow();
        if (fg == hwnd) return; // すでにアクティブなら待機ゼロで即通過！

        uint fgThread = NativeMethods.GetWindowThreadProcessId(fg, out _);
        uint currentThread = NativeMethods.GetCurrentThreadId();
        bool attached = false;

        try
        {
            if (fgThread != 0 && fgThread != currentThread)
            {
                attached = NativeMethods.AttachThreadInput(currentThread, fgThread, true);
            }
            NativeMethods.BringWindowToTop(hwnd);
            NativeMethods.SetForegroundWindow(hwnd);
        }
        finally
        {
            if (attached)
            {
                NativeMethods.AttachThreadInput(currentThread, fgThread, false);
            }
        }

        // 別ウィンドウからフォーカスを切り替えた初回のみ25ms待機
        Thread.Sleep(25);
    }

    private static void ExecutePointerClick(IntPtr hwnd, InputMode mode, int absX, int absY, string clickType)
    {
        if (mode == InputMode.PostMessage)
        {
            var (targetChild, clientPt) = ResolveChildClientPoint(hwnd, absX, absY);
            IntPtr lParam = NativeMethods.MakeLParam(clientPt.X, clientPt.Y);

            if (clickType == "rightclick")
            {
                NativeMethods.PostMessage(targetChild, NativeMethods.WM_RBUTTONDOWN, (IntPtr)NativeMethods.MK_RBUTTON, lParam);
                NativeMethods.PostMessage(targetChild, NativeMethods.WM_RBUTTONUP, IntPtr.Zero, lParam);
            }
            else
            {
                NativeMethods.PostMessage(targetChild, NativeMethods.WM_LBUTTONDOWN, (IntPtr)NativeMethods.MK_LBUTTON, lParam);
                NativeMethods.PostMessage(targetChild, NativeMethods.WM_LBUTTONUP, IntPtr.Zero, lParam);
            }
            return;
        }

        SaveCursorBeforeAction(mode);
        EnsureWindowActive(hwnd);
        if (!IsPointOnTargetWindow(hwnd, absX, absY))
        {
            ScheduleCursorRestore(mode);
            return;
        }

        // 1. カーソル移動 + MOVE イベントで UWP/XAML にボタン上のホバーを即認識させる
        NativeMethods.SetCursorPos(absX, absY);
        NativeMethods.mouse_event(NativeMethods.MOUSEEVENTF_MOVE, 0, 0, 0, UIntPtr.Zero);
        Thread.Sleep(10);

        // 2. クリック注入（連打に追従できるよう最小限の押下時間に短縮）
        if (clickType == "rightclick")
        {
            NativeMethods.mouse_event(NativeMethods.MOUSEEVENTF_RIGHTDOWN, 0, 0, 0, UIntPtr.Zero);
            Thread.Sleep(12);
            NativeMethods.mouse_event(NativeMethods.MOUSEEVENTF_RIGHTUP, 0, 0, 0, UIntPtr.Zero);
        }
        else
        {
            NativeMethods.mouse_event(NativeMethods.MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
            Thread.Sleep(12);
            NativeMethods.mouse_event(NativeMethods.MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
        }

        // 次のクリックとの間に最低8msの間隔だけ空け、カーソル復帰はデバウンスタイマーに任せる
        Thread.Sleep(8);
        ScheduleCursorRestore(mode);
    }

    private static void ExecutePointerDrag(IntPtr hwnd, InputMode mode, int absX, int absY, string evType, int button)
    {
        if (mode == InputMode.PostMessage)
        {
            var (targetChild, clientPt) = ResolveChildClientPoint(hwnd, absX, absY);
            IntPtr lParam = NativeMethods.MakeLParam(clientPt.X, clientPt.Y);
            if (evType == "pointerdown")
            {
                lock (StateLock)
                {
                    _isDragging = true;
                    _dragButton = button;
                    _lastDragMode = mode;
                }
                uint msg = button == 2 ? NativeMethods.WM_RBUTTONDOWN : NativeMethods.WM_LBUTTONDOWN;
                int mk = button == 2 ? NativeMethods.MK_RBUTTON : NativeMethods.MK_LBUTTON;
                NativeMethods.PostMessage(targetChild, msg, (IntPtr)mk, lParam);
            }
            else if (evType == "pointermove" && _isDragging)
            {
                int mk = _dragButton == 2 ? NativeMethods.MK_RBUTTON : NativeMethods.MK_LBUTTON;
                NativeMethods.PostMessage(targetChild, NativeMethods.WM_MOUSEMOVE, (IntPtr)mk, lParam);
            }
            else if (evType == "pointerup" && _isDragging)
            {
                int activeBtn;
                lock (StateLock)
                {
                    activeBtn = _dragButton;
                    _isDragging = false;
                    _dragButton = 0;
                }
                uint msg = activeBtn == 2 ? NativeMethods.WM_RBUTTONUP : NativeMethods.WM_LBUTTONUP;
                NativeMethods.PostMessage(targetChild, msg, IntPtr.Zero, lParam);
            }
            return;
        }

        if (evType == "pointerdown")
        {
            SaveCursorBeforeAction(mode);
            EnsureWindowActive(hwnd);
            if (!IsPointOnTargetWindow(hwnd, absX, absY))
            {
                ScheduleCursorRestore(mode);
                return;
            }
            lock (StateLock)
            {
                _isDragging = true;
                _dragButton = button;
                _lastDragMode = mode;
            }
            NativeMethods.SetCursorPos(absX, absY);
            uint downFlag = button == 2 ? NativeMethods.MOUSEEVENTF_RIGHTDOWN : NativeMethods.MOUSEEVENTF_LEFTDOWN;
            NativeMethods.mouse_event(downFlag, 0, 0, 0, UIntPtr.Zero);
        }
        else if (evType == "pointermove" && _isDragging)
        {
            if (!IsPointOnTargetWindow(hwnd, absX, absY)) return;
            NativeMethods.SetCursorPos(absX, absY);
        }
        else if (evType == "pointerup" && _isDragging)
        {
            int activeBtn;
            lock (StateLock)
            {
                activeBtn = _dragButton;
                _isDragging = false;
                _dragButton = 0;
            }
            if (IsPointOnTargetWindow(hwnd, absX, absY))
            {
                NativeMethods.SetCursorPos(absX, absY);
            }
            uint upFlag = activeBtn == 2 ? NativeMethods.MOUSEEVENTF_RIGHTUP : NativeMethods.MOUSEEVENTF_LEFTUP;
            NativeMethods.mouse_event(upFlag, 0, 0, 0, UIntPtr.Zero);
            ScheduleCursorRestore(mode);
        }
    }

    private static void ExecuteMouseWheel(IntPtr hwnd, InputMode mode, int absX, int absY, int deltaY)
    {
        if (deltaY == 0) return;
        int wheelDelta = deltaY < 0 ? 120 : -120;

        if (mode == InputMode.PostMessage)
        {
            var (targetChild, _) = ResolveChildClientPoint(hwnd, absX, absY);
            IntPtr wParam = (IntPtr)((wheelDelta << 16) & 0xFFFF0000);
            IntPtr lParam = NativeMethods.MakeLParam(absX, absY);
            NativeMethods.PostMessage(targetChild, NativeMethods.WM_MOUSEWHEEL, wParam, lParam);
            return;
        }

        SaveCursorBeforeAction(mode);
        EnsureWindowActive(hwnd);
        if (!IsPointOnTargetWindow(hwnd, absX, absY))
        {
            ScheduleCursorRestore(mode);
            return;
        }
        NativeMethods.SetCursorPos(absX, absY);
        NativeMethods.mouse_event(NativeMethods.MOUSEEVENTF_WHEEL, 0, 0, wheelDelta, UIntPtr.Zero);
        ScheduleCursorRestore(mode);
    }

    private static void ExecuteKeyPress(
        IntPtr hwnd, InputMode mode, string key, string code,
        bool ctrl, bool alt, bool shift, bool meta, bool blockShellShortcuts)
    {
        // Kiosk 脱出防止フィルタ: システムショートカット・Winキー・タスク切り替え・シェル起動等を遮断
        if (IsBlockedSystemShortcut(key, code, ctrl, alt, shift, meta, blockShellShortcuts))
        {
            return;
        }

        if (!TryMapVirtualKey(key, code, out ushort vk, out bool needsShift))
        {
            // マッピング外の単一文字（日本語や記号など）は Unicode テキスト送信にフォールバック
            if (key.Length == 1 && !ctrl && !alt)
            {
                ExecuteTextInput(hwnd, mode, key);
            }
            return;
        }

        bool useShift = shift || needsShift;

        if (mode == InputMode.PostMessage)
        {
            NativeMethods.PostMessage(hwnd, NativeMethods.WM_KEYDOWN, (IntPtr)vk, IntPtr.Zero);
            NativeMethods.PostMessage(hwnd, NativeMethods.WM_KEYUP, (IntPtr)vk, IntPtr.Zero);
            return;
        }

        EnsureWindowActive(hwnd);
        if (!IsTargetWindowForeground(hwnd)) return;

        var inputs = new List<NativeMethods.INPUT>(8);
        if (ctrl) AddKeyInput(inputs, 0x11, false); // VK_CONTROL
        if (useShift) AddKeyInput(inputs, 0x10, false); // VK_SHIFT
        if (alt) AddKeyInput(inputs, 0x12, false); // VK_MENU

        AddKeyInput(inputs, vk, false);
        AddKeyInput(inputs, vk, true);

        if (alt) AddKeyInput(inputs, 0x12, true);
        if (useShift) AddKeyInput(inputs, 0x10, true);
        if (ctrl) AddKeyInput(inputs, 0x11, true);

        var arr = inputs.ToArray();
        NativeMethods.SendInput((uint)arr.Length, arr, Marshal.SizeOf<NativeMethods.INPUT>());
        Thread.Sleep(5);
    }

    private static void ExecuteTextInput(IntPtr hwnd, InputMode mode, string text)
    {
        if (text.Length > MaxTextLength)
        {
            text = text[..MaxTextLength];
        }

        if (mode == InputMode.PostMessage)
        {
            foreach (char ch in text)
            {
                if (char.IsControl(ch)) continue;
                NativeMethods.PostMessage(hwnd, NativeMethods.WM_CHAR, (IntPtr)ch, IntPtr.Zero);
            }
            return;
        }

        EnsureWindowActive(hwnd);
        if (!IsTargetWindowForeground(hwnd)) return;

        var inputs = new List<NativeMethods.INPUT>(text.Length * 2);
        foreach (char ch in text)
        {
            if (char.IsControl(ch)) continue;
            inputs.Add(new NativeMethods.INPUT
            {
                type = NativeMethods.INPUT_KEYBOARD,
                u = new NativeMethods.InputUnion
                {
                    ki = new NativeMethods.KEYBDINPUT
                    {
                        wVk = 0,
                        wScan = ch,
                        dwFlags = NativeMethods.KEYEVENTF_UNICODE
                    }
                }
            });
            inputs.Add(new NativeMethods.INPUT
            {
                type = NativeMethods.INPUT_KEYBOARD,
                u = new NativeMethods.InputUnion
                {
                    ki = new NativeMethods.KEYBDINPUT
                    {
                        wVk = 0,
                        wScan = ch,
                        dwFlags = NativeMethods.KEYEVENTF_UNICODE | NativeMethods.KEYEVENTF_KEYUP
                    }
                }
            });
        }

        if (inputs.Count == 0) return;
        var arr = inputs.ToArray();
        NativeMethods.SendInput((uint)arr.Length, arr, Marshal.SizeOf<NativeMethods.INPUT>());
    }

    private static void AddKeyInput(List<NativeMethods.INPUT> list, ushort vk, bool keyUp)
    {
        list.Add(new NativeMethods.INPUT
        {
            type = NativeMethods.INPUT_KEYBOARD,
            u = new NativeMethods.InputUnion
            {
                ki = new NativeMethods.KEYBDINPUT
                {
                    wVk = vk,
                    dwFlags = keyUp ? NativeMethods.KEYEVENTF_KEYUP : 0
                }
            }
        });
    }

    public static bool IsBlockedSystemShortcut(
        string key, string code, bool ctrl, bool alt, bool shift, bool meta, bool blockShellShortcuts = true)
    {
        // Windows / Meta キーおよびコンテキストメニューキーは常に遮断
        if (meta ||
            key.Equals("Meta", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("OS", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("ContextMenu", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("Apps", StringComparison.OrdinalIgnoreCase) ||
            code.Equals("MetaLeft", StringComparison.OrdinalIgnoreCase) ||
            code.Equals("MetaRight", StringComparison.OrdinalIgnoreCase) ||
            code.Equals("ContextMenu", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // F1 (外部ブラウザ・Windowsヘルプ起動) および Shift+F10 (コンテキストメニュー) を遮断
        if (key.Equals("F1", StringComparison.OrdinalIgnoreCase) ||
            (shift && key.Equals("F10", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        // Alt + Tab / Alt + Esc / Alt + F4 / Alt + Space / Alt + D を遮断
        if (alt && (
            key.Equals("Tab", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("Escape", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("F4", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("d", StringComparison.OrdinalIgnoreCase) ||
            key.Equals(" ", StringComparison.OrdinalIgnoreCase) ||
            code.Equals("Space", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        // Ctrl + Esc / Ctrl + Shift + Esc / Ctrl + Alt + Del / Ctrl + F4 を遮断
        if (ctrl && (
            key.Equals("Escape", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("Delete", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("F4", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        // Kiosk保護有効時: ファイルダイアログ（エクスプローラーシェル経由の任意コマンド実行）や新規ウィンドウ起動ショートカットを遮断
        if (blockShellShortcuts && ctrl && (
            key.Equals("o", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("s", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("p", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("n", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("t", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("w", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("l", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("r", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return false;
    }

    private static bool TryMapVirtualKey(string key, string code, out ushort vk, out bool needsShift)
    {
        needsShift = false;

        // 演算子・テンキー対応（電卓などでキーボードから直接 +, -, *, /, ., = を打てるようにする）
        vk = key switch
        {
            "Backspace" => 0x08,
            "Tab" => 0x09,
            "Enter" => 0x0D,
            "=" => 0x0D,      // 電卓の「=」は Enter (VK_RETURN) で確実に動作
            "Escape" => 0x1B,
            " " => 0x20,
            "PageUp" => 0x21,
            "PageDown" => 0x22,
            "End" => 0x23,
            "Home" => 0x24,
            "ArrowLeft" => 0x25,
            "ArrowUp" => 0x26,
            "ArrowRight" => 0x27,
            "ArrowDown" => 0x28,
            "Insert" => 0x2D,
            "Delete" => 0x2E,
            "*" => 0x6A,      // VK_MULTIPLY
            "+" => 0x6B,      // VK_ADD
            "-" => 0x6D,      // VK_SUBTRACT
            "." => 0x6E,      // VK_DECIMAL
            "/" => 0x6F,      // VK_DIVIDE
            "F1" => 0x70,
            "F2" => 0x71,
            "F3" => 0x72,
            "F4" => 0x73,
            "F5" => 0x74,
            "F6" => 0x75,
            "F7" => 0x76,
            "F8" => 0x77,
            "F9" => 0x78,
            "F10" => 0x79,
            "F11" => 0x7A,
            "F12" => 0x7B,
            _ => 0
        };

        if (vk != 0) return true;

        if (key.Length == 1)
        {
            char upper = char.ToUpperInvariant(key[0]);
            if (upper is >= 'A' and <= 'Z' or >= '0' and <= '9')
            {
                vk = upper;
                return true;
            }
        }

        return false;
    }

    private static (IntPtr targetHwnd, NativeMethods.POINT clientPt) ResolveChildClientPoint(IntPtr rootHwnd, int screenX, int screenY)
    {
        IntPtr current = rootHwnd;
        const uint CWP_SKIPINVISIBLE = 0x0001;
        const uint CWP_SKIPDISABLED = 0x0002;
        const uint CWP_SKIPTRANSPARENT = 0x0004;

        for (int depth = 0; depth < 16; depth++)
        {
            var pt = new NativeMethods.POINT { X = screenX, Y = screenY };
            NativeMethods.ScreenToClient(current, ref pt);
            IntPtr child = NativeMethods.ChildWindowFromPointEx(
                current, pt, CWP_SKIPINVISIBLE | CWP_SKIPDISABLED | CWP_SKIPTRANSPARENT);

            if (child == IntPtr.Zero || child == current)
            {
                return (current, pt);
            }
            current = child;
        }

        var finalPt = new NativeMethods.POINT { X = screenX, Y = screenY };
        NativeMethods.ScreenToClient(current, ref finalPt);
        return (current, finalPt);
    }
}
