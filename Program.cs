using System.Net.WebSockets;
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

        var builder = WebApplication.CreateBuilder(args);

        var section = builder.Configuration.GetSection("RemoteSettings");
        string listenUrl = section["ListenUrl"] ?? "http://0.0.0.0:8000";

        var hostConfig = new HostConfig
        {
            TargetWindowTitle = section["TargetWindowTitle"] ?? "",
            TargetFps = int.TryParse(section["TargetFps"], out int fps) ? fps : 30,
            JpegQuality = long.TryParse(section["JpegQuality"], out long q) ? q : 75L,
            CaptureCursor = bool.TryParse(section["CaptureCursor"], out bool cc) && cc
        };

        if (Enum.TryParse<InputMode>(section["InputMode"], true, out var parsedMode))
        {
            hostConfig.CurrentInputMode = parsedMode;
        }

        // コマンドライン引数 `--window <タイトル部分一致>` による上書き（ホストPC側のみ）
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
            }
            else if (args[i].Equals("--mode", StringComparison.OrdinalIgnoreCase) &&
                     Enum.TryParse<InputMode>(args[i + 1], true, out var cliMode))
            {
                hostConfig.CurrentInputMode = cliMode;
            }
        }

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

            using var ws = await context.WebSockets.AcceptWebSocketAsync();
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

                        if (result.MessageType == WebSocketMessageType.Text && result.Count > 0)
                        {
                            var json = Encoding.UTF8.GetString(buffer, 0, result.Count);
                            try
                            {
                                using var doc = JsonDocument.Parse(json);
                                if (doc.RootElement.TryGetProperty("type", out var tProp) &&
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
        });

        Console.WriteLine("=========================================================");
        Console.WriteLine(" Single-App Remote Desktop Server");
        Console.WriteLine($" Listen URL        : {listenUrl}");
        Console.WriteLine($" Target Window     : {hostConfig.TargetWindowTitle}");
        Console.WriteLine($" Input Mode        : {hostConfig.CurrentInputMode}");
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
}
