using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using QuantEngine.Domain.Entities;
using QuantEngine.Domain.Interfaces;
using QuantEngine.Domain.ValueObjects;

namespace QuantEngine.Infrastructure.Persistence;

public sealed class BacktestRepository : IBacktestRepository
{
    private readonly DatabaseManager _db;
    private readonly ILogger<BacktestRepository> _log;

    public BacktestRepository(DatabaseManager db, ILogger<BacktestRepository> log)
    {
        _db  = db  ?? throw new ArgumentNullException(nameof(db));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public async Task SaveRunAsync(
        string runId, string configJson,
        IReadOnlyList<Trade> trades, PerformanceMetrics m,
        DateTime start, DateTime end, CancellationToken ct = default)
    {
        await using var conn = _db.CreateConnection();
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var txn  = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            await using var rc = conn.CreateCommand();
            rc.Transaction  = txn;
            rc.CommandText  =
                "INSERT INTO BacktestRuns " +
                "(RunId,ConfigJson,StartDate,EndDate,FinalEquity,CAGR,Sharpe,Sortino," +
                " Calmar,MaxDrawdown,WinRate,ProfitFactor,TotalTrades,CreatedAt) " +
                "VALUES (@id,@cfg,@s,@e,@eq,@cagr,@sh,@so,@ca,@dd,@wr,@pf,@tt,@now)";
            rc.Parameters.AddWithValue("@id",   runId);
            rc.Parameters.AddWithValue("@cfg",  configJson);
            rc.Parameters.AddWithValue("@s",    start.ToString("O"));
            rc.Parameters.AddWithValue("@e",    end.ToString("O"));
            rc.Parameters.AddWithValue("@eq",   m.FinalEquity);
            rc.Parameters.AddWithValue("@cagr", m.CAGR);
            rc.Parameters.AddWithValue("@sh",   m.SharpeRatio);
            rc.Parameters.AddWithValue("@so",   m.SortinoRatio);
            rc.Parameters.AddWithValue("@ca",   m.CalmarRatio);
            rc.Parameters.AddWithValue("@dd",   m.MaxDrawdownPct);
            rc.Parameters.AddWithValue("@wr",   m.WinRate);
            rc.Parameters.AddWithValue("@pf",   m.ProfitFactor);
            rc.Parameters.AddWithValue("@tt",   m.TotalTrades);
            rc.Parameters.AddWithValue("@now",  DateTime.UtcNow.ToString("O"));
            await rc.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            if (trades.Count > 0)
            {
                await using var tc = conn.CreateCommand();
                tc.Transaction = txn;
                tc.CommandText =
                    "INSERT INTO Trades " +
                    "(RunId,Symbol,EntryDate,ExitDate,EntryPrice,ExitPrice,Quantity,NetPnl,ExitReason) " +
                    "VALUES (@r,@s,@en,@ex,@ep,@xp,@q,@pnl,@rsn)";
                var pR=tc.Parameters.Add("@r",SqliteType.Text); var pS=tc.Parameters.Add("@s",SqliteType.Text);
                var pE=tc.Parameters.Add("@en",SqliteType.Text); var pX=tc.Parameters.Add("@ex",SqliteType.Text);
                var pEp=tc.Parameters.Add("@ep",SqliteType.Real); var pXp=tc.Parameters.Add("@xp",SqliteType.Real);
                var pQ=tc.Parameters.Add("@q",SqliteType.Integer); var pN=tc.Parameters.Add("@pnl",SqliteType.Real);
                var pRn=tc.Parameters.Add("@rsn",SqliteType.Text);
                await tc.PrepareAsync(ct).ConfigureAwait(false);
                foreach (var t in trades)
                {
                    pR.Value=runId; pS.Value=t.Symbol; pE.Value=t.EntryDate.ToString("O");
                    pX.Value=t.ExitDate.ToString("O"); pEp.Value=t.EntryPrice; pXp.Value=t.ExitPrice;
                    pQ.Value=t.Quantity; pN.Value=t.NetPnl; pRn.Value=t.Reason.ToString();
                    await tc.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }
            }
            await txn.CommitAsync(ct).ConfigureAwait(false);
            _log.LogInformation("[DB] Run {Id} saved ({N} trades)", runId[..8], trades.Count);
        }
        catch { await txn.RollbackAsync(ct).ConfigureAwait(false); throw; }
    }
}
