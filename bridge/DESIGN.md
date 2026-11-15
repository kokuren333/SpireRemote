# SpireBridge 設計書（v0.4.0）

STS2（Slay the Spire 2）自前ブリッジ mod の設計・API仕様・手順書。
既存 mod（STS2AIMCP / STS2-Agent）は内部仕様の参考のみで、コードは全て自前。
ゲームのアップデートに自分で追随できるよう、依存は「ゲーム本体 DLL の公開 API」だけに絞っている。

- 対象ゲーム: STS2 v0.113.1 / build 26625880（public-beta、Godot 4.5系 C#、.NET 9）
- mod: SpireBridge 0.4.0（.NET 9、C#）
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
{"ok":true,"service":"SpireBridge","version":"0.4.0","game_version":"v0.113.1"}
```

- `game_version` は sts2 アセンブリ脇の `release_info.json` から取得。失敗時は `null`。
- version は 0.4.0。上記 game_version は形の例であり、取得できなければ null。ゲーム外実測は末尾と `uiv2/hb_ledger.md` 参照。

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
     "intent": "攻撃 7", "label": "A", "pos": {"x":0.612,"y":0.548,"w":0.11,"h":0.26}, "alive": true}
  ],
  "potions": [
    {"index": 0, "id": "FIRE_POTION", "name": "火炎ポーション", "description": "敵にダメージを与える。"}
  ],
  "choices": [],
  "selected": null
}
```

| フィールド | 内容 | 取得経路 |
|---|---|---|
| `screen` | `main_menu` / `combat` / `map` / `event` / `rest` / `shop` / `treasure` / `rewards` / `card_selection` / `unknown` | CombatManager.IsInProgress を基本に、ActiveScreenContext の現画面型（NRewardsScreen 等）でオーバーレイを上書き、最後に CurrentRoom の具象型 |
| `run.act` | 現在の Act（1始まり） | IRunState.CurrentActIndex + 1 |
| `run.floor` | 通算フロア | IRunState.TotalFloor |
| `run.gold` | ゴールド | Player.Gold |
| `player.hp/max_hp/block` | プレイヤー本体 | Creature.CurrentHp / MaxHp / Block |
| `player.energy/max_energy` | 戦闘中のみ（戦闘外は null） | PlayerCombatState.Energy / MaxEnergy |
| `hand[]` | 手札（並び順 = index）。`cost` は実効コスト、`description` は動的数値反映済み文字列、`playable` は使用可否 | PlayerCombatState.Hand.Cards。cost は `EnergyCost.GetResolved()` → `GetAmountToSpend()` → `Canonical` の順にフォールバック。説明は `GetDescriptionForPile(Hand)` → `Description.GetFormattedText()` |
| `enemies[]` | 敵一覧。`intent` はインテントの表示文字列（攻撃は合計ダメージ付きを試行） | CombatState.Enemies。インテントは `MonsterModel.NextMove.Intents`（public 経路）。※ API_NOTES 記載の `GetIntents()` は v0.113.1 では private のため reflection フォールバックのみ |
| `potions[]` | 所持ポーション（空きスロットは含まない）。description は string または null | Player.Potions / PotionModel.DynamicDescription.GetFormattedText() |
| `choices[]` | index / label / kind / description（string または null）/ enabled（bool または null） | event は !IsLocked、rest は IsEnabled、それ以外の enabled は null。description は独立した説明 |
| `selected` | null または {kind, index, name, description}。kind は card / choice / potion / enemy / other、index は int または null、文字列は nullable | ゲームスレッド上の GetViewport().GuiGetFocusOwner() → 所有ノード → 同一モデル参照を現在の配列内で照合。曖昧・未解決なら null |

`choices` の画面別生成:

| screen | choices の中身 | kind |
|---|---|---|
| map | 現在地から進めるマップノード（PointType 名、(row,col) 順で index 安定化） | `map` |
| event | イベントの現在選択肢（ロック中は label に付記） | `event` |
| rest | 休憩所の選択肢（無効なものは付記） | `rest` |
| rewards | 提示中の報酬（RewardsSet を NRewardsScreen の private フィールド `_rewardsSet` から reflection で best-effort 取得。取れなければ空） | `reward` |
| card_selection | アクティブ画面の CardModel / CardCreationResult コレクションから表示カード。矛盾する複数コレクションは空 | `card_select`（表示専用） |
| shop | 開いている NRun.MerchantRoom.Inventory.Inventory の在庫モデル。label=名前、description=価格G ＋ 説明。売切れとモデルなしスロットは除外 | `shop_item`（表示専用） |
| treasure | 開いたレリック選択 UI の HoldersInUse → Relic.Model | `relic`（表示専用） |

ラン外（メインメニュー等）: `{"screen":"main_menu","run":null,"player":null,"hand":[],"enemies":[],"potions":[],"choices":[],"selected":null}` の形で返す。

Display-only choices add no /actions and have no execution support. Missing values stay null/empty. selected uses reference identity, never names or IDs. New in-engine readers await supervised validation.

### GET /actions

今できるアクションの列挙。`/state` の hand / choices と対応する。

```json
[
  {"action_id": "play_card:0", "kind": "play_card", "label": "ストライク", "target": null, "target_index": null, "target_label": null},
  {"action_id": "play_card:1:0", "kind": "play_card", "label": "バッシュ -> A ジャウワーム", "target": "ジャウワーム", "target_index": 0, "target_label": "A"},
  {"action_id": "end_turn", "kind": "end_turn", "label": "End Turn", "target": null, "target_index": null, "target_label": null},
  {"action_id": "use_potion:0", "kind": "use_potion", "label": "火炎ポーション", "target": null, "target_index": null, "target_label": null},
  {"action_id": "choose_map:0", "kind": "choose_map", "label": "Monster (2,1)", "target": null, "target_index": null, "target_label": null}
]
```

action_id 規約:

D-3 訂正: 対象ありの `target` は敵名であり、文字列の敵 index ではない。矢印は ASCII ` -> `。v0.4.0 は敵ラベルも挿入する（ラベル null なら従来形 `名前 -> 敵名`）。クライアントは `label` / `target` の文字列を解析せず、`action_id` の第 3 要素で敵 index を得る。以下の `/do` の別形式リクエストでの `target:"0"` は入力契約であり、`/actions` の `target` とは異なる。

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
powershell -NoProfile -ExecutionPolicy Bypass -File <this bridge folder>\build.ps1
```

- csproj は `data_sts2_windows_x86_64\sts2.dll` と `GodotSharp.dll` を参照（Private=false ＝ ゲーム側の実物を使う）。ゲームの場所が違う場合は `-p:Sts2GameDir=<パス>`。
- 今回の成果物は `<this bridge folder>\dist\SpireBridge\`（SpireBridge.dll / SpireBridge.json / SpireBridge.pdb）にだけ置かれる。**build.ps1 はゲームの mods フォルダに一切触らない。**
- 実績（2026-11-15、v0.2.0）: 0 エラー / 0 警告。DLL 46,592 bytes。

## 4. 配置手順（実機ロードテスト時にのみ実施）

**0.4.0 はステージングのみ、mods 未配置。** 稼働中ブリッジ、ゲームプロセス、mods、ライブホストには触れない。配備は次回ゲーム起動に合わせて統括が行う。以下は将来の手順であり本工程では実行しない。

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

## 5. 未対応範囲（v0.4.0）

- **ショップ**: 在庫表示は best-effort 実装。購入アクションは未対応。カード除去や未知の在庫型はモデル名を確実に読めないため列挙しない。閉じた在庫は空。実機未確認。
- **宝箱**: 開いた提示レリック UI の choices は best-effort 実装。レリック選択（PickRelicAction）は未対応。閉じた UI / 経路不一致は空。実機未確認。
- **カード選択画面**: アクティブ画面の提示コレクション表示を実装。カードの確定操作は未対応。カード以外の報酬代替や未知の画面型、矛盾するコレクションは列挙しない。
- **selected**: フォーカスノードと親（最大12階層）の確認済み所有メンバーのみ走査。プレビュー対象、キャッシュしたフォーカス、兄弟、モデル一覧は走査しない。キーボード操作時の focus-owner の実挙動、ポップアップ／未知のラッパは実機未検証。確実に照合できなければ null。
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


## Round 2 verification (2026-11-15)

- Build and staging: run `powershell -NoProfile -ExecutionPolicy Bypass -File bridge/build.ps1` from SpireWork. Output stays under `bridge/dist/SpireBridge/`. No mods deployment. Assembly/manifest/health version: 0.3.0.
- Isolated check: `dotnet build bridge/harness/Harness.csproj`, then `python spire/test_proxy.py --harness bridge/harness/bin/Debug/net9.0/SpireBridgeHarness.dll`. Python reserves only owned ephemeral loopback listeners, and launches the harness with an isolated `--port`. It never probes 8765/8766.
- Harness default provider returns the real unknown/empty/null fallback without a game thread. Explicit `--fixture` uses synthetic populated DTOs to check description keys through the same HTTP serializer; this is not evidence of game model reads. Managed startup checks cover reference-only selection, ambiguity, missing focus, guarded reflection and DTO null serialization.
- Fake bridge supports `--port <isolated-port> --card-selection` and `/fixtures/card_selection` for display-only client testing. Combat fixture includes potion description and selected hand card index 1.
- See `API_NOTES.md` ?13 for confirmed metadata paths and unverified runtime behavior; `codex_ledger.md` Round 2 records commands, results and final SHA256.

## v0.4.0 additions and verification (2026-11-16)

既存フィールドは保持し、`enemies[].index` は `CombatState.Enemies` の配列順のまま。`/do` の対象解決は変更しない。取得不能値は null、キーを省略しない。追加処理の例外はその値を null にし、他のスナップショットを保持する。ゲーム API は全て既存の `GameThread.Invoke` 経路で読む。

| 場所 | 追加フィールド | 型・意味 |
|---|---|---|
| enemies[] | label | string または null。戦闘内の個体ラベル |
| enemies[] | pos | object または null。中心 x/y、幅 w、高さ h（小数 4 桁） |
| enemies[] | alive | bool または null。Creature.IsAlive |
| choices[] | enabled | bool または null。event=!IsLocked、rest=IsEnabled、他は null |
| actions[] | target_index | int または null。対象の敵 index |
| actions[] | target_label | string または null。対象の敵 label |

同名の敵と座標取得失敗が混在する応答例（合成値）:

```json
{"enemies":[
  {"index":0,"name":"スライム","hp":20,"max_hp":24,"block":2,"intent":"攻撃 7","label":"A","pos":{"x":0.6120,"y":0.5480,"w":0.1100,"h":0.2600},"alive":true},
  {"index":1,"name":"スライム","hp":24,"max_hp":24,"block":0,"intent":"防御 5","label":"B","pos":null,"alive":true}
]}
```

```json
{"action_id":"play_card:1:1","kind":"play_card","label":"ストライク -> B スライム","target":"スライム","target_index":1,"target_label":"B"}
```

event/rest の `enabled` 読取失敗は null。旧クライアント用の label 末尾 ` [locked]` / ` [disabled]` とアクション列挙は保持する。対象なしアクションの label は変更せず、target_index/target_label は null。JSON の数値は末尾ゼロを省くことがある（精度は小数 4 桁）。

### B-4 ラベル割当

状態は現在の CombatState 参照、ReferenceEqualityComparer による Creature 参照→ラベル表、次番号。CombatState 参照変更または戦闘外で表と番号をリセットし、戦闘外に Creature を保持しない。未登録の敵だけを pos.x 昇順、pos なしは後ろ、同順位は配列 index 昇順に並べる。A〜Z、A2〜Z2、A3…を順に割当し、死亡・消滅・移動があっても既存ラベルは変えない。名前・HP・ID・値の等価性で照合しない。位置計算後に割当し、同じ provider の表を `/state` と `/actions` で共有する。各 Build は同一の Built 内で状態とアクションを構築する。

### B-5 座標の基準と変換

基準はゲームウィンドウのクライアント領域全体（配信フレーム全体）。左上 (0,0)、右下 (1,1)。`NRun.Instance.CombatRoom` を深さ 8・2000 ノード上限で走査し NCreature を列挙する。Entity と enemies の参照同一性で照合し、Hitbox（Control、存在をメタデータ確認済み）の矩形を使う。Hitbox が null なら NCreature 自身を使う。

実装は `Viewport.GetFinalTransform() * Control.GetGlobalTransformWithCanvas()` をローカル矩形 `(Vector2.Zero, Control.Size)` に適用し、クライアント領域ピクセル矩形を得る。グローバル矩形にグローバル変換を二重適用しない。ルートは `GetTree().Root`、サイズは `Window.Size`。純粋関数 EnemyDisplayLogic.Normalize が中心・サイズをウィンドウサイズで割り、小数 4 桁に丸める。範囲判定は丸める前に行い、範囲内へのクランプはしない。

### B-6 pos=null の条件

1. 対応する NCreature が 0 個、または 2 個以上。
2. 対応ノードまたは矩形ノードがツリー外、または IsVisibleInTree() が偽。
3. ノードのビューポートがルート Window でない、または Window.Size の幅・高さが 0 以下。
4. 計算値が非有限、または矩形の幅・高さが 0 以下。
5. 中心 x/y が 0..1 の外（丸める前に判定）。
6. alive が偽。
7. 深さ 8 または 2000 ノードの上限を超える（全敵の pos が null）。
8. 途中で例外。走査全体の失敗は全敵 null、個別取得の失敗はその敵のみ null。

### ゲーム外検証と次回の B-13 チェックリスト

`dotnet build bridge/harness/Harness.csproj` 後、spire を cwd として `python -B test_proxy.py --harness ../bridge/harness/bin/Debug/net9.0/SpireBridgeHarness.dll`。default は実 provider の unknown/空配列/game_thread_unavailable を維持する。`--fixture` は従来の表示専用 card_selection、`--fixture-v4` は値あり/null 両方と enabled true/false/null を実 DTO/シリアライザで検証する合成 fixture。各起動は既存 DisplayChecks と AC-B2/B3/B4 の管理コード検査を通ってから HARNESS_READY を出す。テストは OS 割当の所有ポートのみを使う。

fake の `/fixtures/combat_v4`、`/fixtures/combat_v3`、`/fixtures/event`、`/fixtures/rewards` は `{state,actions}` の組を返す。テストは server.state/server.actions を差し替えてホスト経由の完全一致を確認する。従来の `/fixtures/card_selection` は状態単体のまま。

メタデータとビルド、合成データでの検証のみ実施した。座標精度、戦闘直後、死亡時の配列、召喚、A/D/F の実機挙動は**実機未検証**。以下は統括が次回ゲーム起動時に確認する項目（本工程では実施しない）:

- [ ] ① ウィンドウサイズ・縦横比を変え、pos と映像上の敵が一致するか。
- [ ] ② 戦闘開始直後の初回スナップショットで pos が取れるか。
- [ ] ③ 敵死亡時に Enemies 配列が詰まるか、alive の推移と対象 index がどう変わるか。
- [ ] ④ 召喚された敵に次の未使用ラベルが付き、既存個体のラベルが変わらないか。
- [ ] ⑤ キー A/D/F が山札／捨て札／廃棄を開くか。

v0.4.0 は `bridge/dist/SpireBridge/` にステージングしただけで mods 未配置。配備は次回ゲーム起動時に統括が行う。測定出力・ファイルハッシュは `uiv2/hb_ledger.md`。
