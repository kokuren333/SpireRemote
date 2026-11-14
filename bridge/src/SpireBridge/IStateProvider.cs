using System.Text.Json.Serialization;

namespace SpireBridge;

// ---------------------------------------------------------------------------
// DTOs shared by the HTTP layer and the (future) game-state implementation.
// Shape is the contract agreed for SpireRemote:
//   state:   { screen, run, player, hand, enemies, potions, choices }
//   actions: [{ action_id, kind, label, target }]
// Any field that cannot be obtained yet is null (never omitted / never faked).
// Property names are emitted as snake_case via JsonPropertyName.
// ---------------------------------------------------------------------------

public sealed class RunInfo
{
    [JsonPropertyName("act")] public int? Act { get; set; }
    [JsonPropertyName("floor")] public int? Floor { get; set; }
    [JsonPropertyName("gold")] public int? Gold { get; set; }
}

public sealed class PlayerInfo
{
    [JsonPropertyName("hp")] public int? Hp { get; set; }
    [JsonPropertyName("max_hp")] public int? MaxHp { get; set; }
    [JsonPropertyName("block")] public int? Block { get; set; }
    [JsonPropertyName("energy")] public int? Energy { get; set; }
    [JsonPropertyName("max_energy")] public int? MaxEnergy { get; set; }
}

public sealed class CardInfo
{
    [JsonPropertyName("index")] public int Index { get; set; }
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("cost")] public int? Cost { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("playable")] public bool? Playable { get; set; }
}

public sealed class EnemyInfo
{
    [JsonPropertyName("index")] public int Index { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("hp")] public int? Hp { get; set; }
    [JsonPropertyName("max_hp")] public int? MaxHp { get; set; }
    [JsonPropertyName("block")] public int? Block { get; set; }
    [JsonPropertyName("intent")] public string? Intent { get; set; }
}

public sealed class PotionInfo
{
    [JsonPropertyName("index")] public int Index { get; set; }
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
}

public sealed class ChoiceInfo
{
    [JsonPropertyName("index")] public int Index { get; set; }
    [JsonPropertyName("label")] public string? Label { get; set; }
    [JsonPropertyName("kind")] public string? Kind { get; set; }
}

public sealed class StateSnapshot
{
    [JsonPropertyName("screen")] public string Screen { get; set; } = "unknown";
    [JsonPropertyName("run")] public RunInfo? Run { get; set; }
    [JsonPropertyName("player")] public PlayerInfo? Player { get; set; }
    [JsonPropertyName("hand")] public List<CardInfo> Hand { get; set; } = new();
    [JsonPropertyName("enemies")] public List<EnemyInfo> Enemies { get; set; } = new();
    [JsonPropertyName("potions")] public List<PotionInfo> Potions { get; set; } = new();
    [JsonPropertyName("choices")] public List<ChoiceInfo> Choices { get; set; } = new();
}

public sealed class ActionInfo
{
    [JsonPropertyName("action_id")] public string ActionId { get; set; } = "";
    [JsonPropertyName("kind")] public string Kind { get; set; } = "";
    [JsonPropertyName("label")] public string? Label { get; set; }
    [JsonPropertyName("target")] public string? Target { get; set; }
}

/// <summary>Request body for POST /do.</summary>
public sealed class DoRequest
{
    [JsonPropertyName("action_id")] public string? ActionId { get; set; }
    [JsonPropertyName("kind")] public string? Kind { get; set; }
    [JsonPropertyName("index")] public int? Index { get; set; }
    [JsonPropertyName("target")] public string? Target { get; set; }
}

public sealed class DoResult
{
    [JsonPropertyName("ok")] public bool Ok { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
}

// ---------------------------------------------------------------------------
// Interfaces — the game-state reading / action execution bodies are provided
// by a later stage. The HTTP layer only ever talks to these interfaces.
// ---------------------------------------------------------------------------

public interface IStateProvider
{
    StateSnapshot GetState();
    IReadOnlyList<ActionInfo> GetActions();
}

public interface IActionExecutor
{
    DoResult Execute(DoRequest request);
}
