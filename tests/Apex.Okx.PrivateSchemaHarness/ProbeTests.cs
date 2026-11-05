using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Apex.Adapters.Okx.Execution;
using Apex.Okx.DemoAcceptance;

internal static class ProbeTests
{
    private sealed class Tracking : IBufferAllocator
    {
        internal readonly List<byte[]> Arrays=new();
        public byte[] Allocate(int capacity){var b=new byte[capacity];Arrays.Add(b);return b;}
        public void Release(byte[] b)=>Harness.Check(b.All(x=>x==0));
        internal void Cleared()=>Harness.Check(Arrays.All(b=>b.All(x=>x==0)));
    }
    internal static async Task Run(string evidence,bool instruments=false)
    {
        string reportName=instruments?"instrument-identifiers.json":"balance-identifiers.json";
        string command=instruments?"--probe-instruments":"--probe";
        string Scenario(string scenario)=>instruments?"instrument:"+scenario:scenario;
        string path=Path.Combine(AppContext.BaseDirectory,"Apex.Okx.PrivateSchemaProbe.dll");Harness.Check(File.Exists(path),"PROBE_ENTRY_REQUIRED");
        var entry=Assembly.LoadFrom(path).GetType("Apex.Okx.PrivateSchemaProbe.Program")!.GetMethod("RunAsync",BindingFlags.Static|BindingFlags.NonPublic)!;
        string testBase="/home/ubuntu/apex-deliveries/apex-okx-private-schema-tests";Directory.CreateDirectory(testBase,(UnixFileMode)448);
        string root=Path.Combine(testBase,Path.GetFileName(evidence));Directory.CreateDirectory(root,(UnixFileMode)448);
        string run=Path.Combine(root,"run");Directory.CreateDirectory(run,(UnixFileMode)448);
        string lockPath=Path.Combine(run,".run.lock");using(var f=Create(lockPath)){}
        string[] financial={"last-report.json","naval_authority_broker_v2.json",".naval-broker-initialized-v2"};
        foreach(string name in financial)using(var f=Create(Path.Combine(run,name)))f.Write("SYNTHETIC_PRESERVE"u8);
        var allocator=new Tracking();var vault=new LinuxDemoCredentialVault(Path.Combine(root,"vault"),allocator);
        await vault.SaveNewAsync(Harness.Fields[0],Harness.Fields[1],Harness.Fields[2]);allocator.Cleared();
        byte[] cipher=File.ReadAllBytes(Path.Combine(root,"vault/credentials.cred"));
        var cases=new List<object>();int passed=0,failed=0,index=0;
        async Task Case(string name,Func<Task> body)
        {
            try{await body();allocator.Cleared();Preserved();passed++;cases.Add(new{name,passed=true});Console.WriteLine("PASS "+name);}
            catch{failed++;cases.Add(new{name,passed=false});Console.WriteLine("FAIL "+name);}
        }
        void Preserved()
        {
            Harness.Check(File.ReadAllBytes(Path.Combine(root,"vault/credentials.cred")).SequenceEqual(cipher));
            Harness.Check(financial.All(n=>File.ReadAllBytes(Path.Combine(run,n)).SequenceEqual("SYNTHETIC_PRESERVE"u8.ToArray())));
        }
        async Task<(int Code,int Sends,string Text,string Output)> Invoke(string scenario,string? output=null,string? lockRoot=null,string[]? args=null,CancellationToken ct=default,Action<string>? after=null)
        {
            output??=Path.Combine(root,"out"+(++index));int sends=0;
            Func<EphemeralCredentials,Action<string?,string,int>,CancellationToken,Task<OkxEnrollmentDiscovery>> discover=async(owner,observer,token)=>
            {
                Harness.Check(owner.ContainsSecret(Harness.Fields[0]));
                // A new accepted FileShare.None opener must conflict throughout the run.
                try{using var unexpected=new FileStream(lockPath,FileMode.Open,FileAccess.ReadWrite,FileShare.None);throw new Exception();}
                catch(IOException){}
                if(scenario=="throw")throw new InvalidOperationException("VALUE_NEVER_PUBLISH");
                using var cancel=scenario=="cancel"?CancellationTokenSource.CreateLinkedTokenSource(token):null;
                using var peer=new Harness.Peer(Scenario(scenario.StartsWith("mismatch:",StringComparison.Ordinal)?"two":scenario),cancel);
                var result=await owner.DiscoverPrivateSchema(observer,cancel?.Token??token,peer);
                sends=peer.Count;
                if(scenario=="cancel")throw new OperationCanceledException();
                if(scenario.StartsWith("mismatch:",StringComparison.Ordinal))
                {
                    var d=result.Diagnostic!;
                    result=scenario[9..] switch
                    {
                        "endpoint"=>result with {Diagnostic=d with {Endpoint="/api/v5/account/instruments"}},
                        "phase"=>result with {Diagnostic=d with {Phase="PARSED"}},
                        "stage"=>result with {Diagnostic=d with {LastCompletedStage="ACCOUNT_SAFETY_VALIDATED"}},
                        "http"=>result with {Diagnostic=d with {HttpStatus=500}},
                        "okx"=>result with {Diagnostic=d with {OkxCode="50000"}},
                        "config"=>result with {Diagnostic=d with {ConfigAuthenticated=false}},
                        "safety"=>result with {Diagnostic=d with {AccountSafetyValidated=false}},
                        "reason"=>result with {Diagnostic=d with {Payload=d.Payload! with {Reason="TYPE"}}},
                        "path"=>result with {Diagnostic=d with {Payload=d.Payload! with {Path="$.data[].upcChg[].<unknown>"}}},
                        "code"=>result with {Code="CONNECTED_READ_ONLY_STOP"},
                        _=>throw new Exception()
                    };
                }
                after?.Invoke(output);return result;
            };
            var stdout=Console.Out;var stderr=Console.Error;using var text=new StringWriter();Console.SetOut(text);Console.SetError(text);int code;
            try{code=await (Task<int>)entry.Invoke(null,new object[]{args??new[]{command},lockRoot??run,output,vault,discover,ct})!;}
            finally{Console.SetOut(stdout);Console.SetError(stderr);}
            string printed=text.ToString();
            Harness.Check(!printed.Contains("alphaMetric")&&!printed.Contains("betaMetric")&&!printed.Contains("VALUE_NEVER_PUBLISH")&&!printed.Contains("RECURSIVE_NEVER_COPY")&&!Harness.Fields.Any(f=>printed.Contains(Encoding.ASCII.GetString(f))));
            using(var available=new FileStream(lockPath,FileMode.Open,FileAccess.ReadWrite,FileShare.None)){}
            return(code,sends,printed,output);
        }
        bool NoReport(string output)=>!File.Exists(Path.Combine(output,reportName));
        async Task Expect(string scenario,int code,int sends,bool report)
        {
            if(instruments&&sends==3)sends=4;
            var r=await Invoke(scenario);Harness.Check(r.Code==code&&r.Sends==sends&&NoReport(r.Output)!=report);
            Harness.Check(!File.Exists(Path.Combine(r.Output,instruments?"balance-identifiers.json":"instrument-identifiers.json")));
            if(report)
            {
                string reportPath=Path.Combine(r.Output,reportName);byte[] bytes=File.ReadAllBytes(reportPath);string text=Encoding.ASCII.GetString(bytes);
                Harness.Check(!text.Contains("VALUE_NEVER_PUBLISH")&&!text.Contains("RECURSIVE_NEVER_COPY")&&!text.Contains("OTHER_ROW_NEVER_COPY")&&!text.Contains("uid")&&!text.Contains("fingerprint")&&!Harness.Fields.Any(f=>bytes.AsSpan().IndexOf(f)>=0));
                Harness.Check(File.GetUnixFileMode(reportPath)==(UnixFileMode)384&&File.GetUnixFileMode(r.Output)==(UnixFileMode)448);
                using var doc=JsonDocument.Parse(bytes);var groups=doc.RootElement.GetProperty("groups");
                Harness.Check(doc.RootElement.GetProperty("mode").GetString()=="STOP"&&groups.GetArrayLength()==(scenario=="two"?2:1));
                Harness.Check(groups.EnumerateArray().All(g=>g.GetProperty("count").GetInt32()==1));
                if(scenario=="two")Harness.Check(groups[0].GetProperty("name").GetString()=="alphaMetric"&&groups[1].GetProperty("name").GetString()=="betaMetric");
                string? expected=scenario switch{"recursive"=>"Object","array"=>"Array","number"=>"Number","boolean"=>"Boolean","null"=>"Null",_=>"String"};
                Harness.Check(groups.EnumerateArray().All(g=>g.GetProperty("kind").GetString()==expected));
            }
        }
        await Case("kernel_ipv4_ipv6_denied",async()=>
        {
            await Task.Run(()=>{foreach(var family in new[]{AddressFamily.InterNetwork,AddressFamily.InterNetworkV6})try{using var socket=new Socket(family,SocketType.Stream,ProtocolType.Tcp);throw new Exception();}catch(SocketException e){Harness.Check(e.SocketErrorCode==SocketError.AccessDenied);}});
        });
        await Case(instruments?"native_two_identifiers_four_GET_no_values":"native_two_identifiers_three_signed_GET_no_values",()=>Expect("two",0,3,true));
        await Case("normal_populated_REST_four_GET_no_report",()=>Expect("normal",4,4,false));
        foreach(string scenario in new[]{"recursive","array","number","boolean","null","first-only"})await Case("kind_first_object_"+scenario,()=>Expect(scenario,0,3,true));
        foreach(var pair in new[]{("one_letter","a"),("uppercase","AlphaMetric"),("digit","alpha1"),("underscore","alpha_metric"),("punctuation","alpha.metric"),("unicode","alphaŻ"),("whitespace","alpha metric"),("path","alpha/metric"),("too_long",new string('a',49))})
            await Case("identifier_withheld_"+pair.Item1,()=>Expect("name:"+pair.Item2,5,3,false));
        await Case("maximum48_identifier_admitted",()=>Expect("name:"+new string('a',48),0,3,true));
        foreach(string name in new[]{"probeKey","probeSecret","probePass","extraProbeprobeKeySuffix","extraProbeprobeSecretSuffix","extraProbeprobePassSuffix"})
            await Case("credential_name_calibration_"+(++index),()=>Expect("name:"+name,5,3,false));
        await Case("complete_output_secret_in_fixed_metadata_withheld",async()=>
        {
            var originalVault=vault;byte[] original=Harness.Fields[2];Harness.Fields[2]="name"u8.ToArray();
            try
            {
                vault=new LinuxDemoCredentialVault(Path.Combine(root,"literal-vault"),allocator);
                await vault.SaveNewAsync(Harness.Fields[0],Harness.Fields[1],Harness.Fields[2]);
                await Expect("two",5,3,false);
            }
            finally{vault=originalVault;Harness.Fields[2]=original;}
        });
        await Case("group_cap_withheld",()=>Expect("cap",5,3,false));
        foreach(string scenario in new[]{"wrong-type","wrong-shape","case","duplicate"})await Case("strict_"+scenario,()=>Expect(scenario,4,3,false));
        await Case("observer_exception_cannot_change_refusal",async()=>
        {
            using var peer=new Harness.Peer(Scenario("two"));var r=await Harness.Discover(peer,(_,_,_)=>throw new Exception());
            Harness.Check(r.Observation==null&&r.Diagnostic?.Payload?.Reason=="UNSUPPORTED_PROPERTY"&&peer.Count==(instruments?4:3));
            Harness.Check(!JsonSerializer.Serialize(r).Contains("alphaMetric"));
        });
        await Case("public_observer_absent_retains_unknown",async()=>
        {
            using var peer=new Harness.Peer(Scenario("two"));var r=await Harness.Discover(peer,null);
            if(instruments)Harness.Check(r.Diagnostic is {LastCompletedStage:"BALANCE_VALIDATED",Payload:{Reason:"UNSUPPORTED_PROPERTY",Path:"$.data[].<unknown>",UnsupportedProperties:null}}&&!JsonSerializer.Serialize(r).Contains("alphaMetric"));
            else Harness.Check(r.Diagnostic!.Payload!.UnsupportedProperties!.Value.Single().Name=="<unknown>"&&r.Diagnostic.Payload.UnsupportedProperties.Value.Single().Count==2);
        });
        await Case("cancellation_after_balance_cleans_owned_arrays",()=>Expect("cancel",6,3,false));
        await Case("exception_text_never_serialized",()=>Expect("throw",7,0,false));
        await Case("cancel_before_load_no_allocations",async()=>
        {
            int before=allocator.Arrays.Count;using var c=new CancellationTokenSource();c.Cancel();var r=await Invoke("two",ct:c.Token);Harness.Check(r.Code==6&&r.Sends==0&&allocator.Arrays.Count==before&&NoReport(r.Output));
        });
        await Case("no_clobber_existing_before_decrypt",async()=>
        {
            string output=Path.Combine(root,"existing");Directory.CreateDirectory(output,(UnixFileMode)448);string file=Path.Combine(output,reportName);using(var f=Create(file))f.Write("KEEP"u8);
            int before=allocator.Arrays.Count;var r=await Invoke("two",output);Harness.Check(r.Code==7&&r.Sends==0&&allocator.Arrays.Count==before&&File.ReadAllBytes(file).SequenceEqual("KEEP"u8.ToArray()));
        });
        await Case("no_clobber_race_cleans_owned_staging",async()=>
        {
            var r=await Invoke("two",after:output=>{using var f=Create(Path.Combine(output,reportName));f.Write("KEEP"u8);});
            Harness.Check(r.Code==8&&r.Sends==(instruments?4:3)&&Directory.GetFiles(r.Output).Length==1&&File.ReadAllBytes(Path.Combine(r.Output,reportName)).SequenceEqual("KEEP"u8.ToArray()));
        });
        // The held-lock case cannot use Invoke's post-call availability assertion.
        await Case("cross_process_FileShareNone_blocks_probe_before_decrypt",async()=>
        {
            var info=new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!){UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true};
            info.Environment.Clear();foreach(System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables())info.Environment[(string)e.Key]=(string)e.Value!;
            info.ArgumentList.Add(typeof(Harness).Assembly.Location);info.ArgumentList.Add("hold");info.ArgumentList.Add(lockPath);
            using var child=System.Diagnostics.Process.Start(info)!;
            try
            {
                Harness.Check(await child.StandardOutput.ReadLineAsync()=="SYNTHETIC_LOCK_HELD");int before=allocator.Arrays.Count;
                Func<EphemeralCredentials,Action<string?,string,int>,CancellationToken,Task<OkxEnrollmentDiscovery>> deny=(_,_,_)=>throw new Exception();
                int code=await (Task<int>)entry.Invoke(null,new object[]{new[]{command},run,Path.Combine(root,"busy"),vault,deny,CancellationToken.None})!;
                Harness.Check(code==9&&allocator.Arrays.Count==before&&!Directory.Exists(Path.Combine(root,"busy")));
            }
            finally{if(!child.HasExited)child.Kill();await child.WaitForExitAsync();}
        });
        foreach(string kind in new[]{"directory-mode","ancestor-mode","directory-symlink","report-mode","report-symlink","report-hardlink","lock-symlink","lock-hardlink"})await Case("path_refused_"+kind,async()=>
        {
            string bad=Path.Combine(root,"bad"+(++index));Directory.CreateDirectory(bad,(UnixFileMode)448);string output=Path.Combine(bad,"output"),lr=run;
            if(kind=="directory-mode"){Directory.CreateDirectory(output,(UnixFileMode)493);File.SetUnixFileMode(output,(UnixFileMode)493);}
            if(kind=="ancestor-mode"){File.SetUnixFileMode(bad,(UnixFileMode)504);}
            if(kind=="directory-symlink")Directory.CreateSymbolicLink(output,run);
            if(kind.StartsWith("report-",StringComparison.Ordinal))
            {
                Directory.CreateDirectory(output,(UnixFileMode)448);string file=Path.Combine(output,reportName);
                if(kind=="report-mode"){using var f=Create(file);File.SetUnixFileMode(file,(UnixFileMode)420);}
                if(kind=="report-symlink")File.CreateSymbolicLink(file,Path.Combine(run,financial[0]));
                if(kind=="report-hardlink")Harness.Check(link(Path.Combine(run,financial[0]),file)==0);
            }
            if(kind.StartsWith("lock-",StringComparison.Ordinal))
            {
                lr=Path.Combine(bad,"run");Directory.CreateDirectory(lr,(UnixFileMode)448);
                if(kind=="lock-symlink")File.CreateSymbolicLink(Path.Combine(lr,".run.lock"),lockPath);
                else Harness.Check(link(lockPath,Path.Combine(lr,".run.lock"))==0);
            }
            int before=allocator.Arrays.Count;var r=await Invoke("two",output,lr);Harness.Check(r.Code==8&&r.Sends==0&&allocator.Arrays.Count==before);
            // Remove only synthetic links after refusal so later cases retain single-link operands.
            if(kind=="report-hardlink")File.Delete(Path.Combine(output,reportName));
            if(kind=="lock-hardlink")File.Delete(Path.Combine(lr,".run.lock"));
        });
        foreach(string[] args in new[]{Array.Empty<string>(),new[]{"--unknown"},new[]{"--probe","extra"},new[]{"--check-connection"},new[]{"--save-credentials"}})
            await Case("arguments_refused_"+(++index),async()=>{int before=allocator.Arrays.Count;var r=await Invoke("two",args:args);Harness.Check(r.Code==2&&r.Sends==0&&allocator.Arrays.Count==before&&!Directory.Exists(r.Output));});
        if(instruments)
        {
            await Case("instrument_first_rejected_object_after_valid_row",()=>Expect("middle-only",0,4,true));
            await Case("instrument_known_mixed_types_excluded_from_inventory",()=>Expect("known-mixed",0,4,true));
            foreach(string scenario in new[]{"nested-known","bool-type","array-type","integer-type"})await Case("instrument_strict_"+scenario,()=>Expect(scenario,4,4,false));
            await Case("instrument_property_cap_withheld",()=>Expect("property-cap",5,4,false));
            await Case("instrument_prior_balance_failure_never_mislabeled",async()=>
            {var r=await Invoke("balance-first");Harness.Check(r.Code==4&&r.Sends==3&&NoReport(r.Output)&&!File.Exists(Path.Combine(r.Output,"balance-identifiers.json")));});
            await Case("balance_operation_never_publishes_instrument_inventory",async()=>
            {var r=await Invoke("two",args:new[]{"--probe"});Harness.Check(r.Code==4&&r.Sends==4&&NoReport(r.Output)&&!File.Exists(Path.Combine(r.Output,"balance-identifiers.json")));});
            await Case("instrument_arguments_extra_refused_before_load",async()=>
            {int before=allocator.Arrays.Count;var r=await Invoke("two",args:new[]{command,"extra"});Harness.Check(r.Code==2&&r.Sends==0&&before==allocator.Arrays.Count&&!Directory.Exists(r.Output));});
            foreach(string operand in new[]{"endpoint","phase","stage","http","okx","config","safety","reason","path","code"})
                await Case("instrument_exact_refusal_required_"+operand,()=>Expect("mismatch:"+operand,4,4,false));
        }
        var result=new{passed,failed,total=passed+failed,syntheticRoot=root,network="inherited AF_INET/AF_INET6 EACCES calibrated",cases};
        File.WriteAllText(Path.Combine(evidence,"results.json"),JsonSerializer.Serialize(result,new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine($"RESULT passed={passed} failed={failed} total={passed+failed}");Harness.Check(failed==0);
    }
    private static FileStream Create(string file)=>new(file,new FileStreamOptions{Mode=FileMode.CreateNew,Access=FileAccess.ReadWrite,UnixCreateMode=(UnixFileMode)384});
    [DllImport("libc",SetLastError=true)]private static extern int link(string oldpath,string newpath);
}
