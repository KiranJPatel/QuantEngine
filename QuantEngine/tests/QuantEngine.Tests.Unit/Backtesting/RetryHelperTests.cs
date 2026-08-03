using FluentAssertions;
using QuantEngine.Infrastructure.Utilities;
using Xunit;

namespace QuantEngine.Tests.Unit.Backtesting;

public sealed class RetryHelperTests
{
    [Fact]
    public async Task ExecuteAsync_Returns_On_First_Success()
    {
        int calls = 0;
        int result = await RetryHelper.ExecuteAsync(
            async (_, _) => { calls++; await Task.Yield(); return 42; },
            maxAttempts: 4);
        result.Should().Be(42);
        calls.Should().Be(1, "no retries needed when first attempt succeeds");
    }

    [Fact]
    public async Task ExecuteAsync_Retries_On_Transient_Failure()
    {
        int calls = 0;
        int result = await RetryHelper.ExecuteAsync(
            async (attempt, _) =>
            {
                calls++;
                await Task.Yield();
                if (attempt < 3) throw new HttpRequestException("transient");
                return 99;
            },
            maxAttempts:  4,
            baseDelay:    TimeSpan.FromMilliseconds(1)); // fast for tests
        result.Should().Be(99);
        calls.Should().Be(3, "should retry twice then succeed on attempt 3");
    }

    [Fact]
    public async Task ExecuteAsync_Throws_After_All_Attempts_Fail()
    {
        int calls = 0;
        var act = async () => await RetryHelper.ExecuteAsync(
            async (_, _) => { calls++; await Task.Yield(); throw new HttpRequestException("always fail"); return 0; },
            maxAttempts: 3,
            baseDelay:   TimeSpan.FromMilliseconds(1));
        await act.Should().ThrowAsync<HttpRequestException>();
        calls.Should().Be(3, "all 3 attempts should be made before giving up");
    }

    [Fact]
    public async Task ExecuteAsync_Does_Not_Retry_NonTransient_Exceptions()
    {
        int calls = 0;
        var act = async () => await RetryHelper.ExecuteAsync(
            async (_, _) => { calls++; await Task.Yield(); throw new InvalidOperationException("fatal"); return 0; },
            maxAttempts:  4,
            baseDelay:    TimeSpan.FromMilliseconds(1),
            isTransient:  ex => ex is HttpRequestException);  // only HttpRequestException is transient
        await act.Should().ThrowAsync<InvalidOperationException>();
        calls.Should().Be(1, "non-transient exception must not be retried");
    }

    [Fact]
    public async Task ExecuteAsync_Respects_CancellationToken()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var act = async () => await RetryHelper.ExecuteAsync(
            async (_, c) => { await Task.Delay(1000, c); return 0; },
            ct: cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
