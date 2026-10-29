using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
namespace Apex.Core.Naval;

internal static class NavalCashRiskPolicy
{
    internal static bool Active(NavalReservation r) => r.Pending > 0 || r.Inventory > 0 || !r.TerminalCoverage || r.ReductionUnsettled;
    internal static bool Text(string? s) => !string.IsNullOrWhiteSpace(s) && s.Length <= 200 && s == s.Trim() && !s.Any(char.IsControl);
    internal static void ValidateDefinition(NavalPortfolioDefinition d)
    {
        var l=d.Limits;var f=d.Fees;
        if(!Text(d.Id.PortfolioId)||!Text(d.Id.ReportingCurrency)||l.TradePct<=0||l.TradePct>.5m||l.DailyPct<=0||l.DailyPct>1.5m||
            l.DrawdownPct<=0||l.DrawdownPct>5||l.MaxLeverage<=0||l.MaxLeverage>1||l.CentralMax<1||l.CentralMax>3||
            l.AlphaMax<1||l.AlphaMax>2||l.BravoMax<1||l.BravoMax>2)throw new ArgumentException("Invalid Naval hard limits/identity");
        if(d.Members.Count<1||d.Members.Count>2||d.Members.Select(m=>m.Account).Distinct().Count()!=d.Members.Count||
            d.Members.Select(m=>(m.Account.Venue,m.Account.AccountId)).Distinct().Count()!=d.Members.Count)throw new ArgumentException("Fixed unique membership required");
        foreach(var m in d.Members)
            if(m.Account.Environment!=NavalEnvironment.Offline||!Text(m.Account.Venue)||!Text(m.Account.AccountId)||
                m.Account.QuoteCurrency!=d.Id.ReportingCurrency||m.Allocation<=0)throw new ArgumentException("Offline same-currency funded membership required");
        if(d.Catalog.Count==0||d.Catalog.Select(x=>x.Instrument).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=d.Catalog.Count||
            d.Catalog.Select(x=>x.BaseCurrency).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=d.Catalog.Count)throw new ArgumentException("Ambiguous catalog aliases");
        foreach(var i in d.Catalog)
            if(!Text(i.Instrument)||!Text(i.BaseCurrency)||i.BaseCurrency==i.QuoteCurrency||i.QuoteCurrency!=d.Id.ReportingCurrency||
                i.Quantum<=0||i.MinimumQuantity<=0||i.TickSize<=0)throw new ArgumentException("Invalid instrument quantum/currency");
        if(f.EntryRate<0||f.EntryRate>=1||f.ExitRate<0||f.ExitRate>=1||f.StopSlippageRate<0||f.StopSlippageRate>=1)
            throw new ArgumentException("Unsupported fee/slippage schedule");
        if(d.Signals.Select(x=>x.SignalId).Distinct().Count()!=d.Signals.Count)throw new ArgumentException("Duplicate trusted signal");
        foreach(var s in d.Signals)
            if(!Text(s.SignalId)||!Text(s.RuleIdentity)||!d.Members.Any(m=>m.Account==s.Account)||!Enum.IsDefined(typeof(NavalFleet),s.Fleet)||
                !d.Catalog.Any(i=>i.Instrument==s.Instrument)||s.EarliestUtc>s.ExpiresUtc)throw new ArgumentException("Invalid signal binding");
        _=checked(d.Members.Sum(m=>m.Allocation));
    }
    internal static decimal NetBuy(decimal q,NavalFeeSchedule f)=>checked(q*(f.EntryInBase?1-f.EntryRate:1));
    internal static decimal BuyDebit(decimal q,decimal p,NavalFeeSchedule f)=>checked(q*p*(f.EntryInBase?1:1+f.EntryRate));
    internal static decimal NetSell(decimal inventory,decimal p,NavalFeeSchedule f)=>checked(inventory/(f.ExitInBase?1+f.ExitRate:1)*p*(f.ExitInBase?1:1-f.ExitRate));
    internal static decimal StopValue(decimal inventory,NavalReservation r,NavalFeeSchedule f)=>NetSell(inventory,checked(r.Intent.StopPrice*(1-f.StopSlippageRate)),f);
    internal static NavalReservation Holds(NavalReservation r,NavalPortfolioDefinition d,IReadOnlyDictionary<string,decimal> marks)
    {
        checked
        {
            var f=d.Fees;var mark=marks.TryGetValue(r.Intent.Instrument,out var p)?p:r.Intent.EntryPriceCeiling;
            decimal pendingNet=NetBuy(r.Pending,f),debit=BuyDebit(r.Pending,r.Intent.EntryPriceCeiling,f);
            decimal buffer=f.ExitInBase?0:(pendingNet+r.Inventory)*Math.Max(Math.Max(r.Intent.EntryPriceCeiling,r.Intent.TakeProfitPrice),mark)*f.ExitRate;
            // Posting a reduction is not proof that its fees/fills are final, even at zero inventory.
            if(r.ReductionUnsettled)buffer=Math.Max(buffer,r.CloseFeeHold);
            decimal risk=Math.Max(0,debit-StopValue(pendingNet,r,f))+Math.Max(0,NetSell(r.Inventory,mark,f)-StopValue(r.Inventory,r,f));
            return r with{EntryHold=debit,CloseFeeHold=buffer,RiskHold=risk};
        }
    }
    internal static decimal Held(NavalReservation r)=>checked(r.EntryHold+r.CloseFeeHold);
    internal static NavalPortfolioState Refresh(NavalPortfolioState s,NavalPortfolioDefinition d,DateTimeOffset now,bool reconciled)
    {
        checked
        {
            s=s with{Reservations=s.Reservations.Select(r=>Holds(r,d,s.Marks)).ToArray()};
            bool marked=d.Catalog.All(i=>s.Marks.TryGetValue(i.Instrument,out var p)&&p>0);
            if(!marked)return s with{LiquidationEquity=null};
            decimal eq=s.Accounts.Sum(a=>a.Cash)+s.Reservations.Sum(r=>NetSell(r.Inventory,s.Marks[r.Intent.Instrument],d.Fees));
            var key=now.UtcDateTime.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture);
            if(reconciled&&(s.DayKey==""||string.CompareOrdinal(key,s.DayKey)>0))s=s with{DayKey=key,DayStartEquity=eq,DailyLatch=false};
            decimal peak=Math.Max(s.PeakEquity,eq);
            bool daily=s.DailyLatch||(s.DayStartEquity>0&&s.DayStartEquity-eq>=s.DayStartEquity*d.Limits.DailyPct/100);
            bool dd=s.DrawdownLatch||(peak>0&&peak-eq>=peak*d.Limits.DrawdownPct/100);
            bool cashBreach=s.Accounts.Any(a=>a.Cash-s.Reservations.Where(r=>r.Account==a.Account).Sum(Held)<0);
            bool leverage=eq<=0||Gross(s)>eq*d.Limits.MaxLeverage;
            bool accountLeverage=AccountNotionalBreach(s,d);
            return s with{LiquidationEquity=eq,PeakEquity=peak,DailyLatch=daily,DrawdownLatch=dd,
                Mode=daily||dd||cashBreach||leverage||accountLeverage?"STOP":s.Mode,
                Reason=daily?"DAILY_LATCH":dd?"DRAWDOWN_LATCH":cashBreach?"CASH_BREACH":leverage?"LEVERAGE_BREACH":accountLeverage?"ACCOUNT_NOTIONAL_BREACH":s.Reason};
        }
    }
    internal static bool AccountNotionalBreach(NavalPortfolioState s,NavalPortfolioDefinition d)=>s.Accounts.Any(a=>
    {
        var positions=s.Reservations.Where(r=>r.Account==a.Account).ToArray();
        decimal equity=checked(a.Cash+positions.Sum(r=>NetSell(r.Inventory,s.Marks[r.Intent.Instrument],d.Fees)));
        decimal gross=checked(positions.Sum(r=>r.Pending*r.Intent.EntryPriceCeiling+r.Inventory*s.Marks[r.Intent.Instrument]));
        return equity<=0||gross>checked(equity*d.Limits.MaxLeverage);
    });
    internal static decimal Gross(NavalPortfolioState s)=>checked(s.Reservations.Sum(r=>r.Pending*r.Intent.EntryPriceCeiling+r.Inventory*(s.Marks.TryGetValue(r.Intent.Instrument,out var p)?p:r.Intent.EntryPriceCeiling)));
    internal static string? Admission(NavalPortfolioState s,NavalReservation candidate,NavalPortfolioDefinition d)
    {
        checked
        {
            if(s.LiquidationEquity is not decimal eq||eq<=0)return "EQUITY_UNKNOWN";
            var active=s.Reservations.Where(Active).ToArray();
            if(active.Any(r=>r.Account==candidate.Account&&r.Intent.Instrument==candidate.Intent.Instrument))return "SYMBOL_OWNED";
            if(active.Length>=d.Limits.CentralMax||active.Count(r=>r.Fleet==candidate.Fleet)>=(candidate.Fleet==NavalFleet.Alpha?d.Limits.AlphaMax:d.Limits.BravoMax))return "SLOT_LIMIT";
            var account=s.Accounts.Single(a=>a.Account==candidate.Account);
            if(account.Cash-s.Reservations.Where(r=>r.Account==candidate.Account).Sum(Held)<Held(candidate))return "ACCOUNT_CASH";
            decimal book=s.Accounts.Sum(a=>a.Cash)+s.Reservations.Sum(r=>r.AllocatedCost);
            decimal capital=Math.Max(0,Math.Min(book,eq));
            if(candidate.RiskHold>capital*d.Limits.TradePct/100)return "TRADE_RISK";
            decimal risk=s.Reservations.Sum(r=>r.RiskHold)+candidate.RiskHold;
            if(risk+Math.Max(0,s.DayStartEquity-eq)>s.DayStartEquity*d.Limits.DailyPct/100)return "DAILY_PROJECTED";
            if(risk+Math.Max(0,s.PeakEquity-eq)>s.PeakEquity*d.Limits.DrawdownPct/100)return "DRAWDOWN_PROJECTED";
            if(Gross(s)+candidate.Pending*candidate.Intent.EntryPriceCeiling>eq*d.Limits.MaxLeverage)return "CENTRAL_NOTIONAL";
            decimal accountEq=account.Cash+s.Reservations.Where(r=>r.Account==candidate.Account).Sum(r=>NetSell(r.Inventory,s.Marks[r.Intent.Instrument],d.Fees));
            decimal accountGross=s.Reservations.Where(r=>r.Account==candidate.Account).Sum(r=>r.Pending*r.Intent.EntryPriceCeiling+r.Inventory*s.Marks[r.Intent.Instrument]);
            if(accountGross+candidate.Pending*candidate.Intent.EntryPriceCeiling>accountEq*d.Limits.MaxLeverage)return "ACCOUNT_NOTIONAL";
            return null;
        }
    }
}
