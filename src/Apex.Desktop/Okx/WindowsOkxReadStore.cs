using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Apex.Adapters.Okx.Execution;
using Apex.Desktop.Services;
using Microsoft.Win32.SafeHandles;
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
    private string PendingPath=>Path.Combine(root,"okx-read-pending");
    private void CheckPending(){if(System.IO.Directory.Exists(PendingPath)&&System.IO.Directory.EnumerateFileSystemEntries(PendingPath).Any())throw new InvalidDataException("READ_STORE_PENDING_PRESERVED");}
    internal OkxReadAccount[] List()
    {
        CheckPending();
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
        return Decode(encrypted,path);
    }
    private OkxReadSecret Decode(byte[] encrypted,string path)
    {
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
    internal OkxReadAccount Save(OkxEnrollmentObservation observed,byte[][] values)=>Save(observed,values,null,null);
    internal OkxReadAccount Save(OkxEnrollmentObservation observed,byte[][] values,OkxReadCommit? commit,Action<string>? checkpoint)
    {
        commit?.Check(observed,values);
        if(observed.Environment!="Demo"||observed.Provenance!="DemoTradingApi"||observed.Fingerprint!=Convert.ToHexString(SHA256.HashData(values[0])).ToLowerInvariant())throw new InvalidDataException("READ_STORE_BINDING");
        using var admitted=WindowsPrivateData.Open(root,AppContext.BaseDirectory);
        if(List().Any(a=>a.Uid==observed.Uid&&a.Environment==observed.Environment))throw new InvalidDataException("ACCOUNT_ALREADY_SAVED_RECONNECT");
        var account=new OkxReadAccount("read-"+Guid.NewGuid().ToString("N"),observed.Uid,observed.MainUid,observed.Environment,observed.Provenance,observed.Fingerprint);Validate(account);
        if(!System.IO.Directory.Exists(DirectoryPath))WindowsPathSecurity.CreatePrivateDirectory(DirectoryPath);
        if(!System.IO.Directory.Exists(PendingPath))WindowsPathSecurity.CreatePrivateDirectory(PendingPath);
        // Hold new directories too; do not swap/recreate an existing admitted path.
        admitted.ValidateTree();
        string path=Path.Combine(DirectoryPath,account.ScopeId+".dpapi");byte[] metadata=JsonSerializer.SerializeToUtf8Bytes(account);
        // Exact bounded non-growing encoding, no plaintext JSON secret/string copies.
        if(values.Length!=3||values.Any(b=>b.Length is <=0 or >4096))throw new InvalidDataException("READ_STORE_FIELD");
        byte[] plain=new byte[checked(4+metadata.Length+12+values.Sum(b=>b.Length))];
        try
        {
            using(var stream=new MemoryStream(plain,true))using(var writer=new BinaryWriter(stream,Encoding.UTF8,true))
            {writer.Write(metadata.Length);writer.Write(metadata);foreach(var b in values){writer.Write(b.Length);writer.Write(b);}}
            byte[] encrypted=WindowsOkxCredentialStore.Crypt(plain,Purpose(path),true);
            using var staged=new OwnedCiphertext(Path.Combine(PendingPath,account.ScopeId+".dpapi.pending"),encrypted);
            using var readback=Decode(staged.Read(),path);
            if(readback.Account!=account||!values.Select((v,i)=>v.AsSpan().SequenceEqual(readback.Values[i])).All(b=>b))throw new InvalidDataException("READ_STORE_READBACK_PRESERVED");
            checkpoint?.Invoke("CURRENT_USER_DPAPI_PREPARED");
            if(commit!=null)return commit.Publish(observed,values,account,()=>staged.Publish(path));
            staged.Publish(path);return account; // Internal store helper, never enrollment.
        }
        finally{CryptographicOperations.ZeroMemory(plain);}
    }
    // Handle-owned ciphertext: no pathname-based cleanup, no write/delete sharing,
    // CREATE_NEW and ReplaceIfExists=false. Unknown prior files are never removed.
    private sealed class OwnedCiphertext : IDisposable
    {
        private readonly FileStream file;
        private bool published;
        internal OwnedCiphertext(string path,byte[] encrypted)
        {
            var handle=CreateFileW(path,0xC0010000,0,IntPtr.Zero,1,0x80200000,IntPtr.Zero);
            if(handle.IsInvalid){handle.Dispose();throw new InvalidDataException("READ_STORE_STAGE_CREATE_REFUSED");}
            file=new FileStream(handle,FileAccess.ReadWrite,4096,false);
            try
            {
                if(!GetFileInformationByHandle(handle,out var info)||info.Links!=1||(info.Attributes&(uint)(FileAttributes.ReparsePoint|FileAttributes.Directory))!=0)throw new InvalidDataException("READ_STORE_STAGE_REFUSED");
                using var view=new WindowsPathSecurity.NativeObject(new SafeFileHandle(handle.DangerousGetHandle(),false),path,info);WindowsPathSecurity.Validate(view,true);
                file.Write(encrypted);file.Flush(true);
            }
            catch{Dispose();throw;}
        }
        internal byte[] Read(){file.Position=0;if(file.Length is <=0 or >20000)throw new InvalidDataException("READ_STORE_SIZE");var bytes=new byte[(int)file.Length];file.ReadExactly(bytes);return bytes;}
        internal void Publish(string path)
        {
            // FILE_RENAME_INFO: BOOLEAN, aligned HANDLE, DWORD, UTF16 filename.
            int rootOffset=IntPtr.Size==8?8:4,lengthOffset=rootOffset+IntPtr.Size,nameOffset=lengthOffset+4;
            byte[] name=Encoding.Unicode.GetBytes(path);byte[] info=new byte[checked(nameOffset+name.Length)];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(info.AsSpan(lengthOffset),name.Length);name.CopyTo(info,nameOffset);
            if(!SetFileInformationByHandle(file.SafeFileHandle,3,info,(uint)info.Length))throw new InvalidDataException("READ_STORE_COMMIT_REFUSED_PRESERVED");
            published=true;
        }
        public void Dispose()
        {
            bool cleaned=true;
            try{if(!published)cleaned=SetFileInformationByHandle(file.SafeFileHandle,4,new byte[]{1},1);}
            finally{file.Dispose();}
            if(!cleaned)throw new InvalidDataException("READ_STORE_OWNED_STAGE_CLEANUP_REFUSED_PRESERVED");
        }
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern SafeFileHandle CreateFileW(string path,uint access,uint share,IntPtr attributes,uint creation,uint flags,IntPtr template);
        [DllImport("kernel32.dll",SetLastError=true)]private static extern bool SetFileInformationByHandle(SafeFileHandle file,int infoClass,byte[] info,uint size);
        [DllImport("kernel32.dll",SetLastError=true)]private static extern bool GetFileInformationByHandle(SafeFileHandle file,out WindowsPathSecurity.FileInformation info);
    }
    private static void Validate(OkxReadAccount a)
    {
        if(!Regex.IsMatch(a.ScopeId,"\\Aread-[a-f0-9]{32}\\z")||!Regex.IsMatch(a.Uid,"\\A[0-9]{1,32}\\z")||a.MainUid!=a.Uid||a.Environment!="Demo"||a.Provenance!="DemoTradingApi"||!Regex.IsMatch(a.Fingerprint,"\\A[a-f0-9]{64}\\z"))throw new InvalidDataException("READ_STORE_BINDING");
    }
}
