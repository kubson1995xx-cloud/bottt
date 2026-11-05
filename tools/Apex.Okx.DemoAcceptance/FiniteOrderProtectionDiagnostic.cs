using System.Security.Cryptography;
using System.Text;
using Apex.Adapters.Okx.Execution;
using Apex.Core.Naval;
namespace Apex.Okx.DemoAcceptance;

internal sealed partial class FiniteStopRecovery
{
    // Observations only. No ApplyEconomics, Prepare, ID allocation, dispatcher,
    // funding refresh or interpretation of failed/empty protection as absence.
    private async Task<object> Diagnose(OkxExecutionObservationClient reads,NavalBrokerIntent parent,EphemeralCredentials credentials,string metadataRoot,Action<object> checkpoint,CancellationToken ct,bool retrospective=false)
    {
        var state=session.ReadState();var x=state.NavalBroker!.Operational!;
        var known=x.FillEvidence.Where(f=>f.ClientId==parent.ClientId&&f.Instrument==parent.Instrument&&f.Side=="buy").Select(f=>f.OrderId).Distinct(StringComparer.Ordinal).ToArray();
        bool conflict=known.Length>1||known.Length==1&&parent.Outcome?.OrderId is {} acknowledged&&known[0]!=acknowledged;
        string? knownId=!conflict?(known.SingleOrDefault()??parent.Outcome?.OrderId):null;
        var allocated=x.IdAllocations.Where(a=>a.ReservationId==parent.ReservationId).SelectMany(a=>a.Ids).Where(i=>i.ReservationId==parent.ReservationId&&i.Role==NavalClientIdRole.ProtectionAlgo).Select(i=>i.ClientId).Distinct(StringComparer.Ordinal).ToArray();
        if(allocated.Length>32)throw new InvalidOperationException("DIAGNOSTIC_ALLOCATION_CAP");
        var results=new List<object>();var inventories=new Dictionary<string,PrivateOrderInventory>(StringComparer.Ordinal);var inventoryStates=new Dictionary<string,string>(StringComparer.Ordinal);
        object Snapshot()=>new{reads=results.ToArray(),knownFillOrderIdCount=known.Length,knownOrderIdConflict=conflict,allocatedProtectionCount=allocated.Length,protectionAbsenceEstablished=false,financialClearance=false,terminalCoverageGranted=false,inventory=inventoryStates.Count==0?"NOT_REQUIRED":inventoryStates.Values.Distinct(StringComparer.Ordinal).Count()==1?inventoryStates.Values.First():"PER_ENDPOINT",endpointInventories=inventoryStates.Select(p=>new{endpoint=p.Key,state=p.Value}).ToArray(),newSubmitOrCancelAuthority=false};
        void Remember<T>(OkxObservationRead<T> read,PrivateOrderInventory candidate,object requested,bool identityMatches)
        {
            results.Add(new{requested,status=read.Status.ToString(),read.ErrorCode,diagnostic=read.Diagnostic,read.RequestUtc,read.ReceiveUtc,read.RequestMonotonic,read.ReceiveMonotonic,read.ResponseDigest,identityMatches,observation=read.Status==OkxObservationStatus.Observed?(object)read.Rows:null});
            // Capture at most the FIRST rejected object PER EXACT ENDPOINT.
            // Neither repeated original lookups nor later allocated protections
            // replace it (including a withheld first object). Fresh config matched
            // owner. A rejected envelope can leave OkxCode unknown; collecting
            // its names is NOT success-envelope or financial acceptance.
            if(read.Endpoint is "/api/v5/trade/order" or "/api/v5/trade/order-algo"&&!inventories.ContainsKey(read.Endpoint)&&read.Diagnostic is {HttpStatus:>=200 and <=299,Phase:"PARSE_SCHEMA",Payload:{Reason:"UNSUPPORTED_PROPERTY"} failure}&&candidate.Called)
            {inventories.Add(read.Endpoint,candidate);candidate.Endpoint=read.Endpoint;candidate.Path=failure.Path;inventoryStates[read.Endpoint]=candidate.Withheld?"WITHHELD":"CAPTURED_NOT_PUBLISHED";}
            checkpoint(Snapshot());
        }
        var capture=new PrivateOrderInventory();
        Task<OkxObservationRead<OkxExecutionOrderDto>> Order(OkxObservationOrderQuery query,PrivateOrderInventory candidate)=>retrospective
            ?reads.ReadOwnedRecoveryOrderSchemaAsync(query,context,ct,candidate.Observe)
            :reads.ReadOrderSchemaAsync(query,context,ct,candidate.Observe);
        var original=await Order(new(parent.Instrument,null,parent.ClientId),capture).ConfigureAwait(false);
        bool Match(OkxExecutionOrderDto o)=>o.InstId==parent.Instrument&&o.ClOrdId==parent.ClientId&&o.Side=="buy"&&o.TdMode=="cash"&&o.TradeQuoteCcy==scope.QuoteCurrency&&(knownId==null||o.OrdId==knownId);
        Remember(original,capture,new{kind="ORIGINAL_CLIENT_ID",instrument=parent.Instrument,clientOrderId=parent.ClientId,orderId=(string?)null},original.Rows.Count==1&&Match(original.Rows[0]));
        if(knownId!=null)
        {
            capture=new();
            // Both keys are retained in the request. Venue ordId precedence is
            // NOT an identity rewrite: returned ClOrdId stays broker-observed.
            var exact=await Order(new(parent.Instrument,knownId,parent.ClientId),capture).ConfigureAwait(false);
            Remember(exact,capture,new{kind="KNOWN_INDIVIDUAL_FILL_ORDER_ID",instrument=parent.Instrument,clientOrderId=parent.ClientId,orderId=knownId},exact.Rows.Count==1&&Match(exact.Rows[0]));
        }
        foreach(string client in allocated)
        {
            capture=new();
            // Independent of a rejected order DTO or its attachment collection.
            // Only IDs durably allocated to this reservation may be queried.
            var algo=await (retrospective?reads.ReadOwnedRecoveryAlgoSchemaAsync(new(null,client),context,ct,capture.Observe):reads.ReadAlgoSchemaAsync(new(null,client),context,ct,capture.Observe)).ConfigureAwait(false);
            Remember(algo,capture,new{kind="ALLOCATED_PROTECTION_CLIENT_ID",algoClientOrderId=client},algo.Rows.Count==1&&algo.Rows[0].InstId==parent.Instrument&&algo.Rows[0].AlgoClOrdId==client&&algo.Rows[0].Side=="sell"&&algo.Rows[0].TdMode=="cash"&&algo.Rows[0].TradeQuoteCcy==scope.QuoteCurrency);
        }
        if(inventories.Count>0)
        {
            string[] identifiers=new[]{scope.Uid,scope.MainUid,parent.ClientId,parent.OrderId,parent.Outcome?.OrderId,knownId,state.NavalBroker.Baseline.Observations.Account.Ip}.Where(s=>!string.IsNullOrEmpty(s)).Cast<string>().Concat(allocated).Concat(x.FillEvidence.SelectMany(f=>new[]{f.OrderId,f.ClientId,f.TradeId,f.BillId})).Concat(x.Protection.SelectMany(p=>new[]{p.AlgoId,p.ParentOrderId,p.AttachmentClientId,p.AttachmentId??""})).Distinct(StringComparer.Ordinal).ToArray();
            foreach(var item in inventories)
            {
                if(item.Value.Withheld)continue;
                // Fixed endpoint attribution, not a response/CLI-controlled path.
                string directory=item.Key=="/api/v5/trade/order"?"order":"protection";
                inventoryStates[item.Key]=await item.Value.Publish(Path.Combine(metadataRoot,directory),credentials,identifiers,ct).ConfigureAwait(false)?"PRIVATE_IDENTIFIERS_PUBLISHED":"WITHHELD";
                checkpoint(Snapshot());
            }
        }
        return Snapshot();
    }

    // The reviewed identifier-only recipe: first rejected object, bounded ASCII
    // names/kinds/counts, whole-inventory withholding, fixed cleared output,
    // credential + already-known identifier comparison, native no-clobber publish.
    private sealed class PrivateOrderInventory
    {
        private readonly List<(string Name,string Kind,int Count)> groups=new();
        internal bool Called,Withheld;internal string Endpoint="",Path="";
        internal void Observe(string? name,string kind,int count)
        {
            Called=true;if(Withheld)return;
            if(name==null||groups.Count==32||count is <1 or >256||name.Length is <2 or >48||name[0] is <'a' or >'z'||name.Any(c=>c is not (>= 'a' and <= 'z') and not (>= 'A' and <= 'Z'))||kind is not ("String" or "Number" or "Boolean" or "Null" or "Array" or "Object"))
            {Withheld=true;groups.Clear();return;}
            groups.Add((name,kind,count));
        }
        internal async Task<bool> Publish(string root,EphemeralCredentials credentials,string[] identifiers,CancellationToken ct)
        {
            if(Withheld||groups.Count==0)return false;
            byte[] bytes=new byte[8192];int used=0;
            try
            {
                void Text(string value){used+=Encoding.ASCII.GetBytes(value,bytes.AsSpan(used));}
                Text("{\"schema\":\"apex-private-order-protection-identifiers-v1\",\"mode\":\"STOP\",\"endpoint\":\"");Text(Endpoint);Text("\",\"path\":\"");Text(Path);Text("\",\"groups\":[");bool comma=false;
                foreach(var g in groups)
                {if(comma)Text(",");comma=true;Text("{\"name\":\"");Text(g.Name);Text("\",\"kind\":\"");Text(g.Kind);Text("\",\"count\":");Text(g.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));Text("}");}
                Text("]}\n");
                if(credentials.ContainsSecret(bytes.AsSpan(0,used)))return false;
                foreach(string id in identifiers)
                {
                    byte[] encoded=Encoding.UTF8.GetBytes(id);
                    try{if(encoded.Length>0&&bytes.AsSpan(0,used).IndexOf(encoded)>=0)return false;}
                    finally{CryptographicOperations.ZeroMemory(encoded);}
                }
                ct.ThrowIfCancellationRequested();if(!LinuxInput.SafeForPrivateDispatch())throw new IOException("DIAGNOSTIC_PUBLICATION_STOPPED");
                using var output=await VaultDirectory.OpenAsync(root,true,ct,"order-protection-identifiers.json").ConfigureAwait(false)??throw new IOException("DIAGNOSTIC_PRIVATE_DIRECTORY");
                if(output.InspectRecord()!=null)throw new IOException("DIAGNOSTIC_METADATA_EXISTS");
                output.Publish(bytes.AsSpan(0,used),null,false,ct);return true;
            }
            finally{CryptographicOperations.ZeroMemory(bytes);groups.Clear();}
        }
    }
}
