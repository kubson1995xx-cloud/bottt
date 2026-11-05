using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Apex.Desktop.Services;
using Microsoft.Win32.SafeHandles;
namespace Apex.Desktop.Okx;

// Existing public metadata only. No enrollment, secret input, alternate command/path,
// environment setting, automatic connect or profile/ACL repair is supported here.
internal static class WindowsOkxControllerConnectionStore
{
    private const string Refused="CONTROLLER_CONNECTION_REFUSED";
    private static InvalidOperationException Denied()=>new(Refused);
    internal static Func<CancellationToken,Task<IOkxControllerFrameTransport>> CreateFactory(string root)
        =>cancellation=>
        {
            cancellation.ThrowIfCancellationRequested();
            var config=Load(root);
            cancellation.ThrowIfCancellationRequested();
            var admission=WindowsOkxControllerTransportAdmission.Open(root,config);
            try{return Task.FromResult<IOkxControllerFrameTransport>(WindowsOkxControllerSshTransport.Start(admission,cancellation));}
            catch{admission.Dispose();throw;}
        };
    internal static WindowsOkxControllerTransportConfig Load(string root)
    {
        if(!OperatingSystem.IsWindows())throw new PlatformNotSupportedException("WINDOWS_TRANSPORT_REQUIRED");
        var held=new List<WindowsPathSecurity.NativeObject>();
        try
        {
            root=WindowsPrivateData.ValidateSyntax(root);
            WindowsPathSecurity.ValidateVolume(Path.GetPathRoot(root)!);
            foreach(var path in WindowsPrivateData.Components(root))
            {
                WindowsPrivateData.CheckLocation(path,AppContext.BaseDirectory);
                var opened=WindowsPathSecurity.Open(path);held.Add(opened);
                if(!opened.IsDirectory||!opened.Path.Equals(path,StringComparison.OrdinalIgnoreCase))throw Denied();
                WindowsPathSecurity.Validate(opened,path.Equals(root,StringComparison.OrdinalIgnoreCase));
            }
            string file=Path.Combine(root,"controller-connection.json");
            using var handle=CreateFileW(file,0x80020000,1,IntPtr.Zero,3,0x00200000,IntPtr.Zero);
            if(handle.IsInvalid||!GetFileInformationByHandle(handle,out var original)||original.Links!=1||(original.Attributes&(uint)(FileAttributes.Directory|FileAttributes.ReparsePoint))!=0||original.SizeHigh!=0||original.SizeLow is 0 or >4096)throw Denied();
            using var stream=new FileStream(handle,FileAccess.Read,4096,false);
            using var view=new WindowsPathSecurity.NativeObject(new SafeFileHandle(handle.DangerousGetHandle(),false),file,original);
            void ValidateFile()
            {
                WindowsPathSecurity.Validate(view,true);
                if(stream.GetAccessControl().GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner||owner!=WindowsIdentity.GetCurrent().User)throw Denied();
                using var current=WindowsPathSecurity.Open(file);
                if(!current.SameIdentity(view)||!current.Path.Equals(file,StringComparison.OrdinalIgnoreCase)||!GetFileInformationByHandle(handle,out var now)||now.Links!=1||now.SizeHigh!=original.SizeHigh||now.SizeLow!=original.SizeLow)throw Denied();
            }
            ValidateFile();
            byte[] bytes=new byte[original.SizeLow];stream.ReadExactly(bytes);
            using var json=JsonDocument.Parse(bytes,new JsonDocumentOptions{MaxDepth=4,AllowTrailingCommas=false,CommentHandling=JsonCommentHandling.Disallow});
            var j=json.RootElement;
            string[] fields={"Schema","Host","Port","User","IdentitySha256","HostKey"};
            if(j.ValueKind!=JsonValueKind.Object)throw Denied();
            var properties=j.EnumerateObject().ToArray();
            if(properties.Length!=fields.Length||!properties.Select(p=>p.Name).OrderBy(x=>x,StringComparer.Ordinal).SequenceEqual(fields.OrderBy(x=>x,StringComparer.Ordinal)))throw Denied();
            string Text(string name)=>j.GetProperty(name).ValueKind==JsonValueKind.String?j.GetProperty(name).GetString()!:throw Denied();
            if(Text("Schema")!="apex-controller-connection-v1"||j.GetProperty("Port").ValueKind!=JsonValueKind.Number||!j.GetProperty("Port").TryGetInt32(out int port))throw Denied();
            var config=new WindowsOkxControllerTransportConfig(Text("Host"),port,Text("User"),Text("IdentitySha256"),Text("HostKey"));
            // Bind every typed field to the existing closed key/known-host profile; the
            // metadata document itself cannot confer a capability or launch a process.
            using(var admission=WindowsOkxControllerTransportAdmission.Open(root,config)){}
            ValidateFile();
            foreach(var opened in held)
            {
                WindowsPrivateData.CheckLocation(opened.Path,AppContext.BaseDirectory);
                WindowsPathSecurity.Validate(opened,opened.Path.Equals(root,StringComparison.OrdinalIgnoreCase));
                using var current=WindowsPathSecurity.Open(opened.Path);
                if(!current.SameIdentity(opened)||!current.Path.Equals(opened.Path,StringComparison.OrdinalIgnoreCase))throw Denied();
            }
            return config;
        }
        catch(Exception e) when(e is not OutOfMemoryException){throw Denied();}
        finally{foreach(var opened in held.AsEnumerable().Reverse())opened.Dispose();}
    }
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern SafeFileHandle CreateFileW(string path,uint access,uint share,IntPtr attributes,uint creation,uint flags,IntPtr template);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern bool GetFileInformationByHandle(SafeFileHandle file,out WindowsPathSecurity.FileInformation info);
}
