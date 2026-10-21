using Wms.Persistence.Abstractions;

namespace Wms.Application.Auth;

public sealed record AccessToken(string Value, DateTimeOffset ExpiresAt);

/// <summary>Creates the short-lived signed access token. Implemented by the host (JWT in Wms.Api).</summary>
public interface IAccessTokenIssuer
{
    AccessToken Issue(UserAccount user, DateTimeOffset now);
}
