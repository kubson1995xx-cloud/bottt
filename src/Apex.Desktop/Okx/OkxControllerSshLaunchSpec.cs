using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
namespace Apex.Desktop.Okx;

// Supplied only by future protected pairing. These are SSH identifiers/paths, never broker secrets.
internal sealed record OkxControllerSshEndpoint(string Host,int Port,string User,string IdentityFile,string KnownHostsFile);
internal sealed class OkxControllerSshLaunchSpec
{
    internal const string Relay="/home/ubuntu/.local/bin/apex-okx-demo-controller-relay";
    internal string Executable {get;}
    internal IReadOnlyList<string> Arguments {get;}
    internal IReadOnlyDictionary<string,string> Environment {get;}=new ReadOnlyDictionary<string,string>(new Dictionary<string,string>());
    private OkxControllerSshLaunchSpec(string exe,List<string> args){Executable=exe;Arguments=args.AsReadOnly();}
    internal static OkxControllerSshLaunchSpec Build(OkxControllerSshEndpoint endpoint,string systemSsh,string nullFile)
    {
        static bool Token(string s)=>!string.IsNullOrEmpty(s)&&s.Length<=253&&s[0]!='-'&&s.All(c=>char.IsAsciiLetterOrDigit(c)||c is '.' or '_' or '-');
        static bool PathOperand(string s)=>!string.IsNullOrEmpty(s)&&Path.IsPathFullyQualified(s)&&!s.Any(c=>char.IsControl(c)||c is '%' or '$');
        if(endpoint==null||!Token(endpoint.Host)||!Token(endpoint.User)||endpoint.Port is <1 or >65535||!PathOperand(systemSsh)||!PathOperand(endpoint.IdentityFile)||!PathOperand(endpoint.KnownHostsFile)||nullFile is not ("/dev/null" or "NUL"))throw new ArgumentException("SSH_SPEC_REFUSED");
        var args=new List<string>{"-F",nullFile,"-T","-x","-a"};
        foreach(var option in new[]{"BatchMode=yes","StrictHostKeyChecking=yes","UserKnownHostsFile="+QuoteConfigPath(endpoint.KnownHostsFile),"GlobalKnownHostsFile="+nullFile,"IdentityAgent=none","IdentitiesOnly=yes","PasswordAuthentication=no","KbdInteractiveAuthentication=no","PreferredAuthentications=publickey","ForwardAgent=no","ForwardX11=no","ClearAllForwardings=yes","ExitOnForwardFailure=yes","PermitLocalCommand=no","ProxyCommand=none","ProxyJump=none","CanonicalizeHostname=no","ControlMaster=no","ControlPath=none","ConnectionAttempts=1","ConnectTimeout=10","ServerAliveInterval=10","ServerAliveCountMax=2"}){args.Add("-o");args.Add(option);}
        args.AddRange(new[]{"-i",endpoint.IdentityFile,"-p",endpoint.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),"-l",endpoint.User,"--",endpoint.Host,Relay});
        return new(systemSsh,args);
    }
    // OpenSSH parses -o paths again; quote its configuration grammar, not a shell.
    private static string QuoteConfigPath(string path)=>"\""+path.Replace("\\","\\\\",StringComparison.Ordinal).Replace("\"","\\\"",StringComparison.Ordinal)+"\"";
    // Issuance requires an actual native lease; neither strings nor an arbitrary predicate can mint it.
    internal sealed class AdmittedTransportCapability
    {
        private readonly WindowsOkxControllerTransportAdmission admission;
        private AdmittedTransportCapability(WindowsOkxControllerTransportAdmission admission){this.admission=admission;}
        internal static AdmittedTransportCapability Issue(WindowsOkxControllerTransportAdmission admission)
            =>admission.WithValidatedSpec(admission.Spec,()=>new AdmittedTransportCapability(admission));
        internal ProcessStartInfo Create(OkxControllerSshLaunchSpec spec)=>admission.WithValidatedSpec(spec,spec.BuildStartInfo);
    }
    internal ProcessStartInfo CreateStartInfo(AdmittedTransportCapability capability)
    {
        if(capability==null)throw new InvalidOperationException("NATIVE_TRANSPORT_ADMISSION_REQUIRED");
        return capability.Create(this);
    }
    private ProcessStartInfo BuildStartInfo()
    {
        var info=new ProcessStartInfo(Executable){UseShellExecute=false,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true,CreateNoWindow=true};
        info.Environment.Clear();foreach(var item in Environment)info.Environment.Add(item.Key,item.Value);
        foreach(var arg in Arguments)info.ArgumentList.Add(arg);return info;
    }
}
