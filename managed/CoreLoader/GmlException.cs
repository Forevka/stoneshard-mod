namespace CoreLoader;

/// <summary>A call into the game was refused or faulted.</summary>
public sealed class GmlException : Exception
{
    public GmlException(string message) : base(message) { }
}
