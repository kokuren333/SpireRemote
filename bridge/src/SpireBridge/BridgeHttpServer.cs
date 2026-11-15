using System.Net;
using System.Text;
using System.Text.Json;

namespace SpireBridge;

/// <summary>
/// Loopback-only HTTP bridge on 127.0.0.1:8766.
/// Routes:
///   GET  /health  -> { ok, service, version, game_version }
///   GET  /state   -> StateSnapshot via IStateProvider
///   GET  /actions -> [ActionInfo] via IStateProvider
///   POST /do      -> DoResult via IActionExecutor (body: DoRequest JSON)
/// All handler errors are caught and returned as 500 JSON; nothing thrown
/// from here may crash the game process.
/// </summary>
public sealed class BridgeHttpServer : IDisposable
{
    public const string Prefix = "http://127.0.0.1:8766/";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
    };

    private readonly IStateProvider _stateProvider;
    private readonly IActionExecutor _actionExecutor;
    private readonly string _prefix;
    private readonly HttpListener _listener = new();
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public BridgeHttpServer(IStateProvider stateProvider, IActionExecutor actionExecutor, string prefix = Prefix)
    {
        var uri = new Uri(prefix);
        if (uri.Scheme != "http" || uri.Host != "127.0.0.1" || uri.AbsolutePath != "/" ||
            !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException("Bridge must bind an HTTP IPv4 loopback root prefix", nameof(prefix));
        _stateProvider = stateProvider;
        _actionExecutor = actionExecutor;
        _prefix = prefix;
    }

    public bool IsRunning { get; private set; }

    public void Start()
    {
        if (IsRunning) return;
        _listener.Prefixes.Add(_prefix);
        _listener.Start();
        IsRunning = true;
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => AcceptLoopAsync(_cts.Token));
        BridgeLog.Info($"[SpireBridge] HTTP listening on {_prefix}");
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                if (ct.IsCancellationRequested) break;
                BridgeLog.Warn($"[SpireBridge] accept failed: {ex.Message}");
                continue;
            }

            // Handle each request independently; a failure must not kill the loop.
            _ = Task.Run(() => Handle(context));
        }
    }

    private void Handle(HttpListenerContext context)
    {
        try
        {
            var req = context.Request;
            var path = req.Url?.AbsolutePath ?? "/";
            var method = req.HttpMethod;

            switch ((method, path))
            {
                case ("GET", "/health"):
                {
                    // game_version comes from release_info.json next to the
                    // sts2 assembly; null when unobtainable (e.g. harness).
                    string? gameVersion = null;
                    try { gameVersion = GameVersion.Get(); }
                    catch { gameVersion = null; }
                    WriteJson(context, 200, new { ok = true, service = "SpireBridge", version = "0.4.0", game_version = gameVersion });
                    break;
                }

                case ("GET", "/state"):
                    WriteJson(context, 200, _stateProvider.GetState());
                    break;

                case ("GET", "/actions"):
                    WriteJson(context, 200, _stateProvider.GetActions());
                    break;

                case ("POST", "/do"):
                {
                    string body;
                    using (var reader = new StreamReader(req.InputStream, req.ContentEncoding ?? Encoding.UTF8))
                        body = reader.ReadToEnd();
                    var doReq = string.IsNullOrWhiteSpace(body)
                        ? new DoRequest()
                        : JsonSerializer.Deserialize<DoRequest>(body, JsonOptions) ?? new DoRequest();
                    WriteJson(context, 200, _actionExecutor.Execute(doReq));
                    break;
                }

                default:
                    WriteJson(context, 404, new { ok = false, error = "not_found" });
                    break;
            }
        }
        catch (Exception ex)
        {
            BridgeLog.Warn($"[SpireBridge] request failed: {ex}");
            try { WriteJson(context, 500, new { ok = false, error = "internal_error", message = ex.Message }); }
            catch { /* response already partially sent / connection gone — swallow */ }
        }
    }

    private static void WriteJson(HttpListenerContext context, int status, object payload)
    {
        var json = JsonSerializer.Serialize(payload, JsonOptions);
        var bytes = Encoding.UTF8.GetBytes(json);
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.ContentLength64 = bytes.Length;
        context.Response.OutputStream.Write(bytes, 0, bytes.Length);
        context.Response.OutputStream.Close();
    }

    public void Dispose()
    {
        try { _cts?.Cancel(); } catch { }
        try { if (_listener.IsListening) _listener.Stop(); } catch { }
        try { _listener.Close(); } catch { }
        IsRunning = false;
    }
}

/// <summary>
/// Minimal logging. IMPORTANT: Godot.GD.Print outside the running Godot
/// engine crashes with a NATIVE access violation (verified 2026-11-15 in an
/// out-of-game harness: 0xC0000005 in godotsharp_string_new_with_utf16_chars),
/// which try/catch cannot intercept. Therefore Godot logging is opt-in:
/// ModEntry (which only ever runs inside the game) sets UseGodotLogging=true
/// before anything logs. Everywhere else the safe Console fallback is used.
/// </summary>
internal static class BridgeLog
{
    public static bool UseGodotLogging;

    public static void Info(string message)
    {
        if (UseGodotLogging)
        {
            try { Godot.GD.Print(message); return; }
            catch { /* managed failure — fall through to Console */ }
        }
        Console.WriteLine(message);
    }

    public static void Warn(string message)
    {
        if (UseGodotLogging)
        {
            try { Godot.GD.PrintErr(message); return; }
            catch { /* managed failure — fall through to Console */ }
        }
        Console.Error.WriteLine(message);
    }
}
