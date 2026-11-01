using System;
using System.Collections.Generic;
using System.Linq;
namespace Apex.Core.Naval;
internal sealed partial class NavalBrokerSession
{
    internal NavalRiskResult UpdateQuote(NavalBrokerQuote quote)=>Coordinator.UpdateBrokerQuote(this,quote);
    internal NavalRiskResult EmergencyStop()=>Coordinator.StopBrokerEmergency();
}
public sealed partial class NavalPortfolioCoordinator
{
    internal NavalRiskResult UpdateBrokerQuote(NavalBrokerSession session,NavalBrokerQuote quote)
    {
        lock(_owner)
        {
            var e=_owner.Read();var s=e.Naval!;var c=(INavalBrokerClock)_clock;
            if(!BrokerSource(session)||!ValidQuote(quote)||!_definition.Catalog.Any(i=>i.Instrument==quote.Instrument)||c.Timestamp<_brokerTimestamp||(decimal)(c.Timestamp-_brokerTimestamp)/c.TimestampFrequency>=30)
                return Result(s,NavalMutationStatus.Refused,"QUOTE_OR_PRIVATE_SOURCE_EXPIRED");
            var updated=new Dictionary<string,NavalBrokerQuote>(_brokerQuotes){[quote.Instrument]=quote};_brokerQuotes=updated;
            var marks=new Dictionary<string,decimal>(s.Marks){[quote.Instrument]=quote.Bid};
            // Public polling does not renew the private capture's absolute monotonic/UTC expiry.
            return Commit(e,NavalCashRiskPolicy.Refresh(s with{Marks=marks,Sequence=checked(s.Sequence+1)},_definition,_clock.UtcNow,false),NavalMutationStatus.Applied,"BROKER_QUOTE_UPDATED",Maintenance());
        }
    }
    internal NavalRiskResult StopBrokerEmergency(){lock(_owner)return StopCandidate(_owner.Read(),"EMERGENCY_STOP",true);}
}
