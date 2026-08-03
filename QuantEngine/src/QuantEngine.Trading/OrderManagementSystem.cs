using Microsoft.Extensions.Logging;
using QuantEngine.Domain.Entities;
using QuantEngine.Domain.Enums;
using QuantEngine.Domain.Interfaces;
using QuantEngine.Domain.ValueObjects;
using QuantEngine.Infrastructure.Audit;
using QuantEngine.Risk;

namespace QuantEngine.Trading;

/// <summary>
/// Pre-trade risk gate + place + poll-until-filled + audit trail.
/// </summary>
public sealed class OrderManagementSystem
{
    private readonly IBroker        _broker;
    private readonly LiveRiskManager _risk;
    private readonly AuditLogger    _audit;
    private readonly string         _runId;
    private readonly int            _timeoutSec;
    private readonly ILogger<OrderManagementSystem> _log;

    public OrderManagementSystem(IBroker broker, LiveRiskManager risk,
        AuditLogger audit, string runId, int timeoutSec,
        ILogger<OrderManagementSystem> log)
    {
        _broker     = broker; _risk = risk; _audit = audit;
        _runId      = runId;  _timeoutSec = timeoutSec; _log = log;
    }

    public async Task<BrokerOrder> SubmitAsync(OrderRequest req, CancellationToken ct)
    {
        var rejection = _risk.CheckOrderRisk(req);
        if (rejection is not null)
        {
            _log.LogWarning("[OMS] Rejected ({R}): {Side} {Qty} {Sym}",
                rejection, req.Side, req.Quantity, req.Symbol);
            return new BrokerOrder
            {
                Symbol = req.Symbol, Side = req.Side, Type = req.Type,
                Quantity = req.Quantity, State = OrderState.Rejected, Reason = rejection
            };
        }

        var order = await _broker.PlaceOrderAsync(req, ct).ConfigureAwait(false);
        await _audit.LogOrderAsync(_runId, _broker.BrokerType.ToString(), "PLACED", order)
            .ConfigureAwait(false);
        if (order.State == OrderState.Rejected) return order;

        var deadline = DateTime.UtcNow.AddSeconds(_timeoutSec);
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            await Task.Delay(2000, ct).ConfigureAwait(false);
            order = await _broker.GetOrderStatusAsync(order.OrderId, ct).ConfigureAwait(false);
            if (order.State is OrderState.Filled or OrderState.Rejected or OrderState.Cancelled)
                break;
        }

        await _audit.LogOrderAsync(_runId, _broker.BrokerType.ToString(), "FINAL", order)
            .ConfigureAwait(false);
        if (order.State != OrderState.Filled)
            _log.LogWarning("[OMS] {Id} not filled in {S}s — state: {St}",
                order.OrderId, _timeoutSec, order.State);
        return order;
    }

    public async Task CancelAllAsync(CancellationToken ct)
    {
        var open = await _broker.GetOpenOrdersAsync(ct).ConfigureAwait(false);
        foreach (var o in open)
            await _broker.CancelOrderAsync(o.OrderId, ct).ConfigureAwait(false);
        _log.LogInformation("[OMS] Cancelled {N} open orders", open.Count);
    }
}
