using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace QuantEngine.Infrastructure.Persistence;

public sealed class DatabaseManager
{
    private readonly string _connStr;
    private readonly ILogger<DatabaseManager> _log;

    public DatabaseManager(string dbPath, ILogger<DatabaseManager> log)
    {
        if (string.IsNullOrWhiteSpace(dbPath))
            throw new ArgumentException("DB path cannot be empty", nameof(dbPath));
        _connStr = $"Data Source={dbPath}";
        _log     = log;
    }

    public SqliteConnection CreateConnection() => new(_connStr);

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await using var conn = CreateConnection();
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd  = conn.CreateCommand();
        cmd.CommandText = @"
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous  = NORMAL;

            CREATE TABLE IF NOT EXISTS BacktestRuns (
                RunId        TEXT PRIMARY KEY,
                ConfigJson   TEXT,
                StartDate    TEXT,
                EndDate      TEXT,
                FinalEquity  REAL,
                CAGR         REAL,
                Sharpe       REAL,
                Sortino      REAL,
                Calmar       REAL,
                MaxDrawdown  REAL,
                WinRate      REAL,
                ProfitFactor REAL,
                TotalTrades  INTEGER,
                CreatedAt    TEXT
            );

            CREATE TABLE IF NOT EXISTS Trades (
                TradeId    INTEGER PRIMARY KEY AUTOINCREMENT,
                RunId      TEXT    NOT NULL,
                Symbol     TEXT,
                EntryDate  TEXT,
                ExitDate   TEXT,
                EntryPrice REAL,
                ExitPrice  REAL,
                Quantity   INTEGER,
                NetPnl     REAL,
                ExitReason TEXT,
                FOREIGN KEY (RunId) REFERENCES BacktestRuns(RunId)
            );

            CREATE INDEX IF NOT EXISTS idx_trades_run ON Trades(RunId);
            CREATE INDEX IF NOT EXISTS idx_runs_date  ON BacktestRuns(CreatedAt);";

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        _log.LogInformation("[DB] Schema initialised at {CS}", _connStr);
    }
}
