using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using ThinkTech.Storage.Api.Authentication;
using ThinkTech.Storage.Api.Contracts;

namespace ThinkTech.Storage.Api.Controllers;

/// <summary>Exchanges a client id + client secret for a short-lived bearer token (OAuth 2.0 client credentials).</summary>
[ApiController]
[Route("api/v1/auth")]
[AllowAnonymous]
[EnableRateLimiting(RateLimitPolicies.Token)]
public sealed class AuthController(
    ClientCredentialValidator credentialValidator,
    TokenService tokenService,
    ILogger<AuthController> logger) : ControllerBase
{
    private const int MaxTokenRequestBytes = 16 * 1024;

    [HttpPost("token")]
    [Consumes("application/json")]
    [RequestSizeLimit(MaxTokenRequestBytes)]
    [ProducesResponseType<TokenResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<OAuthErrorResponse>(StatusCodes.Status401Unauthorized)]
    public IActionResult Token([FromBody] TokenRequest request) =>
        IssueToken(request.ClientId, request.ClientSecret);

    [HttpPost("token")]
    [Consumes("application/x-www-form-urlencoded")]
    [RequestSizeLimit(MaxTokenRequestBytes)]
    [ProducesResponseType<TokenResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<OAuthErrorResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<OAuthErrorResponse>(StatusCodes.Status401Unauthorized)]
    public IActionResult TokenForm([FromForm] OAuthTokenFormRequest request)
    {
        if (!string.Equals(request.GrantType, "client_credentials", StringComparison.Ordinal))
        {
            return BadRequest(new OAuthErrorResponse("unsupported_grant_type", "Only the client_credentials grant is supported."));
        }

        return IssueToken(request.ClientId, request.ClientSecret);
    }

    private IActionResult IssueToken(string? clientId, string? clientSecret)
    {
        Response.Headers.CacheControl = "no-store";

        var client = credentialValidator.Validate(clientId, clientSecret);
        if (client is null)
        {
            logger.LogWarning("Rejected token request for client {ClientId} from {RemoteIp}",
                Truncate(clientId), HttpContext.Connection.RemoteIpAddress);
            return Unauthorized(new OAuthErrorResponse("invalid_client", "Client authentication failed."));
        }

        var token = tokenService.CreateToken(client);
        logger.LogInformation("Issued access token to client {ClientId} from {RemoteIp}",
            client.ClientId, HttpContext.Connection.RemoteIpAddress);

        return Ok(new TokenResponse(token.Token, "Bearer", token.ExpiresInSeconds, token.Scope));
    }

    private static string? Truncate(string? value) => value is { Length: > 64 } ? value[..64] : value;
}
