namespace SpireBridge;

/// <summary>
/// Stub implementations used until the real game-state reader is integrated.
/// They intentionally return only what is known: screen="unknown", empty
/// collections, and null for anything not yet obtainable. No fabricated data.
/// </summary>
public sealed class StubStateProvider : IStateProvider
{
    public StateSnapshot GetState() => new()
    {
        Screen = "unknown",
        Run = null,
        Player = null,
        Hand = new List<CardInfo>(),
        Enemies = new List<EnemyInfo>(),
        Potions = new List<PotionInfo>(),
        Choices = new List<ChoiceInfo>(),
    };

    public IReadOnlyList<ActionInfo> GetActions() => Array.Empty<ActionInfo>();
}

/// <summary>Stub executor: every kind is rejected as not_implemented for now.</summary>
public sealed class StubActionExecutor : IActionExecutor
{
    public DoResult Execute(DoRequest request) => new()
    {
        Ok = false,
        Error = "not_implemented",
    };
}
