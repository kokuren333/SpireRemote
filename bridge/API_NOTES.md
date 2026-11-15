# STS2 API ノート — ゲーム本体 DLL 公開 API 調査（SpireBridge 実装担当向け）

- 調査日: 2026-11-15
- 対象ビルド: **v0.113.1**（`release_info.json`: version=v0.113.1 / branch=v0.113.1 / commit=d09d48e5 / date=2026-11-14。先行調査時の v0.113.0 から更新済み）
- 対象ファイル: `data_sts2_windows_x86_64\sts2.dll`（10,648,064 bytes、アセンブリ名 `sts2`、AssemblyVersion 0.1.0.0、型総数 **10,705**）
- 手法: 自作メタデータ解析ツール（`System.Reflection.Metadata` + `PEReader`、net9.0）で型・メソッド・プロパティ・フィールド・カスタム属性を読み取り専用でダンプ。加えて同梱 XML ドキュメント `sts2.xml`（メンバー 22,131 件）の summary を照合。ゲームの起動・終了・入力・既存 mod 変更は一切していない。
- 確度表記: **** = メタデータ（または XML doc / 属性ブロブ実測）で確認。**** = 表面からの推論で未検証。**** = メタデータだけでは分からない。

## ツールと成果物（PC 側）

- ツール: `<this bridge folder>\tools\ApiDump\`（`ApiDump.csproj` / `Program.cs`、.NET SDK 9.0.317 でビルド成功）
- 使い方: `ApiDump.exe <dll> alltypes|detail <部分文字列>|ns <名前空間部分文字列>|search-method <部分文字列>|attrdump <属性名部分文字列>`
- `detail` / `ns` は型の全メンバー（可視性・static・シグネチャ付き）を出力する。`attrdump` はカスタム属性の値ブロブを hex + 可読文字列で出す（属性引数の実測用）。
- ダンプ出力: `<this bridge folder>\tools\out\`（sts2.dll 全型・主要型詳細・検索結果）、`...\tools\out2\`（追加詳細）。PowerShell リダイレクトのため **UTF-16LE** で保存されている点に注意（読むときは変換する）。
- 同ツールで既存 mod `STS2AIMCP.dll`（509 型）もメタデータのみ解析し、初期化パターンの実例として照合した（コードの流用ではなく、公開 API の使い方の実測）。

---

## 1. mod 初期化

### 1.1 属性

```csharp
namespace MegaCrit.Sts2.Core.Modding;

public class ModInitializerAttribute: System.Attribute
{
public string initializerMethod;
public ModInitializerAttribute(string initializerMethod); //.ctor(string)
}
```

- XML doc の原文: *"Declares a class as the main entry point for the mod. If this is present, then upon loading the mod, we'll call the method named initializerMethod within the class. Otherwise, we'll create a harmony instance for the mod and call Harmony.PatchAll."*
- つまり使い方:

```csharp
[ModInitializer("Initialize")]
public static class ModEntry
{
public static void Initialize() { /*... */}
}
```

- **実例で確定**: `STS2AIMCP.ModEntry`（`public abstract sealed` = 静的クラス）に同属性が付き、その属性値ブロブをデコードした結果が `01-00 | 0A "Initialize" | 00-00`（prolog + SerString 長 10 の "Initialize" + named引数 0 件）。同クラスには `public static void Initialize()` が存在する。→ 骨格 `SpireBridge.ModEntry` の現行形（属性 + 同名静的メソッド）は**正しい**。
- 属性が無い場合はローダが自動で Harmony `PatchAll` する（Harmony はゲーム同梱 `0Harmony.dll` 2.4.2）。
- ローダ側: `MegaCrit.Sts2.Core.Modding.ModManager`（静的クラス）
- `public static Task Initialize(IModManagerFileIo, ModSettings, SemanticVersion)`
- `private static bool CallModInitializer(System.Type)` — 初期化呼び出しの実体（private）
- `public static IReadOnlyList<Mod> Mods { get;}` / `public static bool IsRunningModded()` / `public static IEnumerable<Mod> GetLoadedMods()`

### 1.2 ModHelper（任意）

```csharp
public abstract sealed class ModHelper // 静的クラス
{
public static void AddModelToPool(); // ジェネリック版と推定されるが型引数はメタデータ表面に出ない
public static void AddModelToPool(System.Type, System.Type);
public static void SubscribeForRunStateHooks(string, RunHookSubscriptionDelegate);
public static void SubscribeForCombatStateHooks(string, CombatHookSubscriptionDelegate);
public static IEnumerable<AbstractModel> IterateAllRunStateSubscribers(RunState);
public static IEnumerable<AbstractModel> IterateAllCombatStateSubscribers(CombatState);
}
```

ブリッジ用途（状態読み + アクション実行）では必須ではない。コンテンツ追加 mod 向け。

### 1.3 マニフェスト形式

`ModManifest` は C# record（`System.Text.Json.Serialization.JsonPropertyName` 付きフィールド）。実物 `mods\STS2AIMCP\STS2AIMCP.json` の中身はそのまま以下:

```json
{
"id": "STS2AIMCP",
"name": "STS2 AI MCP",
"author": "BMingSY",
"description": "Local HTTP API bridge for Slay the Spire 2 AI and MCP clients.",
"version": "0.1.12",
"min_game_version": "0.107.1",
"has_pck": true,
"has_dll": true,
"dependencies": [],
"affects_gameplay": false
}
```

`ModManifest` のフィールド対応（JSON キーは snake_case）:

| JSON キー | C# フィールド | 型 |
|---|---|---|
| `id` | `id` | string |
| `name` | `name` | string |
| `author` | `author` | string |
| `description` | `description` | string |
| `version` | `version` | string |
| `has_pck` | `hasPck` | bool |
| `has_dll` | `hasDll` | bool |
| `dependencies` | `dependencies` | `List<ModDependency>`（要素は `{ id: string,...}` 形式と推定） |
| `affects_gameplay` | `affectsGameplay` | bool |
| `min_game_version` | `minGameVersion` | string |

配置規則（先行調査 + ローダ表面より）: `<ゲーム>\mods\<任意フォルダ>\<id>.json` + 同名 DLL / PCK。`Mod` レコード側の情報: `modSource: ModSource`、`path: string`、`state: ModLoadState`、`manifest: ModManifest`、`version: SemanticVersion`、`assemblies: List<Assembly>`、`workshopId: ulong?`。

---

## 2. ラン状態

### 2.1 入口: RunManager

```csharp
namespace MegaCrit.Sts2.Core.Runs;

public class RunManager: System.Object // IRunLobbyListener 実装
{
public static RunManager Instance { get;} // ★ public static、get のみ
private RunState State { get; set;} // ★ プロパティ自体が private（get/set とも private）
public RunState DebugOnlyGetState(); // ★ public。State を返す公式の抜け道
public bool IsInProgress { get;}
public bool IsGameOver { get;}
public bool IsAbandoned { get; private set;}
public bool HasRunEnded { get; private set;}
public bool IsPaused { get; set;}
public bool IsChangingRooms { get; private set;}
public bool IsPotionUseLocked { get;}
public bool IsSingleplayerOrFakeMultiplayer { get;}
public long RunTime { get;}
public ActionExecutor ActionExecutor { get; private set;} // §8 参照
public ActionQueueSet ActionQueueSet { get; private set;} // §8 参照
public ActionQueueSynchronizer ActionQueueSynchronizer { get; private set;}// §8 参照（RunManager 経由で取れるかは要確認: プロパティ名は ActionQueueSynchronizer で実在）
public MerchantSynchronizer MerchantSynchronizer { get; private set;} // §9.5
public RewardSynchronizer RewardSynchronizer { get; private set;}
public RestSiteSynchronizer RestSiteSynchronizer { get; private set;}
public TreasureRoomRelicSynchronizer TreasureRoomRelicSynchronizer { get; private set;}
public EventSynchronizer EventSynchronizer { get; private set;}
public MapSelectionSynchronizer MapSelectionSynchronizer { get; private set;}
public CardSelectorStack CardSelectorStack { get; private set;}
public event Action<RunState> RunStarted;
public event Action RoomEntered, RoomExited, ActEntered;
public event Action<bool> MapChanged;
public Task EnterMapCoord(MapCoord);
public void Proceed(); // §9.3（報酬/進行の汎用「進む」）
public Task ProceedFromTerminalRewardsScreenAsync();
// 以下はラン開始・部屋遷移系（ブリッジからは通常使わない）
public RunState Launch();
public Task EnterRoom(AbstractRoom);
public Task EnterNextAct();
public Task EnterAct(int, bool);
public void Abandon();
public void CleanUp(bool);
}
```

- **最重要**: `RunManager.Instance.State` は **private プロパティ**なので通常の C# からは読めない。しかし同一の値を返す **`DebugOnlyGetState()` が public** にある。XML doc の注意書きは *"THIS IS TEMPORARY AND SHOULD ONLY BE USED IN TESTS"* — 将来消える可能性を承知で使うか、private プロパティへのリフレクションを保険として併記すること。既存ブリッジ mod 群もこのメソッド経由（先行調査）。
- `DebugOnlyGetState()` の戻り値がラン未開始時に null か例外かは ****（メタデータでは nullable 注釈付き）。`IsInProgress` で先に判定するのが安全。

### 2.2 RunState / IRunState

`RunState` は public 具象クラスで、読み取り用インターフェース `IRunState`（public）を実装する。以後は `IRunState` 経由で読むのが安定。

```csharp
public interface IRunState: ICardScope, IPlayerCollection
{
IReadOnlyList<ActModel> Acts { get;}
int CurrentActIndex { get; set;}
ActModel Act { get;} // 現在の Act 本体
ActMap Map { get; set;}
IReadOnlyList<MapCoord> VisitedMapCoords { get;}
MapCoord? CurrentMapCoord { get;}
MapPoint CurrentMapPoint { get;} // nullable
RunLocation RunLocation { get;}
MapLocation MapLocation { get;}
int ActFloor { get; set;} // Act 内フロア
int TotalFloor { get;} // 通算フロア
int CurrentRoomCount { get;}
AbstractRoom CurrentRoom { get;} // ★ 現在の部屋（画面判定の第一候補、§7）
AbstractRoom BaseRoom { get;}
bool IsGameOver { get;}
GameMode GameMode { get;}
bool IsCustomGame { get;}
int AscensionLevel { get;}
IReadOnlyList<ModifierModel> Modifiers { get;}
// IPlayerCollection 側:
// IReadOnlyList<Player> Players（RunState 具象に public 実装あり）
}
public class RunState: IRunState
{
public IReadOnlyList<Player> Players { get;}
public static IRunState GetFrom(IEnumerable<Creature>); // IRunState 側の静的ヘルパ（Creature 群から逆引き）
//...
}
```

- ゴールド / HP は RunState ではなく **Player 側**（§5）。
- Act 名などの表示文字列は `ActModel`（AbstractModel 継承）側。`ActModel` の詳細メンバーは今回未精査。

---

## 3. 戦闘状態

### 3.1 入口: CombatManager

```csharp
namespace MegaCrit.Sts2.Core.Combat;

public class CombatManager
{
public static CombatManager Instance { get;} // ★ public static
public CombatState DebugOnlyGetState(); // ★ public、nullable。XML doc: "THIS IS TEMPORARY AND SHOULD ONLY BE USED IN TESTS"
public bool IsInProgress { get;}
public bool IsStarting { get;}
public bool IsEnding { get;}
public bool IsOverOrEnding { get;}
public bool IsAboutToLose { get;}
public bool PlayerActionsDisabled { get; private set;} // ★ アクション受付可否の判定に使える
public bool IsPaused { get; private set;}
public CombatId? CurrentCombatId { get;}
public CombatStateTracker StateTracker { get;}
public CombatHistory History { get;}
public bool IsPlayerReadyToEndTurn(Player);
public bool AllPlayersReadyToEndTurn();
public void SetReadyToEndTurn(Player, bool, Func<Task>); // ターン終了の内部経路の一つ（§9.2）
public void TryUndoReadyToEndTurn(Player);
public bool CanUnEndTurn(Player);
public event Action<CombatState> CombatSetUp, CombatBegan, CreaturesChanged, TurnStarted, TurnEnded, AboutToSwitchToEnemyTurn;
public event Action<CombatRoom> CombatEnded, CombatWon;
public event Action<Player, bool> PlayerEndedTurn;
}
```

- 戦闘状態の取得は `CombatManager.Instance.DebugOnlyGetState()` が唯一の public 経路（`State` プロパティ自体は CombatManager には存在しない）。戦闘外では null を返す設計と推定。`IsInProgress` と併用する。

### 3.2 CombatState / ICombatState

```csharp
public interface ICombatState
{
IRunState RunState { get;}
IReadOnlyList<Creature> Allies { get;}
IReadOnlyList<Creature> Enemies { get;} // ★ 敵一覧
IReadOnlyList<Creature> Creatures { get;}
IReadOnlyList<Creature> PlayerCreatures { get;}
IReadOnlyList<Player> Players { get;}
int RoundNumber { get; set;} // ★ ラウンド（= ターン数相当）
CombatSide CurrentSide { get; set;} // Player / Enemy
EncounterModel Encounter { get;}
IReadOnlyList<Creature> HittableEnemies { get;}
IReadOnlyList<Creature> CreaturesOnCurrentSide { get;}
event Action<ICombatState> CreaturesChanged;
Creature GetCreature(uint?);
}
public enum CombatSide { None, Player, Enemy}
public enum PlayerTurnPhase { None, Start, AutoPrePlay, Play, AutoPostPlay, End}
```

- 手札・エネルギー等は CombatState ではなく **Player.PlayerCombatState**（§4.2）にぶら下がる。

---

## 4. カードとパイル

### 4.1 パイル

```csharp
namespace MegaCrit.Sts2.Core.Entities.Cards;

public enum PileType { None, Draw, Hand, Discard, Exhaust, Play, Deck}

public class CardPile
{
public PileType Type { get;}
public IReadOnlyList<CardModel> Cards { get;} // ★ 列挙はこれ
public bool IsEmpty { get;}
public bool IsCombatPile { get;}
public static CardPile Get(PileType, Player);
public static IEnumerable<CardModel> GetCards(Player, params PileType[]);
public event Action ContentsChanged;
public event Action<CardModel> CardAdded, CardRemoved;
}
```

### 4.2 PlayerCombatState（戦闘中のプレイヤー状態）

```csharp
namespace MegaCrit.Sts2.Core.Entities.Players;

public class PlayerCombatState
{
public CardPile Hand { get;} // ★ 手札
public CardPile DrawPile { get;}
public CardPile DiscardPile { get;}
public CardPile ExhaustPile { get;}
public CardPile PlayPile { get;}
public IReadOnlyList<CardPile> AllPiles { get;}
public IEnumerable<CardModel> AllCards { get;}
public int Energy { get; set;} // ★ 現在エネルギー
public int MaxEnergy { get;}
public int Stars { get; set;}
public int TurnNumber { get; private set;}
public PlayerTurnPhase Phase { get; set;} // Play フェーズかどうかで「カードを出せるか」の目安
public OrbQueue OrbQueue { get;}
public IReadOnlyList<Creature> Pets { get;}
public bool HasEnoughResourcesFor(CardModel, ref UnplayableReason);
public bool HasCardsToPlay();
}
```

取得経路: `Player.PlayerCombatState { get; private set;}`（戦闘外は null）。

### 4.3 CardModel

```csharp
namespace MegaCrit.Sts2.Core.Models;

public abstract class CardModel: AbstractModel
{
// AbstractModel 共通:
// ModelId Id { get;} ← Category / Entry の2文字列を持つ record 型
// bool IsMutable { get;} / bool IsCanonical { get;}
public string Title { get;} // ★ 表示名（string、解決済み）
public LocString TitleLocString { get;}
public LocString Description { get;} // ★ 説明は LocString（§6）
public string GetDescriptionForPile(PileType, Creature); // 解決済み string を返す版
public CardEnergyCost EnergyCost { get;} // ★ コスト本体（§4.4）
public int CurrentStarCost { get;}
public TargetType TargetType { get;} // None/Self/AnyEnemy/AllEnemies/RandomEnemy/AnyPlayer/AnyAlly/AllAllies/TargetedNoCreature/Osty
public CardType Type { get;}
public CardRarity Rarity { get;}
public Player Owner { get;}
public CardPile Pile { get;} // 所属パイル（null の場合あり）
public bool IsUpgraded { get;}
public int CurrentUpgradeLevel { get;}
public IReadOnlySet<CardKeyword> Keywords { get;}
public DynamicVarSet DynamicVars { get;}
public ICombatState CombatState { get;}
public IRunState RunState { get;}
public bool CanPlay(); // ★ 使用可否（引数なし版）
public bool CanPlay(ref UnplayableReason, ref AbstractModel);
public bool CanPlayTargeting(Creature);
public bool IsValidTarget(Creature);
public bool TryManualPlay(Creature target); // ★★★ カード使用の public 入口（§9.1）
private void EnqueueManualPlay(Creature); // ★ 内部実装は private（直接は呼べない）
}
```

- 手札の列挙: `player.PlayerCombatState.Hand.Cards`（`IReadOnlyList<CardModel>`、手札順そのまま）。
- カード名は `Title`（string）が最速。`Id` は `ModelId { string Category; string Entry;}` で安定 ID として使える（`Id.ToString()` / `Id.Entry`）。
- 説明文の文字列化: `Description` は `LocString`。動的数値（ダメージ量等）を反映した表示用文字列は `GetDescriptionForPile(PileType.Hand, target)` が string を直接返す。素の `LocString.GetFormattedText()` でも可（§6）。

### 4.4 CardEnergyCost

```csharp
namespace MegaCrit.Sts2.Core.Entities.Cards;

public class CardEnergyCost
{
public int Canonical { get;} // 素のコスト
public bool CostsX { get;} // X コストか
public int GetWithModifiers(CostModifiers); // 修飾込み
public int GetAmountToSpend(); // 実際に支払う量
public int GetResolved(); // ★ 現在の実効コスト（表示・判定用はこれが候補）
}
```

`CardModel.CanonicalEnergyCost` は **protected** なので外部からは使えない。コスト表示は `EnergyCost.GetResolved()` か `GetAmountToSpend()` を使う。どちらが手札 UI の数字と一致するかは ****（ロードテストで要確認）。

---

## 5. クリーチャー / プレイヤー

### 5.1 Creature（プレイヤー本体も敵もこれ）

```csharp
namespace MegaCrit.Sts2.Core.Entities.Creatures;

public class Creature
{
public int CurrentHp { get; private set;} // ★
public int MaxHp { get; private set;} // ★
public int Block { get; private set;} // ★
public string Name { get;} // 表示名（string）
public ModelId ModelId { get;}
public MonsterModel Monster { get;} // 敵の場合のみ（nullable）
public Player Player { get;} // プレイヤーの場合のみ（nullable）
public bool IsPlayer { get;}
public bool IsMonster { get;}
public bool IsEnemy { get;}
public bool IsAlive { get;}
public bool IsDead { get;}
public bool IsHittable { get;}
public bool IsStunned { get;}
public CombatSide Side { get;}
public uint? CombatId { get; set;} // 戦闘内 ID（アクションの target 指定に使う候補）
public string SlotName { get; set;}
public IReadOnlyList<PowerModel> Powers { get;} // ★ バフ/デバフ一覧
public ICombatState CombatState { get; set;}
public double GetHpPercentRemaining();
}
```

- 敵の名前: `Creature.Name`（string）で足りる。モデル名は `Monster.Title`（LocString、§5.3）。
- パワー量: `PowerModel { LocString Title { get;} int Amount { get; private set;} int DisplayAmount { get;} PowerType Type { get;}}`。

### 5.2 インテント

```csharp
namespace MegaCrit.Sts2.Core.MonsterMoves.Intents;

public abstract class AbstractIntent
{
public IntentType IntentType { get;}
public LocString GetIntentLabel(IEnumerable<Creature> targets, Creature owner);
public LocString GetIntentDescription(IEnumerable<Creature> targets, Creature owner);
}
public abstract class AttackIntent: AbstractIntent
{
public Func<decimal> DamageCalc { get; protected set;}
public int Repeats { get;} // 多段回数
public int GetSingleDamage(IEnumerable<Creature>, Creature); // ★ 1段のダメージ
public int GetTotalDamage(IEnumerable<Creature>, Creature); // ★ 合計ダメージ
}
// 具象: SingleAttackIntent / MultiAttackIntent / DeathBlowIntent（Attack 系）,
// BuffIntent / DebuffIntent / CardDebuffIntent / DefendIntent / HealIntent /
// SleepIntent / StunIntent / SummonIntent / EscapeIntent / HiddenIntent / UnknownIntent
```

取得経路: `Creature.Monster.NextMove` → ではなく、**`MonsterModel.GetIntents()`** が `List<AbstractIntent>` を返す。実体は `creature.Monster` から:

```csharp
// 敵インテント取得の形（型は全て確定、呼び出し順は推測を含む）
var intents = enemy.Monster.GetIntents(); // List<AbstractIntent>
```

注意: `MonsterModel` には `MoveStateMachine` / `NextMove`（`MoveState` 型）もあり、どちらが「現在表示中のインテント」と一致するかは ****（`GetIntents()` が表示用 API として用意されている形跡が強いが、実機未検証）。攻撃インテントのダメージは `AttackIntent.GetTotalDamage(対象群, 本人)` で数値化できる。

### 5.3 MonsterModel / CharacterModel

```csharp
public abstract class MonsterModel: AbstractModel
{
public LocString Title { get;}
public List<AbstractIntent> GetIntents();
public MoveState NextMove { get; private set;}
public bool IntendsToAttack { get;}
public Creature Creature { get; set;}
}
public abstract class CharacterModel: AbstractModel
{
public LocString Title { get;}
public int StartingHp { get;}
public int StartingGold { get;}
// ★ AddDetailsTo は現行ビルドで (LocString, FoolPronounGuess) の2引数版のみ
public void AddDetailsTo(LocString, MegaCrit.Sts2.Core.Entities.Characters.FoolPronounGuess);
}
```

- **互換性メモ（重要）**: 既存 mod STS2AIMCP が v0.113.x で 500 を返す原因は、旧ビルドにあった `CharacterModel.AddDetailsTo(LocString)`（1引数）が現行では2引数版に変わっていることと整合する（先行調査の MissingMethodException ログ + 今回のメタデータで裏付け）。自前実装ではこのメソッドに依存しないこと。

### 5.4 Player

```csharp
namespace MegaCrit.Sts2.Core.Entities.Players;

public class Player
{
public CharacterModel Character { get;} // キャラ本体
public Creature Creature { get;} // ★ HP/ブロックはここ
public ulong NetId { get;}
public int Gold { get; set;} // ★ ゴールド
public CardPile Deck { get;} // ★ デッキ（PileType.Deck）
public IEnumerable<CardPile> Piles { get;}
public PlayerCombatState PlayerCombatState { get; private set;} // 戦闘中のみ（§4.2）
public IReadOnlyList<RelicModel> Relics { get;} // ★ 所持レリック
public IReadOnlyList<PotionModel> PotionSlots { get;} // ★ ポーション欄（空きは null 要素の可能性）
public IEnumerable<PotionModel> Potions { get;} // ★ 所持分のみの列挙はこちらが安全
public int MaxPotionCount { get;}
public bool HasOpenPotionSlots { get;}
public bool CanUseOrRemovePotions { get; set;}
public int MaxEnergy { get; set;}
public IRunState RunState { get; set;}
}
```

- ローカルプレイヤーの特定方法: `RunState.Players` は全プレイヤー一覧。シングルプレイでは `[0]` で足りる。マルチでの「自分」の厳密な特定方法（NetId とネットサービスの照合等）は ****。当面は `Players[0]` 前提 + シングルプレイ判定 `RunManager.IsSingleplayerOrFakeMultiplayer` を併記する。

---

## 6. ローカライズ（LocString）

```csharp
namespace MegaCrit.Sts2.Core.Localization;

public class LocString // XML doc: "A Localized String, uses SmartFormat"
{
public string LocTable { get;}
public string LocEntryKey { get;}
public bool IsEmpty { get;}
public IReadOnlyDictionary<string, object> Variables { get;}
public string GetFormattedText(); // ★ 変数展開済みの表示文字列
public string GetRawText(); // 未展開の生テキスト
public override string ToString(); // 概ね GetFormattedText 相当と推定
public static LocString GetIfExists(string table, string key);
public void Add(string, decimal); // ほか overload 多数（変数の追加）
}
```

- 方針: 表示文字列が要る場所では `locString.GetFormattedText()` を使う。`CardModel.Title` のように string プロパティが用意されているものはそちら優先。
- モデル系の `Title` / `Description` は多くが `LocString` 型（CardModel の `Title` だけは string）。`PotionModel.Title`、`RelicModel.Title`、`PowerModel.Title`、`MonsterModel.Title`、`CharacterModel.Title` は **LocString**。

---

## 7. 画面 / UI 判定

画面判定は単一の公式 API が無く、複合で判定する。候補は優先度順に:

### 7.1 部屋（モデル層）— 第一候補

```csharp
runState.CurrentRoom // AbstractRoom（null の場合あり）
```

具象型で分岐する:

| 型 | 意味 | 追加で取れるもの |
|---|---|---|
| `MegaCrit.Sts2.Core.Rooms.CombatRoom` | 戦闘 | `CombatState CombatState { get;}`、`Encounter`、`Allies` / `Enemies` |
| `MegaCrit.Sts2.Core.Rooms.EventRoom` | イベント | `LocalMutableEvent EventModel`、`CanonicalEvent` |
| `MegaCrit.Sts2.Core.Rooms.RestSiteRoom` | 休憩所 | `IReadOnlyList<RestSiteOption> Options { get;}` |
| `MegaCrit.Sts2.Core.Rooms.MerchantRoom` | ショップ（部屋） | モデル層には在庫なし（§9.5 はノード側） |
| `MegaCrit.Sts2.Core.Rooms.TreasureRoom` | 宝箱 | — |
| `MegaCrit.Sts2.Core.Rooms.MapRoom` | マップ | — |

`AbstractRoom { RoomType RoomType { get;} ModelId ModelId { get;} bool IsPreFinished { get;}}`、`RoomType` 列挙: `Unassigned, Monster, Elite, Boss, Treasure, Shop, Event, RestSite, Map`。

注意: 報酬画面は「部屋」ではなくオーバーレイ表示のため `CurrentRoom` だけでは判定できない（戦闘終了後も CombatRoom のまま報酬が出る、等）。7.2 と組み合わせる。

### 7.2 アクティブ画面（ノード層）

```csharp
namespace MegaCrit.Sts2.Core.Nodes.Screens.ScreenContext;

public class ActiveScreenContext
{
public static ActiveScreenContext Instance { get;} // nullable 注釈
public IScreenContext GetCurrentScreen(); // ★ 現在の画面オブジェクト
public bool IsCurrent(IScreenContext);
public event Action Updated;
}
```

- 既存ブリッジ mod も `IScreenContext` と `CurrentRoom` の突き合わせで画面安定性を判定している（メソッド名からの実測: `DoesScreenMatchCurrentRoom(IScreenContext, AbstractRoom)`）。
- 画面ノードの具象型（型名で判定できる）: `NRewardsScreen`（報酬）、`NMapScreen`（マップ、`Nodes.Screens.Map`）、`NChooseACardSelectionScreen` / `NChooseABundleSelectionScreen`（カード選択、`Nodes.Screens.CardSelection`）、`NDeckUpgradeSelectScreen` 等。`NRewardsScreen { bool IsComplete { get; private set;} NetScreenType ScreenType { get;} static NRewardsScreen ShowScreen(RewardsSet, bool, IRunState)}`。
- `NetScreenType` 列挙（画面種別の粗い分類）: `None, Room, Map, Settings, Compendium, DeckView, CardPile, SimpleCardsView, CardSelection, GameOver, PauseMenu, Rewards, Feedback, SharedRelicPicking, RemotePlayerExpandedState`。ただしこれを返す `ScreenStateTracker.GetCurrentScreen()` は **private**（`MegaCrit.Sts2.Core.Multiplayer.Game.PeerInput.ScreenStateTracker`）で直接は使えない。

### 7.3 シーンツリー側の足場

```csharp
public class MegaCrit.Sts2.Core.Nodes.NGame: Godot.Control
{
public static NGame Instance { get; private set;} // ★ ルート級ノード
public NRun CurrentRunNode { get;} // ラン中のみ（nullable）
public NMainMenu MainMenu { get;}
public static bool IsMainThread(); // ★ メインスレッド判定
}
public class MegaCrit.Sts2.Core.Nodes.NRun: Godot.Control
{
public static NRun Instance { get;} // nullable 注釈
public NCombatRoom CombatRoom { get;}
public NTreasureRoom TreasureRoom { get;}
public NEventRoom EventRoom { get;}
public NRestSiteRoom RestSiteRoom { get;}
public NMapRoom MapRoom { get;}
public NMerchantRoom MerchantRoom { get;}
public static NRun Create(RunState);
}
```

- 判定レシピ:
1. `RunManager.Instance == null ||!IsInProgress` → ラン外（メインメニュー等は `NGame.Instance.MainMenu` の有無でも補助判定）
2. `runState.CurrentRoom` の具象型 → 部屋種別
3. `ActiveScreenContext.Instance?.GetCurrentScreen()` の具象型 → 報酬/選択/マップ等のオーバーレイ画面を上書き判定
4. 戦闘の有無は `CombatManager.Instance.IsInProgress` で確定

---

## 8. アクション実行の共通基盤

STS2 のアクションは `GameAction` をキューへ入れて実行するモデル（STS1 の GameAction とは別物。XML doc 原文: *"A GameAction is a thin wrapper around an async task that should be run in response to player input."*）。

```csharp
namespace MegaCrit.Sts2.Core.GameActions;

public abstract class GameAction
{
public GameActionState State { get; private set;}
public abstract ulong OwnerId { get;}
public abstract GameActionType ActionType { get;}
public Task CompletionTask { get;} // 完了待ちに使える
public Exception Exception { get;}
}

public class ActionExecutor // RunManager.ActionExecutor から取得
{
public bool IsRunning { get;}
public bool IsPaused { get;}
public GameAction CurrentlyRunningAction { get; private set;}
public Task FinishedExecutingActions(); // キュー消化待ち
public event Action<GameAction> BeforeActionExecuted, AfterActionExecuted;
}

namespace MegaCrit.Sts2.Core.GameActions.Multiplayer;

public class ActionQueueSet // RunManager.ActionQueueSet から取得
{
public bool IsEmpty { get;}
public void EnqueueWithoutSynchronizing(GameAction); // ★ 同期なしで直接キュー投入（public）
public event Action<GameAction> ActionEnqueued;
public bool AreAnyPlayerDrivenActionsBeingExecuted(bool);
}
public class ActionQueueSynchronizer // RunManager.ActionQueueSynchronizer から取得
{
public void RequestEnqueue(GameAction); // ★ 正規の投入口（public）
public void RequestEnqueueHookAction(GenericHookGameAction);
}
```

- 投入経路は2つある: `ActionQueueSynchronizer.RequestEnqueue(action)`（ネット同期込みの正規経路）と `ActionQueueSet.EnqueueWithoutSynchronizing(action)`（直接）。シングルプレイではどちらでも動く可能性が高いが、どちらが UI 経由の通常プレイと等価かは ****。まず `RequestEnqueue` を第一候補とする。
- もう一つの近道: モデル自身の「手動操作」メソッド（`CardModel.TryManualPlay` / `PotionModel.EnqueueManualUse`、§9.1 / §9.6）。こちらは UI が内部で使う経路そのものである可能性が高く、ブリッジ用途ではこちらを優先する価値がある。

---

## 9. アクション別・入口一覧

### 9.1 カード使用

第一候補（モデルの手動プレイ API）:

```csharp
CardModel.TryManualPlay(Creature target) -> bool // public。target は対象なしカードでは null
```

- 内部の `EnqueueManualPlay(Creature)` は **private** なので直接は呼べない。`TryManualPlay` が可否判定込みでキュー投入までやる設計と読める（戻り値 bool）。
- 第二候補（GameAction を自前で組み立てる）:

```csharp
public sealed class PlayCardAction: GameAction
{
public PlayCardAction(CardModel card, Creature target); // ★ ローカル用 ctor
public PlayCardAction(Player, NetCombatCard, ModelId, uint? targetId); // ネット用
public Player Player { get;}
public ModelId CardModelId { get;}
public uint? TargetId { get;}
public Creature Target { get;}
}
// 投入: RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(new PlayCardAction(card, target));
```

- 使用可否の事前判定: `card.CanPlay()` / `card.CanPlayTargeting(target)` / `player.PlayerCombatState.HasEnoughResourcesFor(card, ref reason)` / `CombatManager.PlayerActionsDisabled == false` / `PlayerCombatState.Phase == PlayerTurnPhase.Play`。

### 9.2 ターン終了

```csharp
public class EndPlayerTurnAction: GameAction
{
public EndPlayerTurnAction(Player player, int turnNumber); // ★ ctor
}
// 投入は PlayCard と同じ RequestEnqueue / EnqueueWithoutSynchronizing。
```

- 代替経路: `CombatManager.SetReadyToEndTurn(Player, bool, Func<Task>)`（public）。マルチでは全員が Ready になって初めて敵ターンへ進む（`AllPlayersReadyToEndTurn()`）。シングルプレイでどちらが正規かは ****。UI のターン終了ボタンが最終的にどちらへ繋がるかは未追跡。まず `EndPlayerTurnAction`（turnNumber は `PlayerCombatState.TurnNumber` か `CombatState.RoundNumber` のどちらか）を候補とする。
- 取り消し系: `UndoEndPlayerTurnAction`、`CombatManager.TryUndoReadyToEndTurn(Player)` も存在する。

### 9.3 進行（Proceed）/ 報酬受取

```csharp
RunManager.Proceed() -> void // public。報酬画面・マップ等での汎用「進む」
RunManager.ProceedFromTerminalRewardsScreen() /...Async()
```

報酬そのもの:

```csharp
namespace MegaCrit.Sts2.Core.Rewards;

public abstract class Reward
{
public Player Player { get;}
protected RewardType RewardType { get;} // Card/Gold/Potion/Relic/RemoveCard/UpgradeCard/SpecialCard
public LocString Description { get;}
public bool IsPopulated { get;}
public bool SuccessfullySelected { get; private set;}
public Task<bool> SelectUnsynchronized(); // ★ public の受取入口
protected abstract Task<bool> OnSelect(); // 具象側は public override（例: CardReward.OnSelect）
}
public class RewardsSet
{
public List<Reward> Rewards { get;} // ★ 提示中の報酬一覧
public Player Player { get;}
public AbstractRoom Room { get; private set;}
public bool AllRewardsSuccessfullySelected { get;}
public Task Offer();
}
public class CardReward: Reward
{
public IEnumerable<CardModel> Cards { get;} // ★ 提示カード
public bool CanSkip { get; set;}
public bool CanReroll { get; set;}
public void Reroll();
}
// 具象 Reward: CardReward / GoldReward / PotionReward / RelicReward /
// CardRemovalReward / CardUpgradeReward / SpecialCardReward / LinkedRewardSet
```

- 「現在の RewardsSet をどこから取るか」は ****: 候補は (a) `NRewardsScreen` ノードの private フィールド `_rewardsSet`（リフレクション）、(b) `CombatRoom` 終了時のフロー、(c) `RunManager.RewardSynchronizer` 経由。既存ブリッジ mod は `NRewardsScreen` ノード側を触っている形跡がある（メソッド引数に同型を取る）。ここはロードテストでの実地確認が要る最重要の未確定点の一つ。
- カード報酬の「カードを選ぶ」部分は CardReward.OnSelect 後にカード選択画面（`NCardRewardSelectionScreen`）が開く分離フロー。単発のメソッド呼び出しで完結しない可能性が高い。

### 9.4 マップ選択

```csharp
public class MoveToMapCoordAction: GameAction
{
public MoveToMapCoordAction(Player player, MapCoord coord); // ★ ctor
}
public class VoteForMapCoordAction: GameAction { /* マルチ投票用 */}

// マップ構造:
public abstract class ActMap
{
public MapPoint BossMapPoint { get;}
public MapPoint StartingMapPoint { get;}
public MapPoint[,] Grid { get; protected set;}
public MapPoint GetPoint(MapCoord);
public MapPoint GetPoint(int, int);
public IEnumerable<MapPoint> GetAllMapPoints();
}
public class MapPoint
{
public MapCoord coord; // public フィールド（row/col を持つ struct と推定）
public MapPointType PointType { get; set;} // Unassigned/Unknown/Shop/Treasure/RestSite/Monster/Elite/Boss/Ancient
public HashSet<MapPoint> Children { get;} // ★ 次に進める先
public readonly HashSet<MapPoint> parents;
}
// 現在地: runState.CurrentMapPoint / CurrentMapCoord。次候補 = CurrentMapPoint.Children。
// 別経路: RunManager.EnterMapCoord(MapCoord) -> Task（public、直接遷移）
```

- `MoveToMapCoordAction`（キュー経由）と `RunManager.EnterMapCoord`（直接）のどちらがプレイヤー操作と等価かは ****。まず GameAction 経由を候補とする。

### 9.5 イベント選択

```csharp
// 現在のイベント: (runState.CurrentRoom as EventRoom).LocalMutableEvent -> EventModel
public abstract class EventModel: AbstractModel
{
public LocString Title { get;}
public LocString Description { get; private set;}
public IReadOnlyList<EventOption> CurrentOptions { get;} // ★ 現在の選択肢
public bool IsFinished { get; private set;}
public Player Owner { get; private set;}
}
namespace MegaCrit.Sts2.Core.Events;
public class EventOption
{
public string TextKey { get; private set;}
public LocString Title { get; private set;}
public LocString Description { get; private set;}
public bool IsLocked { get;}
public bool IsProceed { get; private set;}
public bool WasChosen { get; private set;}
public Task Chosen(); // ★★ 選択実行の入口（public）
}
```

### 9.6 休憩所選択

```csharp
// (runState.CurrentRoom as RestSiteRoom).Options -> IReadOnlyList<RestSiteOption>
namespace MegaCrit.Sts2.Core.Entities.RestSite;
public abstract class RestSiteOption
{
public string OptionId { get;} // ★ 安定 ID 候補
public LocString Title { get;}
public LocString Description { get;}
public bool IsEnabled { get;}
public abstract Task<bool> OnSelect(); // ★★ 選択実行（public abstract、具象で実装）
public static List<RestSiteOption> Generate(Player);
}
// 具象: HealRestSiteOption / SmithRestSiteOption / DigRestSiteOption / LiftRestSiteOption /
// CloneRestSiteOption / CookRestSiteOption / EvolveRestSiteOption / HatchRestSiteOption /
// KindleRestSiteOption / MendRestSiteOption / RestoreRestSiteOption
```

### 9.7 ショップ購入

```csharp
namespace MegaCrit.Sts2.Core.Entities.Merchant;

public class MerchantInventory
{
public IReadOnlyList<MerchantCardEntry> CharacterCardEntries { get;}
public IReadOnlyList<MerchantCardEntry> ColorlessCardEntries { get;}
public IReadOnlyList<MerchantRelicEntry> RelicEntries { get;}
public IReadOnlyList<MerchantPotionEntry> PotionEntries { get;}
public MerchantCardRemovalEntry CardRemovalEntry { get;}
public List<MerchantEntry> AllEntries { get;}
public Task<bool> TryPurchaseEntryWithIdUnsynchronized(uint id); // ★ ID 指定購入（public）
}
public abstract class MerchantEntry
{
public int Cost { get;}
public bool EnoughGold { get;}
public uint? Id { get; set;}
public bool IsStocked { get;}
public Task<bool> OnTryPurchaseUnsynchronized(MerchantInventory, bool); // ★ エントリ直接購入（public）
}
// 同期込みの正規経路候補:
// RunManager.Instance.MerchantSynchronizer.PurchaseLocalEntry(MerchantEntry) -> Task<bool>（public）
```

- 在庫 `MerchantInventory` の所在: ノード側 `NMerchantRoom.Inventory`（型は UI ラッパ `Nodes.Screens.Shops.NMerchantInventory`）の `.Inventory { get; private set;}` が実体 `MerchantInventory` を返す。経路: `NRun.Instance.MerchantRoom.Inventory.Inventory`、または `NMerchantRoom.Instance.Inventory.Inventory`（`NMerchantRoom` にも static `Instance` あり）。
- エントリの中身（どのカード/レリックか）は具象型 `MerchantCardEntry` / `MerchantRelicEntry` / `MerchantPotionEntry` 側のプロパティで取る（詳細メンバーは未精査）。

### 9.8 宝箱 / レリック選択

```csharp
public class PickRelicAction: GameAction
{
public PickRelicAction(Player player, int? relicIndex); // ★ ctor。null の意味（スキップ等）は不明
}
// 同期: RunManager.TreasureRoomRelicSynchronizer
```

- 宝箱を開ける操作自体は部屋入室で自動進行する可能性が高く、ブリッジ側の主操作は「提示レリックの選択 = PickRelicAction をキュー投入」。提示レリック一覧の取得元（画面ノード `NTreasureRoomRelicCollection` 等）は ****。
- 戦闘報酬のレリックは §9.3 の `RelicReward` 経由。

### 9.9 ポーション使用

```csharp
PotionModel.EnqueueManualUse(Creature target) -> void // ★ public。カードの TryManualPlay に対応する入口
// または GameAction:
public class UsePotionAction: GameAction
{
public UsePotionAction(PotionModel potion, Creature target, bool wasEnqueuedInCombat); // ローカル用 ctor
public UsePotionAction(Player, uint potionIndex, uint? targetId, ulong? targetPlayerId, bool);
public uint PotionIndex { get;}
}
public abstract class PotionModel: AbstractModel
{
public LocString Title { get;} // LocString（string ではない点に注意）
public PotionRarity Rarity { get;}
public PotionUsage Usage { get;} // None/CombatOnly/AnyTime/Automatic
public TargetType TargetType { get;}
public Player Owner { get; set;}
public bool IsValidTarget(Creature);
}
// 破棄: DiscardPotionGameAction（GameAction 具象）も存在する。
// 使用ロック: RunManager.IsPotionUseLocked / Player.CanUseOrRemovePotions を事前確認。
```

### 9.10 カード選択画面（手札選択・デッキ選択等）

- 選択は `MegaCrit.Sts2.Core.CardSelection.CardSelectorStack`（RunManager から取得）と、画面ノード（`NChooseACardSelectionScreen` 等）が担う。モデル層に単一の「選ぶ」メソッドは確認できなかった。
- 既存ブリッジ mod は画面ノードのボタン（`NConfirmButton` 等）を辿って確定操作をしている形跡がある。ブリッジで本格対応する場合はノード層の精査が別途必要。
- ゲーム内蔵の自動プレイ基盤 `MegaCrit.Sts2.Core.AutoSlay`（Handlers: CombatRoomHandler / EventRoomHandler / RestSiteRoomHandler / ShopRoomHandler / TreasureRoomHandler / CardRewardScreenHandler / MapScreenHandler / RewardsScreenHandler 等）が全画面のプログラム的操作の実装例としてゲーム内に同梱されている。行き詰まったらこの名前空間の型構成を手がかりにできる（中身の IL は今回未精査）。

---

## 10. Godot メインスレッドへの寄せ方

対象は GodotSharp 4.5.1（`GodotSharp.dll`、型総数 4,828）。

```csharp
// 1) 任意の Node から:
node.GetTree() -> Godot.SceneTree // Node.GetTree() public
Godot.Engine.GetMainLoop() -> Godot.MainLoop // static。SceneTree は MainLoop 継承
// SceneTree: Root { get;} (Window), CurrentScene { get; set;}, Paused { get; set;},
// event Action ProcessFrame / PhysicsFrame

// 2) 遅延呼び出し:
Godot.Callable.From(System.Action) -> Callable // static、Action/Func の多重 overload
callable.CallDeferred(params Variant[]); // Callable の public メソッド
godotObject.CallDeferred(Godot.StringName, params Variant[]); // GodotObject 標準（Godot 4 系の一般 API。本 DLL でも Node に CallDeferredThreadGroup 等の同系統メソッドを確認）

// 3) ゲーム側のメインスレッド判定:
MegaCrit.Sts2.Core.Nodes.NGame.IsMainThread() -> bool // public static
```

- 実務上の第一候補は **SynchronizationContext 方式**: 既存ブリッジ mod は `SynchronizationContext` をゲームスレッドで捕捉し `InvokeAsync(Func<Task>)` で処理を寄せる専用ヘルパ（`GameThread` 相当）を持っていた（型・メソッド名のメタデータ実測）。初期化時（= ゲームスレッド上）に `SynchronizationContext.Current` を保存するのが確実。
- HTTP サーバ側スレッドから状態を読む場合も、Godot オブジェクト（Texture 等）に触れるプロパティ（`Portrait` 等）は避け、モデル層の値プロパティだけを読むこと。`Title`（string）や `GetFormattedText()` はローカライズ参照のみで安全なはずだが、メインスレッド寄せを基本とする。

---

## 11. 注意点まとめ（実装前に必ず読む）

1. **`State` は private、`DebugOnlyGetState()` が public の抜け道**（RunManager / CombatManager 両方）。XML doc に「TEMPORARY / TESTS ONLY」と明記されており、将来のビルドで消えるリスクがある。消えた場合の代替は private プロパティ `<State>k__BackingField` へのリフレクション。
2. **`CardModel.EnqueueManualPlay` は private**。カード使用は `TryManualPlay(Creature)`（public）か `PlayCardAction` + キュー投入を使う。
3. **変わった API に依存しない**: `CharacterModel.AddDetailsTo(LocString)`（1引数）は現行ビルドに存在しない（2引数版のみ）。既存 mod の破損原因。バージョン差異はまずここを疑う。
4. **表示文字列の型がバラバラ**: `CardModel.Title` は string、`PotionModel/RelicModel/PowerModel/MonsterModel.Title` は LocString。LocString は `GetFormattedText()` で文字列化する。
5. **コストは protected に注意**: `CardModel.CanonicalEnergyCost` は protected。外部からは `EnergyCost.GetResolved()` / `GetAmountToSpend()` / `Canonical`（CardEnergyCost 側は public）。
6. **null の多い設計**: `PlayerCombatState`（戦闘外 null）、`DebugOnlyGetState()`（場面外 null の可能性）、`PotionSlots`（空きスロット表現が未確定）、`CurrentRoom` / `CurrentMapPoint`（nullable）。全てガード前提で書く。
7. **マルチ前提の同期層**: 多くの操作に `*Synchronizer`（Merchant / Reward / RestSite / TreasureRoomRelic / Event / MapSelection）が介在する。`...Unsynchronized` 系メソッドはシングルプレイ前提の近道。方針（同期経由か否か）は最初のロードテストで挙動を見て決める。
8. **報酬フローは未確定要素が最多**: 現在の `RewardsSet` の取得元、カード報酬の選択確定フロー、宝箱の提示レリック一覧。この3点はメタデータだけでは確定できず、実機（ロードテスト）での確認が必須。
9. **ターン終了の turnNumber の出所**（`PlayerCombatState.TurnNumber` vs `CombatState.RoundNumber`）は未検証。`EndPlayerTurnAction` を使う場合は両方を記録して挙動を見ること。
10. ゲームバージョンは **v0.113.1**（2026-11-14 ビルド）。public-beta のため API は今後も動く前提で、ブリッジ側は「取れなければ null を返す」防御的に実装する。

## 12. 未精査・不明点リスト

- `MapCoord` のメンバー（row / col の名前と型）
- `ActModel` の表示名プロパティ、`MerchantCardEntry` 等の具象エントリの中身プロパティ
- `Player.PotionSlots` の空きスロット表現（null 要素か、詰めて返るか）
- 現在の `RewardsSet` の正規の取得経路（§9.3）
- 宝箱画面で提示されるレリック一覧の取得経路（§9.8）
- カード選択画面の確定操作の最小経路（§9.10）
- `ModHelper.AddModelToPool` のジェネリック版シグネチャ詳細（ブリッジ用途では不要）
- ローカルプレイヤーの厳密な特定方法（マルチ時。シングルプレイは `Players[0]` で確定扱い）
- `Hook`（`MegaCrit.Sts2.Core.Hooks.Hook`、集計フックの静的窓口）の詳細 — 状態変更の購読に使える可能性があるが今回未精査

## 13. Round 2 metadata investigation and implementation (0.3.0, 2026-11-15)

Investigation was entirely outside the game. `tools/Inspect` uses reflection on
the workspace copy `bridge/harness/bin/Debug/net9.0/sts2.dll`; it enumerates
metadata only, without constructing game objects or invoking getters. Reports:
`tools/Inspect/round2-metadata.txt`, `round2-focus-metadata.txt`,
`round2-wrapper-metadata.txt`, `round2-reward-metadata.txt`. Builds reference
only the installed managed assembly directory, never mods. No existing bridge
listener, live host or game process was queried or changed.

### Description paths (E-1 / E-2)

- Confirmed: EventOption.Description and RestSiteOption.Description are distinct
  LocString members from Title. Implementation uses GetFormattedText, with
  exceptions yielding null. Reward.Description remains the formatted source;
  PotionReward.Potion and RelicReward.Relic provide a real model title for a
  shorter label where available. Other reward types retain their formatted
  description as both label and description, rather than inventing names.
- PotionModel and RelicModel contain both Description and DynamicDescription
  metadata. Direct Description access failed compilation (inaccessible,
  CS1061); public DynamicDescription compiled successfully. Display readers use
  DynamicDescription.GetFormattedText through the guarded formatter. Actual
  localization/dynamic-variable values remain untested until a future restart.
- Map descriptions are generic UI text for known PointType names. Actual enum
  uses Shop (mapped to ショップ). Unknown maps to ？; unrecognized names including
  Unassigned return null. No invented event-specific descriptions.

### Focus ownership (E-3)

Confirmed metadata ownership paths:

- NCardHolder.CardModel / CardNode -> NCard.Model (private `_model`).
- NEventOptionButton.Option -> EventOption.
- NRestSiteButton.Option (private `_option`) -> RestSiteOption.
- NRewardButton.Reward -> Reward.
- NPotionHolder.Potion -> NPotion.Model -> PotionModel.
- NCreature.Entity -> Creature.
- NTreasureRoomRelicHolder.Relic -> NRelic.Model -> RelicModel.

Runtime implementation (not exercised in-engine): Build already runs through
GameThread.Invoke. It takes the current screen Node, or NRun.Instance, calls
GetViewport().GuiGetFocusOwner(), then inspects that node and at most 12 parent
levels. Reflection reads only the known ownership members (including inherited
private fields), and up to two levels of CardNode/Potion/Relic wrappers. It stops
at the nearest model owner, even when that owner cannot be mapped. It never
scans sibling nodes, collections, arbitrary getters or global model graphs.
`NCard._previewTarget`, cached/last focus fields and canonical model references
are deliberately excluded: those can refer to something other than the focused
item. Two distinct ownership models at the same level return null.

Matching is ReferenceEquals against the current hand, potion, enemy or choice
source list. Value equality, titles and IDs do not establish selection. Name and
description come from the snapshot of that same model (enemy description is
null because there is no verified general enemy description source). Cards in
hand use kind=card; a displayed card/relic represented only in choices uses
kind=choice. All acquisition is guarded; missing focus, unsupported popup,
unknown wrapper, ambiguity or exceptions leave selected=null. The contract
permits kind=other, but the implementation does not fabricate it for unknown UI.

Managed harness checks cover reference identity versus value equality,
duplicate ownership, ambiguous ownership, missing ownership, guarded/inherited
reflection and selected serialization with synthetic tokens. They do NOT prove
that keyboard focus moves to the expected nodes. GuiGetFocusOwner and parent
traversal were compile-checked only, not invoked outside a Godot engine. The
keyboard-only behavior and all positive live model matches remain deferred to
the next supervised restart; this is required by the current stop conditions.

### Card selection (E-4)

Confirmed NChooseACardSelectionScreen._cards and
NCardGridSelectionScreen._cards are IReadOnlyList<CardModel>.
NCardRewardSelectionScreen._options is IReadOnlyList<CardCreationResult>, whose
Card property supplies the offered card. Reflection examines compatible
collection fields on the active screen and its base classes, preserving order.
If multiple nonempty collections disagree by reference/order it returns empty
rather than merging pools with selected subsets. Card alternatives that are
not CardModel are omitted. Choices use kind=card_select, title and guarded card
description; no corresponding actions or execution paths were added. Unknown
screen shapes return empty. Live screen visibility/content remains unverified.

### Merchant (E-5)

The room model MerchantRoom has no inventory member; that route was a miss.
Confirmed UI route: NRun.Instance.MerchantRoom.Inventory (NMerchantInventory)
.Inventory (MerchantInventory). NMerchantRoom.Instance also exists, but the
implementation deliberately uses the current NRun room. NMerchantInventory
exposes IsOpen; closed inventory returns no choices.
MerchantInventory.AllEntries contains MerchantEntry, with Cost and IsStocked.
MerchantCardEntry has CreationResult.Card (not a direct Model property);
MerchantPotionEntry.Model and MerchantRelicEntry.Model are available. Stocked
entries with these models become shop_item choices with real model title and
`<Cost>G ＋ <dynamic description>`; missing descriptions retain only the price.
Card-removal and unknown entry kinds have no supported item model and are
omitted. Reading the route compiled, but positive inventory reads were NOT
performed in the running game. Purchase execution remains unsupported.

### Treasure (E-6)

TreasureRoom model has no offered-relic list; that route was a miss. Confirmed
UI route: NRun.Instance.TreasureRoom (NTreasureRoom), private
`_isRelicCollectionOpen` and `_relicCollection`;
NTreasureRoomRelicCollection.HoldersInUse ->
NTreasureRoomRelicHolder.Relic (NRelic) -> NRelic.Model (RelicModel).
Only the open collection is enumerated, with kind=relic and real title/dynamic
description. Missing/private-field changes return empty. Positive runtime reads,
removal after claims and multiplayer holder order are unverified; no treasure
selection action was added. Actual runtime proof cannot be obtained under the
current prohibition on touching the game, and is deferred explicitly.

### Out-of-game checks and fixtures (E-8)

Production health, startup, assembly and manifest versions are 0.3.0. The server
constructor accepts an optional validated IPv4 loopback root prefix, retaining
127.0.0.1:8766 by default. Only the harness overrides it. Harness requires
`--port <isolated-port>` (rejects 8765/8766); Python tests use OS-assigned owned
ports and readiness from the exact child stdout. They never contact a preexisting
listener. Real provider mode returns honest unknown/empty state and selected=null.
Explicit --fixture mode returns synthetic populated choices/potions via real DTO
serialization, so description-key assertions are non-vacuous. Neither fixture
nor fallback checks prove the in-engine readers work.

`spire/fakebridge/fake_bridge.py` has combat selected card index 1 and potion
description, a `/fixtures/card_selection` response, and a `--card-selection`
mode exposing that state through /state with no /actions. Invalid display-only
actions are rejected. Use `--port` for an isolated client-test instance; do not
start the default port while the real bridge is active.
