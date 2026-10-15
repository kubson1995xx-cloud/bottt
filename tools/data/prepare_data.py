#!/usr/bin/env python3
"""
Przygotowanie danych historycznych do backtestu APEX.

Źródło: publiczne repozytorium https://github.com/FutureSharks/financial-data
  - pyfinancialdata/data/currencies/oanda/<INSTRUMENT>/<ROK>/oanda-<INSTRUMENT>-<ROK>-<MIESIĄC>.csv
    (świece M1, ceny MID z OANDA, znaczniki czasu UTC, 2005-01 .. 2020-05)
  - pyfinancialdata/data/stocks/histdata/<INSTRUMENT>/DAT_ASCII_<INSTRUMENT>_M1_<ROK>.csv
    (świece M1 z histdata.com, czas EST bez zmiany czasu, 2010..2018)

Wynik: data/bars/<SYMBOL>_<TF>.csv  (time_utc,open,high,low,close,volume) dla TF = M15, H1
       data/bars/quality.csv        (raport jakości danych)

Użycie:
  python3 tools/data/prepare_data.py --src /ścieżka/do/financial-data --out data/bars

Zasady (ochrona przed błędami danych):
  - duplikaty znaczników czasu: zostaje pierwszy,
  - świece z high<low, ceną <=0 lub NaN: odrzucone,
  - rażąco błędne ticki (odchylenie > 25% od mediany 31 sąsiednich minut): odrzucone,
  - agregacja: open=pierwszy, high=max, low=min, close=ostatni, volume=suma; świeca powstaje tylko z istniejących minut.
"""
import argparse
import glob
import os
import sys

import numpy as np
import pandas as pd

# Mapowanie nazw źródłowych na nazwy w stylu cTrader/Pepperstone używane w backteście.
OANDA_MAP = {
    "EUR_USD": "EURUSD", "GBP_USD": "GBPUSD", "USD_CAD": "USDCAD", "AUD_USD": "AUDUSD", "AUD_JPY": "AUDJPY",
    "EUR_JPY": "EURJPY", "XAU_USD": "XAUUSD", "WTICO_USD": "SpotCrude", "NATGAS_USD": "NatGas",
    "SPX500_USD": "US500", "NAS100_USD": "NAS100", "US2000_USD": "US2000", "UK100_GBP": "UK100",
    "JP225_USD": "JPN225", "AU200_AUD": "AUS200", "FR40_EUR": "FRA40", "NL25_EUR": "NETH25",
    "DE10YB_EUR": "DE10YB", "UK10YB_GBP": "UK10YB", "USB10Y_USD": "USB10Y", "USB02Y_USD": "USB02Y",
    "CORN_USD": "Corn", "WHEAT_USD": "Wheat", "SOYBN_USD": "Soybeans", "SUGAR_USD": "Sugar",
}
HISTDATA_MAP = {"SPXUSD": "HD_SPX500", "GRXEUR": "GER40", "JPXJPY": "HD_JPN225", "ETXEUR": "EUSTX50"}


def clean(df: pd.DataFrame) -> tuple[pd.DataFrame, dict]:
    stats = {"rows_in": len(df)}
    df = df[~df.index.duplicated(keep="first")]
    df = df.sort_index()
    ok = (df[["open", "high", "low", "close"]] > 0).all(axis=1) & (df["high"] >= df["low"]) \
        & df[["open", "high", "low", "close"]].notna().all(axis=1)
    ok &= (df["high"] >= df[["open", "close"]].max(axis=1) - 1e-12) & (df["low"] <= df[["open", "close"]].min(axis=1) + 1e-12)
    stats["invalid"] = int((~ok).sum())
    df = df[ok]
    # Detekcja rażąco błędnych ticków: odchylenie > 25% od mediany 31 sąsiednich minut.
    # Próg celowo wysoki – prawdziwe gwałtowne ruchy (np. flash crash GBP 2016) zostają w danych,
    # bo ich usunięcie zawyżałoby wyniki backtestu.
    med = df["close"].rolling(31, center=True, min_periods=5).median()
    spike = ((df["high"] / med - 1).abs() > 0.25) | ((df["low"] / med - 1).abs() > 0.25)
    spike = spike.fillna(False)
    stats["spikes"] = int(spike.sum())
    df = df[~spike]
    stats["rows_out"] = len(df)
    return df, stats


def resample(df: pd.DataFrame, rule: str) -> pd.DataFrame:
    agg = df.resample(rule, label="left", closed="left").agg(
        {"open": "first", "high": "max", "low": "min", "close": "last", "volume": "sum"})
    return agg.dropna(subset=["open", "high", "low", "close"])


def load_oanda(src: str, name: str) -> pd.DataFrame:
    files = sorted(glob.glob(os.path.join(src, "pyfinancialdata/data/currencies/oanda", name, "*", "*.csv")))
    parts = []
    for f in files:
        d = pd.read_csv(f)
        if d.empty:
            continue
        parts.append(d)
    df = pd.concat(parts, ignore_index=True)
    df["time"] = pd.to_datetime(df["time"], utc=True)
    df = df.set_index("time")[["open", "high", "low", "close", "volume"]].astype(float)
    return df


def load_histdata(src: str, name: str) -> pd.DataFrame:
    files = sorted(glob.glob(os.path.join(src, "pyfinancialdata/data/stocks/histdata", name, "*.csv")))
    parts = []
    for f in files:
        d = pd.read_csv(f, sep=";", header=None, names=["time", "open", "high", "low", "close", "volume"])
        parts.append(d)
    df = pd.concat(parts, ignore_index=True)
    # histdata: czas EST (UTC-5) bez zmiany czasu letniego.
    df["time"] = pd.to_datetime(df["time"], format="%Y%m%d %H%M%S") + pd.Timedelta(hours=5)
    df["time"] = df["time"].dt.tz_localize("UTC")
    df = df.set_index("time")[["open", "high", "low", "close", "volume"]].astype(float)
    df["volume"] = 1.0
    return df


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--src", required=True)
    ap.add_argument("--out", required=True)
    ap.add_argument("--only", default="")
    args = ap.parse_args()
    os.makedirs(args.out, exist_ok=True)
    quality = []
    jobs = [("oanda", k, v) for k, v in OANDA_MAP.items()] + [("histdata", k, v) for k, v in HISTDATA_MAP.items()]
    for provider, src_name, sym in jobs:
        if args.only and sym not in args.only.split(","):
            continue
        try:
            df = load_oanda(args.src, src_name) if provider == "oanda" else load_histdata(args.src, src_name)
        except ValueError as e:
            print(f"{sym}: brak danych ({e})", file=sys.stderr)
            continue
        df, st = clean(df)
        for tf, rule in (("M15", "15min"), ("H1", "1h")):
            bars = resample(df, rule)
            bars.index = bars.index.tz_convert(None)
            path = os.path.join(args.out, f"{sym}_{tf}.csv")
            bars.to_csv(path, index_label="time_utc", float_format="%.6f", date_format="%Y-%m-%d %H:%M")
            if tf == "H1":
                st.update({"symbol": sym, "source": f"{provider}:{src_name}", "h1_bars": len(bars),
                           "first": str(bars.index.min()), "last": str(bars.index.max())})
        quality.append(st)
        print(f"{sym}: {st}", flush=True)
    pd.DataFrame(quality).to_csv(os.path.join(args.out, "quality.csv"), index=False)


if __name__ == "__main__":
    main()
