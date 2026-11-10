using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;

namespace ThinkTech.Storage.Api.Contracts;

/// <summary>JSON body for <c>POST /api/v1/auth/token</c>.</summary>
public sealed class TokenRequest
{
    [Required, MaxLength(64)]
    public string ClientId { get; set; } = string.Empty;

    [Required, MaxLength(512)]
    public string ClientSecret { get; set; } = string.Empty;
}

/// <summary>Standard OAuth 2.0 form body (<c>grant_type=client_credentials</c>), for OAuth client libraries.</summary>
public sealed class OAuthTokenFormRequest
{
    [FromForm(Name = "grant_type")]
    public string? GrantType { get; set; }

    [FromForm(Name = "client_id"), MaxLength(64)]
    public string? ClientId { get; set; }

    [FromForm(Name = "client_secret"), MaxLength(512)]
    public string? ClientSecret { get; set; }
}

public sealed record TokenResponse(
    [property: JsonPropertyName("access_token")] string AccessToken,
    [property: JsonPropertyName("token_type")] string TokenType,
    [property: JsonPropertyName("expires_in")] int ExpiresIn,
    [property: JsonPropertyName("scope")] string Scope);

public sealed record OAuthErrorResponse(
    [property: JsonPropertyName("error")] string Error,
    [property: JsonPropertyName("error_description")] string? ErrorDescription = null);
