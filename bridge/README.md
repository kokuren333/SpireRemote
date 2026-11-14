# SpireBridge — STS2 自前ブリッジ mod

Slay the Spire 2 のゲーム内から状態を読み、ローカル HTTP で公開するための
自前 mod。SpireRemote の「選択肢ボタン」層の土台。

## 状態（v0.2.0、2026-11-15 統合実装済み）

- 状態読み取り／アクション実行の本体を統合済み（`GameStateProvider` /
  `GameActionExecutor`）。ゲーム API 呼び出しは全て `GameThread`
  （初期化時に捕捉した SynchronizationContext へ Post、5 秒タイムアウト）
  経由でゲームスレッド上で実行する。
- HTTP: `127.0.0.1:8766`
  - `GET /health` → `{ok, service, version:"0.2.0", game_version}`（game_version は
    sts2 アセンブリ脇の `release_info.json` から取得、失敗時は null）
  - `GET /state` → 実読み取り（screen / run / player / hand / enemies /
    potions / choices。取得不能項目は null）
  - `GET /actions` → 実行可能な action 一覧（choices と対応）
  - `POST /do` → action_id 主キーで実行。失敗は `{ok:false, error:...}`
    （例外詳細は DoResult の形を変えず error 文字列に `exception: <message>` で含める）

### 実装範囲（v0.2.0）

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
- shop / treasure / card_selection の choices は v1 では空（未対応）

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
- **実機ロードテストは未実施**（ユーザーのプレイ中は mods に入れない方針）。
  ゲーム内での SynchronizationContext 捕捉、画面判定、インテント表示、
  各アクションの実挙動はロードテストで確認する。

## ビルド（PC 上）

```powershell
# VM の bridge/ を PC の <this bridge folder>\ に同期してから
powershell -ExecutionPolicy Bypass -File <this bridge folder>\build.ps1
```

成果物は `<this bridge folder>\dist\SpireBridge\`（dll + json）にだけ置く。
**ゲームの mods フォルダには一切入れない**（ロードテストは別途調整して実施）。

ゲームのインストール先が違う場合は `-p:Sts2GameDir=...`（csproj のプロパティ）で切り替え可能。
