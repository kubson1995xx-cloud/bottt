using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Xunit;
using Apex.Desktop.Okx;
namespace Apex.Windows.Tests;
[Collection("Extracted Windows")]
public sealed class WindowsOkxAuthApiTests
{
    // A native API causal probe, NOT a substitute for the secure CredUI modal.
    // Password exists only in this disposable hosted Windows test process. No
    // user password, broker credential, provider substitution or approval lease.
    [Fact,Trait("Gate","SimpleConnectSource")]
    public void Protected_password_unpack_and_real_disposable_LogonUser_preserve_1326_and_identity()
    {
        Assert.True(OperatingSystem.IsWindows());string name="Apx"+Guid.NewGuid().ToString("N")[..12];
        string password="a!9"+Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24));
        var user=new UserInfo{Name=name,Password=password,Privilege=1,Flags=0x201};bool created=false;IntPtr packed=IntPtr.Zero;uint size=0;
        char[] username=new char[514],domain=new char[257],oldPassword=new char[514],newPassword=new char[514];
        try
        {
            uint added=NetUserAdd(null,1,ref user,out _);Assert.True(added==0,"Disposable Windows account unavailable; native status "+added);created=true;
            string group=((NTAccount)new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid,null).Translate(typeof(NTAccount))).Value.Split('\\').Last();
            var member=new GroupMember{Name=Environment.MachineName+"\\"+name};uint grouped=NetLocalGroupAddMembers(null,group,3,ref member,1);Assert.True(grouped is 0 or 1378,"Disposable Users membership unavailable; status "+grouped);
            Assert.False(LogonUserW(name,".","not-the-generated-password",2,0,out var wrong));int refused=Marshal.GetLastWin32Error();wrong?.Dispose();Assert.Equal(1326,refused);
            Assert.False(CredPackAuthenticationBufferW(1,Environment.MachineName+"\\"+name,password,IntPtr.Zero,ref size));Assert.InRange(size,1u,65536u);packed=Marshal.AllocHGlobal((int)size);Assert.True(CredPackAuthenticationBufferW(1,Environment.MachineName+"\\"+name,password,packed,ref size));
            uint u=(uint)username.Length,d=(uint)domain.Length,p=(uint)oldPassword.Length;
            bool unpackedOld=CredUnPackAuthenticationBufferW(0,packed,size,username,ref u,domain,ref d,oldPassword,ref p);
            bool oldRecovered=unpackedOld&&oldPassword.AsSpan(0,Array.IndexOf(oldPassword,'\0')).SequenceEqual(password.AsSpan());
            Assert.False(oldRecovered,"Old flag0 unexpectedly recovered plaintext; protected-blob hypothesis not reproduced");
            Array.Clear(username);Array.Clear(domain);u=(uint)username.Length;d=(uint)domain.Length;p=(uint)newPassword.Length;
            Assert.True(WindowsOkxApprovalProvider.UnpackProtected(packed,size,username,ref u,domain,ref d,newPassword,ref p),"Production protected unpack refused; code "+Marshal.GetLastWin32Error());
            Assert.True(newPassword.AsSpan(0,Array.IndexOf(newPassword,'\0')).SequenceEqual(password.AsSpan()),"Native password mismatch; withheld");
            Assert.True(LogonUserW(name,".",password,2,0,out var token),"Disposable genuine password authentication refused; code "+Marshal.GetLastWin32Error());
            using(token)using(var identity=new WindowsIdentity(token.DangerousGetHandle()))using(var current=WindowsIdentity.GetCurrent())Assert.NotEqual(current.User,identity.User);
            WindowsTestHost.Evidence("native-auth-api-hypothesis",new{protectedPack=true,oldFlag0Recovered=false,productionUnpackRecovered=true,realDisposablePasswordAuthenticated=true,wrongPasswordWin32=refused,otherSidDetected=true,securePromptExercised=false,productionProtectedPositive=false,scope="Actual production unpack DLL plus Win32 pack/LogonUser operands; not full production enrollment"});
        }
        finally
        {
            Array.Clear(username);Array.Clear(domain);Array.Clear(oldPassword);Array.Clear(newPassword);
            if(packed!=IntPtr.Zero){for(int i=0;i<size;i++)Marshal.WriteByte(packed,i,0);Marshal.FreeHGlobal(packed);}
            if(created)Assert.Equal(0u,NetUserDel(null,name));
        }
    }
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]private struct UserInfo{internal string Name;internal string Password;internal uint PasswordAge;internal uint Privilege;internal string? Home;internal string? Comment;internal uint Flags;internal string? Script;}
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]private struct GroupMember{internal string Name;}
    [DllImport("netapi32.dll",CharSet=CharSet.Unicode)]private static extern uint NetUserAdd(string? server,uint level,ref UserInfo info,out uint error);
    [DllImport("netapi32.dll",CharSet=CharSet.Unicode)]private static extern uint NetUserDel(string? server,string name);
    [DllImport("netapi32.dll",CharSet=CharSet.Unicode)]private static extern uint NetLocalGroupAddMembers(string? server,string group,uint level,ref GroupMember member,uint count);
    [DllImport("credui.dll",CharSet=CharSet.Unicode,ExactSpelling=true,SetLastError=true)]private static extern bool CredPackAuthenticationBufferW(uint flags,string user,string password,IntPtr packed,ref uint size);
    [DllImport("credui.dll",CharSet=CharSet.Unicode,ExactSpelling=true,SetLastError=true)]private static extern bool CredUnPackAuthenticationBufferW(uint flags,IntPtr packed,uint size,[Out]char[] user,ref uint us,[Out]char[] domain,ref uint ds,[Out]char[] password,ref uint ps);
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,ExactSpelling=true,SetLastError=true)]private static extern bool LogonUserW(string user,string? domain,string password,int type,int provider,out SafeAccessTokenHandle token);
}
