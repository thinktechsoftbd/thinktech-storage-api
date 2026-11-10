using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using ThinkTech.Storage.Api.Configuration;

namespace ThinkTech.Storage.Api.Authentication;

public sealed record AccessToken(string Token, int ExpiresInSeconds, string Scope);

/// <summary>Issues short-lived, HMAC-signed JWT access tokens for authenticated clients.</summary>
public sealed class TokenService(IOptionsMonitor<JwtOptions> jwtOptions, TimeProvider timeProvider)
{
    private readonly JsonWebTokenHandler _handler = new();

    public AccessToken CreateToken(StorageClientOptions client)
    {
        var options = jwtOptions.CurrentValue;
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var lifetime = TimeSpan.FromMinutes(options.AccessTokenMinutes);
        var scope = string.Join(' ', client.Scopes.Distinct(StringComparer.Ordinal));

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = options.Issuer,
            Audience = options.Audience,
            IssuedAt = now,
            NotBefore = now,
            Expires = now.Add(lifetime),
            SigningCredentials = new SigningCredentials(JwtKeys.CreateSigningKey(options.SigningKey), SecurityAlgorithms.HmacSha256),
            Claims = new Dictionary<string, object>
            {
                [JwtRegisteredClaimNames.Sub] = client.ClientId,
                [JwtRegisteredClaimNames.Jti] = Guid.NewGuid().ToString("N"),
                [StorageClaimTypes.ClientId] = client.ClientId,
                [StorageClaimTypes.Scope] = scope,
            },
        };

        return new AccessToken(_handler.CreateToken(descriptor), (int)lifetime.TotalSeconds, scope);
    }
}

public static class JwtKeys
{
    public static SymmetricSecurityKey CreateSigningKey(string signingKey) => new(Encoding.UTF8.GetBytes(signingKey));
}
