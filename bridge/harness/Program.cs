using SpireBridge;

// Out-of-game harness: runs the REAL GameStateProvider / GameActionExecutor
// behind the real HTTP server, but without a game thread (GameThread.Capture
// is never called). Expected behaviour: /health answers (game_version read
// from the sts2 assembly's release_info.json when resolvable), /state and
// /actions return honest fallbacks without exceptions, /do returns
// ok:false instead of crashing.
int port = 8766;
DisplayChecks.Run();
var portIndex = Array.IndexOf(args, "--port");
if (portIndex >= 0) port = int.Parse(args[portIndex + 1]);
if (port < 1024 || port > 65535 || port == 8765 || port == 8766)
    throw new ArgumentException("Harness requires an isolated non-production port (1024..65535)");
IStateProvider provider = args.Contains("--fixture") ? new ContractFixture() : new GameStateProvider();
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
