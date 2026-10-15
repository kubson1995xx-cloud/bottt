//+------------------------------------------------------------------+
//| APEX Bridge dla MetaTrader 5                                     |
//| Łączy terminal MT5 z aplikacją APEX Desktop (protokół APEX       |
//| Bridge v1: JSON w liniach przez TCP, 127.0.0.1).                 |
//|                                                                  |
//| UWAGA: plik nie był kompilowany w środowisku, w którym powstał   |
//| (brak MetaEditora). Przed użyciem skompiluj go w MetaEditorze    |
//| (F7) i przetestuj WYŁĄCZNIE na koncie DEMO.                      |
//|                                                                  |
//| Wymagane: Narzędzia → Opcje → Doradcy → "Zezwalaj na handel      |
//| algorytmiczny". Gniazda do 127.0.0.1 nie wymagają listy URL.     |
//| Jeśli terminal zgłosi błąd 4014, dodaj 127.0.0.1 w opcjach.      |
//+------------------------------------------------------------------+
#property copyright "APEX Trading AI"
#property version   "1.00"

#include <Trade/Trade.mqh>

input string InpPairingKey  = "";          // Klucz parowania (z panelu APEX)
input string InpHost        = "127.0.0.1"; // Adres APEX Desktop
input int    InpPort        = 5078;        // Port TCP APEX Desktop
input bool   InpAllowLive   = false;       // Zezwól na konto LIVE (świadoma decyzja!)
input long   InpMagic       = 770077;      // Numer magic pozycji APEX
input string InpLabel       = "APEX";      // Etykieta APEX (musi zgadzać się z ustawieniem APEX Desktop)
input int    InpMaxSymbols  = 60;          // Maks. subskrybowanych instrumentów
input int    InpDeviation   = 20;          // Maks. poślizg w punktach (gdy APEX nie poda limitu)

#define BRIDGE_VERSION "1.0.0"
#define MAX_SUBS 200
#define MAX_TFS 6

CTrade   g_trade;
int      g_socket      = INVALID_HANDLE;
bool     g_welcomed    = false;
string   g_rx          = "";
datetime g_nextConnect = 0;
datetime g_lastAccount = 0;
long     g_tzOffset    = 0; // sekundy: czas serwera - UTC

string   g_subs[MAX_SUBS];
int      g_subCount = 0;
string   g_tfs[MAX_TFS];
int      g_tfCount  = 0;
datetime g_lastBar[MAX_SUBS][MAX_TFS];

//+------------------------------------------------------------------+
//| Pomocnicze: czas i JSON                                          |
//+------------------------------------------------------------------+
void UpdateTzOffset()
{
   long diff = (long)(TimeTradeServer() - TimeGMT());
   g_tzOffset = (long)MathRound((double)diff / 900.0) * 900; // zaokrąglenie do 15 min
}

long ToUtcSec(datetime serverTime) { return (long)serverTime - g_tzOffset; }

string JEsc(string s)
{
   string r = s;
   StringReplace(r, "\\", "\\\\");
   StringReplace(r, "\"", "\\\"");
   StringReplace(r, "\n", " ");
   StringReplace(r, "\r", " ");
   StringReplace(r, "\t", " ");
   return r;
}

string JStr(string key, string value) { return "\"" + key + "\":\"" + JEsc(value) + "\""; }
string JNum(string key, double value, int digits = 8)
{
   if(!MathIsValidNumber(value)) return "\"" + key + "\":null";
   return "\"" + key + "\":" + DoubleToString(value, digits);
}
string JInt(string key, long value) { return "\"" + key + "\":" + IntegerToString(value); }
string JBool(string key, bool value) { return "\"" + key + "\":" + (value ? "true" : "false"); }

// Wyszukuje wartość dla klucza w płaskim obiekcie JSON (wystarcza dla komunikatów APEX).
int JFind(const string json, const string key)
{
   string pat = "\"" + key + "\"";
   int p = StringFind(json, pat);
   if(p < 0) return -1;
   p = StringFind(json, ":", p + StringLen(pat));
   if(p < 0) return -1;
   p++;
   while(p < StringLen(json) && (StringGetCharacter(json, p) == ' ')) p++;
   return p;
}

string JGetString(const string json, const string key)
{
   int p = JFind(json, key);
   if(p < 0 || StringGetCharacter(json, p) != '"') return "";
   string out = "";
   for(int i = p + 1; i < StringLen(json); i++)
   {
      ushort c = StringGetCharacter(json, i);
      if(c == '\\' && i + 1 < StringLen(json)) { out += ShortToString(StringGetCharacter(json, i + 1)); i++; continue; }
      if(c == '"') break;
      out += ShortToString(c);
   }
   return out;
}

double JGetNumber(const string json, const string key, double def)
{
   int p = JFind(json, key);
   if(p < 0) return def;
   string num = "";
   for(int i = p; i < StringLen(json); i++)
   {
      ushort c = StringGetCharacter(json, i);
      if((c >= '0' && c <= '9') || c == '.' || c == '-' || c == '+' || c == 'e' || c == 'E') num += ShortToString(c);
      else break;
   }
   if(num == "") return def; // null
   return StringToDouble(num);
}

bool JGetBool(const string json, const string key)
{
   int p = JFind(json, key);
   return p >= 0 && StringSubstr(json, p, 4) == "true";
}

// Odczytuje tablicę napisów: "key":["a","b"]
int JGetStringArray(const string json, const string key, string &out[])
{
   ArrayResize(out, 0);
   int p = JFind(json, key);
   if(p < 0 || StringGetCharacter(json, p) != '[') return 0;
   int i = p + 1;
   while(i < StringLen(json))
   {
      ushort c = StringGetCharacter(json, i);
      if(c == ']') break;
      if(c == '"')
      {
         string s = "";
         i++;
         while(i < StringLen(json) && StringGetCharacter(json, i) != '"') { s += ShortToString(StringGetCharacter(json, i)); i++; }
         int n = ArraySize(out);
         ArrayResize(out, n + 1);
         out[n] = s;
      }
      i++;
   }
   return ArraySize(out);
}

//+------------------------------------------------------------------+
//| Gniazdo TCP                                                      |
//+------------------------------------------------------------------+
bool SendLine(string json)
{
   if(g_socket == INVALID_HANDLE) return false;
   uchar buf[];
   int len = StringToCharArray(json + "\n", buf, 0, WHOLE_ARRAY, CP_UTF8) - 1; // bez kończącego zera
   if(len <= 0) return false;
   int sent = SocketSend(g_socket, buf, (uint)len);
   if(sent != len)
   {
      Print("APEX Bridge: błąd wysyłania (", GetLastError(), ") – zamykam połączenie");
      Disconnect();
      return false;
   }
   return true;
}

void Disconnect()
{
   if(g_socket != INVALID_HANDLE) SocketClose(g_socket);
   g_socket = INVALID_HANDLE;
   g_welcomed = false;
   g_rx = "";
   g_nextConnect = TimeLocal() + 5;
   Comment("APEX Bridge: rozłączono – ponowię połączenie za 5 s");
}

void Connect()
{
   g_socket = SocketCreate();
   if(g_socket == INVALID_HANDLE) { Print("APEX Bridge: SocketCreate błąd ", GetLastError()); g_nextConnect = TimeLocal() + 5; return; }
   if(!SocketConnect(g_socket, InpHost, (uint)InpPort, 3000))
   {
      Print("APEX Bridge: brak połączenia z APEX Desktop ", InpHost, ":", InpPort, " (błąd ", GetLastError(), ")");
      SocketClose(g_socket);
      g_socket = INVALID_HANDLE;
      g_nextConnect = TimeLocal() + 5;
      Comment("APEX Bridge: brak połączenia z APEX Desktop – ponowię za 5 s");
      return;
   }
   UpdateTzOffset();
   string hello = "{" + JStr("type", "hello") + "," + JInt("protocol", 1) + "," + JStr("platform", "MT5") + "," +
                  JStr("bridgeVersion", BRIDGE_VERSION) + "," + JStr("key", InpPairingKey) + ",\"account\":" + AccountJson() + "," +
                  JInt("time", (long)TimeGMT() * 1000) + "}";
   SendLine(hello);
   Comment("APEX Bridge: połączono, oczekiwanie na akceptację APEX…");
}

void ReadSocket()
{
   if(g_socket == INVALID_HANDLE) return;
   if(!SocketIsConnected(g_socket)) { Disconnect(); return; }
   uint avail = SocketIsReadable(g_socket);
   while(avail > 0)
   {
      uchar buf[];
      int n = SocketRead(g_socket, buf, avail, 100);
      if(n <= 0) break;
      g_rx += CharArrayToString(buf, 0, n, CP_UTF8);
      avail = SocketIsReadable(g_socket);
   }
   int nl = StringFind(g_rx, "\n");
   while(nl >= 0)
   {
      string line = StringSubstr(g_rx, 0, nl);
      g_rx = StringSubstr(g_rx, nl + 1);
      if(StringLen(line) > 0) Handle(line);
      nl = StringFind(g_rx, "\n");
   }
   if(StringLen(g_rx) > 4 * 1024 * 1024) g_rx = ""; // ochrona pamięci
}

//+------------------------------------------------------------------+
//| Dane konta, symboli, notowań i pozycji                           |
//+------------------------------------------------------------------+
string AccountJson()
{
   bool live = (ENUM_ACCOUNT_TRADE_MODE)AccountInfoInteger(ACCOUNT_TRADE_MODE) == ACCOUNT_TRADE_MODE_REAL;
   bool hedging = (ENUM_ACCOUNT_MARGIN_MODE)AccountInfoInteger(ACCOUNT_MARGIN_MODE) == ACCOUNT_MARGIN_MODE_RETAIL_HEDGING;
   double ml = AccountInfoDouble(ACCOUNT_MARGIN_LEVEL);
   return "{" + JInt("number", AccountInfoInteger(ACCOUNT_LOGIN)) + "," + JStr("broker", AccountInfoString(ACCOUNT_COMPANY)) + "," +
          JStr("currency", AccountInfoString(ACCOUNT_CURRENCY)) + "," + JBool("isLive", live) + "," + JBool("hedging", hedging) + "," +
          JNum("balance", AccountInfoDouble(ACCOUNT_BALANCE), 2) + "," + JNum("equity", AccountInfoDouble(ACCOUNT_EQUITY), 2) + "," +
          JNum("margin", AccountInfoDouble(ACCOUNT_MARGIN), 2) + "," + JNum("freeMargin", AccountInfoDouble(ACCOUNT_MARGIN_FREE), 2) + "," +
          (ml > 0 ? JNum("marginLevel", ml, 2) : "\"marginLevel\":null") + "}";
}

double PipSize(string sym)
{
   int digits = (int)SymbolInfoInteger(sym, SYMBOL_DIGITS);
   double point = SymbolInfoDouble(sym, SYMBOL_POINT);
   return (digits == 3 || digits == 5) ? point * 10.0 : point;
}

// Wartość 1 pipsa dla 1 JEDNOSTKI (nie lota) w walucie konta.
double PipValuePerUnit(string sym)
{
   double tickValue = SymbolInfoDouble(sym, SYMBOL_TRADE_TICK_VALUE);
   double tickSize = SymbolInfoDouble(sym, SYMBOL_TRADE_TICK_SIZE);
   double contract = SymbolInfoDouble(sym, SYMBOL_TRADE_CONTRACT_SIZE);
   if(tickSize <= 0 || contract <= 0) return 0;
   return tickValue / tickSize * PipSize(sym) / contract;
}

bool IsCryptoPath(string sym)
{
   string path = SymbolInfoString(sym, SYMBOL_PATH);
   string low = path;
   StringToLower(low);
   return StringFind(low, "crypto") >= 0 || StringFind(low, "bitcoin") >= 0 || StringFind(low, "coin") >= 0;
}

string SymbolJson(string sym)
{
   double contract = SymbolInfoDouble(sym, SYMBOL_TRADE_CONTRACT_SIZE);
   ENUM_SYMBOL_TRADE_MODE tm = (ENUM_SYMBOL_TRADE_MODE)SymbolInfoInteger(sym, SYMBOL_TRADE_MODE);
   double point = SymbolInfoDouble(sym, SYMBOL_POINT);
   double pip = PipSize(sym);
   double stopsPips = pip > 0 ? SymbolInfoInteger(sym, SYMBOL_TRADE_STOPS_LEVEL) * point / pip : 0;
   string desc = SymbolInfoString(sym, SYMBOL_DESCRIPTION) + " [" + SymbolInfoString(sym, SYMBOL_PATH) + "]";
   return "{" + JStr("name", sym) + "," + JStr("description", desc) + "," +
          JStr("baseAsset", SymbolInfoString(sym, SYMBOL_CURRENCY_BASE)) + "," + JStr("quoteAsset", SymbolInfoString(sym, SYMBOL_CURRENCY_PROFIT)) + "," +
          JInt("digits", SymbolInfoInteger(sym, SYMBOL_DIGITS)) + "," + JNum("pipSize", pip, 10) + "," +
          JNum("tickSize", SymbolInfoDouble(sym, SYMBOL_TRADE_TICK_SIZE), 10) + "," + JNum("pipValue", PipValuePerUnit(sym), 12) + "," +
          JNum("lotSize", contract, 4) + "," + JNum("volumeMin", SymbolInfoDouble(sym, SYMBOL_VOLUME_MIN) * contract, 6) + "," +
          JNum("volumeStep", SymbolInfoDouble(sym, SYMBOL_VOLUME_STEP) * contract, 6) + "," + JNum("volumeMax", SymbolInfoDouble(sym, SYMBOL_VOLUME_MAX) * contract, 2) + "," +
          JNum("minStopPips", stopsPips, 2) + "," + JBool("tradingEnabled", tm == SYMBOL_TRADE_MODE_FULL) + "," + JBool("closeOnly", tm == SYMBOL_TRADE_MODE_CLOSEONLY) + "," +
          // MT5 nie udostępnia prowizji przez API – APEX Desktop wymaga jej ręcznego ustawienia.
          JStr("commissionKind", "Unknown") + "," + JNum("commissionValue", 0, 2) + "," + JNum("usdToAccount", 1, 2) + "," + JNum("quoteToAccount", 1, 2) + "," +
          JBool("isCustom", SymbolInfoInteger(sym, SYMBOL_CUSTOM) != 0) + "}";
}

void SendSymbols()
{
   int total = SymbolsTotal(false);
   string chunk = "";
   int inChunk = 0, sent = 0;
   for(int i = 0; i < total; i++)
   {
      string sym = SymbolName(i, false);
      if(sym == "" || IsCryptoPath(sym)) continue; // kryptowaluty nie są nawet przekazywane
      chunk += (inChunk > 0 ? "," : "") + SymbolJson(sym);
      inChunk++;
      sent++;
      if(inChunk >= 100)
      {
         SendLine("{" + JStr("type", "symbols") + ",\"items\":[" + chunk + "]," + JBool("final", false) + "}");
         chunk = "";
         inChunk = 0;
      }
   }
   SendLine("{" + JStr("type", "symbols") + ",\"items\":[" + chunk + "]," + JBool("final", true) + "}");
   SendLine("{" + JStr("type", "log") + "," + JStr("level", "info") + "," + JStr("message", "Wysłano " + IntegerToString(sent) + " symboli (bez krypto).") + "}");
}

ENUM_TIMEFRAMES ToPeriod(string tf)
{
   if(tf == "M5") return PERIOD_M5;
   if(tf == "M15") return PERIOD_M15;
   if(tf == "M30") return PERIOD_M30;
   if(tf == "H1") return PERIOD_H1;
   if(tf == "H4") return PERIOD_H4;
   if(tf == "D1") return PERIOD_D1;
   return PERIOD_CURRENT;
}

string BarJson(const MqlRates &r)
{
   return "[" + IntegerToString(ToUtcSec(r.time)) + "," + DoubleToString(r.open, 10) + "," + DoubleToString(r.high, 10) + "," +
          DoubleToString(r.low, 10) + "," + DoubleToString(r.close, 10) + "," + IntegerToString(r.tick_volume) + "]";
}

void SendBarsSnapshot(string sym, string tf, int count)
{
   MqlRates rates[];
   ArraySetAsSeries(rates, false);
   int got = CopyRates(sym, ToPeriod(tf), 1, count, rates); // od 1 = bez bieżącej (niezamkniętej) świecy
   if(got <= 0)
   {
      SendLine("{" + JStr("type", "log") + "," + JStr("level", "warn") + "," + JStr("message", "Brak historii " + sym + " " + tf + " (błąd " + IntegerToString(GetLastError()) + ")") + "}");
      return;
   }
   string items = "";
   for(int i = 0; i < got; i++) items += (i > 0 ? "," : "") + BarJson(rates[i]);
   SendLine("{" + JStr("type", "bars") + "," + JStr("symbol", sym) + "," + JStr("tf", tf) + "," + JBool("snapshot", true) + ",\"bars\":[" + items + "]}");
}

void CheckNewBars()
{
   for(int s = 0; s < g_subCount; s++)
      for(int t = 0; t < g_tfCount; t++)
      {
         datetime closedOpen = iTime(g_subs[s], ToPeriod(g_tfs[t]), 1);
         if(closedOpen <= 0 || closedOpen <= g_lastBar[s][t]) continue;
         MqlRates r[];
         if(CopyRates(g_subs[s], ToPeriod(g_tfs[t]), 1, 1, r) == 1)
         {
            SendLine("{" + JStr("type", "bars") + "," + JStr("symbol", g_subs[s]) + "," + JStr("tf", g_tfs[t]) + "," + JBool("snapshot", false) + ",\"bars\":[" + BarJson(r[0]) + "]}");
            g_lastBar[s][t] = closedOpen;
         }
      }
}

// Czy rynek jest otwarty teraz oraz ile sekund do końca bieżącej sesji handlowej.
bool MarketOpen(string sym, long &ttc)
{
   ttc = -1;
   MqlDateTime now;
   datetime srv = TimeTradeServer();
   TimeToStruct(srv, now);
   datetime dayStart = srv - (now.hour * 3600 + now.min * 60 + now.sec);
   for(uint i = 0; i < 10; i++)
   {
      datetime from, to;
      if(!SymbolInfoSessionTrade(sym, (ENUM_DAY_OF_WEEK)now.day_of_week, i, from, to)) break;
      datetime f = dayStart + (long)from;
      datetime e = dayStart + (long)to;
      if(srv >= f && srv < e) { ttc = (long)(e - srv); return true; }
   }
   return false;
}

void SendQuotes()
{
   if(g_subCount == 0) return;
   string items = "";
   int n = 0;
   for(int s = 0; s < g_subCount; s++)
   {
      MqlTick tick;
      if(!SymbolInfoTick(g_subs[s], tick) || tick.bid <= 0 || tick.ask <= 0) continue;
      long ttc;
      bool open = MarketOpen(g_subs[s], ttc);
      long tUtcMs = (long)tick.time_msc - g_tzOffset * 1000;
      items += (n > 0 ? "," : "") + "{" + JStr("s", g_subs[s]) + "," + JNum("b", tick.bid, 10) + "," + JNum("a", tick.ask, 10) + "," +
               JInt("t", tUtcMs) + "," + JNum("pv", PipValuePerUnit(g_subs[s]), 12) + "," + JBool("o", open) + "," + JInt("ttc", ttc) + "}";
      n++;
   }
   SendLine("{" + JStr("type", "quotes") + ",\"items\":[" + items + "]}");
}

bool IsOwnPosition()
{
   return PositionGetInteger(POSITION_MAGIC) == InpMagic;
}

string PositionJson(ulong ticket)
{
   string sym = PositionGetString(POSITION_SYMBOL);
   double contract = SymbolInfoDouble(sym, SYMBOL_TRADE_CONTRACT_SIZE);
   bool buy = (ENUM_POSITION_TYPE)PositionGetInteger(POSITION_TYPE) == POSITION_TYPE_BUY;
   double sl = PositionGetDouble(POSITION_SL), tp = PositionGetDouble(POSITION_TP);
   double profit = PositionGetDouble(POSITION_PROFIT), swap = PositionGetDouble(POSITION_SWAP);
   long entryMs = (long)PositionGetInteger(POSITION_TIME_MSC) - g_tzOffset * 1000;
   return "{" + JInt("id", (long)ticket) + "," + JStr("symbol", sym) + "," + JStr("side", buy ? "buy" : "sell") + "," +
          JNum("units", PositionGetDouble(POSITION_VOLUME) * contract, 6) + "," + JNum("entry", PositionGetDouble(POSITION_PRICE_OPEN), 10) + "," +
          JInt("entryTime", entryMs) + "," + (sl > 0 ? JNum("sl", sl, 10) : "\"sl\":null") + "," + (tp > 0 ? JNum("tp", tp, 10) : "\"tp\":null") + "," +
          JStr("label", IsOwnPosition() ? InpLabel : "") + "," + JStr("comment", PositionGetString(POSITION_COMMENT)) + "," +
          JNum("net", profit + swap, 2) + "," + JNum("gross", profit, 2) + "," + JNum("commission", 0, 2) + "," + JNum("swap", swap, 2) + "," +
          JNum("price", PositionGetDouble(POSITION_PRICE_CURRENT), 10) + "," + JNum("margin", 0, 2) + "}";
}

void SendPositions()
{
   if(!g_welcomed) return;
   string items = "";
   int n = 0;
   for(int i = PositionsTotal() - 1; i >= 0; i--)
   {
      ulong ticket = PositionGetTicket(i);
      if(ticket == 0 || !PositionSelectByTicket(ticket)) continue;
      items += (n > 0 ? "," : "") + PositionJson(ticket);
      n++;
   }
   SendLine("{" + JStr("type", "positions") + ",\"items\":[" + items + "]}");
}

//+------------------------------------------------------------------+
//| Polecenia z APEX                                                 |
//+------------------------------------------------------------------+
string ErrorName(uint rc)
{
   switch(rc)
   {
      case TRADE_RETCODE_NO_MONEY:       return "NoMoney";
      case TRADE_RETCODE_MARKET_CLOSED:  return "MarketClosed";
      case TRADE_RETCODE_INVALID_STOPS:  return "InvalidStopLossTakeProfit";
      case TRADE_RETCODE_INVALID_VOLUME: return "BadVolume";
      case TRADE_RETCODE_TRADE_DISABLED: return "NoTradingPermission";
      case TRADE_RETCODE_TIMEOUT:        return "Timeout";
      case TRADE_RETCODE_CONNECTION:     return "Disconnected";
      case TRADE_RETCODE_ERROR:          return "TechnicalError";
      default:                           return "InvalidRequest";
   }
}

string StatusOf(uint rc)
{
   if(rc == TRADE_RETCODE_DONE || rc == TRADE_RETCODE_DONE_PARTIAL || rc == TRADE_RETCODE_PLACED) return "filled";
   if(rc == TRADE_RETCODE_TIMEOUT || rc == TRADE_RETCODE_CONNECTION || rc == TRADE_RETCODE_ERROR || rc == 0) return "uncertain";
   return "rejected";
}

void Reply(string id, uint rc, long positionId, double price, string message)
{
   SendPositions();
   string st = StatusOf(rc);
   SendLine("{" + JStr("type", "result") + "," + JStr("id", id) + "," + JStr("status", st) + "," + JInt("positionId", positionId) + "," +
            JNum("price", price, 10) + "," + (st == "filled" ? "\"error\":null" : JStr("error", ErrorName(rc))) + "," + JStr("message", message) + "}");
}

void Reject(string id, string error, string message)
{
   SendLine("{" + JStr("type", "result") + "," + JStr("id", id) + "," + JStr("status", "rejected") + "," + JStr("error", error) + "," + JStr("message", message) + "}");
}

bool LiveBlocked(string id)
{
   bool live = (ENUM_ACCOUNT_TRADE_MODE)AccountInfoInteger(ACCOUNT_TRADE_MODE) == ACCOUNT_TRADE_MODE_REAL;
   if(live && !InpAllowLive) { Reject(id, "NoTradingPermission", "Konto LIVE zablokowane w moście MT5."); return true; }
   return false;
}

void OnOpen(string json)
{
   string id = JGetString(json, "id");
   if(LiveBlocked(id)) return;
   string sym = JGetString(json, "symbol");
   string side = JGetString(json, "side");
   double units = JGetNumber(json, "units", 0);
   double slPips = JGetNumber(json, "slPips", 0);
   double tpPips = JGetNumber(json, "tpPips", 0);
   double maxSlip = JGetNumber(json, "maxSlipPips", 0);
   string comment = JGetString(json, "comment");
   string label = JGetString(json, "label");
   if(StringFind(label, InpLabel) != 0) { Reject(id, "InvalidRequest", "nieprawidłowa etykieta"); return; }
   if(slPips <= 0) { Reject(id, "InvalidStopLossTakeProfit", "zlecenie bez SL jest niedozwolone"); return; }
   if(IsCryptoPath(sym)) { Reject(id, "InvalidRequest", "kryptowaluty wykluczone"); return; }
   if(!SymbolSelect(sym, true)) { Reject(id, "UnknownSymbol", "nieznany symbol"); return; }
   double contract = SymbolInfoDouble(sym, SYMBOL_TRADE_CONTRACT_SIZE);
   double step = SymbolInfoDouble(sym, SYMBOL_VOLUME_STEP), vmin = SymbolInfoDouble(sym, SYMBOL_VOLUME_MIN), vmax = SymbolInfoDouble(sym, SYMBOL_VOLUME_MAX);
   if(contract <= 0 || step <= 0) { Reject(id, "BadVolume", "brak specyfikacji wolumenu"); return; }
   double lots = MathFloor(units / contract / step + 1e-9) * step; // zaokrąglenie wyłącznie w dół
   if(lots < vmin) { Reject(id, "BadVolume", "wolumen poniżej minimum"); return; }
   if(lots > vmax) lots = MathFloor(vmax / step) * step;
   int digits = (int)SymbolInfoInteger(sym, SYMBOL_DIGITS);
   double pip = PipSize(sym);
   MqlTick tick;
   if(!SymbolInfoTick(sym, tick)) { Reject(id, "MarketClosed", "brak notowań"); return; }
   bool buy = side == "buy";
   double price = buy ? tick.ask : tick.bid;
   double sl = NormalizeDouble(buy ? price - slPips * pip : price + slPips * pip, digits);
   double tp = tpPips > 0 ? NormalizeDouble(buy ? price + tpPips * pip : price - tpPips * pip, digits) : 0;
   double point = SymbolInfoDouble(sym, SYMBOL_POINT);
   g_trade.SetExpertMagicNumber(InpMagic);
   g_trade.SetTypeFillingBySymbol(sym);
   g_trade.SetDeviationInPoints(maxSlip > 0 && point > 0 ? (ulong)MathMax(1, MathRound(maxSlip * pip / point)) : (ulong)InpDeviation);
   if(StringLen(comment) > 31) comment = StringSubstr(comment, 0, 31);
   bool ok = buy ? g_trade.Buy(lots, sym, price, sl, tp, comment) : g_trade.Sell(lots, sym, price, sl, tp, comment);
   uint rc = g_trade.ResultRetcode();
   long pos = (long)g_trade.ResultOrder(); // na kontach hedging bilet pozycji = bilet zlecenia
   Reply(id, ok ? rc : (rc == 0 ? TRADE_RETCODE_ERROR : rc), pos, g_trade.ResultPrice(), g_trade.ResultRetcodeDescription());
}

bool SelectOwn(ulong ticket, string id)
{
   if(!PositionSelectByTicket(ticket)) { Reject(id, "EntityNotFound", "pozycja nie istnieje"); return false; }
   if(!IsOwnPosition()) { Reject(id, "InvalidRequest", "to nie jest pozycja APEX – most jej nie dotknie"); return false; }
   return true;
}

void OnModify(string json)
{
   string id = JGetString(json, "id");
   if(LiveBlocked(id)) return;
   ulong ticket = (ulong)JGetNumber(json, "positionId", 0);
   double sl = JGetNumber(json, "sl", 0);
   if(!SelectOwn(ticket, id)) return;
   string sym = PositionGetString(POSITION_SYMBOL);
   sl = NormalizeDouble(sl, (int)SymbolInfoInteger(sym, SYMBOL_DIGITS));
   g_trade.SetExpertMagicNumber(InpMagic);
   g_trade.PositionModify(ticket, sl, PositionGetDouble(POSITION_TP));
   Reply(id, g_trade.ResultRetcode(), (long)ticket, sl, g_trade.ResultRetcodeDescription());
}

void OnClose(string json)
{
   string id = JGetString(json, "id");
   ulong ticket = (ulong)JGetNumber(json, "positionId", 0);
   double units = JGetNumber(json, "units", 0);
   if(!SelectOwn(ticket, id)) return;
   string sym = PositionGetString(POSITION_SYMBOL);
   double contract = SymbolInfoDouble(sym, SYMBOL_TRADE_CONTRACT_SIZE), step = SymbolInfoDouble(sym, SYMBOL_VOLUME_STEP);
   double vol = PositionGetDouble(POSITION_VOLUME);
   g_trade.SetExpertMagicNumber(InpMagic);
   g_trade.SetTypeFillingBySymbol(sym);
   if(units > 0 && contract > 0 && units / contract < vol)
   {
      double lots = MathFloor(units / contract / step + 1e-9) * step;
      if(lots <= 0) { Reject(id, "BadVolume", "wolumen częściowego zamknięcia za mały"); return; }
      g_trade.PositionClosePartial(ticket, lots);
   }
   else g_trade.PositionClose(ticket);
   Reply(id, g_trade.ResultRetcode(), (long)ticket, g_trade.ResultPrice(), g_trade.ResultRetcodeDescription());
}

void OnMargin(string json)
{
   string id = JGetString(json, "id");
   string sym = JGetString(json, "symbol");
   double units = JGetNumber(json, "units", 0);
   bool buy = JGetString(json, "side") == "buy";
   double contract = SymbolInfoDouble(sym, SYMBOL_TRADE_CONTRACT_SIZE);
   double margin = 0;
   MqlTick tick;
   bool ok = contract > 0 && SymbolInfoTick(sym, tick) &&
             OrderCalcMargin(buy ? ORDER_TYPE_BUY : ORDER_TYPE_SELL, sym, units / contract, buy ? tick.ask : tick.bid, margin);
   SendLine("{" + JStr("type", "marginResult") + "," + JStr("id", id) + "," + (ok ? JNum("margin", margin, 2) : "\"margin\":null") + "}");
}

void OnSubscribe(string json)
{
   string syms[];
   string tfs[];
   JGetStringArray(json, "symbols", syms);
   JGetStringArray(json, "tfs", tfs);
   g_subCount = 0;
   g_tfCount = 0;
   for(int t = 0; t < ArraySize(tfs) && g_tfCount < MAX_TFS; t++)
      if(ToPeriod(tfs[t]) != PERIOD_CURRENT) g_tfs[g_tfCount++] = tfs[t];
   int histStart = StringFind(json, "\"history\"");
   string hist = histStart >= 0 ? StringSubstr(json, histStart) : "";
   for(int i = 0; i < ArraySize(syms) && g_subCount < MathMin(MAX_SUBS, InpMaxSymbols); i++)
   {
      if(IsCryptoPath(syms[i]) || !SymbolSelect(syms[i], true)) continue;
      int s = g_subCount++;
      g_subs[s] = syms[i];
      for(int t = 0; t < g_tfCount; t++)
      {
         int count = (int)JGetNumber(hist, g_tfs[t], 500);
         SendBarsSnapshot(g_subs[s], g_tfs[t], MathMin(count, 5000));
         g_lastBar[s][t] = iTime(g_subs[s], ToPeriod(g_tfs[t]), 1);
      }
   }
   SendQuotes();
   SendLine("{" + JStr("type", "log") + "," + JStr("level", "info") + "," + JStr("message", "Subskrypcja: " + IntegerToString(g_subCount) + " instrumentów.") + "}");
}

void Handle(string json)
{
   string type = JGetString(json, "type");
   if(type == "welcome")
   {
      if(!JGetBool(json, "accepted"))
      {
         string reason = JGetString(json, "reason");
         Print("APEX Bridge: APEX odrzucił połączenie – ", reason);
         Comment("APEX Bridge: odrzucono – ", reason);
         SocketClose(g_socket);
         g_socket = INVALID_HANDLE;
         g_nextConnect = TimeLocal() + 60;
         return;
      }
      g_welcomed = true;
      Comment("APEX Bridge ", BRIDGE_VERSION, ": połączono z APEX – konto ", AccountInfoInteger(ACCOUNT_LOGIN));
      SendSymbols();
      SendLine("{" + JStr("type", "account") + ",\"account\":" + AccountJson() + "}");
      SendPositions();
   }
   else if(type == "subscribe") OnSubscribe(json);
   else if(type == "open") OnOpen(json);
   else if(type == "modify") OnModify(json);
   else if(type == "close") OnClose(json);
   else if(type == "margin") OnMargin(json);
   else if(type == "ping") SendLine("{" + JStr("type", "pong") + "}");
}

//+------------------------------------------------------------------+
//| Zdarzenia EA                                                     |
//+------------------------------------------------------------------+
int OnInit()
{
   bool live = (ENUM_ACCOUNT_TRADE_MODE)AccountInfoInteger(ACCOUNT_TRADE_MODE) == ACCOUNT_TRADE_MODE_REAL;
   if(live && !InpAllowLive)
   {
      Alert("APEX Bridge: konto LIVE – most zablokowany. Działa tylko na DEMO, chyba że świadomie włączysz 'Zezwól na konto LIVE'.");
      return INIT_FAILED;
   }
   if(StringLen(InpPairingKey) < 8)
   {
      Alert("APEX Bridge: wpisz klucz parowania z panelu APEX (Połączenia platform).");
      return INIT_PARAMETERS_INCORRECT;
   }
   EventSetMillisecondTimer(500);
   Connect();
   return INIT_SUCCEEDED;
}

void OnDeinit(const int reason)
{
   EventKillTimer();
   if(g_socket != INVALID_HANDLE)
   {
      SendLine("{" + JStr("type", "bye") + "," + JStr("reason", "EA zatrzymany") + "}");
      SocketClose(g_socket);
   }
   Comment("");
}

void OnTimer()
{
   if(g_socket == INVALID_HANDLE)
   {
      if(TimeLocal() >= g_nextConnect) Connect();
      return;
   }
   ReadSocket();
   if(!g_welcomed) return;
   static ulong lastQuotes = 0;
   if(GetTickCount64() - lastQuotes >= 1000)
   {
      lastQuotes = GetTickCount64();
      UpdateTzOffset();
      SendQuotes();
      CheckNewBars();
   }
   if(TimeLocal() - g_lastAccount >= 2)
   {
      g_lastAccount = TimeLocal();
      SendLine("{" + JStr("type", "account") + ",\"account\":" + AccountJson() + "}");
      SendPositions();
   }
}

void OnTradeTransaction(const MqlTradeTransaction &trans, const MqlTradeRequest &request, const MqlTradeResult &result)
{
   if(!g_welcomed || trans.type != TRADE_TRANSACTION_DEAL_ADD) return;
   if(!HistoryDealSelect(trans.deal)) return;
   ENUM_DEAL_ENTRY entry = (ENUM_DEAL_ENTRY)HistoryDealGetInteger(trans.deal, DEAL_ENTRY);
   if(entry != DEAL_ENTRY_OUT && entry != DEAL_ENTRY_OUT_BY) { SendPositions(); return; }
   if(HistoryDealGetInteger(trans.deal, DEAL_MAGIC) != InpMagic) { SendPositions(); return; }
   ulong posId = (ulong)HistoryDealGetInteger(trans.deal, DEAL_POSITION_ID);
   if(PositionSelectByTicket(posId)) { SendPositions(); return; } // zamknięcie częściowe – pozycja nadal istnieje
   if(!HistorySelectByPosition(posId)) return;
   double gross = 0, comm = 0, swap = 0, entryPrice = 0, exitPrice = 0, units = 0;
   long entryTime = 0, exitTime = 0;
   string sym = "", comment = "", side = "buy", reason = "Closed";
   for(int i = 0; i < HistoryDealsTotal(); i++)
   {
      ulong d = HistoryDealGetTicket(i);
      if(d == 0) continue;
      ENUM_DEAL_ENTRY e = (ENUM_DEAL_ENTRY)HistoryDealGetInteger(d, DEAL_ENTRY);
      comm += HistoryDealGetDouble(d, DEAL_COMMISSION);
      swap += HistoryDealGetDouble(d, DEAL_SWAP);
      gross += HistoryDealGetDouble(d, DEAL_PROFIT);
      sym = HistoryDealGetString(d, DEAL_SYMBOL);
      double contract = SymbolInfoDouble(sym, SYMBOL_TRADE_CONTRACT_SIZE);
      if(e == DEAL_ENTRY_IN)
      {
         entryPrice = HistoryDealGetDouble(d, DEAL_PRICE);
         entryTime = (long)HistoryDealGetInteger(d, DEAL_TIME_MSC) - g_tzOffset * 1000;
         units = HistoryDealGetDouble(d, DEAL_VOLUME) * contract;
         comment = HistoryDealGetString(d, DEAL_COMMENT);
         side = (ENUM_DEAL_TYPE)HistoryDealGetInteger(d, DEAL_TYPE) == DEAL_TYPE_BUY ? "buy" : "sell";
      }
      else
      {
         exitPrice = HistoryDealGetDouble(d, DEAL_PRICE);
         exitTime = (long)HistoryDealGetInteger(d, DEAL_TIME_MSC) - g_tzOffset * 1000;
         ENUM_DEAL_REASON r = (ENUM_DEAL_REASON)HistoryDealGetInteger(d, DEAL_REASON);
         reason = r == DEAL_REASON_SL ? "StopLoss" : r == DEAL_REASON_TP ? "TakeProfit" : r == DEAL_REASON_SO ? "StopOut" : "Closed";
      }
   }
   SendPositions();
   SendLine("{" + JStr("type", "closed") + ",\"position\":{" + JInt("id", (long)posId) + "," + JStr("symbol", sym) + "," + JStr("label", InpLabel) + "," +
            JStr("comment", comment) + "," + JStr("side", side) + "," + JNum("units", units, 6) + "," + JNum("entry", entryPrice, 10) + "," +
            JNum("exit", exitPrice, 10) + "," + JInt("entryTime", entryTime) + "," + JInt("exitTime", exitTime) + "," + JNum("gross", gross, 2) + "," +
            JNum("commission", comm, 2) + "," + JNum("swap", swap, 2) + "," + JNum("net", gross + comm + swap, 2) + "," + JStr("reason", reason) + "}}");
}
//+------------------------------------------------------------------+
