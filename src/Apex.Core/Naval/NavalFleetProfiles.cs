using System;

namespace Apex.Core.Naval
{
    public static class NavalFleetProfiles
    {
        private static readonly NavalFleetProfile Alpha = new(NavalFleet.Alpha,
            Array.AsReadOnly(new[] { "BTC", "ETH" }), 2, new NavalSettings(), 14, 2m, 3m);
        private static readonly NavalFleetProfile Bravo = new(NavalFleet.Bravo,
            Array.AsReadOnly(new[] { "BTC", "ETH", "SOL", "XRP", "DOGE", "ADA", "LTC", "LINK", "BCH", "AVAX" }),
            2, new NavalSettings(), 14, 2m, 3m);
        public static NavalFleetProfile Get(NavalFleet fleet) => fleet switch
        {
            NavalFleet.Alpha => Alpha,
            NavalFleet.Bravo => Bravo,
            _ => throw new ArgumentOutOfRangeException(nameof(fleet))
        };
    }
}
