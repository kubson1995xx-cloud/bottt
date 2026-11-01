using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Apex.Adapters.Okx.Execution;
using Apex.Desktop.Services;
namespace Apex.Desktop.Okx;

internal sealed record OkxReadAccount(string ScopeId,string Uid,string MainUid,string Environment,string Provenance,string Fingerprint);
internal sealed class OkxReadSecret(OkxReadAccount account,byte[][] values):IDisposable
{
    internal OkxReadAccount Account=>account;
    internal byte[][] Values=>values;
    public void Dispose(){foreach(var b in values)CryptographicOperations.ZeroMemory(b);}
}
// Additive, observation-only CurrentUser DPAPI store. Does not touch HostConfig,
// old scope credentials, financial directories, activation or owner state.
internal sealed class WindowsOkxReadStore(string root)
{
    private string DirectoryPath=>Path.Combine(root,"okx-read");
    internal OkxReadAccount[] List()
    {
        if(!System.IO.Directory.Exists(DirectoryPath))return Array.Empty<OkxReadAccount>();
        using var admitted=WindowsPrivateData.Open(root,AppContext.BaseDirectory);
        var files=System.IO.Directory.GetFiles(DirectoryPath);if(files.Length>32)throw new InvalidDataException("READ_STORE_LIMIT");
        return files.Select(p=>{using var s=OpenFile(p);return s.Account;}).ToArray();
    }
    internal OkxReadSecret Open(string id)
    {
        if(!Regex.IsMatch(id,"\\Aread-[a-f0-9]{32}\\z"))throw new InvalidDataException("READ_STORE_ID");
        using var admitted=WindowsPrivateData.Open(root,AppContext.BaseDirectory);
        return OpenFile(Path.Combine(DirectoryPath,id+".dpapi"));
    }
    private byte[] Purpose(string path)=>WindowsOkxCredentialStore.ReadStorePurpose(root,Path.GetFileName(path));
    private OkxReadSecret OpenFile(string path)
    {
        if(Path.GetDirectoryName(path)!=DirectoryPath||!Regex.IsMatch(Path.GetFileName(path),"\\Aread-[a-f0-9]{32}\\.dpapi\\z"))throw new InvalidDataException("READ_STORE_LAYOUT_PRESERVED");
        using var handle=WindowsPathSecurity.Open(path);WindowsOkxCredentialStore.ValidateReadFile(handle);
        if(handle.IsDirectory)throw new InvalidDataException("READ_STORE_LAYOUT_PRESERVED");
        using var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
        if(file.Length is <=0 or >20000)throw new InvalidDataException("READ_STORE_SIZE");
        var encrypted=new byte[(int)file.Length];file.ReadExactly(encrypted);
        byte[] plain=WindowsOkxCredentialStore.Crypt(encrypted,Purpose(path),false);byte[][] values=new byte[3][];
        try
        {
            using var stream=new MemoryStream(plain,false);using var reader=new BinaryReader(stream,Encoding.UTF8,true);
            int length=reader.ReadInt32();if(length is <=0 or >2048)throw new InvalidDataException("READ_STORE_METADATA");
            var account=JsonSerializer.Deserialize<OkxReadAccount>(reader.ReadBytes(length))??throw new InvalidDataException("READ_STORE_METADATA");
            Validate(account);if(Path.GetFileName(path)!=account.ScopeId+".dpapi")throw new InvalidDataException("READ_STORE_BINDING");
            for(int i=0;i<3;i++){int n=reader.ReadInt32();if(n is <=0 or >4096)throw new InvalidDataException("READ_STORE_FIELD");values[i]=reader.ReadBytes(n);if(values[i].Length!=n)throw new InvalidDataException("READ_STORE_FIELD");}
            if(stream.Position!=stream.Length||account.Fingerprint!=Convert.ToHexString(SHA256.HashData(values[0])).ToLowerInvariant())throw new InvalidDataException("READ_STORE_BINDING");
            return new(account,values);
        }
        catch{foreach(var b in values)if(b!=null)CryptographicOperations.ZeroMemory(b);throw;}
        finally{CryptographicOperations.ZeroMemory(plain);}
    }
    internal OkxReadAccount Save(OkxEnrollmentObservation observed,byte[][] values)
    {
        if(observed.Environment!="Demo"||observed.Provenance!="DemoTradingApi"||observed.Fingerprint!=Convert.ToHexString(SHA256.HashData(values[0])).ToLowerInvariant())throw new InvalidDataException("READ_STORE_BINDING");
        using var admitted=WindowsPrivateData.Open(root,AppContext.BaseDirectory);
        if(List().Any(a=>a.Uid==observed.Uid&&a.Environment==observed.Environment))throw new InvalidDataException("ACCOUNT_ALREADY_SAVED_RECONNECT");
        var account=new OkxReadAccount("read-"+Guid.NewGuid().ToString("N"),observed.Uid,observed.MainUid,observed.Environment,observed.Provenance,observed.Fingerprint);Validate(account);
        if(!System.IO.Directory.Exists(DirectoryPath))WindowsPathSecurity.CreatePrivateDirectory(DirectoryPath);
        string path=Path.Combine(DirectoryPath,account.ScopeId+".dpapi");byte[] metadata=JsonSerializer.SerializeToUtf8Bytes(account);
        // Exact bounded non-growing encoding, no plaintext JSON secret/string copies.
        if(values.Length!=3||values.Any(b=>b.Length is <=0 or >4096))throw new InvalidDataException("READ_STORE_FIELD");
        byte[] plain=new byte[checked(4+metadata.Length+12+values.Sum(b=>b.Length))];
        try
        {
            using(var stream=new MemoryStream(plain,true))using(var writer=new BinaryWriter(stream,Encoding.UTF8,true))
            {writer.Write(metadata.Length);writer.Write(metadata);foreach(var b in values){writer.Write(b.Length);writer.Write(b);}}
            byte[] encrypted=WindowsOkxCredentialStore.Crypt(plain,Purpose(path),true);
            using(var file=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None,4096,FileOptions.WriteThrough)){file.Write(encrypted);file.Flush(true);}
            using var readback=Open(account.ScopeId);
            if(readback.Account!=account||!values.Select((v,i)=>v.AsSpan().SequenceEqual(readback.Values[i])).All(b=>b))throw new InvalidDataException("READ_STORE_READBACK_PRESERVED");
            return account;
        }
        finally{CryptographicOperations.ZeroMemory(plain);}
    }
    private static void Validate(OkxReadAccount a)
    {
        if(!Regex.IsMatch(a.ScopeId,"\\Aread-[a-f0-9]{32}\\z")||!Regex.IsMatch(a.Uid,"\\A[0-9]{1,32}\\z")||a.MainUid!=a.Uid||a.Environment!="Demo"||a.Provenance!="DemoTradingApi"||!Regex.IsMatch(a.Fingerprint,"\\A[a-f0-9]{64}\\z"))throw new InvalidDataException("READ_STORE_BINDING");
    }
}
