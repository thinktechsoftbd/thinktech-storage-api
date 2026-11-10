using Microsoft.Extensions.Options;
using ThinkTech.Storage.Api.Configuration;

namespace ThinkTech.Storage.Api.Authentication;

/// <summary>Checks a client id / client secret pair against the configured clients.</summary>
public sealed class ClientCredentialValidator(IOptionsMonitor<StorageClientsOptions> options)
{
    // Verifying against a dummy hash for unknown clients keeps response times uniform,
    // so callers cannot discover which client ids exist.
    private static readonly string DummyHash = ClientSecretHasher.Hash(ClientSecretHasher.GenerateSecret());

    public StorageClientOptions? Validate(string? clientId, string? clientSecret)
    {
        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrEmpty(clientSecret))
        {
            return null;
        }

        var client = FindActiveClient(clientId);
        if (client is null)
        {
            _ = ClientSecretHasher.Verify(clientSecret, DummyHash);
            return null;
        }

        return ClientSecretHasher.Verify(clientSecret, client.SecretHash) ? client : null;
    }

    /// <summary>Returns the client if it is still configured and enabled (settings reload without a restart).</summary>
    public StorageClientOptions? FindActiveClient(string clientId) =>
        options.CurrentValue.Clients.FirstOrDefault(c =>
            c.Enabled && string.Equals(c.ClientId, clientId, StringComparison.Ordinal));
}
