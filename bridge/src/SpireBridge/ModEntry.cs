using MegaCrit.Sts2.Core.Modding;

namespace SpireBridge;

// ---------------------------------------------------------------------------
// Mod entry point. The initializer shape below is confirmed against the
// current game build (v0.113.1) by API_NOTES.md §1: ModInitializerAttribute
// carries the initializer method name ("Initialize"), and an existing mod
// (STS2AIMCP) uses exactly this form on a static class.
// ---------------------------------------------------------------------------
[ModInitializer(nameof(Initialize))]
public static class ModEntry
{
    private static BridgeHttpServer? _server;
    private static bool _initialized;

    public static void Initialize()
    {
        if (_initialized) return;
        _initialized = true;

        try
        {
            // Running inside the game from here on: Godot logging is safe.
            // (GD.Print outside the engine crashes natively — see BridgeLog.)
            BridgeLog.UseGodotLogging = true;
            BridgeLog.Info("[SpireBridge] Initializing SpireBridge 0.2.0");

            // Capture the game thread's SynchronizationContext before any
            // HTTP handler can run; all game API access is marshaled
            // through it (see GameThread).
            GameThread.Capture();

            IStateProvider stateProvider;
            IActionExecutor actionExecutor;
            try
            {
                stateProvider = new GameStateProvider();
                actionExecutor = new GameActionExecutor();
                BridgeLog.Info("[SpireBridge] Game state provider active");
            }
            catch (Exception ex)
            {
                // Keep the HTTP server alive even if the game-facing
                // providers cannot be constructed.
                BridgeLog.Warn($"[SpireBridge] Game provider init failed, falling back to stub: {ex.Message}");
                stateProvider = new StubStateProvider();
                actionExecutor = new StubActionExecutor();
            }

            _server = new BridgeHttpServer(stateProvider, actionExecutor);
            _server.Start();
            BridgeLog.Info("[SpireBridge] Ready");
        }
        catch (Exception ex)
        {
            // Never let bridge startup failure crash the game.
            BridgeLog.Warn($"[SpireBridge] Initialization failed: {ex}");
        }
    }
}
