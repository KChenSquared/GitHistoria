namespace Historia.Services.Graph;

/// <summary>
/// Thrown when a graph store is invalid, corrupted, or receives an object ID with a mismatched hash size.
/// </summary>
public sealed class StoreInvalidException : InvalidOperationException
{
    public StoreInvalidException() { }
    public StoreInvalidException(string message) : base(message) { }
    public StoreInvalidException(string message, Exception innerException) : base(message, innerException) { }
}
