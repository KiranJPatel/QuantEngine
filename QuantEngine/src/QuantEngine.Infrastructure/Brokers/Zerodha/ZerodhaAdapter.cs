using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using QuantEngine.Domain.Entities;
using QuantEngine.Domain.Enums;
using QuantEngine.Domain.Interfaces;
using QuantEngine.Domain.Options;
using QuantEngine.Domain.ValueObjects;
using QuantEngine.Domain.Utilities;

namespace QuantEngine.Infrastructure.Brokers.Zerodha;

/// <summary>
/// Zerodha Kite Connect v3 REST adapter.
/// Auth: SHA-256 checksum exchange for access_token.
/// Orders: form-encoded to /orders/regular.
/// </summary>
public sealed class ZerodhaAdapter : IBroker
{
    private const string Base        = "https://api.kite.trade";
    private const string KiteVersion = "3";

    private static readonly JsonSerializerOptions Json =
        new() { PropertyNameCaseInsensitive = true,
                NumberHandling = JsonNumberHandling.AllowReadingFromString };

    private record KiteWrap<T>(
        [property: JsonPropertyName("status")]  string? Status,
        [property: JsonPropertyName("data")]    T?      Data,
        [property: JsonPropertyName("message")] string? Message);

    private record KiteTokenData(
        [property: JsonPropertyName("access_token")] string? AccessToken);

    private record KiteOrderIdData(
        [property: JsonPropertyName("order_id")] string? OrderId);

    private sealed class KiteOrderData
    {
        [JsonPropertyName("order_id")]        public string?  OrderId      { get; set; }
        [JsonPropertyName("tradingsymbol")]   public string?  Symbol       { get; set; }
        [JsonPropertyName("transaction_type")]public string?  Side         { get; set; }
        [JsonPropertyName("status")]          public string?  Status       { get; set; }
        [JsonPropertyName("quantity")]        public int      Qty          { get; set; }
        [JsonPropertyName("filled_quantity")] public int      FilledQty    { get; set; }
        [JsonPropertyName("price")]           public double   Price        { get; set; }
        [JsonPropertyName("trigger_price")]   public double   TriggerPrice { get; set; }
        [JsonPropertyName("average_price")]   public double   AvgPrice     { get; set; }
        [JsonPropertyName("status_message")]  public string?  StatusMsg    { get; set; }
    }

    private sealed class KitePositionItem
    {
        [JsonPropertyName("tradingsymbol")] public string? Symbol    { get; set; }
        [JsonPropertyName("quantity")]      public int     Qty       { get; set; }
        [JsonPropertyName("average_price")] public double  AvgPrice  { get; set; }
        [JsonPropertyName("last_price")]    public double  LastPrice { get; set; }
        [JsonPropertyName("pnl")]          public double  Pnl       { get; set; }
        [JsonPropertyName("realised")]      public double  Realised  { get; set; }
    }

    private record KitePositionsData(
        [property: JsonPropertyName("net")] KitePositionItem[]? Net);

    private sealed class KiteMarginData
    {
        [JsonPropertyName("net")]       public double Net      { get; set; }
        [JsonPropertyName("available")] public KiteAvail? Avail { get; set; }
    }
    private sealed class KiteAvail
    {
        [JsonPropertyName("live_balance")] public double LiveBalance { get; set; }
    }

    private readonly ZerodhaOptions _opts;
    private readonly HttpClient     _http;
    private readonly ILogger<ZerodhaAdapter> _log;

    public BrokerType BrokerType => BrokerType.Zerodha;

    public ZerodhaAdapter(IOptions<ZerodhaOptions> opts, ILogger<ZerodhaAdapter> log)
    {
        _opts = opts.Value;
        _log  = log;
        _http = new HttpClient { BaseAddress = new Uri(Base) };
        _http.DefaultRequestHeaders.Add("X-Kite-Version", KiteVersion);
        SetAuth();
    }

    private void SetAuth()
    {
        _http.DefaultRequestHeaders.Remove("Authorization");
        if (!string.IsNullOrWhiteSpace(_opts.AccessToken))
            _http.DefaultRequestHeaders.Add("Authorization",
                $"token {_opts.ApiKey}:{_opts.AccessToken}");
    }

    public async Task<bool> AuthenticateAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_opts.AccessToken))
        {
            _log.LogWarning("[Zerodha] access_token missing — run --auth");
            return false;
        }
        try
        {
            var r = await _http.GetAsync("/user/profile", ct).ConfigureAwait(false);
            if (r.IsSuccessStatusCode) { _log.LogInformation("[Zerodha] Auth OK"); return true; }
            _log.LogError("[Zerodha] Auth failed {Code}", r.StatusCode); return false;
        }
        catch (Exception ex) { _log.LogError(ex, "[Zerodha] Auth check failed"); return false; }
    }

    public async Task<string> GenerateAccessTokenAsync(CancellationToken ct)
    {
        string loginUrl = $"https://kite.zerodha.com/connect/login?v=3&api_key={_opts.ApiKey}";
        _log.LogInformation("[Zerodha] Open: {Url}", loginUrl);
        Console.WriteLine($"  {loginUrl}");
        Console.Write("  Paste request_token from redirect URL: ");
        string reqToken = (Console.ReadLine() ?? string.Empty).Trim();

        string raw      = _opts.ApiKey + reqToken + _opts.ApiSecret;
        string checksum = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();

        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["api_key"] = _opts.ApiKey, ["request_token"] = reqToken, ["checksum"] = checksum
        });
        using var resp = await _http.PostAsync("/session/token", form, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var data = JsonSerializer.Deserialize<KiteWrap<KiteTokenData>>(body, Json);
        if (data?.Status == "success" && data.Data?.AccessToken is string tok)
        {
            _log.LogInformation("[Zerodha] Token obtained — update Brokers:Zerodha:AccessToken");
            Console.WriteLine($"  access_token = {tok}");
            return tok;
        }
        throw new InvalidOperationException($"[Zerodha] Token exchange failed: {data?.Message ?? body}");
    }

    public async Task<BrokerOrder> PlaceOrderAsync(OrderRequest req, CancellationToken ct)
    {
        string txn  = req.Side == OrderSide.Buy ? "BUY" : "SELL";
        string otyp = req.Type switch
        {
            OrderType.Limit          => "LIMIT",
            OrderType.StopLoss       => "SL",
            OrderType.StopLossMarket => "SL-M",
            _                        => "MARKET"
        };
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["exchange"]         = _opts.Exchange,
            ["tradingsymbol"]    = req.Symbol.ToUpperInvariant(),
            ["transaction_type"] = txn,
            ["quantity"]         = req.Quantity.ToString(),
            ["product"]          = _opts.Product,
            ["order_type"]       = otyp,
            ["validity"]         = "DAY",
            ["price"]            = req.Price > 0 ? req.Price.ToString("F2") : "0",
            ["trigger_price"]    = req.TriggerPrice > 0 ? req.TriggerPrice.ToString("F2") : "0",
            ["tag"]              = req.Tag
        });

        using var resp = await _http.PostAsync("/orders/regular", form, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var data = JsonSerializer.Deserialize<KiteWrap<KiteOrderIdData>>(body, Json);

        var order = new BrokerOrder
        {
            Symbol = req.Symbol, Side = req.Side, Type = req.Type,
            Quantity = req.Quantity, Price = req.Price, TriggerPrice = req.TriggerPrice
        };
        if (data?.Status == "success" && data.Data?.OrderId is string oid)
        {
            order.OrderId = oid; order.State = OrderState.Open;
            _log.LogInformation("[Zerodha] Order placed {Id} {Side} {Qty} {Sym}", oid, txn, req.Quantity, req.Symbol);
        }
        else
        {
            order.State = OrderState.Rejected; order.Reason = data?.Message ?? body;
            _log.LogError("[Zerodha] Order rejected: {Reason}", order.Reason);
        }
        return order;
    }

    public async Task<bool> CancelOrderAsync(string orderId, CancellationToken ct)
    {
        using var r = await _http.DeleteAsync($"/orders/regular/{orderId}", ct).ConfigureAwait(false);
        return r.IsSuccessStatusCode;
    }

    public async Task<BrokerOrder> GetOrderStatusAsync(string orderId, CancellationToken ct)
    {
        using var r = await _http.GetAsync($"/orders/{orderId}", ct).ConfigureAwait(false);
        var body = await r.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var data = JsonSerializer.Deserialize<KiteWrap<KiteOrderData[]>>(body, Json);
        var d    = data?.Data?.LastOrDefault();
        if (d is null) return new BrokerOrder { OrderId = orderId, State = OrderState.Rejected };
        return new BrokerOrder
        {
            OrderId = d.OrderId ?? orderId, Symbol = d.Symbol ?? string.Empty,
            Side    = d.Side == "SELL" ? OrderSide.Sell : OrderSide.Buy,
            Quantity = d.Qty, FilledQty = d.FilledQty, Price = d.Price,
            TriggerPrice = d.TriggerPrice, AvgFillPrice = d.AvgPrice,
            Reason = d.StatusMsg ?? string.Empty, State = ParseState(d.Status)
        };
    }

    public async Task<IReadOnlyList<BrokerOrder>> GetOpenOrdersAsync(CancellationToken ct)
    {
        using var r = await _http.GetAsync("/orders", ct).ConfigureAwait(false);
        var body = await r.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var data = JsonSerializer.Deserialize<KiteWrap<KiteOrderData[]>>(body, Json);
        return data?.Data?
            .Where(d => ParseState(d.Status) is OrderState.Open or OrderState.PartialFill)
            .Select(d => new BrokerOrder
            {
                OrderId = d.OrderId ?? string.Empty, Symbol = d.Symbol ?? string.Empty,
                Side = d.Side == "SELL" ? OrderSide.Sell : OrderSide.Buy,
                Quantity = d.Qty, FilledQty = d.FilledQty, State = ParseState(d.Status)
            }).ToList() ?? [];
    }

    public async Task<IReadOnlyList<BrokerPosition>> GetPositionsAsync(CancellationToken ct)
    {
        using var r = await _http.GetAsync("/portfolio/positions", ct).ConfigureAwait(false);
        var body = await r.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var data = JsonSerializer.Deserialize<KiteWrap<KitePositionsData>>(body, Json);
        return data?.Data?.Net?
            .Where(p => p.Qty != 0)
            .Select(p => new BrokerPosition(p.Symbol ?? string.Empty, p.Qty,
                p.AvgPrice, p.LastPrice, p.Pnl, p.Realised))
            .ToList() ?? [];
    }

    public async Task<BrokerFunds> GetFundsAsync(CancellationToken ct)
    {
        using var r = await _http.GetAsync("/user/margins/equity", ct).ConfigureAwait(false);
        var body = await r.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var data = JsonSerializer.Deserialize<KiteWrap<KiteMarginData>>(body, Json);
        var d    = data?.Data;
        double avail = d?.Avail?.LiveBalance ?? d?.Net ?? 0;
        return new BrokerFunds(avail, Math.Max(0, (d?.Net ?? 0) - avail), d?.Net ?? 0);
    }

    public async Task<Dictionary<string, double>> GetLtpAsync(
        IEnumerable<string> symbols, CancellationToken ct)
    {
        string qs = string.Join("&", symbols
            .Select(s => $"i={SymbolMapper.ToZerodhaQuote(s, _opts.Exchange)}"));
        using var r = await _http.GetAsync($"/quote/ltp?{qs}", ct).ConfigureAwait(false);
        var body = await r.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(body);
        var result    = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        if (doc.RootElement.TryGetProperty("data", out var dataEl))
            foreach (var prop in dataEl.EnumerateObject())
            {
                string sym = prop.Name.Contains(':')
                    ? prop.Name[(prop.Name.IndexOf(':') + 1)..] : prop.Name;
                if (prop.Value.TryGetProperty("last_price", out var lp))
                    result[sym] = lp.GetDouble();
            }
        return result;
    }

    private static OrderState ParseState(string? s) => s?.ToUpperInvariant() switch
    {
        "COMPLETE"  => OrderState.Filled,
        "OPEN"      => OrderState.Open,
        "REJECTED"  => OrderState.Rejected,
        "CANCELLED" => OrderState.Cancelled,
        _           => OrderState.Pending
    };

    public void Dispose() => _http.Dispose();
}
