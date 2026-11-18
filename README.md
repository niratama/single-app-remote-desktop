# Single-App Remote Desktop

Windows 10 / 11 上の **「指定した1つのアプリケーションウィンドウだけ」** をキャプチャし、LAN内のWebブラウザ（PC / スマートフォン / タブレット）から閲覧・操作できる、単一ウィンドウ限定（Kiosk型）のリモート操作ツールです。

外部 NuGet パッケージへの依存が一切なく（**Zero External Dependencies**）、.NET 8 と Windows 標準 API（Windows Graphics Capture / Direct3D11 / Win32 API）のみで完結しており、単一の `.exe` ファイルにビルドして配布・実行できます。

---

## 主な特徴

### 1. 厳格な Kiosk 制限（リモート側から他のアプリへ出られない設計）
- **ホストPC側専用のターゲット選択**: Webクライアント（ブラウザ）側にはウィンドウ一覧取得や切り替え用の API を一切公開していません。キャプチャ対象の変更はホストPC側のタスクトレイ（または起動引数）からのみ行えます。
- **ウィンドウ内への座標クランプ & 前面ウィンドウ検証 (`WindowFromPoint`)**: ブラウザからは相対座標（`0.0 〜 1.0`）のみを受け取り、`DwmGetWindowAttribute(DWMWA_EXTENDED_FRAME_BOUNDS)` で透明なドロップシャドウ枠を除いた実際のウィンドウ矩形内に厳密にクランプします。さらにクリック直前にその座標の最前面ウィンドウが対象アプリ自身であることを検証し、前面に重なった別ウィンドウや自身の設定ダイアログへのクリック貫通を防止します。
- **システム・シェル起動ショートカットの遮断**: `Win` キー、`Alt+Tab`、`Alt+F4`、`Alt+Esc`、`Ctrl+Shift+Esc`、`Ctrl+Alt+Del`、`F1`、`Shift+F10` に加え、Kiosk保護有効時はファイルダイアログ等を開く `Ctrl+O` / `Ctrl+S` / `Ctrl+P` / `Ctrl+N` 等もサーバー側で遮断します。
- **WebSocket 保護 & トークン認証**: `Origin` ヘッダー検証による CSWSH（Cross-Site WebSocket Hijacking）防止、同時接続数・入力キュー上限による DoS 防止、および任意のアクセストークン認証（`--token` / `--generate-token`）に対応しています。

### 2. 高速キャプチャ（Windows Graphics Capture + Direct3D11）
- **背面ウィンドウでもキャプチャ可能**: WGC API を使用しているため、対象ウィンドウの上に別のウィンドウが重なっていても（最小化さえされていなければ）、対象ウィンドウ単体の映像を GPU 経由で高速に取得・配信します。
- **リアルタイム状態通知**: 対象ウィンドウが「未選択」「最小化中」「閉じられた」場合、Webブラウザ側のオーバーレイ表示へ即座にステータスが反映されます。

### 3. 3種類の入力注入モード（UWP / Electron / Win32 対応）
ホスト側のタスクトレイメニューから、操作対象アプリの性質に合わせていつでも切り替え可能です。
1. **`SendInputRestoreCursor`（デフォルト・推奨）**:
   - 操作直前のホストPCのマウスカーソル位置を保存し、対象ウィンドウを操作した後に元のカーソル位置へ自動復帰します。
   - **高速連打対応（デバウンス復帰 & 非同期キュー）**: 連続クリック中はカーソル復帰をスキップして連打完了後にまとめて戻すため、UWP（電卓など）・Electron・WPF・DirectX アプリでも取りこぼしなく高速に入力できます。
2. **`SendInputDirect`**:
   - カーソル位置を戻さず直接移動します（ドラッグ操作を多用する場合や、仮想ディスプレイへ対象アプリを退避させている場合に最適）。
3. **`PostMessage`**:
   - ホストのマウスカーソルもウィンドウフォーカスも一切奪わず、バックグラウンドで直接子ウィンドウへ `WM_LBUTTONDOWN` / `WM_CHAR` 等を送信します（従来のネイティブ Win32 アプリ向け）。

### 4. タスクトレイ常駐 & ウィンドウ選択・詳細設定ダイアログ
- ダブルクリック起動時は黒いコンソール画面を出さずにタスクトレイへ静かに常駐します（`WinExe`）。
- タスクトレイアイコンの**左クリック**で「キャプチャ対象ウィンドウの選択」ダイアログが開き、ウィンドウタイトル・プロセス名・PID でインクリメンタル検索して即座に切り替えられます。
- タスクトレイメニューの **「設定 (ネットワーク・認証・Kiosk保護)...」** から設定ダイアログを開き、待受URL、アクセストークン（ランダム生成・LAN接続用URLコピー機能付き）、FPS、JPEG画質、入力モード、Kiosk保護などをGUIで設定できます。変更した設定は Windows 標準の `%LocalAppData%\SingleAppRemote\settings.json` に自動保存され、次回起動時に復元されます。

---

## 動作要件

- **OS**: Windows 10 (Version 2004 / Build 19041 以降) または Windows 11（Home / Pro どちらも可）
- **ビルド環境**（ソースからビルドする場合のみ）: [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) 以降

---

## 使い方

### 1. 起動と対象ウィンドウの選択
1. `SingleAppRemote.exe` を起動すると、タスクトレイにアイコン（青いモニターアイコン）が常駐します。
2. タスクトレイアイコンを **左クリック** すると、現在開いているウィンドウの検索・選択ダイアログが表示されます。
3. リモート操作したいウィンドウを選択して **「このウィンドウを選択」**（または一覧をダブルクリック）します。

### 2. ブラウザからアクセス
- 同じ LAN 内の PC・スマホ・タブレットのブラウザから `http://<ホストPCのIPアドレス>:8000`（ホストPC自身で試す場合は `http://localhost:8000`）を開きます。
- **ブラウザ上の操作パネル**:
  - `↻`: キャプチャセッションの再読み込み（最小化されているウィンドウの復元も同時に行います）
  - `右クリックモード`: ON にすると、スマホのタップや左クリックを右クリックとして送信します
  - `ドラッグ操作`: ON にすると、ポインタのドラッグ移動（スライダー操作や範囲選択など）をリアルタイムに送信します
  - `テキスト送信`: 日本語などの文字列をまとめて対象ウィンドウへ直接入力（Unicode `SendInput`）します
  - `全画面`: ブラウザをフルスクリーン表示に切り替えます

---

## コマンドライン引数 & 設定ファイル

### `appsettings.json`
```json
{
  "RemoteSettings": {
    "ListenUrl": "http://0.0.0.0:8000",
    "TargetWindowTitle": "",
    "TargetFps": 30,
    "JpegQuality": 75,
    "InputMode": "SendInputRestoreCursor",
    "CaptureCursor": false,
    "BlockShellShortcuts": true,
    "AccessToken": ""
  }
}
```

### コマンドラインオプション
```powershell
# 起動時から特定のウィンドウ（タイトル部分一致）を対象にする
SingleAppRemote.exe --window "電卓"

# 待受ポート・URLを変更して起動する
SingleAppRemote.exe --url "http://0.0.0.0:9000"

# 入力モードを指定して起動する (SendInputRestoreCursor | SendInputDirect | PostMessage)
SingleAppRemote.exe --window "メモ帳" --mode PostMessage

# アクセストークン認証を有効化して起動する（クライアントは http://<IP>:8000/?token=mysecret で接続）
SingleAppRemote.exe --token "mysecret"

# ランダムなアクセストークンを自動生成して起動する
SingleAppRemote.exe --generate-token
```

---

## ビルド方法

### 開発実行・通常ビルド
```powershell
dotnet build -c Release
dotnet run -c Release
```

### 単一 `.exe` ファイルの発行（ランタイム同梱・圧縮有効）
`SingleAppRemote.csproj` に単一バイナリ発行設定が組み込まれているため、以下のコマンドだけで .NET ランタイム不要で動く `SingleAppRemote.exe` が `./publish` フォルダに出力されます。

```powershell
dotnet publish
```

---

## プロジェクト構成とコード解説

C / Go / Python / TypeScript 経験者向けに、C# (.NET 8) の文法や Win32 / Direct3D11 / WGC の内部動作を詳しく解説したドキュメントを用意しています。

👉 **[ARCHITECTURE_GUIDE.md（コード＆仕組み解説）](./ARCHITECTURE_GUIDE.md)**

```text
single-app-remote-desktop/
├── Program.cs                   # エントリポイント・ASP.NET Core Kestrel & WebSocket サーバー
├── SingleAppRemote.csproj       # プロジェクト設定 (net8.0-windows10.0.19041.0, WinExe)
├── appsettings.json             # デフォルト設定ファイル
├── app.ico                      # マルチ解像度アプリケーションアイコン
├── Capture/
│   ├── WgcWindowCapturer.cs     # Windows Graphics Capture セッション管理・動的ターゲット切替
│   └── D3D11Helper.cs           # Direct3D11 COM vtable 直接呼び出し・VRAM→JPEG エンコード
├── Host/
│   ├── HostConfig.cs            # スレッドセーフな設定・ウィンドウ列挙・ステータス管理・永続化
│   ├── TrayApplicationContext.cs# ホストPC専用タスクトレイ常駐メニュー
│   ├── WindowPickerDialog.cs    # インクリメンタル検索付きウィンドウ選択モーダルダイアログ
│   ├── SettingsDialog.cs        # ネットワーク・トークン認証・画質・Kiosk保護の設定ダイアログ
│   └── AppIconGenerator.cs      # GDI+ によるベクターアイコン生成
├── Input/
│   └── WindowInputHelper.cs     # 非同期入力キュー・デバウンス付きカーソル復帰・Kiosk 遮断フィルタ
├── Interop/
│   └── NativeMethods.cs         # Win32 / DWM / Direct3D11 / WGC P/Invoke & COM 定義
└── Web/
    └── ClientHtml.cs            # PC / モバイル対応 HTML5 Canvas Web クライアント
```

## ライセンス

[MIT License](./LICENSE)
