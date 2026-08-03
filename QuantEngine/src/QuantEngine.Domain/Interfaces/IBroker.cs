using QuantEngine.Domain.Entities;
using QuantEngine.Domain.Enums;
using QuantEngine.Domain.ValueObjects;
namespace QuantEngine.Domain.Interfaces;

public interface IBroker : IDisposable
{
    BrokerType BrokerType { get; }
    Task<bool>                          AuthenticateAsync(CancellationToken ct);
    Task<string>                        GenerateAccessTokenAsync(CancellationToken ct);
    Task<BrokerOrder>                   PlaceOrderAsync(OrderRequest req, CancellationToken ct);
    Task<bool>                          CancelOrderAsync(string orderId, CancellationToken ct);
    Task<BrokerOrder>                   GetOrderStatusAsync(string orderId, CancellationToken ct);
    Task<IReadOnlyList<BrokerOrder>>    GetOpenOrdersAsync(CancellationToken ct);
    Task<IReadOnlyList<BrokerPosition>> GetPositionsAsync(CancellationToken ct);
    Task<BrokerFunds>                   GetFundsAsync(CancellationToken ct);
    Task<Dictionary<string, double>>    GetLtpAsync(IEnumerable<string> symbols, CancellationToken ct);
}
