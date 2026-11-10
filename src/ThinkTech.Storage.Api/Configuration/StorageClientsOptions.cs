using Microsoft.Extensions.Options;
using ThinkTech.Storage.Api.Authentication;
using ThinkTech.Storage.Api.Storage;

namespace ThinkTech.Storage.Api.Configuration;

/// <summary>The external applications that may call the API, each with its own client id and secret.</summary>
public sealed class StorageClientsOptions
{
    public const string SectionName = "StorageClients";

    public List<StorageClientOptions> Clients { get; set; } = [];
}

public sealed class StorageClientOptions
{
    /// <summary>Lowercase id, e.g. <c>bizone-erp</c>. Also used as the client's private storage folder name.</summary>
    public string ClientId { get; set; } = string.Empty;

    public string? DisplayName { get; set; }

    /// <summary>PBKDF2 hash of the client secret, produced by <c>generate-client</c>. The plain secret is never stored.</summary>
    public string SecretHash { get; set; } = string.Empty;

    /// <summary>Any of <c>storage.read</c>, <c>storage.write</c>, <c>storage.delete</c>.</summary>
    public List<string> Scopes { get; set; } = [];

    /// <summary>Set to false to block the client immediately, including tokens it already holds.</summary>
    public bool Enabled { get; set; } = true;
}

public sealed class StorageClientsOptionsValidator : IValidateOptions<StorageClientsOptions>
{
    public ValidateOptionsResult Validate(string? name, StorageClientsOptions options)
    {
        var failures = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < options.Clients.Count; i++)
        {
            var client = options.Clients[i];
            var clientId = client.ClientId ?? string.Empty; // binding can leave it null for an empty entry
            var label = $"{StorageClientsOptions.SectionName}:Clients:{i}";

            if (!StorageIdentifiers.ClientId().IsMatch(clientId))
            {
                failures.Add($"{label}:ClientId '{clientId}' must be 3-64 characters of lowercase letters, digits, '-' or '_'.");
            }
            else if (!seen.Add(clientId))
            {
                failures.Add($"{label}:ClientId '{clientId}' is configured more than once.");
            }

            if (!ClientSecretHasher.IsValidHash(client.SecretHash))
            {
                failures.Add($"{label}:SecretHash is missing or invalid. Create one with 'generate-client {clientId}'.");
            }

            if (client.Scopes.Count == 0)
            {
                failures.Add($"{label}:Scopes must contain at least one scope.");
            }

            var unknown = client.Scopes.Where(s => !StorageScopes.All.Contains(s)).ToList();
            if (unknown.Count > 0)
            {
                failures.Add($"{label}:Scopes contains unknown scope(s): {string.Join(", ", unknown)}. Allowed: {string.Join(", ", StorageScopes.All)}.");
            }
        }

        return failures.Count > 0 ? ValidateOptionsResult.Fail(failures) : ValidateOptionsResult.Success;
    }
}
