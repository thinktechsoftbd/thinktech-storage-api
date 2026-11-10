using System.Buffers.Text;
using System.Security.Cryptography;
using ThinkTech.Storage.Api.Authentication;
using ThinkTech.Storage.Api.Storage;

namespace ThinkTech.Storage.Api.Tools;

/// <summary>
/// Admin commands built into the API executable, so the server needs no extra tools:
/// <code>
///   ThinkTech.Storage.Api generate-client bizone-erp
///   ThinkTech.Storage.Api generate-signing-key
/// </code>
/// </summary>
public static class CommandLineTools
{
    public static bool TryRun(string[] args, out int exitCode)
    {
        exitCode = 0;
        if (args.Length == 0)
        {
            return false;
        }

        switch (args[0])
        {
            case "generate-client":
                exitCode = GenerateClient(args.Skip(1).ToArray());
                return true;

            case "generate-signing-key":
                Console.WriteLine(Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(64)));
                return true;

            case "help" or "--help" or "-h":
                PrintHelp();
                return true;

            default:
                return false; // Not a tool command: start the web API (e.g. --urls is handled by ASP.NET Core).
        }
    }

    private static int GenerateClient(string[] args)
    {
        if (args.Length == 0 || !StorageIdentifiers.ClientId().IsMatch(args[0]))
        {
            Console.Error.WriteLine("Usage: generate-client <client-id>");
            Console.Error.WriteLine("  client-id: 3-64 characters of lowercase letters, digits, '-' or '_' (e.g. bizone-erp).");
            return 1;
        }

        var clientId = args[0];
        var secret = ClientSecretHasher.GenerateSecret();
        var hash = ClientSecretHasher.Hash(secret);

        Console.WriteLine($$"""

            Client ID     : {{clientId}}
            Client secret : {{secret}}

            Give the client ID and secret to the calling application (store it in its user-secrets,
            environment variables or a vault). The secret is shown only once and is not stored anywhere.

            Add this entry to "StorageClients": { "Clients": [ ... ] } in the server settings file:

                {
                  "ClientId": "{{clientId}}",
                  "DisplayName": "{{clientId}}",
                  "SecretHash": "{{hash}}",
                  "Scopes": [ "{{StorageScopes.Read}}", "{{StorageScopes.Write}}", "{{StorageScopes.Delete}}" ],
                  "Enabled": true
                }

            """);
        return 0;
    }

    private static void PrintHelp() => Console.WriteLine("""
        ThinkTech Storage API

        Run without arguments to start the web API.

        Admin commands:
          generate-client <client-id>   Create a client secret and print the hash to put in the settings file.
          generate-signing-key          Print a random key for Jwt:SigningKey.
        """);
}
