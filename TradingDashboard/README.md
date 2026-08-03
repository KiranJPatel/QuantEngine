# QuantEngine Trading Intelligence Dashboard

A fully self-contained HTML trading dashboard — **no server, no build step, no dependencies to install**.
Open `index.html` directly in any modern browser.

## Features

| Tab | Content |
|-----|---------|
| **Overview** | 8 KPI cards · Equity curve vs SPY · Monthly returns heatmap · Drawdown profile |
| **Trade Analysis** | Win/Loss donut · Exit reason pie · Symbol P&L bars · Sortable/filterable trade log with CSV export |
| **Risk Dashboard** | Sharpe/Sortino/Calmar gauges · Drawdown chart · P&L scatter by exit type · Avg win vs loss bars |
| **Run Comparison** | Full metrics comparison table (best/worst highlighted) · Side-by-side bar chart · Multi-run equity curves |
| **Recommendations** | 7 prioritised evidence-based actions with confidence ratings |

## Interactivity

- **Dark / Light theme** — toggle via ☀ button (top-right)
- **Run selector** — switch between Conservative / Base / Fast parameter sets
- **Trade filters** — by exit reason, symbol, P&L sign, or text search
- **Sort** — click any column header in the trade table (toggle asc/desc)
- **Export CSV** — downloads the filtered trade log as `quantengine_trades.csv`
- **IST clock** — live Indian Standard Time in the header

## How to Connect to a Real SQLite Database

Replace the embedded `const DATA = {...}` block in `index.html` with a fetch call:

```javascript
const DATA = await fetch('/api/dashboard-data').then(r => r.json());
```

Then add a minimal ASP.NET endpoint in `QuantEngine.Host/Program.cs`:

```csharp
app.MapGet("/api/dashboard-data", async (DatabaseManager db, CancellationToken ct) => {
    await using var conn = db.CreateConnection();
    await conn.OpenAsync(ct);
    // Query BacktestRuns + Trades, return as JSON
    return Results.Json(data);
});
```

## Data Schema (from QuantEngine SQLite)

```sql
-- BacktestRuns
RunId, ConfigJson, StartDate, EndDate, FinalEquity, CAGR,
Sharpe, Sortino, Calmar, MaxDrawdown, WinRate, ProfitFactor, TotalTrades

-- Trades
TradeId, RunId, Symbol, EntryDate, ExitDate,
EntryPrice, ExitPrice, Quantity, NetPnl, ExitReason
```

## Browser Requirements

Chrome 90+, Firefox 88+, Safari 14+, Edge 90+ — any browser with ES2020 support.
Chart.js 4.4.1 is loaded from cdnjs.cloudflare.com (requires internet).
For offline use, download chart.umd.min.js and replace the CDN link.
