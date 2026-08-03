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

namespace QuantEngine.Infrastructure.Brokers.Upstox;

/// <summary>
/// Upstox API v2 REST adapter.
/// Auth: OAuth2 authorization_code flow with Bearer token.
/// </summary>
public sealed class UpstoxAdapter : IBroker
{
    private const string Base = "https://api.upstox.com/v2";

    private static readonly JsonSerializerOptions Json =
        new() { PropertyNameCaseInsensitive = true,
                NumberHandling = JsonNumberHandling.AllowReadingFromString };

    private sealed class UpstoxWrap<T>
    {
        [JsonPropertyName("status")]  public string? Status  { get; set; }
        [JsonPropertyName("data")]    public T?      Data    { get; set; }
        [JsonPropertyName("message")] public string? Message { get; set; }
    }
    private sealed class UpstoxTokenData
    { [JsonPropertyName("access_token")] public string? AccessToken { get; set; } }
    private sealed class UpstoxOrderIdData
    { [JsonPropertyName("order_id")] public string? OrderId { get; set; } }
    private sealed class UpstoxOrderData
    {
        [JsonPropertyName("order_id")]         public string?  OrderId     { get; set; }
        [JsonPropertyName("instrument_token")] public string?  Instrument  { get; set; }
        [JsonPropertyName("transaction_type")] public string?  Side        { get; set; }
        [JsonPropertyName("status")]           public string?  Status      { get; set; }
        [JsonPropertyName("quantity")]         public int      Qty         { get; set; }
        [JsonPropertyName("filled_quantity")]  public int      FilledQty   { get; set; }
        [JsonPropertyName("price")]            public double   Price       { get; set; }
        [JsonPropertyName("trigger_price")]    public double   TriggerPrice{ get; set; }
        [JsonPropertyName("average_price")]    public double   AvgPrice    { get; set; }
        [JsonPropertyName("reason")]           public string?  Reason      { get; set; }
    }
    private sealed class UpstoxPositionItem
    {
        [JsonPropertyName("instrument_token")] public string? Instrument { get; set; }
        [JsonPropertyName("quantity")]         public int     Qty        { get; set; }
        [JsonPropertyName("average_price")]    public double  AvgPrice   { get; set; }
        [JsonPropertyName("last_price")]       public double  LastPrice  { get; set; }
        [JsonPropertyName("pnl")]             public double  Pnl        { get; set; }
        [JsonPropertyName("realised_profit")]  public double  Realised   { get; set; }
    }
    private sealed class UpstoxFundWrap
    {
        [JsonPropertyName("equity")] public UpstoxFundEquity? Equity { get; set; }
    }
    private sealed class UpstoxFundEquity
    {
        [JsonPropertyName("used_margin")]      public double Used  { get; set; }
        [JsonPropertyName("available_margin")] public double Avail { get; set; }
        [JsonPropertyName("net")]              public double Net   { get; set; }
    }

    private readonly UpstoxOptions _opts;
    private readonly HttpClient    _http;
    private readonly ILogger<UpstoxAdapter> _log;

    public BrokerType BrokerType => BrokerType.Upstox;

    public UpstoxAdapter(IOptions<UpstoxOptions> opts, ILogger<UpstoxAdapter> log)
    {
        _opts = opts.Value; _log = log;
        _http = new HttpClient { BaseAddress = new Uri(Base) };
        _http.DefaultRequestHeaders.Add("Accept", "application/json");
        SetAuth();
    }

    private void SetAuth()
    {
        _http.DefaultRequestHeaders.Remove("Authorization");
        if (!string.IsNullOrWhiteSpace(_opts.AccessToken))
            _http.DefaultRequestHeaders.Add("Authorization", $"Bearer {_opts.AccessToken}");
    }

    public async Task<bool> AuthenticateAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_opts.AccessToken))
        { _log.LogWarning("[Upstox] access_token missing — run --auth"); return false; }
        try
        {
            var r = await _http.GetAsync("/user/profile", ct).ConfigureAwait(false);
            if (r.IsSuccessStatusCode) { _log.LogInformation("[Upstox] Auth OK"); return true; }
            _log.LogError("[Upstox] Auth failed {Code}", r.StatusCode); return false;
        }
        catch (Exception ex) { _log.LogError(ex, "[Upstox] Auth failed"); return false; }
    }

    public async Task<string> GenerateAccessTokenAsync(CancellationToken ct)
    {
        string authUrl =
            $"https://api.upstox.com/v2/login/authorization/dialog" +
            $"?client_id={_opts.ApiKey}&redirect_uri={Uri.EscapeDataString(_opts.RedirectUri)}" +
            $"&response_type=code";
        _log.LogInformation("[Upstox] Open: {Url}", authUrl);
        Console.WriteLine($"  {authUrl}");
        Console.Write("  Paste 'code' from redirect URL: ");
        string code = (Console.ReadLine() ?? string.Empty).Trim();

        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["code"] = code, ["client_id"] = _opts.ApiKey,
            ["client_secret"] = _opts.ApiSecret, ["redirect_uri"] = _opts.RedirectUri,
            ["grant_type"] = "authorization_code"
        });
        using var tmp = new HttpClient();
        using var resp = await tmp.PostAsync(
            "https://api.upstox.com/v2/login/authorization/token", form, ct)
            .ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var data = JsonSerializer.Deserialize<UpstoxWrap<UpstoxTokenData>>(body, Json);
        if (data?.Status == "success" && data.Data?.AccessToken is string tok)
        {
            _log.LogInformation("[Upstox] Token obtained — update Brokers:Upstox:AccessToken");
            Console.WriteLine($"  access_token = {tok}");
            return tok;
        }
        throw new InvalidOperationException($"[Upstox] Token exchange failed: {body}");
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
        var json = JsonSerializer.Serialize(new
        {
            quantity = req.Quantity, product = _opts.Product, validity = "DAY",
            price = req.Price, tag = req.Tag,
            instrument_token = SymbolMapper.ToUpstoxKey(req.Symbol, _opts.Exchange),
            order_type = otyp, transaction_type = txn,
            disclosed_quantity = 0, trigger_price = req.TriggerPrice, is_amo = false
        });
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var resp    = await _http.PostAsync("/order/place", content, ct).ConfigureAwait(false);
        var raw   = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var data  = JsonSerializer.Deserialize<UpstoxWrap<UpstoxOrderIdData>>(raw, Json);
        var order = new BrokerOrder
        {
            Symbol = req.Symbol, Side = req.Side, Type = req.Type,
            Quantity = req.Quantity, Price = req.Price, TriggerPrice = req.TriggerPrice
        };
        if (data?.Status == "success" && data.Data?.OrderId is string oid)
        {
            order.OrderId = oid; order.State = OrderState.Open;
            _log.LogInformation("[Upstox] Order placed {Id} {Side} {Qty} {Sym}", oid, txn, req.Quantity, req.Symbol);
        }
        else
        {
            order.State = OrderState.Rejected; order.Reason = data?.Message ?? raw;
            _log.LogError("[Upstox] Order rejected: {Reason}", order.Reason);
        }
        return order;
    }

    public async Task<bool> CancelOrderAsync(string orderId, CancellationToken ct)
    {
        using var r = await _http.DeleteAsync($"/order/cancel?order_id={orderId}", ct).ConfigureAwait(false);
        return r.IsSuccessStatusCode;
    }

    public async Task<BrokerOrder> GetOrderStatusAsync(string orderId, CancellationToken ct)
    {
        using var r = await _http.GetAsync($"/order/details?order_id={orderId}", ct).ConfigureAwait(false);
        var body = await r.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var data = JsonSerializer.Deserialize<UpstoxWrap<UpstoxOrderData>>(body, Json);
        var d    = data?.Data;
        if (d is null) return new BrokerOrder { OrderId = orderId, State = OrderState.Rejected };
        return new BrokerOrder
        {
            OrderId = d.OrderId ?? orderId,
            Symbol  = SymbolMapper.FromUpstoxKey(d.Instrument ?? string.Empty),
            Side    = d.Side == "SELL" ? OrderSide.Sell : OrderSide.Buy,
            Quantity = d.Qty, FilledQty = d.FilledQty, Price = d.Price,
            TriggerPrice = d.TriggerPrice, AvgFillPrice = d.AvgPrice,
            Reason = d.Reason ?? string.Empty, State = ParseState(d.Status)
        };
    }

    public async Task<IReadOnlyList<BrokerOrder>> GetOpenOrdersAsync(CancellationToken ct)
    {
        using var r = await _http.GetAsync("/order/retrieve-all", ct).ConfigureAwait(false);
        var body = await r.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var data = JsonSerializer.Deserialize<UpstoxWrap<UpstoxOrderData[]>>(body, Json);
        return data?.Data?
            .Where(d => ParseState(d.Status) is OrderState.Open or OrderState.PartialFill)
            .Select(d => new BrokerOrder
            {
                OrderId = d.OrderId ?? string.Empty,
                Symbol  = SymbolMapper.FromUpstoxKey(d.Instrument ?? string.Empty),
                Side    = d.Side == "SELL" ? OrderSide.Sell : OrderSide.Buy,
                Quantity = d.Qty, FilledQty = d.FilledQty, State = ParseState(d.Status)
            }).ToList() ?? [];
    }

    public async Task<IReadOnlyList<BrokerPosition>> GetPositionsAsync(CancellationToken ct)
    {
        using var r = await _http.GetAsync("/portfolio/short-term-positions", ct).ConfigureAwait(false);
        var body = await r.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var data = JsonSerializer.Deserialize<UpstoxWrap<UpstoxPositionItem[]>>(body, Json);
        return data?.Data?
            .Where(p => p.Qty != 0)
            .Select(p => new BrokerPosition(
                SymbolMapper.FromUpstoxKey(p.Instrument ?? string.Empty),
                p.Qty, p.AvgPrice, p.LastPrice, p.Pnl, p.Realised))
            .ToList() ?? [];
    }

    public async Task<BrokerFunds> GetFundsAsync(CancellationToken ct)
    {
        using var r = await _http.GetAsync("/user/fund-and-margin?segment=SEC", ct).ConfigureAwait(false);
        var body = await r.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var data = JsonSerializer.Deserialize<UpstoxWrap<UpstoxFundWrap>>(body, Json);
        var eq   = data?.Data?.Equity;
        return new BrokerFunds(eq?.Avail ?? 0, eq?.Used ?? 0, eq?.Net ?? 0);
    }

    public async Task<Dictionary<string, double>> GetLtpAsync(
        IEnumerable<string> symbols, CancellationToken ct)
    {
        string keys = string.Join(",",
            symbols.Select(s => SymbolMapper.ToUpstoxKey(s, _opts.Exchange)));
        using var r = await _http
            .GetAsync($"/market-quote/ltp?instrument_key={Uri.EscapeDataString(keys)}", ct)
            .ConfigureAwait(false);
        var body = await r.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(body);
        var result    = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        if (doc.RootElement.TryGetProperty("data", out var dataEl))
            foreach (var prop in dataEl.EnumerateObject())
            {
                string sym = SymbolMapper.FromUpstoxKey(prop.Name.Replace(':', '|'));
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
