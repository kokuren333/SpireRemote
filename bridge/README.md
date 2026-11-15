# SpireBridge — STS2 自前ブリッジ mod

Slay the Spire 2 のゲーム内から状態を読み、ローカル HTTP で公開するための
自前 mod。SpireRemote の「選択肢ボタン」層の土台。

## 状態（v0.3.0、2026-11-15 ビルド済み・ステージング／実機では v0.2.0 が稼働中）

- 状態読み取り／アクション実行の本体を統合済み（`GameStateProvider` /
  `GameActionExecutor`）。ゲーム API 呼び出しは全て `GameThread`
  （初期化時に捕捉した SynchronizationContext へ Post、5 秒タイムアウト）
  経由でゲームスレッド上で実行する。
- HTTP: `127.0.0.1:8766`
  - `GET /health` → `{ok, service, version:"0.3.0", game_version}`（game_version は
    sts2 アセンブリ脇の `release_info.json` から取得、失敗時は null）
  - `GET /state` → 実読み取り（screen / run / player / hand / enemies /
    potions / choices。取得不能項目は null）。v0.3.0 で choices / potions に
    description、state に selected（ゲーム側フォーカス中要素。参照を確実に
    解決できる場合のみ、曖昧なら null）を追加
  - `GET /actions` → 実行可能な action 一覧（choices と対応）
  - `POST /do` → action_id 主キーで実行。失敗は `{ok:false, error:...}`
    （例外詳細は DoResult の形を変えず error 文字列に `exception: <message>` で含める）

### 実装範囲（v0.2.0 + v0.3.0）

- /state: ラン外判定（main_menu）、画面判定（combat を基本に
  NRewardsScreen→rewards / NMapScreen→map / カード選択系→card_selection の
  オーバーレイ上書き、部屋型フォールバック）、run(act/floor/gold)、
  player(hp/max_hp/block、戦闘中は energy/max_energy)、hand（cost は
  GetResolved→GetAmountToSpend→Canonical のフォールバック、説明は
  GetDescriptionForPile→Description のフォールバック、playable=CanPlay）、
  enemies（intent 付き）、potions、choices（map/event/rest/rewards）
- /do: play_card（対象ありは敵ごと）、end_turn（EndPlayerTurnAction →
  SetReadyToEndTurn フォールバック）、use_potion、choose_map
  （MoveToMapCoordAction → EnterMapCoord フォールバック）、choose_event、
  choose_rest、claim_reward、proceed
- rewards: NRewardsScreen の private フィールド（RewardsSet 型）を
  reflection で best-effort 取得。取れなければ choices 空 /
  claim_reward は `rewards_unavailable`
- shop / treasure / card_selection の choices は v0.2.0 では空だったが、
  v0.3.0 で表示専用の列挙を追加（card_select=提示カード名+説明、
  shop_item=在庫名+価格+説明、relic=提示レリック名+説明）。
  購入・宝箱選択・カード選択の「実行」は引き続き未対応

### API 実測による訂正（2026-11-15、ApiDump で確認）

- `MonsterModel.GetIntents()` は v0.113.1 では **private**（API_NOTES の
  スケッチと相違）。公開経路は `MonsterModel.NextMove`（MoveState）の
  `Intents` プロパティ。実装はそちらを第一経路にし、private GetIntents は
  reflection フォールバックとしてのみ使う。
- `MapCoord` のメンバーは public フィールド `col` / `row`（確定）。
  マップ choices は (row, col) 順ソートで index を安定化している。
- `CombatManager.SetReadyToEndTurn(Player, bool, Func<Task>)`、
  `EndPlayerTurnAction(Player, int)`、`MoveToMapCoordAction(Player, MapCoord)`、
  `NRewardsScreen` の private フィールド `_rewardsSet`（RewardsSet 型）を実測確認。

## 検証済み（2026-11-15、v0.2.0）

- PC（.NET SDK 9.0.317）で `build.ps1` 成功：**0 エラー / 0 警告**。
  dist へ SpireBridge.dll（46,592 bytes）+ SpireBridge.json（version 0.2.0）+
  pdb をステージング。ゲームの mods フォルダは無変更（STS2AIMCP のみ）。
- 生成 DLL のメタデータで `SpireBridge.ModEntry` の
  ModInitializerAttribute（引数 "Initialize"）を再確認。
- ゲーム外ハーネス（`harness/`、実プロバイダ構成・ゲームスレッドなし）で実測:
  - `GET /health` → `{"ok":true,"service":"SpireBridge","version":"0.2.0","game_version":"v0.113.1"}`
  - `GET /state` → `{"screen":"unknown",...空配列}`（例外なしフォールバック）
  - `GET /actions` → `[]`
  - `POST /do` → `{"ok":false,"error":"game_thread_unavailable"}`（落ちない）
- **v0.2.0 実機ロードテスト合格（2026-11-15）**：mods へ配置して実ゲームで
  起動し、SynchronizationContext の捕捉に成功。戦闘中の /state が実画面と
  完全一致（手札・コスト・説明・敵HP・インテント）、SpireRemote 経由の
  /api/state 中継も 200 を確認。/do の実行系は実プレイ中につき未検証。
- **v0.3.0（2026-11-15）**：`build.ps1` 成功（0 エラー / 0 警告）、
  SpireBridge.dll（57,856 bytes）を dist へステージング。ゲーム外ハーネスの
  DTO / HTTP 検証 PASS。**実機配置はまだ**（次回ゲーム起動時に v0.2.0 と
  差し替える。selected の実機一致、shop / treasure の実読み取りはその時に確認）。

## ビルド（PC 上）

```powershell
# VM の bridge/ を PC の <this bridge folder>\ に同期してから
powershell -ExecutionPolicy Bypass -File <this bridge folder>\build.ps1
```

成果物は `<this bridge folder>\dist\SpireBridge\`（dll + json）にだけ置く。
**ゲームの mods フォルダには一切入れない**（ロードテストは別途調整して実施）。

ゲームのインストール先が違う場合は `-p:Sts2GameDir=...`（csproj のプロパティ）で切り替え可能。
