# SpireRemote — “SPIRE BOY”

自宅の Windows PC で動いている **Slay the Spire 2** を、自分のスマホから遊ぶためのリモート PWA。
接続は **自分の Tailscale ネットワークの中だけ**。ゲームボーイ風の操作デッキで、十字キーとボタンでそのままプレイできます。

Play **Slay the Spire 2** running on your Windows PC from your phone, over **your own Tailscale network only**.
A Game Boy-style PWA deck: D-pad + buttons, backed by the game's own keyboard-only mode.

![license](https://img.shields.io/badge/license-Apache--2.0-blue)

---

## 特徴

- **ウィンドウ単体キャプチャ** — Windows Graphics Capture でゲームウィンドウだけを配信。タスクバーや他のウィンドウ、通知ポップアップは映りません
- **ゲームボーイ風デッキ** — 十字キー / A（決定）/ B（もどる）/ START（選択）/ SELECT（前面化）。ゲーム側の「キーボード専用モード」のキー割当にそのまま対応
- **手札番号ボタン 1–10** — ゲーム設定「手札の番号を表示」と連動
- **よく使うキー** — Tab（マップ）/ Q（デッキ）/ A（山札）/ D（捨て札）/ F（廃棄札）/ W（上部パネル）
- **全画面モード** — ⛶で没入表示。画面タップ＝クリック、ドラッグ＝スワイプ、長押し＝拡大
- **起動・終了もスマホから** — Steam 経由の起動、通常終了／強制終了
- **トークン認証** — 初回にリンクから開けばトークンは端末に保持され、次回からは入力不要（🔑からいつでも変更可）
- **レスポンシブ** — 画面の高さに必ず収まるレイアウト。横向きスマホでは左右分割

## 必要なもの

- Windows 10 / 11 の PC（Steam 版 Slay the Spire 2）
- Python 3.11 以上（[python.org](https://www.python.org/downloads/)、導入時に「Add python.exe to PATH」を推奨）
- [Tailscale](https://tailscale.com/) アカウント — PC とスマホの両方にインストールし、**同じ tailnet** にサインイン
- スマホのブラウザ（iOS Safari / Android Chrome 等）

## インストール（PC 側で 1 回だけ）

```powershell
git clone https://github.com/kokuren333/SpireRemote.git
cd SpireRemote
powershell -ExecutionPolicy Bypass -File install.ps1
```

`install.ps1` がやること：

1. Python ライブラリ（Pillow / windows-capture）をユーザースコープに導入
2. `config.json` をランダムなトークン付きで生成（既にあれば保持）
3. ログオン時に `host.py` を常駐させるスケジュールタスク `SpireRemoteHost` を登録・起動
4. Tailscale CLI があれば `tailscale serve` で `/spire` パスに公開
5. 最後にスマホ用 URL を表示

## 使い方（スマホ側）

1. スマホの Tailscale アプリを **Connected** にする
2. インストール時に表示された URL を開く
3. 「ホーム画面に追加」するとアプリとして使えます
4. ゲーム側のおすすめ設定（初回のみ）：
   - 設定 → コントロール → **キーボード専用モード = ON**
   - 設定 → 一般 → **手札の番号を表示 = ON**

あとは普通に遊ぶだけ。PC 側でゲームを起動していなくても、PWA の「▶ 起動」から Steam 経由で立ち上げられます。

## セキュリティ

- ホストは `127.0.0.1:8765` にのみバインドします。外への公開経路は Tailscale Serve（tailnet 内のみ）だけです
- **Tailscale Funnel は絶対に使わないでください**（インターネット全体に公開されてしまいます）
- URL の `?t=` トークンはこのリモートのパスワードです。URL を他人に共有しないでください
- トークンを変えたい場合は PC 側の `config.json` を削除して `restart.ps1` を実行すると再生成されます

## 仕組み

```
[Phone PWA] --HTTPS (Tailscale Serve)--> [host.py on PC] --WGC capture / SendInput--> [Slay the Spire 2]
```

- 映像：Windows Graphics Capture でゲームウィンドウのサーフェスだけを取得（PrintWindow は DirectX ゲームでは真っ黒になるため不採用）。変化駆動なので軽い
- 表示：iOS の standalone PWA では MJPEG が描画できないため、JPEG フレームの fetch ポーリング方式
- 入力：SendInput（マウス／キー）。ホストは DPI 対応（Per-Monitor v2）で、200% スケーリング環境でも座標がずれません
- PWA は開かれた URL のベースパスを自動判定します（`/spire` 配下でもルートでも動作）

## SpireBridge（実験的・任意）

`bridge/` には、ゲーム内部の状態（手札・敵・選択肢）をローカル HTTP で返す **自前 mod** のソースがあります。
選択肢を名前付きボタンで出す「人向け MCP」的な UI のための土台です。

- 要 .NET 9 SDK。`bridge/build.ps1` でビルドし、生成物をゲームの `mods/SpireBridge/` に置きます
- 仕様と現状は [bridge/DESIGN.md](bridge/DESIGN.md) を参照（v0.2.0 は実機ロードテスト合格・稼働中。v0.3.0 は説明文・フォーカス中要素・表示専用 choices を追加してビルド済み、次回ゲーム起動時に有効化）

## 開発メモ

- ホスト再起動：`restart.ps1`／アンインストール：`uninstall.ps1`
- ログは `host.log`（トークンは記録されません）

## ライセンス

Apache License 2.0（[LICENSE](LICENSE)）。

本プロジェクトは Mega Crit とは無関係の非公式ファンメイドツールです。Slay the Spire 2 のゲーム本体は含まれません。
