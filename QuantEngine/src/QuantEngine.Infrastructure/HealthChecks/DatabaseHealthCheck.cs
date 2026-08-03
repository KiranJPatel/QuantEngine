using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using QuantEngine.Infrastructure.Persistence;

namespace QuantEngine.Infrastructure.HealthChecks;

/// <summary>
/// Verifies SQLite connectivity and that the BacktestRuns table exists.
/// Registered as a named health check "database".
/// </summary>
public sealed class DatabaseHealthCheck : IHealthCheck
{
    private readonly DatabaseManager _db;
    private readonly ILogger<DatabaseHealthCheck> _log;

    public DatabaseHealthCheck(DatabaseManager db, ILogger<DatabaseHealthCheck> log)
    {
        _db  = db;
        _log = log;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken ct = default)
    {
        try
        {
            await using var conn = _db.CreateConnection();
            await conn.OpenAsync(ct).ConfigureAwait(false);
            await using var cmd  = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='BacktestRuns'";
            var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            long tableCount = result is long l ? l : 0;

            if (tableCount == 0)
                return HealthCheckResult.Degraded(
                    "BacktestRuns table missing — run InitializeAsync first");

            return HealthCheckResult.Healthy($"SQLite reachable, schema present");
        }
        catch (SqliteException ex)
        {
            _log.LogError(ex, "[Health] Database check failed");
            return HealthCheckResult.Unhealthy("SQLite connection failed", ex);
        }
    }
}
