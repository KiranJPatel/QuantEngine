using FluentAssertions;
using QuantEngine.Domain.Enums;
using QuantEngine.Domain.Options;
using QuantEngine.Host;
using Xunit;

namespace QuantEngine.Tests.Unit.Backtesting;

public sealed class StartupValidatorTests
{
    // Helpers
    private static DataOptions       Data()    => new() { Start = new(2020,1,1), End = new(2024,1,1) };
    private static IndicatorsOptions Ind()     => new();
    private static StrategyOptions   Strat()   => new();
    private static RiskOptions       Risk()    => new();
    private static BacktestOptions   Bt()      => new();
    private static OptimizationOptions Opt()   => new();
    private static LiveTradingOptions Live()   => new();
    private static BrokersOptions     Brk()    => new();

    [Fact]
    public void Validate_Passes_For_Valid_Defaults()
    {
        var act = () => StartupValidator.Validate(
            Data(), Ind(), Strat(), Risk(), Bt(), Opt(), Live(), Brk(), AppMode.Backtest);
        act.Should().NotThrow("default values are valid");
    }

    [Fact]
    public void Validate_Fails_When_Start_After_End()
    {
        var data = new DataOptions { Start = new(2025, 1, 1), End = new(2020, 1, 1) };
        var act  = () => StartupValidator.Validate(
            data, Ind(), Strat(), Risk(), Bt(), Opt(), Live(), Brk(), AppMode.Backtest);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Start*End*");
    }

    [Fact]
    public void Validate_Fails_When_HmaFast_GTE_HmaSlow()
    {
        var ind = new IndicatorsOptions { HmaFast = 50, HmaSlow = 20 };
        var act = () => StartupValidator.Validate(
            Data(), ind, Strat(), Risk(), Bt(), Opt(), Live(), Brk(), AppMode.Backtest);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*HmaFast*HmaSlow*");
    }

    [Fact]
    public void Validate_Fails_When_TakeProfit_LTE_StopLoss()
    {
        var strat = new StrategyOptions { TakeProfitAtrMultiple = 1.0, StopLossAtrMultiple = 2.0 };
        var act   = () => StartupValidator.Validate(
            Data(), Ind(), strat, Risk(), Bt(), Opt(), Live(), Brk(), AppMode.Backtest);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*TakeProfitAtrMultiple*StopLossAtrMultiple*");
    }

    [Fact]
    public void Validate_Fails_LiveTrade_Without_Zerodha_ApiKey()
    {
        var brk = new BrokersOptions
        {
            ActiveBroker = BrokerType.Zerodha,
            Zerodha      = new ZerodhaOptions { ApiKey = "" }
        };
        var act = () => StartupValidator.Validate(
            Data(), Ind(), Strat(), Risk(), Bt(), Opt(), Live(), brk, AppMode.LiveTrade);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*ApiKey*");
    }

    [Fact]
    public void Validate_Fails_LiveTrade_Without_Upstox_ApiKey()
    {
        var brk = new BrokersOptions
        {
            ActiveBroker = BrokerType.Upstox,
            Upstox       = new UpstoxOptions { ApiKey = "" }
        };
        var act = () => StartupValidator.Validate(
            Data(), Ind(), Strat(), Risk(), Bt(), Opt(), Live(), brk, AppMode.LiveTrade);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*ApiKey*");
    }

    [Fact]
    public void Validate_Fails_When_InitialCapital_Is_Zero()
    {
        var bt  = new BacktestOptions { InitialCapital = 0 };
        var act = () => StartupValidator.Validate(
            Data(), Ind(), Strat(), Risk(), bt, Opt(), Live(), Brk(), AppMode.Backtest);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*InitialCapital*");
    }
}
