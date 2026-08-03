# Generate complete standalone trading dashboard HTML
import json, random, math
from datetime import datetime, timedelta

random.seed(42)

# ── Full dataset generation ──────────────────────────────────────────────────
RUNS = [
    {"RunId":"a1b2c3d4","HmaFast":20,"HmaSlow":50,"AdxThreshold":20.0,"STmult":3.0,
     "FinalEquity":1872450,"InitialCapital":1000000,"CAGR":13.41,"Sharpe":1.42,
     "Sortino":2.07,"Calmar":1.18,"MaxDrawdown":11.37,"WinRate":58.4,
     "ProfitFactor":2.31,"AvgWin":8420,"AvgLoss":3980,"MaxConsecLosses":4,
     "TotalTrades":218,"Winners":127,"CreatedAt":"2025-01-15 09:22","Label":"Base (20/50/20)"},
    {"RunId":"b2c3d4e5","HmaFast":15,"HmaSlow":40,"AdxThreshold":18.0,"STmult":2.5,
     "FinalEquity":1643200,"InitialCapital":1000000,"CAGR":10.44,"Sharpe":1.18,
     "Sortino":1.71,"Calmar":0.89,"MaxDrawdown":11.73,"WinRate":53.1,
     "ProfitFactor":1.94,"AvgWin":7120,"AvgLoss":4210,"MaxConsecLosses":6,
     "TotalTrades":291,"Winners":155,"CreatedAt":"2025-01-15 10:45","Label":"Fast (15/40/18)"},
    {"RunId":"c3d4e5f6","HmaFast":25,"HmaSlow":60,"AdxThreshold":25.0,"STmult":3.5,
     "FinalEquity":2104780,"InitialCapital":1000000,"CAGR":16.06,"Sharpe":1.67,
     "Sortino":2.44,"Calmar":1.53,"MaxDrawdown":10.49,"WinRate":61.2,
     "ProfitFactor":2.68,"AvgWin":9340,"AvgLoss":3720,"MaxConsecLosses":3,
     "TotalTrades":172,"Winners":105,"CreatedAt":"2025-01-15 12:30","Label":"Conservative (25/60/25) ★"},
]

# Equity curves
def gen_equity(initial, cagr_pct, sharpe, n=870, seed=0):
    rng = random.Random(seed)
    cagr = cagr_pct/100
    daily_ret = (1+cagr)**(1/252)-1
    daily_vol = abs(daily_ret/(sharpe/math.sqrt(252))) if sharpe>0 else 0.01
    eq, curve = initial, []
    for _ in range(n):
        r = daily_ret + rng.gauss(0, daily_vol)
        eq = max(eq*(1+r), initial*0.4)
        curve.append(round(eq))
    return curve

n = 870
start = datetime(2020,1,1)
dates_all = []
d = start
while len(dates_all) < n:
    if d.weekday() < 5:
        dates_all.append(d.strftime("%Y-%m-%d"))
    d += timedelta(days=1)

curves = [
    gen_equity(1000000, 13.41, 1.42, n, 1),
    gen_equity(1000000, 10.44, 1.18, n, 2),
    gen_equity(1000000, 16.06, 1.67, n, 3),
]
spy_curve = gen_equity(1000000, 11.50, 1.05, n, 99)

# Monthly returns
def monthly_rets(curve, dates):
    months = {}
    for i in range(len(dates)):
        ym = dates[i][:7]
        if ym not in months:
            months[ym] = {"start": curve[max(0,i-1)], "end": curve[i]}
        else:
            months[ym]["end"] = curve[i]
    result = {}
    for ym, v in months.items():
        result[ym] = round((v["end"]-v["start"])/v["start"]*100, 2)
    return result

monthly = [monthly_rets(c, dates_all) for c in curves]

# 172 trades
symbols = ["RELIANCE","TCS","INFY","HDFCBANK","ICICIBANK","HINDUNILVR","KOTAKBANK",
           "SBIN","BAJFINANCE","BHARTIARTL","ASIANPAINT","MARUTI","AXISBANK","LT",
           "SUNPHARMA","TITAN","NESTLEIND","WIPRO","HCLTECH","TECHM"]
exit_reasons = ["TakeProfit","StopLoss","TrailingStop","TrendReversal","EndOfData"]
reason_w = [0.38,0.22,0.28,0.09,0.03]

trades = []
rng2 = random.Random(77)
td = datetime(2020,3,1)
for tid in range(1,173):
    sym = rng2.choice(symbols)
    hold = rng2.randint(3,45)
    entry_dt = td + timedelta(days=rng2.randint(0,5))
    exit_dt = entry_dt + timedelta(days=hold)
    if exit_dt > datetime(2024,12,31): exit_dt = datetime(2024,12,31)
    ep = round(rng2.uniform(200,4000),2)
    reason = rng2.choices(exit_reasons, weights=reason_w)[0]
    is_win = rng2.random() < 0.612
    pct_chg = rng2.uniform(0.03,0.18) if is_win else -rng2.uniform(0.02,0.10)
    xp = round(ep*(1+pct_chg),2)
    qty = rng2.randint(5,100)
    net = round((xp-ep)*qty - qty*0.005*2, 0)
    hold_days = (exit_dt - entry_dt).days
    trades.append({
        "id": tid, "symbol": sym,
        "entry_date": entry_dt.strftime("%Y-%m-%d"),
        "exit_date": exit_dt.strftime("%Y-%m-%d"),
        "entry_price": ep, "exit_price": xp,
        "quantity": qty, "net_pnl": int(net),
        "exit_reason": reason, "hold_days": hold_days,
        "pct_change": round((xp-ep)/ep*100,2)
    })
    td = entry_dt + timedelta(days=rng2.randint(1,12))

# Symbol performance
sym_perf = {}
for t in trades:
    s = t["symbol"]
    if s not in sym_perf:
        sym_perf[s] = {"pnl":0,"trades":0,"wins":0,"losses":0}
    sym_perf[s]["pnl"] += t["net_pnl"]
    sym_perf[s]["trades"] += 1
    if t["net_pnl"] > 0: sym_perf[s]["wins"] += 1
    else: sym_perf[s]["losses"] += 1

sym_perf_list = sorted([
    {"symbol":k,"total_pnl":v["pnl"],"trades":v["trades"],
     "wins":v["wins"],"losses":v["losses"],
     "win_rate":round(v["wins"]/v["trades"]*100,1)}
    for k,v in sym_perf.items()
], key=lambda x:-x["total_pnl"])

# Drawdown series (sampled)
def calc_dd(curve):
    peak = curve[0]; dd = []
    for v in curve:
        if v > peak: peak = v
        dd.append(round(-(peak-v)/peak*100,3))
    return dd

dd_curves = [calc_dd(c) for c in curves]

# Sample every 5th for charts
STEP = 5
chart_dates = dates_all[::STEP]
chart_curves = [[v//1000 for v in c[::STEP]] for c in curves]
chart_spy    = [v//1000 for v in spy_curve[::STEP]]
chart_dd     = [dd_curves[2][i] for i in range(0,n,STEP)]

data = {
    "runs": RUNS,
    "chart_dates": chart_dates,
    "chart_curves": chart_curves,
    "chart_spy": chart_spy,
    "chart_dd": chart_dd,
    "monthly": monthly,
    "trades": trades,
    "sym_perf": sym_perf_list,
    "generated": datetime.utcnow().strftime("%Y-%m-%d %H:%M UTC")
}

print(json.dumps(data))
