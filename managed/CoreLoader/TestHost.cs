using System.Collections;
using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CoreLoader.Runtime;
using Microsoft.Win32.SafeHandles;

namespace CoreLoader;

/// <summary>
/// A development-only automation host: a named pipe that lets scripts and
/// agents drive the running game and read its state back, without clicking
/// through the overlay. Off unless the game was started with the environment
/// variable <c>CORELOADER_TEST=1</c>, or <c>CoreLoader\testhost.enable</c> exists.
/// </summary>
/// <remarks>
/// The protocol is one JSON object per line each way. A request is
/// <c>{"id":1,"cmd":"call","args":["scr_foo",1,"a"]}</c>, optionally with
/// <c>"as"</c> and <c>"timeout"</c> (seconds, 1 to 600, default 30); the
/// response is <c>{"id":1,"ok":true,"result":...}</c> or
/// <c>{"id":1,"ok":false,"error":"..."}</c>. A request the game thread has not
/// started by a second before its timeout is dropped unrun and answered as
/// expired; a client should wait a little past the timeout it sends
/// (tools\coreloader.ps1 waits two seconds more) so it never mistakes a request
/// that ran just in time for a dropped one. A request line is at most 1M
/// characters. The pipe is
/// <c>\\.\pipe\coreloader-&lt;pid&gt;</c>, readable and writable by the current
/// user only, and its name is written to <c>CoreLoader\Logs\testhost.pipe</c>.
///
/// The pipe is served on background threads, but every command runs on the
/// game thread, at the start of the next frame. Nothing waits there: a command
/// that needs time (<c>wait-frames</c>) is parked and answered on a later
/// frame, and anything longer is polled from the client.
/// </remarks>
public static partial class TestHost
{
    private static readonly Logger Log = new("TestHost");

    /// <summary>
    /// Whether this session runs the test host (<c>CORELOADER_TEST=1</c>, or a
    /// <c>CoreLoader\testhost.enable</c> file). Mods register their test
    /// commands only when it does.
    /// </summary>
    public static bool Enabled { get; } = Environment.GetEnvironmentVariable("CORELOADER_TEST") == "1" || MarkerPresent();

    // Steam relaunches some games through steam.exe, which drops the launching
    // shell's environment, so the variable never arrives. A marker file in the
    // loader's folder turns the host on instead (tools\run-game.ps1 -TestHost
    // writes it, and removes it on a launch without -TestHost). It asks no more
    // trust than the variable: whoever can write that folder can replace the
    // loader itself.
    private const string MarkerFile = "testhost.enable";

    private static unsafe bool MarkerPresent()
    {
        // Type initialisation may in principle run before Entry sets the API
        // table; a null table is a crash no catch could stop.
        if (Loader.Api == null) return false;
        try { return File.Exists(Path.Combine(Game.LoaderDirectory, MarkerFile)); }
        catch (Exception) { return false; }
    }

    /// <summary>The pipe's name (without <c>\\.\pipe\</c>), or null when the host is off.</summary>
    public static string? PipeName { get; private set; }

    /// <summary>Frames seen since the loader started (counted only while the host is on).</summary>
    public static long Frame => _frame;

    /// <summary>
    /// Adds a test command. <paramref name="handler"/> runs on the game thread
    /// with the request's arguments and returns the result: null, a bool, a
    /// number, a string, an <see cref="RValue"/>, an <see cref="InstanceRef"/>,
    /// a collection or dictionary of those, or any object, whose public
    /// properties are serialised.
    /// </summary>
    /// <remarks>
    /// The command belongs to the registering mod and goes when it unloads or
    /// hot-reloads. A handler that throws answers <c>ok:false</c> with the
    /// message and does not fault the mod: a test run feeds commands bad input on
    /// purpose, and one bad argument must not disable the mod for the rest of it.
    /// A faulted mod's commands answer with its fault until it is reloaded.
    /// </remarks>
    public static void Register(string name, Func<IReadOnlyList<JsonElement>, object?> handler, string help = "")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(handler);
        if (BuiltIn.ContainsKey(name)) throw new ArgumentException($"'{name}' is a built-in test command", nameof(name));
        // Nothing can call it: no pipe, and no command kept for a session without one.
        if (!Enabled) return;
        var owner = ModManager.OwnerOf(handler);
        lock (Commands)
        {
            // A mod's hot-reloaded copy is constructed before the old one is
            // unloaded, so the old copy's command gives way to it.
            if (Commands.TryGetValue(name, out var existing) && existing.Owner != owner &&
                existing.Owner != null && ModManager.Mods.Contains(existing.Owner) &&
                !string.Equals(existing.Owner.Path, owner?.Path, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"test command '{name}' is already registered by {existing.Owner.Instance.Info.Name}");
            Commands[name] = new Command(name, handler, help, owner);
        }
    }

    /// <summary>
    /// The argument as a GML value: numbers, strings, booleans and null
    /// (undefined). Game thread only (a string becomes a GML string).
    /// </summary>
    public static RValue ToRValue(JsonElement arg) => arg.ValueKind switch
    {
        JsonValueKind.Number => RValue.FromReal(arg.GetDouble()),
        JsonValueKind.String => RValue.FromString(arg.GetString()!),
        JsonValueKind.True => RValue.FromBool(true),
        JsonValueKind.False => RValue.FromBool(false),
        JsonValueKind.Null or JsonValueKind.Undefined => RValue.Undefined,
        _ => throw new ArgumentException($"unsupported argument {arg.GetRawText()}: only numbers, strings, booleans and null"),
    };

    // ------------------------------------------------------------ internals

    private sealed record Command(string Name, Func<IReadOnlyList<JsonElement>, object?> Handler, string Help, LoadedMod? Owner);

    private sealed class Request
    {
        public JsonNode? Id;
        public required string Cmd;
        public required JsonElement[] Args;
        public JsonElement? As;
        public long DueFrame;
        // The last moment the request may start: a second before the client's
        // timeout, while the client waits two seconds past it. A request still
        // queued by then (the game stopped presenting frames) is dropped rather
        // than run late, and one that did start is answered before the client
        // gives up, so a command the client reported as dropped never ran.
        public DateTime Deadline;
        public readonly TaskCompletionSource<string> Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private static readonly Dictionary<string, Command> Commands = new(StringComparer.Ordinal);
    private static readonly ConcurrentQueue<Request> Incoming = new();
    private static readonly List<Request> Parked = new();
    private static long _frame;
    private static string? _pipeFile;

    // A client that asked for more than this is waiting on something else.
    private const int MaxWaitFrames = 60 * 60 * 10;

    // A request line longer than this (in characters) is refused unread: every
    // command fits in a few hundred, and the reader must not buffer without end.
    private const int MaxRequestChars = 1 << 20;

    // Start requests this long before the client's timeout (see Request.Deadline),
    // but always give the game thread at least MinStartWindow to reach one.
    private static readonly TimeSpan StartMargin = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MinStartWindow = TimeSpan.FromMilliseconds(500);

    // The loader's log lives beside the pipe file. CORELOADER_DATA_DIR moves both,
    // as it moves the log on the native side; SSMOD_DATA_DIR is its old name,
    // still read as a fallback for one release.
    private static string LogDirectory =>
        Environment.GetEnvironmentVariable("CORELOADER_DATA_DIR") is { Length: > 0 } d ? d
        : Environment.GetEnvironmentVariable("SSMOD_DATA_DIR") is { Length: > 0 } old ? old
        : Path.Combine(Game.LoaderDirectory, "Logs");

    /// <summary>From Entry.Init: starts the pipe if the session asked for it, and says so either way.</summary>
    internal static void Start()
    {
        if (!Enabled)
        {
            Log.Info($"test host off (start the game with CORELOADER_TEST=1, or create CoreLoader\\{MarkerFile}, to drive it over a pipe)");
            return;
        }
        PipeName = $"coreloader-{Environment.ProcessId}";
        try
        {
            Directory.CreateDirectory(LogDirectory);
            _pipeFile = Path.Combine(LogDirectory, "testhost.pipe");
            File.WriteAllText(_pipeFile, PipeName);
        }
        catch (Exception ex)
        {
            Log.Warning($"could not write the pipe name file: {ex.Message}");
        }
        new Thread(AcceptLoop) { IsBackground = true, Name = "CoreLoader test host" }.Start();
        string how = Environment.GetEnvironmentVariable("CORELOADER_TEST") == "1" ? "CORELOADER_TEST=1" : $"CoreLoader\\{MarkerFile}";
        Log.Info($"test host ON ({how}): listening on \\\\.\\pipe\\{PipeName}, current user only. Development use only");
    }

    /// <summary>From Entry.Shutdown: the name file goes, so a client does not dial a dead game.</summary>
    internal static void Stop()
    {
        if (_pipeFile == null) return;
        try { File.Delete(_pipeFile); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    internal static void RemoveOwner(LoadedMod owner)
    {
        lock (Commands)
            foreach (var name in Commands.Where(kv => kv.Value.Owner == owner).Select(kv => kv.Key).ToList())
                Commands.Remove(name);
    }

    private static void AcceptLoop()
    {
        while (true)
        {
            NamedPipeServerStream? server = null;
            try
            {
                // CurrentUserOnly: the pipe's ACL admits this user alone, and
                // the client's identity is checked on connect.
                server = new NamedPipeServerStream(PipeName!, PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
                    PipeOptions.CurrentUserOnly);
                server.WaitForConnection();
                if (!IsLocalClient(server))
                {
                    Log.Warning("refused a test client connecting over the network (the test host is local only)");
                    server.Dispose();
                    server = null;
                    continue;
                }
                var connected = server;
                server = null;
                new Thread(() => Serve(connected)) { IsBackground = true, Name = "CoreLoader test client" }.Start();
            }
            catch (Exception ex)
            {
                server?.Dispose();
                Log.Warning($"pipe accept failed: {ex.Message}");
                Thread.Sleep(1000);
            }
        }
    }

    // NamedPipeServerStream does not create its pipe with
    // PIPE_REJECT_REMOTE_CLIENTS (checked on .NET 10: a client dialling
    // \\<this machine>\pipe\... over SMB connects, even with CurrentUserOnly,
    // which only sets the ACL). So each client is checked once connected, and
    // before anything is read: the call fails with ERROR_PIPE_LOCAL for a local
    // client and names the computer for a remote one. Any other outcome is
    // treated as remote.
    private const int ErrorPipeLocal = 229;

    private static unsafe bool IsLocalClient(NamedPipeServerStream pipe)
    {
        char* name = stackalloc char[256];
        if (GetNamedPipeClientComputerName(pipe.SafePipeHandle, name, 256 * sizeof(char))) return false;
        return Marshal.GetLastPInvokeError() == ErrorPipeLocal;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "GetNamedPipeClientComputerNameW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool GetNamedPipeClientComputerName(SafePipeHandle pipe, char* name, uint nameBytes);

    // One client, one request at a time: each line is answered before the next is read.
    private static void Serve(NamedPipeServerStream pipe)
    {
        var utf8 = new UTF8Encoding(false);
        try
        {
            using (pipe)
            using (var reader = new StreamReader(pipe, utf8))
            using (var writer = new StreamWriter(pipe, utf8) { AutoFlush = true, NewLine = "\n" })
            {
                var buffer = new StringBuilder();
                while (ReadRequest(reader, buffer, out bool tooLarge) is { } line)
                {
                    if (tooLarge)
                    {
                        writer.WriteLine(Reply(null, false, null, $"request too large (over {MaxRequestChars} characters)"));
                        continue;
                    }
                    if (line.Trim().Length == 0) continue;
                    writer.WriteLine(Handle(line));
                }
            }
        }
        catch (IOException) { /* the client went away */ }
        catch (ObjectDisposedException) { }
        catch (Exception ex)
        {
            Log.Warning($"test client dropped: {ex.Message}");
        }
    }

    // ReadLine with a cap: the next line, or null at the end of the stream. A
    // line past MaxRequestChars is read to its end and dropped, and reported
    // through tooLarge, so the connection stays in step with the client.
    private static string? ReadRequest(StreamReader reader, StringBuilder line, out bool tooLarge)
    {
        line.Clear();
        tooLarge = false;
        int c;
        while ((c = reader.Read()) >= 0)
        {
            if (c == '\n') return tooLarge ? "" : line.ToString().TrimEnd('\r');
            if (tooLarge) continue;
            if (line.Length >= MaxRequestChars)
            {
                tooLarge = true;
                line.Clear();
                continue;
            }
            line.Append((char)c);
        }
        // The stream ended; a last line without its newline still counts.
        return tooLarge ? "" : line.Length > 0 ? line.ToString().TrimEnd('\r') : null;
    }

    // Background thread: parse, queue for the game thread, wait for its answer.
    private static string Handle(string line)
    {
        Request req;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return Reply(null, false, null, "a request is a JSON object");
            JsonNode? id = root.TryGetProperty("id", out var idEl) ? JsonNode.Parse(idEl.GetRawText()) : null;
            if (!root.TryGetProperty("cmd", out var cmdEl) || cmdEl.ValueKind != JsonValueKind.String)
                return Reply(id, false, null, "missing \"cmd\"");
            var args = Array.Empty<JsonElement>();
            if (root.TryGetProperty("args", out var argsEl))
            {
                if (argsEl.ValueKind != JsonValueKind.Array) return Reply(id, false, null, "\"args\" must be an array");
                args = argsEl.EnumerateArray().Select(a => a.Clone()).ToArray();
            }
            req = new Request
            {
                Id = id,
                Cmd = cmdEl.GetString()!,
                Args = args,
                As = root.TryGetProperty("as", out var asEl) ? asEl.Clone() : null,
                Deadline = DateTime.UtcNow + StartWindow(
                    root.TryGetProperty("timeout", out var toEl) && toEl.ValueKind == JsonValueKind.Number
                        ? Math.Clamp(toEl.GetDouble(), 1, 600) : 30),
            };
        }
        catch (JsonException ex)
        {
            return Reply(null, false, null, $"bad JSON: {ex.Message}");
        }

        Incoming.Enqueue(req);
        // The game may be loading or paused between presents; the client has
        // its own, shorter, timeout and this one only frees the thread.
        return req.Done.Task.Wait(TimeSpan.FromMinutes(10))
            ? req.Done.Task.Result
            : Reply(req.Id, false, null, "no answer from the game thread (is the game still presenting frames?)");
    }

    private static TimeSpan StartWindow(double clientTimeoutSeconds)
    {
        var window = TimeSpan.FromSeconds(clientTimeoutSeconds) - StartMargin;
        return window < MinStartWindow ? MinStartWindow : window;
    }

    // Game text (item names, log lines) stays readable on the wire; nothing
    // here is ever embedded in HTML, which is what the default escaping is for.
    private static readonly JsonSerializerOptions JsonOptions =
        new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static string Reply(JsonNode? id, bool ok, JsonNode? result, string? error)
    {
        var o = new JsonObject { ["id"] = id?.DeepClone(), ["ok"] = ok };
        if (ok) o["result"] = result;
        else o["error"] = error;
        return o.ToJsonString(JsonOptions);
    }

    /// <summary>Called every frame on the game thread: runs what arrived, answers what is due.</summary>
    internal static void Tick()
    {
        if (!Enabled) return;
        _frame++;

        for (int i = Parked.Count - 1; i >= 0; i--)
        {
            if (Parked[i].DueFrame > _frame) continue;
            var due = Parked[i];
            Parked.RemoveAt(i);
            due.Done.TrySetResult(Reply(due.Id, true, JsonValue.Create(_frame), null));
        }

        // Only what was queued before this frame, as with Game.RunOnGameThread.
        for (int n = Incoming.Count; n > 0 && Incoming.TryDequeue(out var req); n--)
        {
            if (DateTime.UtcNow > req.Deadline)
                req.Done.TrySetResult(Reply(req.Id, false, null, "expired: the game thread did not reach it before the client's timeout; not run"));
            else Execute(req);
        }
    }

    private static void Execute(Request req)
    {
        try
        {
            if (req.Cmd == "wait-frames")
            {
                int frames = req.Args.Length > 0 ? (int)Math.Clamp(Num(req.Args, 0, "frames"), 0, MaxWaitFrames) : 1;
                req.DueFrame = _frame + frames;
                Parked.Add(req);
                return;
            }

            object? result;
            if (BuiltIn.TryGetValue(req.Cmd, out var builtin))
            {
                // Until the game has its assets (when mods start), a call can
                // land in half-initialised game state; the client polls status.
                if (!ModManager.Started && req.Cmd is "call" or "builtin" or "global-get" or "global-set"
                        or "instance-get" or "instance-set" or "object-count")
                    throw new InvalidOperationException($"{req.Cmd}: the game is still loading its assets (status.modsStarted is false)");
                result = builtin.Run(req);
            }
            else
            {
                Command? cmd;
                lock (Commands) Commands.TryGetValue(req.Cmd, out cmd);
                if (cmd == null) throw new ArgumentException($"unknown command '{req.Cmd}' (list-commands lists them)");
                result = RunModCommand(cmd, req.Args);
            }
            // Converted here, on the game thread and within the frame: strings
            // and arrays from the game are released when the frame ends.
            req.Done.TrySetResult(Reply(req.Id, true, ToNode(result, 0), null));
        }
        catch (Exception caught)
        {
            // A throwing property getter on a result object arrives wrapped.
            var ex = caught is TargetInvocationException { InnerException: { } inner } ? inner : caught;
            string why = ex is GmlException or ArgumentException or InvalidOperationException
                ? ex.Message
                : $"{ex.GetType().Name}: {ex.Message}";
            req.Done.TrySetResult(Reply(req.Id, false, null, why));
        }
    }

    private static object? RunModCommand(Command cmd, IReadOnlyList<JsonElement> args)
    {
        if (cmd.Owner is { } owner)
        {
            if (!ModManager.Mods.Contains(owner)) throw new InvalidOperationException($"{cmd.Name}: its mod has been unloaded");
            if (owner.State == ModState.Faulted) throw new InvalidOperationException($"{cmd.Name}: {owner.Instance.Info.Name} is disabled: {owner.Fault}");
            if (owner.State != ModState.Running) throw new InvalidOperationException($"{cmd.Name}: {owner.Instance.Info.Name} has not started yet");
        }
        // Run as the mod, so what the handler registers belongs to it. Not
        // through ModManager.Invoke, which would fault the mod on a throw.
        var previous = ModManager.Current;
        ModManager.Current = cmd.Owner;
        try { return cmd.Handler(args); }
        finally { ModManager.Current = previous; }
    }

    // --------------------------------------------------------- built-ins

    private sealed record BuiltInCommand(string Help, Func<Request, object?> Run);

    private static readonly Dictionary<string, BuiltInCommand> BuiltIn = new(StringComparer.Ordinal)
    {
        ["ping"] = new("ping: answers \"pong\"", _ => "pong"),
        ["status"] = new("status: game, GML bridge, frame count, mods", _ => Status()),
        ["log"] = new("log [n=50]: the last n lines of the loader log", r => LogTail(r.Args.Length > 0 ? (int)Num(r.Args, 0, "n") : 50)),
        ["mods"] = new("mods: every loaded mod with its state and fault", _ => ModList()),
        ["reload"] = new("reload <mod|all>: reloads a mod by name or file name, as the Loader tab does", r => Reload(Str(r.Args, 0, "mod"))),
        ["call"] = new("call <script> [args...] (\"as\":\"current\" runs as the current self): calls a script",
            r =>
            {
                // Resolved once: self and other are the same instance.
                var self = Self(r);
                return Game.CallScriptAs(self, self, Str(r.Args, 0, "script"), Rest(r.Args, 1));
            }),
        ["builtin"] = new("builtin <name> [args...] (\"as\":\"current\" runs as the current self): calls a builtin",
            r => Game.CallBuiltinAs(Self(r), Str(r.Args, 0, "name"), Rest(r.Args, 1))),
        ["global-get"] = new("global-get <name>: a global variable", r => Globals.Get(Str(r.Args, 0, "name"))),
        ["global-set"] = new("global-set <name> <value>: writes a global, answers the value read back", r =>
        {
            string name = Str(r.Args, 0, "name");
            Globals.Set(name, ToRValue(Arg(r.Args, 1, "value")));
            return Globals.Get(name);
        }),
        ["instance-get"] = new("instance-get <object> <n> <var> | <id> <var>: a variable of the n-th live instance of the object, or of an instance id",
            r => InstanceVar(r.Args, set: false)),
        ["instance-set"] = new("instance-set <object> <n> <var> <value> | <id> <var> <value>: writes an instance variable, answers the value read back",
            r => InstanceVar(r.Args, set: true)),
        ["object-count"] = new("object-count <object>: live instances of the object, children included", r =>
        {
            string name = Str(r.Args, 0, "object");
            return (GmlObject.Find(name) ?? throw new ArgumentException($"no object named '{name}'")).InstanceCount;
        }),
        ["wait-frames"] = new("wait-frames [n=1]: answers after n frames, with the frame count", _ => null),
        ["list-commands"] = new("list-commands: every command, built-in and registered by mods", _ => ListCommands()),
    };

    private static object Status() => new
    {
        loader = typeof(TestHost).Assembly.GetName().Version?.ToString(),
        game = Game.Name,
        gmlReady = Game.IsGmlReady,
        abiProven = Game.IsAbiProven,
        builtins = Game.BuiltinCount,
        frame = _frame,
        modsStarted = ModManager.Started,
        mods = ModList(),
    };

    private static List<object> ModList() => ModManager.Mods.Select(m => (object)new
    {
        name = m.Instance.Info.Name,
        version = m.Instance.Info.Version,
        file = Path.GetFileName(m.Path),
        state = m.State.ToString().ToLowerInvariant(),
        fault = m.Fault,
        hooks = Hooks.SubscriptionCount(m),
    }).ToList();

    private static List<object> ListCommands()
    {
        var list = BuiltIn.Select(kv => (object)new { name = kv.Key, help = kv.Value.Help, owner = "CoreLoader" }).ToList();
        lock (Commands)
            list.AddRange(Commands.Values.OrderBy(c => c.Name, StringComparer.Ordinal)
                .Select(c => (object)new { name = c.Name, help = c.Help, owner = c.Owner?.Instance.Info.Name ?? "CoreLoader" }));
        return list;
    }

    private static List<string> LogTail(int n)
    {
        n = Math.Clamp(n, 1, 5000);
        var path = Path.Combine(LogDirectory, "coreloader.log");
        // Shared read: the native side keeps the log open for writing. Only
        // the end is read; a long session's log can be large.
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        const long Window = 1 << 20;
        if (fs.Length > Window) fs.Seek(-Window, SeekOrigin.End);
        using var reader = new StreamReader(fs, Encoding.UTF8);
        var lines = reader.ReadToEnd().Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        if (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        return lines.Skip(Math.Max(0, lines.Count - n)).ToList();
    }

    // Reloading from here is safe: the test host runs between frames, with no
    // mod code on the stack, where hot reload runs too.
    private static object Reload(string which)
    {
        if (which.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            ModManager.ReloadAll();
            return ModList();
        }
        var mod = ModManager.Mods.FirstOrDefault(m =>
                      string.Equals(m.Instance.Info.Name, which, StringComparison.OrdinalIgnoreCase) ||
                      string.Equals(Path.GetFileNameWithoutExtension(m.Path), which, StringComparison.OrdinalIgnoreCase))
                  ?? throw new ArgumentException($"no loaded mod named '{which}'");
        string path = mod.Path;
        ModManager.Reload(mod);
        var fresh = ModManager.Mods.FirstOrDefault(m => string.Equals(m.Path, path, StringComparison.OrdinalIgnoreCase));
        if (fresh == null) throw new InvalidOperationException($"{which} is no longer loaded (its file is gone)");
        if (fresh == mod) throw new InvalidOperationException($"{which} could not be reloaded yet; the running copy stays (see the log)");
        return new { name = fresh.Instance.Info.Name, state = fresh.State.ToString().ToLowerInvariant(), fault = fresh.Fault };
    }

    // "current" is whatever the game last ran; a number is an instance id,
    // turned into the instance through the runtime's id table.
    private static Instance Self(Request r)
    {
        if (r.As is not { } a || a.ValueKind is JsonValueKind.Null) return default;
        if (a.ValueKind == JsonValueKind.String && a.GetString() == "current") return Game.CurrentSelf;
        // Clients on a command line send the id as text, so a numeric string counts.
        double id = double.NaN;
        if (a.ValueKind == JsonValueKind.Number) id = a.GetDouble();
        else if (a.ValueKind == JsonValueKind.String &&
                 double.TryParse(a.GetString(), System.Globalization.NumberStyles.Float,
                                 System.Globalization.CultureInfo.InvariantCulture, out var parsed)) id = parsed;
        if (!double.IsNaN(id))
        {
            if (!Game.CanResolveInstances)
                throw new InvalidOperationException("this runtime's instance id lookup is not proven; use \"as\": \"current\"");
            return new InstanceRef(RValue.FromReal(id)).Resolve()
                   ?? throw new ArgumentException($"no live instance with id {id}");
        }
        throw new ArgumentException("\"as\" takes \"current\" (the instance the game is running) or an instance id");
    }

    private static RValue InstanceVar(JsonElement[] args, bool set)
    {
        // <object> <n> <var> [value] or <id> [n] <var> [value]: an id needs no n.
        var target = Arg(args, 0, "object or id");
        int i = 1;
        InstanceRef inst;
        if (target.ValueKind == JsonValueKind.String)
        {
            string name = target.GetString()!;
            var obj = GmlObject.Find(name) ?? throw new ArgumentException($"no object named '{name}'");
            int n = (int)Num(args, i++, "n");
            if (n < 0 || n >= obj.InstanceCount) throw new ArgumentException($"no live {name}[{n}] ({obj.InstanceCount} exist)");
            inst = obj.Instance(n);
        }
        else
        {
            inst = new InstanceRef(ToRValue(target));
            if (args.Length > i && args[i].ValueKind == JsonValueKind.Number) i++;
            if (!inst.Exists) throw new ArgumentException($"no live instance {target.GetRawText()}");
        }
        string variable = Str(args, i++, "variable");
        if (set) inst.Set(variable, ToRValue(Arg(args, i, "value")));
        return inst.Get(variable);
    }

    private static JsonElement Arg(IReadOnlyList<JsonElement> args, int i, string what) =>
        i < args.Count ? args[i] : throw new ArgumentException($"missing argument {i + 1}: {what}");

    private static string Str(IReadOnlyList<JsonElement> args, int i, string what)
    {
        var a = Arg(args, i, what);
        return a.ValueKind == JsonValueKind.String ? a.GetString()! : a.GetRawText();
    }

    private static double Num(IReadOnlyList<JsonElement> args, int i, string what)
    {
        var a = Arg(args, i, what);
        if (a.ValueKind == JsonValueKind.Number) return a.GetDouble();
        if (a.ValueKind == JsonValueKind.String && double.TryParse(a.GetString(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var d)) return d;
        throw new ArgumentException($"argument {i + 1} ({what}) must be a number, not {a.GetRawText()}");
    }

    private static RValue[] Rest(JsonElement[] args, int from) =>
        args.Skip(from).Select(ToRValue).ToArray();

    // ----------------------------------------------------------- results

    // GML arrays can contain themselves, and object graphs can too; past this
    // depth a value is cut off rather than followed.
    private const int MaxDepth = 8;
    private const int MaxItems = 10_000;

    private static JsonNode? ToNode(object? value, int depth)
    {
        if (depth > MaxDepth) return JsonValue.Create("...");
        switch (value)
        {
            case null: return null;
            case JsonNode n: return n.Parent == null ? n : n.DeepClone();
            case JsonElement e: return JsonNode.Parse(e.GetRawText());
            case RValue v: return FromRValue(v, depth);
            case InstanceRef r: return FromRValue(r.Id, depth);
            // A raw pointer, shown as one: its properties would read the game
            // through a pointer that may no longer be an instance.
            case Instance inst: return JsonValue.Create($"0x{inst.Pointer:X}");
            // Its parent by name only: walking Parent would dump the whole chain.
            case GmlObject go:
                return new JsonObject
                {
                    ["Index"] = go.Index,
                    ["Name"] = go.Name,
                    ["Parent"] = go.Parent?.Name,
                    ["InstanceCount"] = go.InstanceCount,
                };
            case string s: return JsonValue.Create(s);
            case bool b: return JsonValue.Create(b);
            case char c: return JsonValue.Create(c.ToString());
            case Enum en: return JsonValue.Create(en.ToString());
            case double or float or decimal: return Number(Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture));
            case sbyte or byte or short or ushort or int or uint or long:
                return JsonValue.Create(Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture));
            case ulong ul: return JsonValue.Create(ul);
            case IDictionary dict:
            {
                var o = new JsonObject();
                foreach (DictionaryEntry kv in dict) o[kv.Key.ToString() ?? ""] = ToNode(kv.Value, depth + 1);
                return o;
            }
            case IEnumerable seq:
            {
                var a = new JsonArray();
                foreach (var item in seq)
                {
                    if (a.Count >= MaxItems) { a.Add(JsonValue.Create("...")); break; }
                    a.Add(ToNode(item, depth + 1));
                }
                return a;
            }
        }

        // Records, tuples and anonymous objects: their public properties and
        // fields, each converted the same way (so an RValue inside one is read
        // as a value, not dumped as its raw bits).
        var type = value.GetType();
        var obj = new JsonObject();
        foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            if (p.GetIndexParameters().Length == 0 && p.CanRead) obj[p.Name] = ToNode(p.GetValue(value), depth + 1);
        foreach (var f in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
            obj[f.Name] = ToNode(f.GetValue(value), depth + 1);
        return obj;
    }

    // JSON has no NaN or infinity; they are sent as their names.
    private static JsonNode Number(double d) =>
        double.IsFinite(d) ? JsonValue.Create(d) : JsonValue.Create(d.ToString(System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>
    /// Numbers, strings, booleans, undefined (null) and arrays as themselves.
    /// A reference (an instance or asset on 2024+ runtimes) as the number in its
    /// low 32 bits, which the runtime accepts back as an id. Anything else as
    /// its GML string.
    /// </summary>
    private static JsonNode? FromRValue(RValue v, int depth)
    {
        switch (v.Kind)
        {
            case RValueKind.Undefined or RValueKind.Unset: return null;
            case RValueKind.Bool: return JsonValue.Create(v.AsBool);
            case RValueKind.String: return JsonValue.Create(v.ToString());
            case RValueKind.Reference: return JsonValue.Create(v.Int64 & 0xFFFFFFFF);
            case RValueKind.Array when depth < MaxDepth:
            {
                int n = Gml.ArrayLength(v);
                var a = new JsonArray();
                for (int i = 0; i < Math.Min(n, MaxItems); i++) a.Add(FromRValue(Gml.ArrayGet(v, i), depth + 1));
                if (n > MaxItems) a.Add(JsonValue.Create("..."));
                return a;
            }
        }
        return v.IsNumber ? Number(v.AsReal) : JsonValue.Create(v.ToString());
    }
}
