using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Apex.Adapters.Okx.Execution;
using Apex.Core.Naval;
namespace Apex.Desktop.Okx;

// Same signed ordinary inspection as the existing Linux host, native approval stays
// in WindowsOkxApprovalProvider. This historical-owner path never approves a baseline.
internal sealed class WindowsOkxOrdinaryApprovalProvider(Func<OkxAccountConnectionService?> account):IOkxProtectedBaselineProvider
{
    public async ValueTask<OkxKeyScopeInspection?> InspectAsync(OkxScopeBinding binding,string fingerprint,CancellationToken ct)
    {
        var service=account();
        if(!OperatingSystem.IsWindows()||service==null||!service.OrdinaryDemoPolicy||binding!=service.Binding||binding.Account.Environment!=NavalEnvironment.OkxDemo||service.ObservationClient==null)return null;
        var a=await service.ObservationClient.ReadAccountAsync(service.ObservationContext!,ct).ConfigureAwait(false);
        var balances=await service.ObservationClient.ReadBalancesAsync(service.ObservationContext!,ct).ConfigureAwait(false);
        if(a.Status!=OkxObservationStatus.Observed||a.Rows.Count!=1||balances.Status!=OkxObservationStatus.Observed||balances.Rows.Count!=1)return null;
        var row=a.Rows[0];var mapped=balances.Rows[0].Details.Select(OkxBrokerObservationMapper.MapBalance).ToArray();
        if(row.Uid!=binding.Account.AccountId||row.MainUid!=binding.MainUid||row.Permissions!="read_only,trade"||row.AccountLevel!="1"||row.AccountType!="0"||row.SpotRoleType!="0"||row.AutoLoan||row.EnableSpotBorrow||row.SpotBorrowAutoRepay||row.FeeType is not ("0" or "1")||string.IsNullOrWhiteSpace(row.Ip)||
            mapped.Length is <1 or >256||mapped.Select(v=>v.Currency).Distinct(StringComparer.Ordinal).Count()!=mapped.Length||mapped.Any(v=>!NavalCurrentMarketFunding.RawDebtCompatible(v)||v.StrategyEquity!=0||v.FixedBalance!=0||v.SourceUtc==null||v.SourceUtc>service.ObservationClock.GetUtcNow()))return null;
        _=await OkxCurrentMarketFunding.ReadOrdinaryAsync(service.ObservationClient,service.ObservationContext!,fingerprint,mapped,service.ObservationClock,ct).ConfigureAwait(false);
        return new(fingerprint,"okx-ordinary-demo-current-market-v1","Fresh signed DEMO Read+Trade/no Withdraw, cash-only ordinary current-Market evidence; existing native historical owner only.",row.Ip);
    }
    public ValueTask<bool> ApproveAsync(NavalBaselineApprovalRequest request,CancellationToken ct)=>ValueTask.FromResult(false);
}
