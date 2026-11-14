# SpireBridge 設計書（v0.2.0）

STS2（Slay the Spire 2）自前ブリッジ mod の設計・API仕様・手順書。
既存 mod（STS2AIMCP / STS2-Agent）は内部仕様の参考のみで、コードは全て自前。
ゲームのアップデートに自分で追随できるよう、依存は「ゲーム本体 DLL の公開 API」だけに絞っている。

- 対象ゲーム: STS2 v0.113.1 / build 26625880（public-beta、Godot 4.5系 C#、.NET 9）
- mod: SpireBridge 0.2.0（.NET 9、C#）
- HTTP: `http://127.0.0.1:8766/`（loopback のみ、認証なし＝PC 内からの利用前提。外部公開は SpireRemote ホスト側のトークン認証付き中継で行う）
- 調査根拠: `API_NOTES.md`（sts2.dll メタデータ実測ノート）

## 1. アーキテクチャ

```
[SpireRemote host.py (将来)] --HTTP--> [SpireBridge mod (ゲームプロセス内)]
                                          ├─ BridgeHttpServer (HttpListener, ワーカースレッド)
                                          ├─ GameThread (SynchronizationContext へ Post、5秒タイムアウト)
                                          ├─ GameStateProvider (/state, /actions の実読み取り)
                                          └─ GameActionExecutor (/do の実行)
```

- HTTP はワーカースレッドで受けるが、ゲーム API には一切触らない。全ての状態読み取り・アクション実行は、mod 初期化時（ゲームスレッド）に捕捉した `SynchronizationContext` へ Post してゲームスレッド上で実行する（`GameThread.Invoke`）。捕捉できていない／タイムアウト／例外の場合は、無理に実行せず失敗を返す。
- ゲーム API 呼び出しは各所 try/catch で防御。取れない項目は null / 空配列で正直に返し、捏造しない。
- ログは `BridgeLog` 経由のみ（GD.Print はエンジン外でネイティブ例外になるため、ゲーム内初期化後にのみ有効化）。

### ファイル構成（正本: VM `bridge/`）

| ファイル | 役割 |
|---|---|
| `src/SpireBridge/ModEntry.cs` | `[ModInitializer("Initialize")]` エントリ。GameThread 捕捉、プロバイダ生成、HTTP 起動（失敗時は Stub にフォールバックしてサーバだけ生かす） |
| `src/SpireBridge/BridgeHttpServer.cs` | HttpListener ルーティング + BridgeLog |
| `src/SpireBridge/GameThread.cs` | ゲームスレッドへのマーシャリング |
| `src/SpireBridge/GameAccess.cs` | RunManager/CombatManager/ローカルプレイヤー等の共通取得ヘルパ |
| `src/SpireBridge/GameStateProvider.cs` | /state・/actions の実読み取り |
| `src/SpireBridge/GameActionExecutor.cs` | /do の実行 |
| `src/SpireBridge/IStateProvider.cs` | DTO 定義（JSON 契約）+ インターフェース |
| `src/SpireBridge/StubProviders.cs` | フォールバック用スタブ |
| `SpireBridge.json` | mod マニフェスト（id=SpireBridge, has_dll=true, has_pck=false, affects_gameplay=false, min_game_version=0.113.0） |
| `build.ps1` | ビルド + dist へのステージング（mods フォルダには触れない） |
| `harness/` | ゲーム外で HTTP 層だけを検証するテストハーネス |

## 2. API 仕様

### GET /health

mod の生存と版情報。

```json
{"ok":true,"service":"SpireBridge","version":"0.2.0","game_version":"v0.113.1"}
```

- `game_version` は sts2 アセンブリ脇の `release_info.json` から取得。失敗時は `null`。
- 上記はゲーム外ハーネスでの実測値（2026-11-15）。

### GET /state

現在のゲーム状態スナップショット。取得できない項目は `null`（配列は空）。

戦闘中の応答例（形の例示。実機値はロードテストで採取する）:

```json
{
  "screen": "combat",
  "run": {"act": 1, "floor": 7, "gold": 143},
  "player": {"hp": 61, "max_hp": 75, "block": 0, "energy": 3, "max_energy": 3},
  "hand": [
    {"index": 0, "id": "STRIKE", "name": "ストライク", "cost": 1,
     "description": "6ダメージを与える。", "playable": true},
    {"index": 1, "id": "DEFEND", "name": "防御", "cost": 1,
     "description": "5ブロックを得る。", "playable": true}
  ],
  "enemies": [
    {"index": 0, "name": "ジャウワーム", "hp": 44, "max_hp": 44, "block": 0,
     "intent": "攻撃 7"}
  ],
  "potions": [
    {"index": 0, "id": "FIRE_POTION", "name": "火炎ポーション"}
  ],
  "choices": []
}
```

| フィールド | 内容 | 取得経路 |
|---|---|---|
| `screen` | `main_menu` / `combat` / `map` / `event` / `rest` / `merchant` / `treasure` / `rewards` / `card_selection` / `unknown` | CombatManager.IsInProgress を基本に、ActiveScreenContext の現画面型（NRewardsScreen 等）でオーバーレイを上書き、最後に CurrentRoom の具象型 |
| `run.act` | 現在の Act（1始まり） | IRunState.CurrentActIndex + 1 |
| `run.floor` | 通算フロア | IRunState.TotalFloor |
| `run.gold` | ゴールド | Player.Gold |
| `player.hp/max_hp/block` | プレイヤー本体 | Creature.CurrentHp / MaxHp / Block |
| `player.energy/max_energy` | 戦闘中のみ（戦闘外は null） | PlayerCombatState.Energy / MaxEnergy |
| `hand[]` | 手札（並び順 = index）。`cost` は実効コスト、`description` は動的数値反映済み文字列、`playable` は使用可否 | PlayerCombatState.Hand.Cards。cost は `EnergyCost.GetResolved()` → `GetAmountToSpend()` → `Canonical` の順にフォールバック。説明は `GetDescriptionForPile(Hand)` → `Description.GetFormattedText()` |
| `enemies[]` | 敵一覧。`intent` はインテントの表示文字列（攻撃は合計ダメージ付きを試行） | CombatState.Enemies。インテントは `MonsterModel.NextMove.Intents`（public 経路）。※ API_NOTES 記載の `GetIntents()` は v0.113.1 では private のため reflection フォールバックのみ |
| `potions[]` | 所持ポーション（空きスロットは含まない） | Player.Potions |
| `choices[]` | 選択肢が出る画面での選択肢一覧（下表） | 画面別 |

`choices` の画面別生成:

| screen | choices の中身 | kind |
|---|---|---|
| map | 現在地から進めるマップノード（PointType 名、(row,col) 順で index 安定化） | `map` |
| event | イベントの現在選択肢（ロック中は label に付記） | `event` |
| rest | 休憩所の選択肢（無効なものは付記） | `rest` |
| rewards | 提示中の報酬（RewardsSet を NRewardsScreen の private フィールド `_rewardsSet` から reflection で best-effort 取得。取れなければ空） | `reward` |
| merchant / treasure / card_selection | **v0.2.0 では空**（未対応、§5 参照） | — |

ラン外（メインメニュー等）: `{"screen":"main_menu","run":null,"player":null,"hand":[],"enemies":[],"potions":[],"choices":[]}` の形で返す。

### GET /actions

今できるアクションの列挙。`/state` の hand / choices と対応する。

```json
[
  {"action_id": "play_card:0", "kind": "play_card", "label": "ストライク", "target": null},
  {"action_id": "play_card:1:0", "kind": "play_card", "label": "バッシュ → ジャウワーム", "target": "0"},
  {"action_id": "end_turn", "kind": "end_turn", "label": "ターン終了", "target": null},
  {"action_id": "use_potion:0", "kind": "use_potion", "label": "火炎ポーション", "target": null},
  {"action_id": "choose_map:0", "kind": "choose_map", "label": "モンスター (row 1, col 2)", "target": null}
]
```

action_id 規約:

| kind | action_id | 意味 |
|---|---|---|
| play_card | `play_card:{handIndex}` / `play_card:{handIndex}:{enemyIndex}` | 対象が要るカードは敵ごとに1アクション |
| end_turn | `end_turn` | ターン終了（戦闘中・自ターン） |
| use_potion | `use_potion:{potionIndex}` / `:{enemyIndex}` 付き | ポーション使用 |
| choose_map | `choose_map:{choiceIndex}` | マップノード選択 |
| choose_event | `choose_event:{choiceIndex}` | イベント選択肢 |
| choose_rest | `choose_rest:{choiceIndex}` | 休憩所選択肢 |
| claim_reward | `claim_reward:{choiceIndex}` | 報酬の受け取り |
| proceed | `proceed` | 報酬画面等の「進む」 |

### POST /do

リクエスト:

```json
{"action_id": "play_card:1:0"}
```

`action_id` の代わりに `{"kind":"play_card","index":1,"target":"0"}` 形式でも指定可能。

成功:

```json
{"ok": true, "error": null}
```

失敗（HTTP は 200、結果は JSON で返す。サーバ内部例外時のみ 500）:

```json
{"ok": false, "error": "card_not_found"}
```

主な error 値: `game_thread_unavailable`（ゲームスレッド未捕捉/タイムアウト）、`not_implemented`、`unsupported_kind`、`card_not_found` / `target_not_found` / `choice_not_found` / `potion_not_found`、`not_playable`、`potion_use_locked`、`rewards_unavailable`、`not_in_combat`、`exception: <詳細>`。

実行経路（ゲーム側の正規 API を使用）:

| kind | 実行方法 |
|---|---|
| play_card | `CardModel.TryManualPlay(target)` |
| end_turn | `EndPlayerTurnAction(player, TurnNumber)` を `ActionQueueSynchronizer.RequestEnqueue`。失敗時 `CombatManager.SetReadyToEndTurn(player, true, null)` にフォールバック |
| use_potion | `PotionModel.EnqueueManualUse(target)`。`RunManager.IsPotionUseLocked` のときは事前エラー |
| choose_map | `MoveToMapCoordAction(player, coord)` を RequestEnqueue。失敗時 `RunManager.EnterMapCoord(coord)` |
| choose_event | `EventOption.Chosen()` を発火（完了は待たない） |
| choose_rest | `RestSiteOption.OnSelect()` を発火（同上） |
| claim_reward | `Reward.SelectUnsynchronized()` を発火（RewardsSet が取れている場合のみ） |
| proceed | `RunManager.Proceed()` |

注意: /do は実行時点の最新状態で対象を再解決する。/actions 取得から時間が経って手札や選択肢が変わっていると、index の指す対象がずれる可能性がある（AIMCP の decision_id 方式のような版数チェックは v0.2.0 では未実装）。

## 3. ビルド手順（PC 上）

前提: PC に .NET SDK 9.0.317、ゲームが `C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2` にインストール済み。

```powershell
# 1. VM の正本を PC へ同期（VM 側から）
#    bridge/ -> <this bridge folder>\
# 2. PC 上でビルド + ステージング
powershell -ExecutionPolicy Bypass -File <this bridge folder>\build.ps1
```

- csproj は `data_sts2_windows_x86_64\sts2.dll` と `GodotSharp.dll` を参照（Private=false ＝ ゲーム側の実物を使う）。ゲームの場所が違う場合は `-p:Sts2GameDir=<パス>`。
- 成果物は `<this bridge folder>\dist\SpireBridge\`（SpireBridge.dll / SpireBridge.json / SpireBridge.pdb）にだけ置かれる。**build.ps1 はゲームの mods フォルダに一切触らない。**
- 実績（2026-11-15、v0.2.0）: 0 エラー / 0 警告。DLL 46,592 bytes。

## 4. 配置手順（実機ロードテスト時にのみ実施）

**現在は未配置。** ユーザーのプレイ中は mods フォルダに入れない方針のため、配置は次回ゲーム起動に合わせてユーザーの前で行う。

```powershell
# ゲームが終了していることを確認してから:
New-Item -ItemType Directory -Force "C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2\mods\SpireBridge"
Copy-Item <this bridge folder>\dist\SpireBridge\SpireBridge.dll  "...\mods\SpireBridge\"
Copy-Item <this bridge folder>\dist\SpireBridge\SpireBridge.json "...\mods\SpireBridge\"
# （pdb は任意）
```

- 配置後、ゲームを通常通り起動（mod は自動で読み込まれる想定。ゲーム内 Mod 設定で有効化が必要な場合はそこで有効化）。
- 戻し方: `mods\SpireBridge\` フォルダを削除するだけ。既存 mod（STS2AIMCP 等）には一切干渉しない。
- ポート 8766 が他プロセスと競合した場合は、BridgeHttpServer の Prefix を変更して再ビルド（報告書を残すこと）。

## 5. 未対応範囲（v0.2.0）

- **ショップ**: 在庫の choices 列挙と購入（buy アクション）未対応。NMerchantRoom が abstract + UI ラッパ経由で、モデル層 `MerchantInventory` への素直な経路が未確定のため見送り（画面・所持品の表示自体は /state で返る）。
- **宝箱**: 提示レリック一覧の取得経路が未確定のため、choices 列挙とレリック選択（PickRelicAction）未対応。
- **カード選択画面**（カード報酬の選択、手札/デッキからの選択等）: 確定操作の最小経路が未確定のため未対応（screen 判定のみ）。
- **報酬フロー全体**: RewardsSet の取得が reflection best-effort。claim_reward 後のカード報酬選択画面への遷移も含め、実機確認が必須。
- **ポーションの対象**: 対象必須判定は AnyEnemy のみ対応。AnyPlayer/AnyAlly 等は null 対象で渡している。
- **マルチプレイ**: ローカルプレイヤーは `Players[0]` 前提（シングルプレイ想定）。同期層（RequestEnqueue 経由）がシングルで問題ないかも含め実機確認待ち。
- **被ダメージ予測などの preview**: AIMCP にあった end-turn シミュレーション等は未実装（/actions の label は名称と対象のみ）。

## 6. 実機テスト計画（次回ゲーム起動時、ユーザーの前で実施）

前提: §4 の配置 → ゲーム起動 → PC ローカルから `curl http://127.0.0.1:8766/...` で確認（SpireRemote ホスト中継はその後）。

1. **ロード確認**: ゲームが正常起動し、godot.log に `[SpireBridge] Ready` が出ること。/health が `game_version:"v0.113.1"` を返すこと。
   - ここで SynchronizationContext が捕捉できているかも同時に分かる（/state が unknown のままなら捕捉失敗 → GameThread を Callable.CallDeferred 方式へ切り替える改修が必要）。
2. **メインメニュー**: /state が `screen:"main_menu"` を返すこと。
3. **ラン開始 → マップ**: /state が `screen:"map"` + choices（次ノード一覧）を返すこと。/do `choose_map:0` で実際に進めること。
4. **戦闘**: /state の hand（コスト表示がゲーム内数字と一致するか）、enemies の intent（表示中インテントと一致するか、攻撃ダメージ数値が正しいか）、energy を目視照合。/do で `play_card`（対象なし・対象あり両方）と `end_turn` が効くこと。end_turn は EndPlayerTurnAction 経路と SetReadyToEndTurn フォールバックのどちらで進んだかログで確認。
5. **ポーション**: /do `use_potion` が効くこと（戦闘中・戦闘外）。
6. **戦闘終了 → 報酬**: /state が `screen:"rewards"` になり choices が出るか（reflection 取得の成否）。`claim_reward` → カード報酬は選択画面に遷移するか（遷移先は未対応なので、画面が出たところまでを確認し、手動で選ぶ）。
7. **イベント / 休憩所**: choices の label がゲーム表示と一致すること、/do で選択が進行すること。
8. **終了処理**: ゲームを通常終了し、異常なエラーログが残っていないこと。問題があれば mods\SpireBridge を削除して切り分ける。

各ステップで /state の実 JSON を採取し、この DESIGN.md の「応答例」を実測値に差し替えること。
