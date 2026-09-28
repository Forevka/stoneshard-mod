namespace CoreLoader;

public enum LogLevel
{
    Info = 0,
    Warning = 1,
    Error = 2,
}

/// <summary>Writes to the loader log, tagged with the owner's name.</summary>
public sealed class Logger
{
    public string Source { get; }

    public Logger(string source) => Source = source;

    public void Info(string message) => Loader.Log(LogLevel.Info, Source, message);

    public void Warning(string message) => Loader.Log(LogLevel.Warning, Source, message);

    public void Error(string message) => Loader.Log(LogLevel.Error, Source, message);

    public void Error(string message, Exception ex) =>
        Loader.Log(LogLevel.Error, Source, $"{message}: {ex}");
}
