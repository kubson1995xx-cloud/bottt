using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Apex.Desktop.Okx;
using Xunit;
namespace Apex.Tests;
public sealed class OkxLinuxPrivateTests
{
    [Fact]
    public void Real_Linux_private_publication_refuses_links_modes_and_FIFO_without_repair_or_blocking()
    {
        if(!OperatingSystem.IsLinux())return;
        // Durable native evidence lives outside scratch: this machine's scratch
        // ancestor is group-writable, which the production path gate must refuse.
        string parent=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),"apex-private","okx-demo-fee0-offline");
        Directory.CreateDirectory(parent,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute);
        string root=Path.Combine(parent,Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute);
        try
        {
            OkxLinuxPrivate.AdmitDirectory(root,false);string file=Path.Combine(root,"report.json");
            OkxLinuxPrivate.Publish(file,new byte[]{1,2,3});Assert.Equal(new byte[]{1,2,3},OkxLinuxPrivate.ReadFile(file));
            File.SetUnixFileMode(file,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.GroupRead);Assert.Throws<IOException>(()=>OkxLinuxPrivate.AdmitFile(file));Assert.True(File.GetUnixFileMode(file).HasFlag(UnixFileMode.GroupRead));File.SetUnixFileMode(file,UnixFileMode.UserRead|UnixFileMode.UserWrite);
            string alias=Path.Combine(root,"alias");File.CreateSymbolicLink(alias,file);Assert.Throws<IOException>(()=>OkxLinuxPrivate.AdmitFile(alias));File.Delete(alias);
            Assert.Equal(0,link(file,alias));Assert.Throws<IOException>(()=>OkxLinuxPrivate.AdmitFile(alias));Assert.Throws<IOException>(()=>OkxLinuxPrivate.AdmitFile(file));File.Delete(alias);
            string fifo=Path.Combine(root,"fifo");Assert.Equal(0,mkfifo(fifo,0x180));var wait=System.Diagnostics.Stopwatch.StartNew();Assert.Throws<IOException>(()=>OkxLinuxPrivate.AdmitFile(fifo));Assert.True(wait.Elapsed<TimeSpan.FromSeconds(1));File.Delete(fifo);
            string dirAlias=root+"-alias";Directory.CreateSymbolicLink(dirAlias,root);try{Assert.Throws<IOException>(()=>OkxLinuxPrivate.AdmitDirectory(dirAlias,false));}finally{Directory.Delete(dirAlias);}
        }
        finally{Console.WriteLine("Synthetic native private-path evidence retained at "+root);}
    }
    [DllImport("libc",SetLastError=true)]private static extern int mkfifo(string path,uint mode);
    [DllImport("libc",SetLastError=true)]private static extern int link(string path,string target);
}
