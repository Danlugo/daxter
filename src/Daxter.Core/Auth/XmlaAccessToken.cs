namespace Daxter.Core.Auth;

/// <summary>An acquired OAuth bearer token plus its expiry, ready to inject into ADOMD.NET.</summary>
public readonly record struct XmlaAccessToken(string Token, DateTimeOffset ExpiresOn)
{
    public bool IsExpired(TimeSpan skew) => DateTimeOffset.UtcNow >= ExpiresOn - skew;
}
