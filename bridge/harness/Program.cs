using SpireBridge;

// Out-of-game harness: runs the REAL GameStateProvider / GameActionExecutor
// behind the real HTTP server, but without a game thread (GameThread.Capture
// is never called). Expected behaviour: /health answers (game_version read
// from the sts2 assembly's release_info.json when resolvable), /state and
// /actions return honest fallbacks without exceptions, /do returns
// ok:false instead of crashing.
var server = new BridgeHttpServer(new GameStateProvider(), new GameActionExecutor());
server.Start();
Console.WriteLine("HARNESS_READY");
Thread.Sleep(Timeout.Infinite);
