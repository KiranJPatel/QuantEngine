using QuantEngine.Domain.Entities;
namespace QuantEngine.Domain.Interfaces;
public interface IOhlcProvider
{
    Task<OhlcData> GetOhlcAsync(
        string symbol, DateTime start, DateTime end, CancellationToken ct = default);
}
