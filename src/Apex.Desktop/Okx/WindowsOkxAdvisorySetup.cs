using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Apex.Desktop.Services;
namespace Apex.Desktop.Okx;
internal static class WindowsOkxAdvisorySetup
{
    internal static async Task<string> ShowAsync(string root,CancellationToken ct)
    {
        var accepted=await WindowsOkxDialog.ShowAsync("APEX — lokalne narzędzia AI","Jednorazowa instalacja przypiętych Vibe-Trading, TradingAgents i Hermes na tym PC. Bez VPS i bez płatnego klucza API. Następnie otworzy się logowanie OpenAI w przeglądarce; nie wpisuj tokenów w panelu. Budżet wspólny: 48 żądań / 900 sekund zarezerwowanych dziennie. Brak AI blokuje wyłącznie NOWE zakupy. Zatrzymaj bota przed aktualizacją; istniejące konto, historia i DPAPI pozostają bez zmian.",Array.Empty<OkxNativeField>(),ct).ConfigureAwait(false);
        if(accepted==null)return "AI_SETUP_CANCELLED_PRESERVED";
        string bundle=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"..","AI"));
        string installed=await InstallAsync(root,bundle,ct).ConfigureAwait(false);
        if(installed!="AI_INSTALLED_LOCAL_SIGN_IN_REQUIRED")return installed;
        return await OkxAdvisoryCoordinator.RunCompanion(root,"login",null,ct).ConfigureAwait(false);
    }
    internal static async Task<string> InstallAsync(string root,string bundle,CancellationToken ct)
    {
        if(!OperatingSystem.IsWindows())return "AI_NATIVE_WINDOWS_REQUIRED";
        ct.ThrowIfCancellationRequested();
        // This separate root is NOT inside the broker profile; large Python closures cannot
        // exhaust the existing broker startup's bounded private-tree scanner.
        string control=Path.Combine(root,"control");using var lease=WindowsPrivateData.Open(control,AppContext.BaseDirectory);
        using(var manifest=OkxAdvisorySnapshot.Document(File.ReadAllBytes(Path.Combine(bundle,"bundle.json")),16777216))
        {
            var e=manifest.RootElement;OkxConfiguration.Fields(e,new[]{"Schema","Files"});if(e.GetProperty("Schema").GetString()!="apex-advisory-bundle-v1")return "AI_BUNDLE_REFUSED";
            var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);int count=0;
            foreach(var row in e.GetProperty("Files").EnumerateArray())
            {
                ct.ThrowIfCancellationRequested();OkxConfiguration.Fields(row,new[]{"Path","Sha256"});string rel=row.GetProperty("Path").GetString()!,hash=row.GetProperty("Sha256").GetString()!;
                if(++count>100000||rel.Length==0||rel.Contains('\\')||rel.Contains(':')||rel.StartsWith('/')||rel.Split('/').Any(s=>s is "" or "." or "..")||!names.Add(rel)||!OkxAdvisorySnapshot.Hex(hash,64))return "AI_BUNDLE_REFUSED";
                string path=Path.Combine(bundle,rel.Replace('/',Path.DirectorySeparatorChar));
                if((File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0||!HashFile(path,hash))return "AI_BUNDLE_HASH_REFUSED";
            }
            if(!names.Contains("python-base/python.exe")||!names.Contains("bridge/local_install.py")||!names.Contains("ApexAdvisoryStoreGuard.exe"))return "AI_BUNDLE_REFUSED";
            var actual=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void Visit(string directory)
            {
                foreach(string path in Directory.EnumerateFileSystemEntries(directory))
                {
                    var attributes=File.GetAttributes(path);if((attributes&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException("AI_BUNDLE_REPARSE");
                    if((attributes&FileAttributes.Directory)!=0)Visit(path);else actual.Add(Path.GetRelativePath(bundle,path).Replace('\\','/'));
                }
            }
            Visit(bundle);names.Add("bundle.json");if(!actual.SetEquals(names))return "AI_BUNDLE_UNLISTED_FILES";
        }
        var start=OkxAdvisoryCoordinator.StartInfo(Path.Combine(bundle,"python-base","python.exe"),control);start.ArgumentList.Add("-B");start.ArgumentList.Add("-I");start.ArgumentList.Add(Path.Combine(bundle,"bridge","local_install.py"));start.ArgumentList.Add(bundle);start.ArgumentList.Add(root);
        start.RedirectStandardError=true;
        using var process=new Process{StartInfo=start};process.Start();
        string stage="START";
        async Task CaptureStage()
        {
            var buffer=new char[256];var line=new System.Text.StringBuilder(256);int count=0,total=0,n;bool overflow=false;
            while((n=await process.StandardError.ReadAsync(buffer.AsMemory()).ConfigureAwait(false))>0)
            {
                total+=n;if(total>65536){if(!process.HasExited)process.Kill(true);return;}
                for(int i=0;i<n;i++)
                {
                    char c=buffer[i];if(c=='\r')continue;
                    if(c=='\n')
                    {
                        string text=line.ToString();line.Clear();
                        if(!overflow&&++count<=64&&System.Text.RegularExpressions.Regex.IsMatch(text,"\\AAI_INSTALL_(?:STAGE|FAILURE)_[A-Za-z0-9_.-]{1,180}\\z")){stage=text;Console.WriteLine(text);}overflow=false;
                    }
                    else if(line.Length<256)line.Append(c);else overflow=true;
                }
            }
        }
        Task stages=CaptureStage();
        try
        {
            process.StandardInput.Close();using var limit=new CancellationTokenSource(TimeSpan.FromMinutes(20));using var linked=CancellationTokenSource.CreateLinkedTokenSource(ct,limit.Token);
            char[] output=new char[257];int used=0,read;
            while(used<output.Length&&(read=await process.StandardOutput.ReadAsync(output.AsMemory(used),linked.Token).ConfigureAwait(false))>0)used+=read;
            if(used==output.Length){process.Kill(true);return "AI_INSTALL_IPC_REFUSED_PARTIAL_PRESERVED";}
            string line=new string(output,0,used).Trim();await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);await stages.ConfigureAwait(false);
            return process.ExitCode==0&&line=="AI_INSTALLED_LOCAL_SIGN_IN_REQUIRED"?line:"AI_INSTALL_REFUSED_PARTIAL_PRESERVED_"+stage;
        }
        finally{if(!process.HasExited){process.Kill(true);await process.WaitForExitAsync().ConfigureAwait(false);}}
    }
    static bool HashFile(string path,string expected){using var f=File.OpenRead(path);return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(f)).ToLowerInvariant()==expected;}
}
