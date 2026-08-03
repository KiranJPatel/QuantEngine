using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using QuantEngine.Domain.Interfaces;
using QuantEngine.Domain.Options;

namespace QuantEngine.Infrastructure.HealthChecks;

/// <summary>
/// Validates broker credentials are non-empty and the broker responds to an auth check.
/// Registered as named health check "broker".
/// Deliberately non-blocking: if broker is temporarily down during trading,
/// the system should continue to report Degraded rather than Unhealthy.
/// </summary>
public sealed class BrokerHealthCheck : IHealthCheck
{
    private readonly IBroker   _broker;
    private readonly BrokersOptions _opts;
    private readonly ILogger<BrokerHealthCheck> _log;

    public BrokerHealthCheck(
        IBroker broker,
        IOptions<BrokersOptions> opts,
        ILogger<BrokerHealthCheck> log)
    {
        _broker = broker;
        _opts   = opts.Value;
        _log    = log;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken ct = default)
    {
        // Credential presence check (fast, synchronous)
        bool hasKey = _opts.ActiveBroker switch
        {
            QuantEngine.Domain.Enums.BrokerType.Zerodha =>
                !string.IsNullOrWhiteSpace(_opts.Zerodha.ApiKey) &&
                !string.IsNullOrWhiteSpace(_opts.Zerodha.AccessToken),
            QuantEngine.Domain.Enums.BrokerType.Upstox =>
                !string.IsNullOrWhiteSpace(_opts.Upstox.ApiKey) &&
                !string.IsNullOrWhiteSpace(_opts.Upstox.AccessToken),
            _ => false
        };

        if (!hasKey)
            return HealthCheckResult.Degraded(
                $"{_opts.ActiveBroker} credentials not configured. Run dotnet run -- --auth");

        // Live connectivity check (async, timeout protected)
        try
        {
            using var cts     = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));
            bool ok = await _broker.AuthenticateAsync(cts.Token).ConfigureAwait(false);
            return ok
                ? HealthCheckResult.Healthy($"{_opts.ActiveBroker} authenticated")
                : HealthCheckResult.Unhealthy(
                    $"{_opts.ActiveBroker} auth failed — regenerate access_token via --auth");
        }
        catch (OperationCanceledException)
        {
            return HealthCheckResult.Degraded($"{_opts.ActiveBroker} auth timed out after 10s");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[Health] Broker check failed");
            return HealthCheckResult.Unhealthy("Broker connectivity error", ex);
        }
    }
}
