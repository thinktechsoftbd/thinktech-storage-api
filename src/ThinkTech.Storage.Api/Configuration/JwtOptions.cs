using System.ComponentModel.DataAnnotations;

namespace ThinkTech.Storage.Api.Configuration;

/// <summary>Settings for the access tokens issued by <c>POST /api/v1/auth/token</c>.</summary>
public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    [Required]
    public string Issuer { get; set; } = "thinktech-storage-api";

    [Required]
    public string Audience { get; set; } = "thinktech-storage-clients";

    /// <summary>
    /// HMAC-SHA256 signing key. Generate one with <c>ThinkTech.Storage.Api generate-signing-key</c>
    /// and keep it out of source control.
    /// </summary>
    [Required(ErrorMessage = "Jwt:SigningKey is required. Generate one with 'generate-signing-key' and put it in the settings file named by THINKTECH_STORAGE_SETTINGS.")]
    [MinLength(32, ErrorMessage = "Jwt:SigningKey must be at least 32 characters long.")]
    public string SigningKey { get; set; } = string.Empty;

    [Range(1, 1440)]
    public int AccessTokenMinutes { get; set; } = 60;
}
