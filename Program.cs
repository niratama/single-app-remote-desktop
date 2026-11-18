using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;
using SingleAppRemote.Capture;
using SingleAppRemote.Host;
using SingleAppRemote.Input;
using SingleAppRemote.Interop;
using SingleAppRemote.Web;

namespace SingleAppRemote;

internal static class Program
{
    private const int MaxConcurrentClients = 4;

    [STAThread]
    public static void Main(string[] args)
    {
        NativeMethods.SetProcessDpiAwareness(2);

        // --list-windows オプション（ホストPCのコンソール確認用）
        if (args.Contains("--list-windows", StringComparer.OrdinalIgnoreCase))
        {
            Console.WriteLine("=== 現在表示中のウィンドウ一覧 ===");
            foreach (var win in HostConfig.GetVisibleWindows())
            {
                Console.WriteLine($"  - {win}");
            }
            return;
        }

        bool headless = args.Contains("--no-tray", StringComparer.OrdinalIgnoreCase);

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory
        });

        // Windows標準のユーザー設定 (%LocalAppData%\SingleAppRemote\settings.json) が存在すれば上書き読み込み
        builder.Configuration.AddJsonFile(HostConfig.SettingsFilePath, optional: true, reloadOnChange: false);

        var section = builder.Configuration.GetSection("RemoteSettings");
        string listenUrl = section["ListenUrl"] ?? "http://0.0.0.0:8000";

        var hostConfig = new HostConfig
        {
            ListenUrl = listenUrl,
            TargetWindowTitle = section["TargetWindowTitle"] ?? "",
            TargetFps = int.TryParse(section["TargetFps"], out int fps) ? fps : 30,
            JpegQuality = long.TryParse(section["JpegQuality"], out long q) ? q : 75L,
            CaptureCursor = bool.TryParse(section["CaptureCursor"], out bool cc) && cc,
            BlockShellShortcuts = !bool.TryParse(section["BlockShellShortcuts"], out bool bss) || bss,
            AccessToken = section["AccessToken"] ?? ""
        };

        if (Enum.TryParse<InputMode>(section["InputMode"], true, out var parsedMode))
        {
            hostConfig.CurrentInputMode = parsedMode;
        }

        if (args.Contains("--generate-token", StringComparer.OrdinalIgnoreCase))
        {
            hostConfig.AccessToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
        }

        // コマンドライン引数 `--window <タイトル部分一致>` 等による上書き（ホストPC側のみ）
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals("--window", StringComparison.OrdinalIgnoreCase) ||
                args[i].Equals("-w", StringComparison.OrdinalIgnoreCase))
            {
                hostConfig.TargetWindowTitle = args[i + 1];
            }
            else if (args[i].Equals("--url", StringComparison.OrdinalIgnoreCase))
            {
                listenUrl = args[i + 1];
                hostConfig.ListenUrl = listenUrl;
            }
            else if (args[i].Equals("--mode", StringComparison.OrdinalIgnoreCase) &&
                     Enum.TryParse<InputMode>(args[i + 1], true, out var cliMode))
            {
                hostConfig.CurrentInputMode = cliMode;
            }
            else if (args[i].Equals("--token", StringComparison.OrdinalIgnoreCase))
            {
                hostConfig.AccessToken = args[i + 1];
            }
        }

        hostConfig.TargetChanged += () => WindowInputHelper.ReleaseDragIfActive(hostConfig.CurrentInputMode);

        builder.WebHost.ConfigureKestrel(options =>
        {
            options.ConfigureEndpointDefaults(lo =>
            {
                lo.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http1;
            });
        });
        builder.WebHost.UseUrls(listenUrl);
        var app = builder.Build();

        app.UseWebSockets();

        byte[]? latestJpeg = null;
        object frameLock = new();
        int activeClients = 0;

        using var capturer = new WgcWindowCapturer(hostConfig, jpegBytes =>
        {
            lock (frameLock)
            {
                latestJpeg = jpegBytes;
            }
        });
        capturer.Start();

        // Android Chrome 等のモバイルブラウザで Chunked Transfer + Keep-Alive 再利用時の
        // ERR_TOO_MANY_RETRIES を防ぐため、事前に UTF-8 バイト列化して Content-Length を明示する。
        byte[] htmlBytes = Encoding.UTF8.GetBytes(ClientHtml.Page);

        // Kiosk セキュリティ設計:
        // Webクライアントには "/" (HTML描画) と "/ws" (映像受信＋相対座標/フィルタ済みキー送信) のみを公開し、
        // 他のウィンドウ一覧取得やターゲット変更APIは一切公開しない。
        app.MapGet("/", async context =>
        {
            context.Response.StatusCode = 200;
            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.ContentLength = htmlBytes.Length;
            context.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
            context.Response.Headers.XContentTypeOptions = "nosniff";
            context.Response.Headers.XFrameOptions = "DENY";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            context.Response.Headers.ContentSecurityPolicy =
                "default-src 'self'; img-src 'self' blob: data:; style-src 'unsafe-inline'; script-src 'unsafe-inline'; connect-src 'self' ws: wss:; frame-ancestors 'none'";
            context.Response.Headers.Connection = "close";
            await context.Response.Body.WriteAsync(htmlBytes, context.RequestAborted);
            await context.Response.Body.FlushAsync(context.RequestAborted);
        });

        app.MapGet("/favicon.ico", context =>
        {
            context.Response.StatusCode = 204;
            context.Response.Headers.Connection = "close";
            return Task.CompletedTask;
        });

        app.Map("/ws", async context =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = 400;
                return;
            }

            // CSWSH (Cross-Site WebSocket Hijacking) 対策: Origin ヘッダーと Host ヘッダーの一致を検証
            string? origin = context.Request.Headers.Origin;
            if (!string.IsNullOrEmpty(origin))
            {
                if (!Uri.TryCreate(origin, UriKind.Absolute, out var originUri) ||
                    !string.Equals(originUri.Authority, context.Request.Host.Value, StringComparison.OrdinalIgnoreCase))
                {
                    context.Response.StatusCode = 403;
                    return;
                }
            }

            // 同時接続数の上限チェック (DoS対策)
            int currentCount = Interlocked.Increment(ref activeClients);
            if (currentCount > MaxConcurrentClients)
            {
                Interlocked.Decrement(ref activeClients);
                context.Response.StatusCode = 429;
                return;
            }

            try
            {
                using var ws = await context.WebSockets.AcceptWebSocketAsync();

                // トークン認証チェック（AccessToken が設定されている場合のみ定数時間比較で検証）
                string expectedToken = hostConfig.AccessToken;
                if (!string.IsNullOrEmpty(expectedToken))
                {
                    string providedToken = context.Request.Query["token"].ToString();
                    if (string.IsNullOrEmpty(providedToken))
                    {
                        providedToken = context.Request.Headers["X-Access-Token"].ToString();
                    }

                    if (!IsValidToken(providedToken, expectedToken))
                    {
                        await ws.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Unauthorized", CancellationToken.None);
                        return;
                    }
                }

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(
                    context.RequestAborted,
                    app.Lifetime.ApplicationStopping);

                var sendTask = Task.Run(async () =>
                {
                    byte[]? lastSent = null;
                    string lastStatusJson = "";
                    long lastStatusCheckTick = 0;

                    try
                    {
                        while (!cts.Token.IsCancellationRequested && ws.State == WebSocketState.Open)
                        {
                            long nowTick = Environment.TickCount64;
                            if (nowTick - lastStatusCheckTick >= 400)
                            {
                                lastStatusCheckTick = nowTick;
                                var (state, title) = hostConfig.GetTargetStatus();
                                if (state is "closed" or "unselected")
                                {
                                    lock (frameLock) { latestJpeg = null; }
                                }
                                string statusJson = JsonSerializer.Serialize(new { type = "status", state, title });
                                if (statusJson != lastStatusJson)
                                {
                                    var statusBytes = Encoding.UTF8.GetBytes(statusJson);
                                    await ws.SendAsync(statusBytes, WebSocketMessageType.Text, true, cts.Token);
                                    lastStatusJson = statusJson;
                                }
                            }

                            byte[]? frameToSend;
                            lock (frameLock)
                            {
                                frameToSend = latestJpeg;
                            }

                            if (frameToSend != null && !ReferenceEquals(frameToSend, lastSent))
                            {
                                await ws.SendAsync(frameToSend, WebSocketMessageType.Binary, true, cts.Token);
                                lastSent = frameToSend;
                            }

                            var interval = TimeSpan.FromMilliseconds(1000.0 / Math.Max(1, hostConfig.TargetFps));
                            await Task.Delay(interval, cts.Token);
                        }
                    }
                    catch (OperationCanceledException) { }
                    catch (WebSocketException) { }
                }, cts.Token);

                var recvTask = Task.Run(async () =>
                {
                    var buffer = new byte[4096];
                    try
                    {
                        while (!cts.Token.IsCancellationRequested && ws.State == WebSocketState.Open)
                        {
                            var result = await ws.ReceiveAsync(buffer, cts.Token);
                            if (result.MessageType == WebSocketMessageType.Close)
                            {
                                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
                                break;
                            }

                            // 4096バイトを超える分割フレーム（異常に巨大なペイロード）は後続を読み捨てて無視する
                            if (!result.EndOfMessage)
                            {
                                do
                                {
                                    result = await ws.ReceiveAsync(buffer, cts.Token);
                                }
                                while (!result.EndOfMessage && !cts.Token.IsCancellationRequested);
                                continue;
                            }

                            if (result.MessageType == WebSocketMessageType.Text && result.Count > 0)
                            {
                                var json = Encoding.UTF8.GetString(buffer, 0, result.Count);
                                try
                                {
                                    using var doc = JsonDocument.Parse(json);
                                    if (doc.RootElement.TryGetProperty("type", out var tProp) &&
                                        tProp.ValueKind == JsonValueKind.String &&
                                        tProp.GetString() == "refresh")
                                    {
                                        hostConfig.ForceRefreshTarget();
                                    }
                                    else
                                    {
                                        WindowInputHelper.HandleClientMessage(hostConfig, doc.RootElement);
                                    }
                                }
                                catch (JsonException) { }
                            }
                        }
                    }
                    catch (OperationCanceledException) { }
                    catch (WebSocketException) { }
                }, cts.Token);

                await Task.WhenAny(sendTask, recvTask);
                cts.Cancel();
            }
            finally
            {
                if (Interlocked.Decrement(ref activeClients) <= 0)
                {
                    WindowInputHelper.OnClientDisconnected(hostConfig.CurrentInputMode);
                }
                else
                {
                    WindowInputHelper.ReleaseDragIfActive(hostConfig.CurrentInputMode);
                }
            }
        });

        Console.WriteLine("=========================================================");
        Console.WriteLine(" Single-App Remote Desktop Server");
        Console.WriteLine($" Listen URL        : {listenUrl}");
        Console.WriteLine($" Target Window     : {hostConfig.TargetWindowTitle}");
        Console.WriteLine($" Input Mode        : {hostConfig.CurrentInputMode}");
        Console.WriteLine($" Access Token      : {(string.IsNullOrEmpty(hostConfig.AccessToken) ? "(none)" : hostConfig.AccessToken)}");
        Console.WriteLine(" Security Mode     : Host-Locked Single-Window Kiosk");
        Console.WriteLine("=========================================================");

        if (headless)
        {
            app.Run();
        }
        else
        {
            ApplicationConfiguration.Initialize();
            var webTask = app.RunAsync();
            using var trayContext = new TrayApplicationContext(hostConfig, listenUrl, () =>
            {
                // UIスレッドをブロックせずにホストへ停止シグナルを送る
                app.Lifetime.StopApplication();
            });

            // コンソール側で Ctrl+C 等により停止された場合もタスクトレイのループを終了させる
            app.Lifetime.ApplicationStopping.Register(() =>
            {
                trayContext.ExitThread();
            });

            Application.Run(trayContext);
            capturer.Dispose();

            using var shutdownCts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            try { app.StopAsync(shutdownCts.Token).GetAwaiter().GetResult(); } catch { }
            Environment.Exit(0);
        }
    }

    private static bool IsValidToken(string provided, string expected)
    {
        if (string.IsNullOrEmpty(provided) || string.IsNullOrEmpty(expected)) return false;
        byte[] providedBytes = Encoding.UTF8.GetBytes(provided);
        byte[] expectedBytes = Encoding.UTF8.GetBytes(expected);
        return CryptographicOperations.FixedTimeEquals(providedBytes, expectedBytes);
    }
}
