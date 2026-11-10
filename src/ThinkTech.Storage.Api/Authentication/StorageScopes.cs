using System.Security.Claims;

namespace ThinkTech.Storage.Api.Authentication;

/// <summary>Permissions a client can be granted in configuration.</summary>
public static class StorageScopes
{
    public const string Read = "storage.read";
    public const string Write = "storage.write";
    public const string Delete = "storage.delete";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal) { Read, Write, Delete };
}

/// <summary>Authorization policy names used on the endpoints.</summary>
public static class StoragePolicies
{
    public const string Read = "Storage.Read";
    public const string Write = "Storage.Write";
    public const string Delete = "Storage.Delete";
}

public static class StorageClaimTypes
{
    public const string ClientId = "client_id";
    public const string Scope = "scope";
}

public static class ClaimsPrincipalExtensions
{
    public static string GetClientId(this ClaimsPrincipal user) =>
        user.FindFirstValue(StorageClaimTypes.ClientId)
        ?? throw new InvalidOperationException("The authenticated principal has no client_id claim.");

    /// <summary>Scopes are carried as one space-delimited claim (RFC 9068 style).</summary>
    public static bool HasScope(this ClaimsPrincipal user, string scope) =>
        user.FindAll(StorageClaimTypes.Scope)
            .SelectMany(c => c.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Contains(scope, StringComparer.Ordinal);
}
