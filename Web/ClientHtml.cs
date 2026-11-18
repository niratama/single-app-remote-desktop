namespace SingleAppRemote.Web;

public static class ClientHtml
{
    public const string Page = """
    <!DOCTYPE html>
    <html lang="ja">
    <head>
        <meta charset="UTF-8">
        <meta name="viewport" content="width=device-width, initial-scale=1.0, maximum-scale=1.0, user-scalable=no">
        <title>Single-App Remote Desktop</title>
        <link rel="icon" type="image/svg+xml" href="data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 64 64'%3E%3Cdefs%3E%3ClinearGradient id='g' x1='0' y1='0' x2='1' y2='1'%3E%3Cstop offset='0%25' stop-color='%232563eb'/%3E%3Cstop offset='100%25' stop-color='%230ea5e9'/%3E%3C/linearGradient%3E%3C/defs%3E%3Crect x='2' y='2' width='60' height='60' rx='14' fill='url(%23g)'/%3E%3Crect x='10' y='13' width='44' height='33' rx='4' fill='%230f172a' stroke='%23fff' stroke-width='3'/%3E%3Cline x1='10' y1='22' x2='54' y2='22' stroke='%23fff' stroke-width='3'/%3E%3Ccircle cx='32' cy='34' r='6' fill='%2334d399'/%3E%3Cline x1='32' y1='47' x2='32' y2='53' stroke='%23fff' stroke-width='3.5' stroke-linecap='round'/%3E%3Cline x1='22' y1='53' x2='42' y2='53' stroke='%23fff' stroke-width='3.5' stroke-linecap='round'/%3E%3C/svg%3E">
        <style>
            :root {
                --bg: #121214;
                --bar-bg: rgba(28, 28, 32, 0.92);
                --accent: #3b82f6;
                --text: #e4e4e7;
                --muted: #a1a1aa;
            }
            * { box-sizing: border-box; user-select: none; -webkit-user-select: none; }
            body {
                margin: 0;
                background: var(--bg);
                color: var(--text);
                font-family: system-ui, -apple-system, sans-serif;
                display: flex;
                flex-direction: column;
                height: 100dvh;
                overflow: hidden;
            }
            header {
                display: flex;
                align-items: center;
                justify-content: space-between;
                gap: 8px;
                padding: 6px 12px;
                background: var(--bar-bg);
                border-bottom: 1px solid rgba(255,255,255,0.08);
                font-size: 13px;
                z-index: 10;
            }
            .status-group {
                display: flex;
                align-items: center;
                gap: 10px;
            }
            .dot {
                width: 9px;
                height: 9px;
                border-radius: 50%;
                background: #ef4444;
                display: inline-block;
            }
            .dot.connected { background: #22c55e; }
            .dot.warning { background: #f59e0b; }
            .controls {
                display: flex;
                align-items: center;
                gap: 6px;
                flex-wrap: wrap;
            }
            button, input[type="text"] {
                background: #27272a;
                color: var(--text);
                border: 1px solid #3f3f46;
                border-radius: 6px;
                padding: 5px 10px;
                font-size: 12px;
                cursor: pointer;
            }
            button.active {
                background: var(--accent);
                border-color: var(--accent);
                color: #fff;
            }
            button:hover { border-color: #71717a; }
            input[type="text"] {
                width: 150px;
                cursor: text;
                user-select: text;
                -webkit-user-select: text;
            }
            main {
                flex: 1;
                display: flex;
                justify-content: center;
                align-items: center;
                overflow: hidden;
                position: relative;
                padding: 4px;
            }
            canvas {
                max-width: 100%;
                max-height: 100%;
                object-fit: contain;
                box-shadow: 0 8px 32px rgba(0,0,0,0.75);
                cursor: crosshair;
                touch-action: none;
                background: #000;
                transition: filter 0.2s ease, opacity 0.2s ease;
            }
            canvas.inactive {
                filter: grayscale(0.85) brightness(0.35);
                pointer-events: none;
            }
            .overlay-box {
                position: absolute;
                background: rgba(24, 24, 27, 0.92);
                border: 1px solid rgba(255, 255, 255, 0.14);
                border-radius: 10px;
                padding: 18px 26px;
                color: var(--text);
                font-size: 14px;
                text-align: center;
                pointer-events: none;
                box-shadow: 0 10px 30px rgba(0,0,0,0.6);
                z-index: 5;
                max-width: 85vw;
            }
            .overlay-box .sub {
                margin-top: 6px;
                font-size: 12px;
                color: var(--muted);
            }
        </style>
    </head>
    <body>
        <header>
            <div class="status-group">
                <span id="statusDot" class="dot"></span>
                <span id="statusText">接続中...</span>
                <span id="fpsCounter" style="color: var(--muted);">0 FPS</span>
            </div>
            <div class="controls">
                <button id="btnRefresh" title="再読み込み" style="padding: 5px 7px; display: inline-flex; align-items: center; justify-content: center;">
                    <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.4" stroke-linecap="round" stroke-linejoin="round">
                        <path d="M21 12a9 9 0 1 1-2.64-6.36"/>
                        <polyline points="21 3 21 9 15 9"/>
                    </svg>
                </button>
                <button id="btnRightClick" title="スマホ/タッチ操作時に右クリックとして送信">右クリックモード: OFF</button>
                <button id="btnDragMode" title="ドラッグ移動の有効化">ドラッグ操作: OFF</button>
                <input id="txtSend" type="text" placeholder="テキスト送信 (Enter)" />
                <button id="btnSendText">送信</button>
                <button id="btnFullscreen">全画面</button>
            </div>
        </header>
        <main>
            <div id="overlayBox" class="overlay-box">
                <div id="overlayTitle">対象ウィンドウの映像を待機しています...</div>
                <div id="overlaySub" class="sub">ホストPC側で対象アプリを起動または選択してください</div>
            </div>
            <canvas id="screen" tabindex="0"></canvas>
        </main>
        <script>
            const canvas = document.getElementById("screen");
            const ctx = canvas.getContext("2d");
            const statusDot = document.getElementById("statusDot");
            const statusText = document.getElementById("statusText");
            const fpsCounter = document.getElementById("fpsCounter");
            const overlayBox = document.getElementById("overlayBox");
            const overlayTitle = document.getElementById("overlayTitle");
            const overlaySub = document.getElementById("overlaySub");
            const btnRefresh = document.getElementById("btnRefresh");
            const btnRightClick = document.getElementById("btnRightClick");
            const btnDragMode = document.getElementById("btnDragMode");
            const txtSend = document.getElementById("txtSend");
            const btnSendText = document.getElementById("btnSendText");
            const btnFullscreen = document.getElementById("btnFullscreen");

            let ws = null;
            let rightClickMode = false;
            let dragMode = false;
            let isPointerDown = false;
            let frameCount = 0;
            let lastMoveSent = 0;
            let targetState = "active";

            setInterval(() => {
                fpsCounter.textContent = `${frameCount} FPS`;
                frameCount = 0;
            }, 1000);

            btnRightClick.addEventListener("click", () => {
                rightClickMode = !rightClickMode;
                btnRightClick.classList.toggle("active", rightClickMode);
                btnRightClick.textContent = `右クリックモード: ${rightClickMode ? "ON" : "OFF"}`;
            });

            btnRefresh.addEventListener("click", () => {
                if (ws && ws.readyState === WebSocket.OPEN) {
                    ws.send(JSON.stringify({ type: "refresh" }));
                } else {
                    connect();
                }
            });

            btnDragMode.addEventListener("click", () => {
                dragMode = !dragMode;
                btnDragMode.classList.toggle("active", dragMode);
                btnDragMode.textContent = `ドラッグ操作: ${dragMode ? "ON" : "OFF"}`;
            });

            btnFullscreen.addEventListener("click", () => {
                if (!document.fullscreenElement) {
                    document.documentElement.requestFullscreen().catch(() => {});
                } else {
                    document.exitFullscreen().catch(() => {});
                }
            });

            function sendJson(payload) {
                if (targetState === "closed" || targetState === "unselected") return;
                if (ws && ws.readyState === WebSocket.OPEN) {
                    ws.send(JSON.stringify(payload));
                }
            }

            function getRelativeCoords(e) {
                const rect = canvas.getBoundingClientRect();
                if (rect.width <= 0 || rect.height <= 0) return { x: 0, y: 0 };
                const x = Math.max(0, Math.min(1, (e.clientX - rect.left) / rect.width));
                const y = Math.max(0, Math.min(1, (e.clientY - rect.top) / rect.height));
                return { x, y };
            }

            canvas.addEventListener("contextmenu", (e) => {
                e.preventDefault();
            });

            canvas.addEventListener("pointerdown", (e) => {
                if (targetState === "closed") return;
                canvas.focus();
                e.preventDefault();
                isPointerDown = true;
                canvas.setPointerCapture(e.pointerId);

                if (dragMode) {
                    const { x, y } = getRelativeCoords(e);
                    const isRight = e.button === 2 || rightClickMode;
                    sendJson({ type: "pointerdown", x, y, button: isRight ? 2 : 0 });
                }
            });

            canvas.addEventListener("pointermove", (e) => {
                if (!dragMode || !isPointerDown || targetState === "closed") return;
                const now = performance.now();
                if (now - lastMoveSent < 25) return;
                lastMoveSent = now;
                const { x, y } = getRelativeCoords(e);
                sendJson({ type: "pointermove", x, y });
            });

            canvas.addEventListener("pointerup", (e) => {
                if (!isPointerDown) return;
                isPointerDown = false;
                if (targetState === "closed") return;
                e.preventDefault();
                const { x, y } = getRelativeCoords(e);
                const isRight = e.button === 2 || rightClickMode;

                if (dragMode) {
                    sendJson({ type: "pointerup", x, y, button: isRight ? 2 : 0 });
                } else {
                    sendJson({ type: isRight ? "rightclick" : "click", x, y });
                }
            });

            canvas.addEventListener("pointercancel", (e) => {
                if (!isPointerDown) return;
                isPointerDown = false;
                if (dragMode && targetState !== "closed") {
                    const { x, y } = getRelativeCoords(e);
                    const isRight = e.button === 2 || rightClickMode;
                    sendJson({ type: "pointerup", x, y, button: isRight ? 2 : 0 });
                }
            });

            canvas.addEventListener("wheel", (e) => {
                if (targetState === "closed") return;
                e.preventDefault();
                const { x, y } = getRelativeCoords(e);
                sendJson({ type: "wheel", x, y, deltaY: e.deltaY });
            }, { passive: false });

            canvas.addEventListener("keydown", (e) => {
                if (targetState === "closed") return;
                e.preventDefault();
                sendJson({
                    type: "key",
                    key: e.key,
                    code: e.code,
                    ctrlKey: e.ctrlKey,
                    altKey: e.altKey,
                    shiftKey: e.shiftKey,
                    metaKey: e.metaKey
                });
            });

            function submitTextInput() {
                const text = txtSend.value;
                if (!text || targetState === "closed") return;
                sendJson({ type: "text", text });
                txtSend.value = "";
                canvas.focus();
            }

            btnSendText.addEventListener("click", submitTextInput);
            txtSend.addEventListener("keydown", (e) => {
                e.stopPropagation();
                if (e.key === "Enter") {
                    e.preventDefault();
                    submitTextInput();
                }
            });

            function applyTargetStatus(state, title) {
                targetState = state;
                if (state === "unselected") {
                    statusDot.className = "dot warning";
                    statusText.textContent = "対象ウィンドウ未選択";
                    canvas.classList.add("inactive");
                    overlayTitle.textContent = "キャプチャ対象のウィンドウが選択されていません";
                    overlaySub.textContent = "ホストPC側のタスクトレイアイコンをクリックして対象ウィンドウを選択してください";
                    overlayBox.style.display = "block";
                } else if (state === "closed") {
                    statusDot.className = "dot warning";
                    statusText.textContent = `対象ウィンドウ未検出: ${title}`;
                    canvas.classList.add("inactive");
                    overlayTitle.textContent = `対象ウィンドウ「${title}」が閉じられました`;
                    overlaySub.textContent = "ホストPC側でアプリを再起動するか、タスクトレイから別ウィンドウを選択してください";
                    overlayBox.style.display = "block";
                } else if (state === "minimized") {
                    statusDot.className = "dot warning";
                    statusText.textContent = `最小化中: ${title} (クリックで復帰)`;
                    canvas.classList.remove("inactive");
                    overlayTitle.textContent = `「${title}」は最小化されています`;
                    overlaySub.textContent = "画面内をクリックすると元のサイズに復元されます";
                    overlayBox.style.display = "block";
                } else {
                    statusDot.className = "dot connected";
                    statusText.textContent = `配信中: ${title}`;
                    canvas.classList.remove("inactive");
                    overlayBox.style.display = "none";
                }
            }

            function connect() {
                const proto = location.protocol === "https:" ? "wss:" : "ws:";
                ws = new WebSocket(`${proto}//${location.host}/ws${location.search}`);
                ws.binaryType = "blob";

                ws.onopen = () => {
                    statusDot.className = "dot connected";
                    statusText.textContent = "接続済み";
                };

                ws.onmessage = async (event) => {
                    if (typeof event.data === "string") {
                        try {
                            const msg = JSON.parse(event.data);
                            if (msg.type === "status") {
                                applyTargetStatus(msg.state, msg.title || "");
                            }
                        } catch {}
                        return;
                    }

                    if (event.data instanceof Blob) {
                        try {
                            const bitmap = await createImageBitmap(event.data);
                            if (canvas.width !== bitmap.width || canvas.height !== bitmap.height) {
                                canvas.width = bitmap.width;
                                canvas.height = bitmap.height;
                            }
                            ctx.drawImage(bitmap, 0, 0);
                            bitmap.close();
                            if (targetState === "active") {
                                overlayBox.style.display = "none";
                            }
                            frameCount++;
                        } catch {}
                    }
                };

                ws.onclose = (event) => {
                    statusDot.className = "dot";
                    canvas.classList.add("inactive");
                    overlayBox.style.display = "block";
                    if (event && event.code === 1008) {
                        statusText.textContent = "認証エラー (アクセス拒否)";
                        overlayTitle.textContent = "アクセストークンが無効、または指定されていません";
                        overlaySub.textContent = "URLに ?token=<トークン> を付与して再度アクセスしてください";
                        return;
                    }
                    statusText.textContent = "サーバー切断 (再接続待機中...)";
                    overlayTitle.textContent = "ホストサーバーとの接続が切断されました";
                    overlaySub.textContent = "自動的に再接続を試みています...";
                    setTimeout(connect, 1500);
                };
            }

            if (document.readyState === "complete") {
                setTimeout(connect, 100);
            } else {
                window.addEventListener("load", () => setTimeout(connect, 100));
            }
        </script>
    </body>
    </html>
    """;
}
