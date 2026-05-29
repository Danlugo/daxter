namespace Daxter.Core.Connection;

/// <summary>Creates an open <see cref="IXmlaSession"/> (acquires a token and connects).</summary>
public interface IXmlaSessionFactory
{
    Task<IXmlaSession> CreateAsync(CancellationToken cancellationToken = default);
}
