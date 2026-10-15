using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Apex.Core.Model;

namespace Apex.Core.Safety
{
    /// <summary>
    /// Klasyfikacja instrumentów na podstawie nazwy, opisu i aktywów bazowego/kwotowanego.
    /// API cTrader Algo nie udostępnia kategorii symbolu, dlatego klasyfikacja jest heurystyczna i działa w trybie
    /// "fail-closed": instrument nierozpoznany = Unknown = NIE jest handlowany.
    /// Wykrywanie kryptowalut jest wielowarstwowe (aktywa, nazwa, opis, lista wykluczeń) i ma pierwszeństwo przed resztą.
    /// </summary>
    public static class InstrumentClassifier
    {
        public static readonly HashSet<string> Fiat = new(StringComparer.OrdinalIgnoreCase)
        {
            "USD","EUR","GBP","JPY","CHF","AUD","NZD","CAD","SEK","NOK","DKK","PLN","HUF","CZK","TRY","ZAR","MXN","SGD","HKD",
            "CNH","CNY","RUB","ILS","THB","INR","KRW","TWD","BRL","CLP","COP","IDR","MYR","PHP","SAR","AED","KWD","QAR","BHD",
            "RON","BGN","HRK","ISK","ARS","PEN","EGP","NGN","KES","MAD","VND","PKR","UAH","KZT",
        };

        /// <summary>Tickery kryptowalut i stablecoinów (łącznie z oznaczeniami spotykanymi u brokerów, np. LNK, XBT, DSH).</summary>
        public static readonly HashSet<string> CryptoTickers = new(StringComparer.OrdinalIgnoreCase)
        {
            "BTC","XBT","ETH","LTC","XRP","BCH","BSV","BTG","ADA","DOT","SOL","DOGE","SHIB","BNB","XLM","EOS","LINK","LNK","UNI",
            "AVAX","MATIC","POL","TRX","ETC","XMR","DASH","DSH","ZEC","NEO","XTZ","ATOM","ALGO","AAVE","COMP","MKR","SUSHI","YFI",
            "SNX","FIL","ICP","NEAR","APT","ARB","SAND","MANA","AXS","CHZ","ENJ","BAT","GRT","LRC","KSM","EGLD","FTM","HBAR","VET",
            "THETA","XEM","QTUM","ONT","ZIL","ICX","WAVES","IOTA","MIOTA","CRV","1INCH","GALA","APE","PEPE","BONK","WIF","TON",
            "SUI","SEI","INJ","TIA","RNDR","RENDER","FET","AGIX","OCEAN","KAS","STX","LDO","RUNE","IMX","FLOW","MINA","QNT","HNT",
            "CAKE","ZRX","ENS","LUNA","LUNC","USTC","USDT","USDC","DAI","BUSD","TUSD","FDUSD","PYUSD","WBTC","STETH","PAXG","XAUT",
            "JUP","PYTH","JTO","ORDI","STRK","ZK","ENA","W","NOT","OM","BLUR","DYDX","GMX","KAVA","OSMO","ROSE","CFX","CRO","OKB",
            "LEO","HT","FTT","CEL","BGB","TAO","WLD","FLOKI","TRUMP","XDC","XCN","AR","AKT","BEAM","AXL","PENDLE","ETHFI",
        };

        private static readonly string[] CryptoKeywords =
        {
            "crypto", "bitcoin", "ethereum", "litecoin", "ripple", "dogecoin", "cardano", "polkadot", "solana", "chainlink",
            "stellar", "tezos", "uniswap", "avalanche", "polygon", "tether", "binance", "blockchain", "stablecoin", "defi",
            "coin", "satoshi", "altcoin", "web3", "monero", "zcash",
        };

        /// <summary>Krótkie słowa kluczowe dopasowywane tylko jako całe wyrazy (np. "defi" nie może trafić w "Definitive").</summary>
        private static readonly string[] CryptoWholeWords = { "defi", "nft", "nfts", "token", "tokens", "dao" };

        /// <summary>Kody walut, po których rozpoznajemy parę "TICKER+WALUTA" (np. BTCUSD, ETHUSDT).</summary>
        private static readonly string[] QuoteSuffixes = { "USDT", "USDC", "USD", "EUR", "GBP", "JPY", "AUD", "CAD", "CHF", "BTC", "ETH", "NZD", "SGD", "HKD", "ZAR", "PLN" };

        private static readonly HashSet<string> MetalCodes = new(StringComparer.OrdinalIgnoreCase)
        { "XAU", "XAG", "XPT", "XPD", "XCU", "GOLD", "SILVER", "PLATINUM", "PALLADIUM", "COPPER" };

        private static readonly string[] EnergyNames =
        {
            "XTI", "XBR", "XNG", "WTI", "BRENT", "USOIL", "UKOIL", "SPOTCRUDE", "SPOTBRENT", "CRUDE", "NATGAS", "NGAS", "GASOIL",
            "HEATINGOIL", "GASOLINE", "RBOB", "WTICO", "BCO", "OILUSD", "CL",
        };

        private static readonly string[] EnergyKeywords = { "crude", " oil", "oil ", "natural gas", "brent", "gasoline", "heating oil", "petroleum" };

        private static readonly string[] IndexNames =
        {
            "US500", "US30", "NAS100", "US2000", "USTEC", "US100", "SPX500", "SPX", "NDX", "DJ30", "DJI", "GER40", "GER30", "DE40",
            "DE30", "DAX", "UK100", "FTSE", "FRA40", "FR40", "F40", "EUSTX50", "EU50", "STOXX50", "ESTX50", "ESP35", "SPA35", "IT40",
            "NETH25", "NL25", "SWI20", "CH20", "JPN225", "JP225", "N225", "AUS200", "AU200", "HK50", "HSI", "CHINA50", "CN50",
            "CHINAH", "SG30", "SING30", "VIX", "USDX", "DXY", "EURX", "JPYX", "INDIA50", "NIFTY", "SA40", "MEX35", "CA60",
        };

        private static readonly string[] BondNames = { "BUND", "BOBL", "SCHATZ", "GILT", "UST", "TNOTE", "TBOND", "JGB", "DE10YB", "UK10YB", "USB10Y", "USB02Y", "USB05Y", "USB30Y", "US10Y", "US2Y", "US5Y", "US30Y", "BTP", "OAT" };
        private static readonly string[] BondKeywords = { "bond", "treasury", "gilt", "bund", "t-note", "t-bond" };

        private static readonly string[] CommodityNames =
        {
            "CORN", "WHEAT", "SOYBEAN", "SOYBEANS", "SOYBN", "SOYB", "SUGAR", "COFFEE", "COCOA", "COTTON", "OJ", "ORANGEJUICE",
            "CATTLE", "LIVECATTLE", "HOGS", "LEANHOGS", "LUMBER", "OATS", "RICE", "SOYOIL", "SOYMEAL",
        };

        private static readonly string[] CommodityKeywords = { "corn", "wheat", "soybean", "sugar", "coffee", "cocoa", "cotton", "orange juice", "cattle", "hogs", "lumber" };

        private static readonly HashSet<string> KnownEtfs = new(StringComparer.OrdinalIgnoreCase)
        {
            "SPY","QQQ","IWM","EEM","GLD","SLV","USO","TLT","XLF","XLE","XLK","XLV","XLI","XLY","XLP","XLU","VTI","VOO","DIA","EFA",
            "HYG","LQD","GDX","GDXJ","ARKK","VWO","IEMG","AGG","BND","SMH","SOXX","KWEB","FXI","EWZ","EWJ","IVV","BNO","UNG",
        };

        private static readonly string[] StockSuffixes =
        {
            ".US", ".UK", ".DE", ".FR", ".NL", ".ES", ".IT", ".CH", ".AU", ".HK", ".JP", ".SE", ".NO", ".DK", ".FI", ".PT", ".BE",
            ".AT", ".IE", ".CA", ".O", ".N", ".OQ", ".L", ".PA", ".AS", ".MC", ".MI", ".SW", ".AX", ".TO",
        };

        private static readonly string[] CompanyKeywords = { " inc", " corp", " ltd", " plc", " ag", " sa", " n.v", " nv", " holdings", " group", " company", " co.", " se", " spa", " ab", " asa", " oyj", " limited", "incorporated", "corporation" };

        public static string Normalize(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length);
            foreach (char ch in s)
                if (char.IsLetterOrDigit(ch)) sb.Append(char.ToUpperInvariant(ch));
            return sb.ToString();
        }

        /// <summary>
        /// Czy instrument jest (lub może być) kryptowalutą. Zasada: w razie wątpliwości – TAK (wykluczamy).
        /// </summary>
        public static bool IsCrypto(string name, string? description, string? baseAsset, string? quoteAsset)
        {
            if (!string.IsNullOrEmpty(baseAsset) && CryptoTickers.Contains(baseAsset!.Trim())) return true;
            if (!string.IsNullOrEmpty(quoteAsset) && CryptoTickers.Contains(quoteAsset!.Trim())) return true;

            string norm = Normalize(name);
            if (norm.Length == 0) return true; // brak nazwy = nie ufamy

            if (CryptoTickers.Contains(norm)) return true;
            if (norm.StartsWith("CRYPTO", StringComparison.Ordinal) || norm.Contains("CRYPTO")) return true;

            // Pary typu BTCUSD, ETHUSDT, DOGEUSD, XRPEUR. Pomijamy tickery 1–2 znakowe w dopasowaniu prefiksowym (fałszywe trafienia).
            foreach (var t in CryptoTickers)
            {
                if (t.Length < 3) continue;
                if (norm.StartsWith(t, StringComparison.Ordinal))
                {
                    string rest = norm.Substring(t.Length);
                    if (rest.Length == 0 || QuoteSuffixes.Contains(rest)) return true;
                }
                if (norm.EndsWith(t, StringComparison.Ordinal) && norm.Length > t.Length)
                {
                    string head = norm.Substring(0, norm.Length - t.Length);
                    if (Fiat.Contains(head)) return true;
                }
            }

            // Tokeny nazwy rozdzielone separatorami, np. "BTC/USD", "ETH-PERP".
            foreach (var token in Tokens(name))
                if (token.Length >= 3 && CryptoTickers.Contains(token)) return true;

            if (!string.IsNullOrEmpty(description))
            {
                string d = description!.ToLowerInvariant();
                foreach (var kw in CryptoKeywords)
                    if (d.Contains(kw)) return true;
                foreach (var word in Tokens(d))
                    foreach (var w in CryptoWholeWords)
                        if (string.Equals(word, w, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private static IEnumerable<string> Tokens(string s)
        {
            if (string.IsNullOrEmpty(s)) yield break;
            var sb = new StringBuilder();
            foreach (char ch in s)
            {
                if (char.IsLetterOrDigit(ch)) sb.Append(char.ToUpperInvariant(ch));
                else if (sb.Length > 0) { yield return sb.ToString(); sb.Clear(); }
            }
            if (sb.Length > 0) yield return sb.ToString();
        }

        public static AssetClass Classify(string name, string? description, string? baseAsset, string? quoteAsset)
        {
            if (IsCrypto(name, description, baseAsset, quoteAsset)) return AssetClass.Crypto;

            string norm = Normalize(name);
            string desc = (description ?? "").ToLowerInvariant();
            string b = (baseAsset ?? "").Trim().ToUpperInvariant();
            string q = (quoteAsset ?? "").Trim().ToUpperInvariant();
            string upperName = (name ?? "").ToUpperInvariant();

            // Metale (przed Forex, bo XAU/XAG bywają traktowane jak waluty).
            if (MetalCodes.Contains(b)) return AssetClass.Metal;
            foreach (var m in MetalCodes)
                if (norm.StartsWith(m, StringComparison.Ordinal) && (norm.Length == m.Length || Fiat.Contains(norm.Substring(m.Length)))) return AssetClass.Metal;

            // Forex: obie strony to waluty fiducjarne.
            if (Fiat.Contains(b) && Fiat.Contains(q) && !string.Equals(b, q, StringComparison.OrdinalIgnoreCase)) return AssetClass.Forex;
            if (norm.Length == 6 && Fiat.Contains(norm.Substring(0, 3)) && Fiat.Contains(norm.Substring(3, 3)) && norm.Substring(0, 3) != norm.Substring(3, 3))
                return AssetClass.Forex;

            if (MatchesName(norm, EnergyNames) || ContainsAny(" " + desc + " ", EnergyKeywords)) return AssetClass.Energy;
            if (MatchesName(norm, BondNames) || ContainsAny(desc, BondKeywords)) return AssetClass.Bond;
            if (MatchesName(norm, CommodityNames) || ContainsAny(desc, CommodityKeywords)) return AssetClass.Commodity;
            if (MatchesName(norm, IndexNames) || desc.Contains("index") || desc.Contains("indeks")) return AssetClass.Index;

            string ticker = TickerWithoutSuffix(upperName);
            bool hasStockSuffix = StockSuffixes.Any(s => upperName.EndsWith(s, StringComparison.Ordinal));
            if (KnownEtfs.Contains(ticker) || desc.Contains(" etf") || desc.StartsWith("etf") || desc.Contains("exchange traded") || desc.Contains("spdr") || desc.Contains("ishares"))
                return AssetClass.Etf;
            if (hasStockSuffix || ContainsAny(" " + desc, CompanyKeywords)) return AssetClass.Stock;

            return AssetClass.Unknown;
        }

        private static string TickerWithoutSuffix(string upperName)
        {
            int dot = upperName.IndexOf('.');
            if (dot > 0) return upperName.Substring(0, dot);
            int us = upperName.IndexOf('_');
            if (us > 0) return upperName.Substring(0, us);
            return upperName;
        }

        private static bool MatchesName(string norm, string[] names)
        {
            foreach (var n in names)
            {
                if (norm == n) return true;
                // Warianty z sufiksem waluty lub ".cash"/"m" itp.: GER40EUR, US500CASH, XTIUSD
                if (n.Length >= 3 && norm.StartsWith(n, StringComparison.Ordinal))
                {
                    string rest = norm.Substring(n.Length);
                    if (rest.Length == 0 || Fiat.Contains(rest) || rest == "CASH" || rest == "SPOT" || rest == "M" || rest == "X") return true;
                }
            }
            return false;
        }

        private static bool ContainsAny(string text, string[] keywords)
        {
            foreach (var k in keywords)
                if (text.Contains(k)) return true;
            return false;
        }

        /// <summary>Waluty, na które instrument daje ekspozycję (dla kontroli koncentracji walutowej).</summary>
        public static (string? Base, string? Quote) CurrencyLegs(AssetClass c, string name, string? baseAsset, string? quoteAsset)
        {
            string norm = Normalize(name);
            string? b = string.IsNullOrWhiteSpace(baseAsset) ? null : baseAsset!.Trim().ToUpperInvariant();
            string? q = string.IsNullOrWhiteSpace(quoteAsset) ? null : quoteAsset!.Trim().ToUpperInvariant();
            if (c == AssetClass.Forex)
            {
                if (b == null && norm.Length == 6) b = norm.Substring(0, 3);
                if (q == null && norm.Length == 6) q = norm.Substring(3, 3);
                return (b, q);
            }
            if (c == AssetClass.Metal)
            {
                // Metale szlachetne zachowują się jak waluty (np. long XAUUSD = krótko USD).
                return (b ?? (norm.Length >= 3 ? norm.Substring(0, 3) : norm), q);
            }
            // Indeksy, akcje, surowce: ekspozycja na samo aktywo (kupno US500 nie jest krótką pozycją na USD).
            return (norm, null);
        }
    }
}
