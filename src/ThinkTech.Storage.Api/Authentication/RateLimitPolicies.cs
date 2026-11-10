namespace ThinkTech.Storage.Api.Authentication;

public static class RateLimitPolicies
{
    /// <summary>Per-IP limit on the token endpoint to slow down secret guessing.</summary>
    public const string Token = "token";

    public const int TokenRequestsPerMinute = 20;
}
