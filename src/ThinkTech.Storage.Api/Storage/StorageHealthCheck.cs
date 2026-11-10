using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace ThinkTech.Storage.Api.Storage;

/// <summary>Reports unhealthy when the storage folder cannot be written (disk full, wrong permissions, missing mount).</summary>
public sealed class StorageHealthCheck(LocalFileStorage storage) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var probePath = Path.Combine(storage.RootPath, $".health-{Guid.NewGuid():N}");

        try
        {
            await File.WriteAllTextAsync(probePath, "ok", cancellationToken);
            File.Delete(probePath);
            return HealthCheckResult.Healthy("Storage is writable.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return HealthCheckResult.Unhealthy("Storage is not writable.", ex);
        }
    }
}
