namespace CoreLoader;

/// <summary>A call into the game was refused or faulted.</summary>
public sealed class GmlException : Exception
{
    public GmlException(string message) : base(message) { }

    /// <summary>
    /// "<paramref name="what"/> failed: reason", with the reason the loader
    /// recovered for the call that just failed on this thread (the GML error's
    /// message, or the exception it raised).
    /// </summary>
    internal static unsafe GmlException CallFailed(string what)
    {
        var reason = Native.Utf8.Read(Loader.Api->LastGmlError());
        return new GmlException(string.IsNullOrEmpty(reason)
            ? $"{what} failed (see the loader log)"
            : $"{what} failed: {reason}");
    }
}
