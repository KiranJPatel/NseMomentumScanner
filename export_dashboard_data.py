#!/usr/bin/env python3
"""
export_dashboard_data.py
────────────────────────
OPTIONAL / LEGACY: as of v3.1, `dotnet run -- --serve` hosts the dashboard and
reads the SQLite DB live on every request — you generally don't need this script
anymore. It's kept for producing a one-off, fully offline static snapshot
(e.g. to share a fixed point-in-time view with someone who can't run the app).

Reads the latest scan session from NseMomentumScanner.db and writes
dashboard_data.json, which nse_dashboard.html loads automatically
when placed in the same folder.

Usage:
  python export_dashboard_data.py
  python export_dashboard_data.py --db path/to/NseMomentumScanner.db --out dashboard_data.json

Requirements: Python 3.8+  (only stdlib — sqlite3, json, argparse)
"""

import sqlite3, json, argparse, sys
from pathlib import Path
from datetime import datetime

# ── CLI ────────────────────────────────────────────────────────────────────────
ap = argparse.ArgumentParser(description="Export NSE Scanner SQLite → JSON dashboard feed")
ap.add_argument("--db",  default="NseMomentumScanner.db", help="SQLite database path")
ap.add_argument("--out", default="dashboard_data.json",   help="Output JSON file path")
ap.add_argument("--session", type=int, default=None,      help="Session ID (default: latest)")
args = ap.parse_args()

db_path = Path(args.db)
if not db_path.exists():
    print(f"[ERROR] Database not found: {db_path}")
    print("  Run the NSE Momentum Scanner first to generate NseMomentumScanner.db")
    sys.exit(1)

con = sqlite3.connect(db_path)
con.row_factory = sqlite3.Row
cur = con.cursor()

# ── Resolve session ────────────────────────────────────────────────────────────
if args.session:
    sid = args.session
else:
    row = cur.execute(
        "SELECT session_id FROM scan_sessions ORDER BY session_id DESC LIMIT 1"
    ).fetchone()
    if not row:
        print("[ERROR] No scan sessions found in database.")
        sys.exit(1)
    sid = row["session_id"]

sess_row = cur.execute(
    "SELECT * FROM scan_sessions WHERE session_id = ?", (sid,)
).fetchone()
if not sess_row:
    print(f"[ERROR] Session {sid} not found.")
    sys.exit(1)

print(f"Exporting session #{sid}  run: {sess_row['run_timestamp'][:19]}")

# ── Aggregated results (latest scan_date in this session) ─────────────────────
scan_date_row = cur.execute(
    "SELECT MAX(scan_date) AS d FROM aggregated_results WHERE session_id = ?", (sid,)
).fetchone()
scan_date = scan_date_row["d"] if scan_date_row else None

agg_rows = cur.execute("""
    SELECT
        symbol, scan_date, aggregate_score, monthly_score, weekly_score, daily_score,
        hourly_score, fifteen_min_score, five_min_score,
        bullish_tf, bearish_tf, recommendation, rank, last_close, data_tf,
        weekly_high, weekly_low, monthly_high, monthly_low,
        quarterly_high, quarterly_low, yearly_high, yearly_low,
        pct_52w_high, pct_52w_low,
        st_bullish, st_days, st_flip_date, st_cal_days,
        st_hh, st_hh_ago, st_ll, st_ll_ago
    FROM aggregated_results
    WHERE session_id = ? AND scan_date = ?
    ORDER BY aggregate_score DESC
""", (sid, scan_date)).fetchall()

# ── Scan results (daily TF on same date — for indicator details) ───────────────
sr_rows = cur.execute("""
    SELECT
        symbol, timeframe, total_score, supertrend_score, hma_score,
        adx_score, vcp_score, volume_score, trend_score,
        last_close, last_high, last_low,
        supertrend_val, bullish_st, hma_fast, hma_slow, ema_fast, ema_slow,
        avg_volume, volume_ratio, adx_value, pdi_value, mdi_value,
        vcp_detected,
        st_hh, st_hh_ago, st_hl, st_hl_ago, st_lh, st_lh_ago, st_ll, st_ll_ago
    FROM scan_results
    WHERE session_id = ? AND scan_date = ? AND timeframe = '1d'
""", (sid, scan_date)).fetchall()

# Index scan_results by symbol
sr_by_sym = {r["symbol"]: dict(r) for r in sr_rows}

# ── Score history (last 30 days, daily TF, top 30 buy symbols) ────────────────
top_syms = [r["symbol"] for r in agg_rows if r["recommendation"] == "BUY"][:30]
placeholders = ",".join("?" * len(top_syms)) if top_syms else "''"
history_rows = cur.execute(f"""
    SELECT symbol, scan_date, total_score, timeframe
    FROM scan_results
    WHERE session_id = ? AND timeframe = '1d' AND symbol IN ({placeholders})
    ORDER BY symbol, scan_date
""", [sid] + top_syms).fetchall() if top_syms else []

# Group history by symbol
history: dict = {}
for r in history_rows:
    sym = r["symbol"]
    if sym not in history:
        history[sym] = []
    history[sym].append({"date": r["scan_date"], "score": round(r["total_score"], 1)})

# ── Timeframe breakdown (all TFs, latest date) ─────────────────────────────────
tf_rows = cur.execute("""
    SELECT timeframe,
           COUNT(*) as total,
           SUM(CASE WHEN total_score > 0 THEN 1 ELSE 0 END) as bulls,
           SUM(CASE WHEN total_score < 0 THEN 1 ELSE 0 END) as bears,
           AVG(total_score) as avg_score
    FROM scan_results
    WHERE session_id = ? AND scan_date = ?
    GROUP BY timeframe
    ORDER BY CASE timeframe WHEN '1wk' THEN 1 WHEN '1d' THEN 2
             WHEN '1h' THEN 3 WHEN '15m' THEN 4 WHEN '5m' THEN 5 ELSE 6 END
""", (sid, scan_date)).fetchall()

# ── Build output JSON ──────────────────────────────────────────────────────────
def _f(v): return round(float(v), 2) if v is not None else None
def _i(v): return int(v) if v is not None else None

stocks_out = []
for r in agg_rows:
    sym  = r["symbol"]
    sr   = sr_by_sym.get(sym, {})
    # sector not stored in DB — infer from symbol prefix or use "—"
    sector = "—"
    s = {
        "sym":     sym,
        "rec":     r["recommendation"] or "HOLD",
        "rank":    _i(r["rank"]),
        "close":   _f(r["last_close"]),
        "aggScore":_f(r["aggregate_score"]),
        "moSc":    _f(r["monthly_score"]),
        "wSc":     _f(r["weekly_score"]),
        "dSc":     _f(r["daily_score"]),
        "hSc":     _f(r["hourly_score"]),
        "m15Sc":   _f(r["fifteen_min_score"]),
        "m5Sc":    _f(r["five_min_score"]),
        "bullTf":  _i(r["bullish_tf"]) or 0,
        "bearTf":  _i(r["bearish_tf"]) or 0,
        "sector":  sector,
        "levels": {
            "yH":  _f(r["yearly_high"]),   "yL":  _f(r["yearly_low"]),
            "qH":  _f(r["quarterly_high"]), "qL": _f(r["quarterly_low"]),
            "mH":  _f(r["monthly_high"]),  "mL":  _f(r["monthly_low"]),
            "wH":  _f(r["weekly_high"]),   "wL":  _f(r["weekly_low"]),
            "p52H":_f(r["pct_52w_high"]),  "p52L":_f(r["pct_52w_low"]),
            "pMH": _f(sr.get("last_high") and r["last_close"] and r["monthly_high"] and
                      (r["last_close"] - r["monthly_high"]) / r["monthly_high"] * 100),
            "pWH": _f(sr.get("last_high") and r["last_close"] and r["weekly_high"] and
                      (r["last_close"] - r["weekly_high"]) / r["weekly_high"] * 100),
        },
        "st": {
            "bull":    bool(r["st_bullish"]),
            "days":    _i(r["st_days"]) or 0,
            "flip":    r["st_flip_date"] or "",
            "calDays": _i(r["st_cal_days"]) or 0,
            "hh":      _f(r["st_hh"]),       "hhAgo": _i(r["st_hh_ago"]) or 0,
            "ll":      _f(r["st_ll"]),       "llAgo": _i(r["st_ll_ago"]) or 0,
            "hl":      _f(sr.get("st_hl")),  "hlAgo": _i(sr.get("st_hl_ago")) or 0,
            "lh":      _f(sr.get("st_lh")),  "lhAgo": _i(sr.get("st_lh_ago")) or 0,
        },
        # Indicator details from daily scan_results
        "adx":       _f(sr.get("adx_value")),
        "pdi":       _f(sr.get("pdi_value")),
        "mdi":       _f(sr.get("mdi_value")),
        "volR":      _f(sr.get("volume_ratio")),
        "avgVol":    _f(sr.get("avg_volume")),
        "vcp":       bool(sr.get("vcp_detected")),
        "hmaFast":   _f(sr.get("hma_fast")),
        "hmaSlow":   _f(sr.get("hma_slow")),
        "emaFast":   _f(sr.get("ema_fast")),
        "emaSlow":   _f(sr.get("ema_slow")),
        "scores": {
            "st":  _f(sr.get("supertrend_score")),
            "hma": _f(sr.get("hma_score")),
            "adx": _f(sr.get("adx_score")),
            "vcp": _f(sr.get("vcp_score")),
            "vol": _f(sr.get("volume_score")),
            "tr":  _f(sr.get("trend_score")),
        },
        "history": history.get(sym, []),
    }
    stocks_out.append(s)

tf_out = []
for r in tf_rows:
    tf_out.append({
        "tf":   r["timeframe"],
        "total":_i(r["total"]),
        "bulls":_i(r["bulls"]),
        "bears":_i(r["bears"]),
        "avg":  round(float(r["avg_score"] or 0), 1),
    })

output = {
    "generated":  datetime.utcnow().strftime("%Y-%m-%dT%H:%M:%SZ"),
    "scanDate":   scan_date,
    "session": {
        "id":      sid,
        "runDate": sess_row["run_timestamp"][:10],
        "total":   _i(sess_row["total_tickers"]),
        "scored":  _i(sess_row["scored_tickers"]),
        "universe":sess_row["universe"],
    },
    "stocks":    stocks_out,
    "tfBreakdown": tf_out,
}

# ── Write JSON ─────────────────────────────────────────────────────────────────
out_path = Path(args.out)
out_path.write_text(json.dumps(output, indent=2, ensure_ascii=False), encoding="utf-8")

buys  = sum(1 for s in stocks_out if s["rec"] == "BUY")
sells = sum(1 for s in stocks_out if s["rec"] == "IMMEDIATE_SELL")

print(f"✓  Exported {len(stocks_out)} stocks → {out_path}")
print(f"   Scan date : {scan_date}")
print(f"   Buy signals  : {buys}")
print(f"   Sell signals : {sells}")
print(f"\nOpen nse_dashboard.html in your browser.")
print("The dashboard auto-loads dashboard_data.json when present.")

con.close()
