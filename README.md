# SpireRemote

自宅の Windows PC で動いている **Slay the Spire 2** を、自分のスマホから遊ぶためのリモート PWA。
接続は **自分の Tailscale ネットワークの中だけ**。縦持ち 1 画面に映像と操作をまとめ、カードや敵は「選んでから確認」で操作します。

Play **Slay the Spire 2** running on your Windows PC from your phone, over **your own Tailscale network only**.
A one-screen portrait PWA: pick a card, pick a target, confirm — enemies are labelled A/B/C with a target arrow on the video.

![license](https://img.shields.io/badge/license-Apache--2.0-blue)

---

## 特徴

- **縦持ち 1 画面** — 上から「トップバー／映像／操作パネル／汎用ボタン 2 段」の 4 段。ページはスクロールせず、360×640 程度の画面でも全操作が収まります。横持ちは 2 カラム、⛶ で映像だけの全画面表示
- **選択→確認フロー** — カード・ポーション・マップ・イベント・休憩は「選ぶ → ▶ 確認」の 2 段階。確認の直前に最新の状態と照合し、手札などが変わっていたら送信せずに中止します。ターン終了は使えるカードが残っているとき、「進む」は報酬が残っているときだけ 2 度押し
- **敵ラベル A / B / C と矢印** — 敵には英字ラベルが付き、戦闘中は倒しても繰り上がりません。対象を選ぶと映像上の敵に曲線の矢印とラベルのバッジを表示（位置が取れないときは矢印を出さず、敵チップと対象表示だけで示します）
- **画質 3 プリセット** — ☰ メニューから「低遅延（おすすめ・既定）／バランス／高画質」を選択。選択は端末に保存され、トップバーにプリセット名・幅・fps・入力遅延を表示
- **直接タッチの制御** — 映像を触るとゲームにクリックが飛ぶ「直接タッチ」は、既定の「自動」ではボタンで操作できない画面（ショップ等）・ブリッジ未接続時・全画面でだけ有効。映像右上の 👆 で一時的にオンにでき、☰ から「自動／常にオン／常にオフ」を切り替え
- **汎用ボタン** — マップ／デッキ／山札／捨て札／廃棄、🧪 ポーション、戻る、決定、ターン終了（または「進む」）
- **ウィンドウ単体キャプチャ** — Windows Graphics Capture でゲームウィンドウだけを配信。タスクバーや他のウィンドウ、通知ポップアップは映りません
- **起動・終了もスマホから** — Steam 経由の起動、通常終了／強制終了（終了は ☰ メニュー内で確認ダイアログつき）
- **トークン認証** — 初回にリンクから開けばトークンは端末に保持され、次回からは入力不要（☰ → 🔑 からいつでも変更可）

カード・敵・選択肢のボタン操作には SpireBridge（下記）が必要です。ブリッジが無いときは縮退モードになり、映像の直接タッチと汎用ボタンで操作します。

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
4. ゲーム側のおすすめ設定（初回のみ）：設定 → コントロール → **キーボード専用モード = ON**（決定・戻る・マップ等のボタンはキー入力で送られます）

PC 側でゲームを起動していなくても、操作パネルの「▶ ゲームを起動」（または ☰ → ▶ 起動）から Steam 経由で立ち上げられます。

## セキュリティ

- ホストは `127.0.0.1:8765` にのみバインドします。外への公開経路は Tailscale Serve（tailnet 内のみ）だけです
- **Tailscale Funnel は絶対に使わないでください**（インターネット全体に公開されてしまいます）
- URL の `?t=` トークンはこのリモートのパスワードです。URL を他人に共有しないでください
- トークンを変えたい場合は PC 側の `config.json` を削除して `restart.ps1` を実行すると再生成されます

## 仕組み

```
[Phone PWA] --HTTPS (Tailscale Serve)--> [host.py on PC] --WGC capture / SendInput--> [Slay the Spire 2]
                                              |
                                              +--localhost HTTP--> [SpireBridge mod]（状態取得・ボタン操作）
```

- 映像：Windows Graphics Capture でゲームウィンドウのサーフェスだけを取得（PrintWindow は DirectX ゲームでは真っ黒になるため不採用）。変化駆動なので軽い
- 表示：iOS の standalone PWA では MJPEG が描画できないため、JPEG フレームの fetch ポーリング方式。画質プリセットごとに幅と 1 枚あたりのサイズ上限を切り替え、回線状況に応じてプリセットの範囲内で自動調整
- 入力：SendInput（マウス／キー）。ホストは DPI 対応（Per-Monitor v2）で、200% スケーリング環境でも座標がずれません
- 操作パネル：ホスト経由で SpireBridge の `/state`・`/actions` を取得し、確認後に `/do` で実行
- PWA は開かれた URL のベースパスを自動判定します（`/spire` 配下でもルートでも動作）

## SpireBridge（実験的・任意）

`bridge/` には、ゲーム内部の状態（手札・敵・選択肢）をローカル HTTP で返し、ボタン操作を受け付ける **自前 mod** のソースがあります。

- 要 .NET 9 SDK。`bridge/build.ps1` でビルドし、生成物をゲームの `mods/SpireBridge/` に置きます
- 仕様と現状は [bridge/DESIGN.md](bridge/DESIGN.md)・[bridge/README.md](bridge/README.md) を参照。v0.4.0（敵ラベル・画面上の位置・生存フラグ、選択肢の有効フラグ、アクションの対象情報を追加）はビルド済みですが、**実機では未検証**です

## 開発メモ

- ホスト再起動：`restart.ps1`／アンインストール：`uninstall.ps1`
- ログは `host.log`（トークンは記録されません）

## ライセンス

Apache License 2.0（[LICENSE](LICENSE)）。

本プロジェクトは Mega Crit とは無関係の非公式ファンメイドツールです。Slay the Spire 2 のゲーム本体は含まれません。
