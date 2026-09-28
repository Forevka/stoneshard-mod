using CoreLoader.Native;

namespace CoreLoader;

/// <summary>Loader-wide state shared by the runtime and the public API.</summary>
internal static unsafe class Loader
{
    public static CoreApi* Api;

    // The render thread the native overlay runs on is GameMaker's game thread:
    // the only thread GML may be called from. Captured on the first frame.
    private static int _gameThreadId = -1;

    public static bool OnGameThread => Environment.CurrentManagedThreadId == _gameThreadId;

    public static void MarkGameThread() => _gameThreadId = Environment.CurrentManagedThreadId;

    public static void EnsureGameThread()
    {
        if (!OnGameThread)
            throw new InvalidOperationException(
                "GML can only be touched on the game thread - from OnUpdate, OnGUI or a hook. " +
                "Use Game.RunOnGameThread to get there from another thread.");
    }

    public static void Log(LogLevel level, string source, string message)
    {
        if (Api == null) return;
        fixed (byte* s = Utf8.Encode(source))
        fixed (byte* m = Utf8.Encode(message))
            Api->Log((int)level, s, m);
    }
}
