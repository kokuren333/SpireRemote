using SpireBridge;

// Out-of-game harness: runs the REAL GameStateProvider / GameActionExecutor
// behind the real HTTP server, but without a game thread (GameThread.Capture
// is never called). Expected behaviour: /health answers (game_version read
// from the sts2 assembly's release_info.json when resolvable), /state and
// /actions return honest fallbacks without exceptions, /do returns
// ok:false instead of crashing.
int port = 8766;
DisplayChecks.Run();
EnemyChecks.Run();
var portIndex = Array.IndexOf(args, "--port");
if (portIndex >= 0) port = int.Parse(args[portIndex + 1]);
if (port < 1024 || port > 65535 || port == 8765 || port == 8766)
    throw new ArgumentException("Harness requires an isolated non-production port (1024..65535)");
IStateProvider provider = args.Contains("--fixture-v4") ? new V4Fixture()
    : args.Contains("--fixture") ? new ContractFixture() : new GameStateProvider();
using var server = new BridgeHttpServer(provider, new GameActionExecutor(), $"http://127.0.0.1:{port}/");
server.Start();
Console.WriteLine("HARNESS_READY");
Thread.Sleep(Timeout.Infinite);

// Explicit synthetic fixture exercises the real DTO + HTTP serializer without
// instantiating game models or calling Godot. Default mode retains real fallback.
sealed class ContractFixture : IStateProvider
{
    public StateSnapshot GetState() => new()
    {
        Screen = "card_selection",
        Potions = new() { new PotionInfo { Index = 0, Name = "Fixture potion", Description = "Fixture potion description" } },
        Choices = new() { new ChoiceInfo { Index = 0, Kind = "card_select", Label = "Fixture card", Description = "Fixture card description" } },
        Selected = null,
    };
    public IReadOnlyList<ActionInfo> GetActions() => Array.Empty<ActionInfo>();
}

sealed class V4Fixture : IStateProvider
{
    public StateSnapshot GetState() => new()
    {
        Screen = "combat",
        Enemies = new() {
            new EnemyInfo { Index = 0, Name = "スライム", Hp = 20, MaxHp = 24, Block = 2, Intent = "攻撃 7",
                Label = "A", Alive = true, Pos = new EnemyPosition { X = .612, Y = .548, W = .11, H = .26 } },
            new EnemyInfo { Index = 1, Name = "スライム", Label = "B", Alive = true, Pos = null },
            new EnemyInfo { Index = 2, Name = "Unknown", Label = null, Alive = null, Pos = null },
        },
        Choices = new() {
            new ChoiceInfo { Index = 0, Kind = "event", Label = "Open", Enabled = true },
            new ChoiceInfo { Index = 1, Kind = "event", Label = "Closed [locked]", Enabled = false },
            new ChoiceInfo { Index = 2, Kind = "reward", Label = "Reward", Enabled = null },
        },
    };
    public IReadOnlyList<ActionInfo> GetActions() => new[] {
        new ActionInfo { ActionId = "play_card:1:1", Kind = "play_card", Label = EnemyDisplayLogic.TargetedLabel("ストライク", "スライム", "B"),
            Target = "スライム", TargetIndex = 1, TargetLabel = "B" },
        new ActionInfo { ActionId = "end_turn", Kind = "end_turn", Label = "End Turn" },
    };
}
