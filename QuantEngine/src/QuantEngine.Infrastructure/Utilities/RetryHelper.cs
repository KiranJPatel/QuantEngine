using Microsoft.Extensions.Logging;

namespace QuantEngine.Infrastructure.Utilities;

/// <summary>
/// Provides exponential back-off retry for transient I/O failures.
/// Avoids a Polly dependency for cases where the full Polly pipeline is overkill.
/// </summary>
public static class RetryHelper
{
    /// <summary>
    /// Retries <paramref name="operation"/> up to <paramref name="maxAttempts"/> times
    /// with exponential back-off starting at <paramref name="baseDelay"/>.
    /// Returns the result of the first successful attempt.
    /// Throws the last exception if all attempts fail.
    /// </summary>
    public static async Task<T> ExecuteAsync<T>(
        Func<int, CancellationToken, Task<T>> operation,
        int             maxAttempts  = 4,
        TimeSpan?       baseDelay    = null,
        Func<Exception, bool>? isTransient = null,
        ILogger?        log          = null,
        string          operationName = "operation",
        CancellationToken ct         = default)
    {
        var delay   = baseDelay ?? TimeSpan.FromSeconds(2);
        isTransient ??= _ => true;   // treat everything as transient by default

        Exception? lastEx = null;
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                return await operation(attempt, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (isTransient(ex) && attempt < maxAttempts)
            {
                lastEx = ex;
                var backOff = delay * Math.Pow(2, attempt - 1);
                log?.LogWarning(ex,
                    "[Retry] {Op} attempt {A}/{M} failed — retry in {Ms:F0}ms",
                    operationName, attempt, maxAttempts, backOff.TotalMilliseconds);
                await Task.Delay(backOff, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                log?.LogError(ex, "[Retry] {Op} failed (non-transient)", operationName);
                throw;
            }
        }
        log?.LogError(lastEx, "[Retry] {Op} exhausted all {M} attempts", operationName, maxAttempts);
        throw lastEx!;
    }

    /// <summary>Void overload (fire-and-forget style operations).</summary>
    public static Task ExecuteAsync(
        Func<int, CancellationToken, Task> operation,
        int maxAttempts = 4, TimeSpan? baseDelay = null,
        Func<Exception, bool>? isTransient = null,
        ILogger? log = null, string operationName = "operation",
        CancellationToken ct = default) =>
        ExecuteAsync(
            async (a, c) => { await operation(a, c).ConfigureAwait(false); return 0; },
            maxAttempts, baseDelay, isTransient, log, operationName, ct);

    /// <summary>Determines if an HttpRequestException is worth retrying (5xx, timeout).</summary>
    public static bool IsHttpTransient(Exception ex) =>
        ex is HttpRequestException httpEx &&
        httpEx.StatusCode is System.Net.HttpStatusCode.TooManyRequests
                          or System.Net.HttpStatusCode.ServiceUnavailable
                          or System.Net.HttpStatusCode.GatewayTimeout
                          or null;  // null = network-level timeout
}
