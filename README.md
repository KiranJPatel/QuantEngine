# QuantEngine v5.0

**Full-Stack Production Trading Platform** — HMA × Supertrend × ADX | Zerodha + Upstox Live Trading

## Prerequisites

| Tool | Minimum Version |
|------|----------------|
| .NET SDK | 8.0 (pinned via `global.json`) |
| OS | Windows 10+, Ubuntu 22.04+, macOS 13+ |
| (Optional) dotnet-counters | For live metrics monitoring |

## Quick Start

```bash
# 1. Clone or unzip the solution
cd QuantEngineV5

# 2. Restore packages
dotnet restore

# 3. Build (all 9 projects)
dotnet build -c Release

# 4. Run unit tests (35+ tests, ~5 seconds)
dotnet test

# 5. Run a backtest (Yahoo data auto-fetched; first run seeds .quant_cache/)
dotnet run --project src/QuantEngine.Host -c Release
```

## Modes

```bash
# Backtest (default — reads AppMode from appsettings.json)
dotnet run --project src/QuantEngine.Host -- --mode Backtest

# Grid optimization
dotnet run --project src/QuantEngine.Host -- --mode Optimize

# Walk-forward validation (set Optimization.EnableWalkForward=true)
dotnet run --project src/QuantEngine.Host -- --mode Optimize

# Paper trading (daily watchlist + persistent JSON state)
dotnet run --project src/QuantEngine.Host -- --mode PaperTrade

# Live trading — Zerodha
dotnet run --project src/QuantEngine.Host -- --mode LiveTrade --broker Zerodha

# Live trading — Upstox
dotnet run --project src/QuantEngine.Host -- --mode LiveTrade --broker Upstox

# Health check only (DB + broker connectivity)
dotnet run --project src/QuantEngine.Host -- --health

# Generate broker access token (required daily)
dotnet run --project src/QuantEngine.Host -- --auth --broker Zerodha
dotnet run --project src/QuantEngine.Host -- --auth --broker Upstox
```

## Configuration

All settings live in `src/QuantEngine.Host/appsettings.json`.  
Production overrides go in `appsettings.Production.json`.  
Every key can be overridden via environment variable with the `QE_` prefix:

```bash
export QE_Brokers__Zerodha__AccessToken="your_daily_token"
export QE_Data__BenchmarkSymbol="^NSEI"
export DOTNET_ENVIRONMENT=Production
```

### Broker Setup (one-time + daily)

**Zerodha:**
1. Create a Kite Connect app at https://developers.kite.trade
2. Set `Brokers.Zerodha.ApiKey` and `Brokers.Zerodha.ApiSecret`
3. Run `dotnet run -- --auth --broker Zerodha` daily to refresh `AccessToken`

**Upstox:**
1. Create an app at https://account.upstox.com/developer/apps
2. Set `Brokers.Upstox.ApiKey`, `Brokers.Upstox.ApiSecret`, and `Brokers.Upstox.RedirectUri`
3. Run `dotnet run -- --auth --broker Upstox` daily to refresh `AccessToken`

### Universe

Edit `universe.json` (NSE symbols, no `.NS` suffix — the engine adds it for Yahoo Finance):
```json
["RELIANCE", "TCS", "INFY", "HDFCBANK", "ICICIBANK"]
```

## Strategy Logic (immutable)

| Component | Formula |
|-----------|---------|
| Entry signal | HmaFast > HmaSlow AND SuperTrendDir = +1 AND ADX > threshold |
| Regime | BullTrending = benchmark HmaFast > HmaSlow AND benchmark ADX > threshold |
| Stop loss | Entry − StopLossAtrMultiple × ATR |
| Take profit | Entry + TakeProfitAtrMultiple × ATR |
| Position size | Equity × AccountRiskPerTradePct ÷ (Entry − StopLoss) |
| Exit order | TakeProfit → StopLoss → TrailingStop → HMA TrendReversal |
| Alpha score | Clamp(ADX × 1.5 + Clamp((HmaFast−HmaSlow)/HmaSlow × 1000, 0, 40), 0, 100) |

## Observability

```bash
# Live metrics (requires dotnet-counters)
dotnet tool install -g dotnet-counters
dotnet-counters monitor --name QuantEngine --counters QuantEngine

# Logs (structured JSON in production, coloured in dev)
tail -f logs/quant_*.log | jq .

# Audit trail (every live order event)
cat reports/audit_YYYYMMDD_*.csv
```

## Running Tests

```bash
# All tests
dotnet test

# Only unit tests tagged for CI (excludes integration tests)
dotnet test --filter "Category!=Integration"

# With coverage (requires coverlet)
dotnet test --collect:"XPlat Code Coverage"
```

## Project Structure

```
QuantEngine.sln
├── src/
│   ├── QuantEngine.Domain          # Entities, Enums, ValueObjects, Options, Interfaces
│   ├── QuantEngine.Indicators      # Skender wrapper; IndicatorEngine.Build()
│   ├── QuantEngine.Strategy        # RegimeEngine, AlphaScorer (strategy logic)
│   ├── QuantEngine.Risk            # LiveRiskManager (pre-trade risk controls)
│   ├── QuantEngine.Backtesting     # PortfolioBacktester, GridOptimizer, WalkForward
│   ├── QuantEngine.Infrastructure  # Yahoo, CSV, Zerodha, Upstox, SQLite, Feeds
│   ├── QuantEngine.Trading         # LiveTradingEngine, OMS, PaperTradingEngine
│   ├── QuantEngine.Reporting       # CSV exports, console dashboards
│   └── QuantEngine.Host            # Program.cs, DI, appsettings.json
└── tests/
    └── QuantEngine.Tests.Unit      # 35+ xUnit tests; BacktesterIntegrityTests
```
