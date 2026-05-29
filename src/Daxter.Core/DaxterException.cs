namespace Daxter.Core;

/// <summary>
/// Raised for user-actionable failures (bad configuration, connection or query
/// errors). The CLI catches these and prints the message without a stack trace.
/// </summary>
public sealed class DaxterException : Exception
{
    public DaxterException(string message) : base(message) { }

    public DaxterException(string message, Exception innerException)
        : base(message, innerException) { }
}
