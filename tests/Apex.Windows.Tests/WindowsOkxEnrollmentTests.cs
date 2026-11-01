using System.Security.Cryptography;
using System.Text;
using Apex.Desktop.Okx;
using Apex.Desktop.Services;
using Xunit;
namespace Apex.Windows.Tests;
[Collection("Extracted Windows")]
public sealed class WindowsOkxEnrollmentTests
{
    private static OkxScopeOptions Option()=>new(){ScopeId="enrollment-native",Environment="OkxDemo",Region="EEA",ExpectedUid="123456",ExpectedMainUid="123456",QuoteCurrency="EUR",CredentialBindingId="native-owned-fixture",SafetyVersion="okx-cash-v1"};
    private static OkxScopeBinding Binding(OkxScopeOptions o)=>OkxConfiguration.Bind(new(){Schema="okx-naval-config-v1",Scopes=new[]{o}})[0];
    [Fact,Trait("Gate","EnrollmentSource")]
    public async Task Actual_masked_secret_form_validation_then_native_persist_and_reopen()
    {
        Assert.True(OperatingSystem.IsWindows());string root=WindowsTestHost.NewRoot();using(var admitted=WindowsPrivateData.Open(root,AppContext.BaseDirectory)){}
        using var ct=new CancellationTokenSource(TimeSpan.FromSeconds(25));var option=Option();var binding=Binding(option);byte[][]? values=null;
        var synthetic=Enumerable.Range(0,3).Select(_=>Convert.ToHexString(RandomNumberGenerator.GetBytes(32))).ToArray();
        try
        {
            var config=new ConfigStore(root);byte[] original=File.ReadAllBytes(config.Path_);
            var pending=WindowsOkxProvisioning.ShowSecretsAsync(binding,ct.Token);
            var w=await WindowsNativeUi.Window(Environment.ProcessId,"APEX — lokalny klucz OKX");
            bool masked=Enumerable.Range(100,3).All(id=>WindowsNativeUi.Masked(w,id));Assert.True(masked);
            WindowsNativeUi.Click(w,1);await Task.Delay(150);Assert.True(WindowsNativeUi.Visible(w));Assert.False(string.IsNullOrEmpty(WindowsNativeUi.Error(w)));
            for(int i=0;i<3;i++)WindowsNativeUi.Text(w,100+i,synthetic[i]);WindowsNativeUi.Click(w,1);
            var form=await pending.WaitAsync(TimeSpan.FromSeconds(5));Assert.Equal("ACCEPTED",form.Code);Assert.NotNull(form.Values);values=form.Values;
            Assert.True(values.Select((v,i)=>v.AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(synthetic[i]))).All(v=>v),"Native field roundtrip mismatch; values withheld");
            var saved=WindowsOkxProvisioning.PersistAndVerify(config,option,binding,values);Assert.Equal("SAVED_KEY_CONFIG_READBACK_VERIFIED_STOP",saved.Code);
            var restarted=new ConfigStore(root);Assert.True(WindowsOkxProvisioning.SavedBindingMatches(restarted,binding));
            await using var runtime=OkxNavalRuntime.Compose(root,OkxConfiguration.Bind(restarted.Config.OkxNaval));Assert.True(runtime.VerifySavedCredential(binding));Assert.Equal("STOP",runtime.Mode);
            await using var control=new OkxOperatorControl(runtime,restarted);Assert.Equal("SAVED_KEY_CONFIG_RUNTIME_VERIFIED",await control.ExecuteAsync(binding.ScopeId,"diagnose","owned-diagnosis",default));
            foreach(string text in synthetic){Assert.False(File.ReadAllText(config.Path_).Contains(text),"Secret in config; value withheld");Assert.False(Encoding.UTF8.GetString(File.ReadAllBytes(OkxConfiguration.Paths(root,binding).Credentials)).Contains(text),"Secret in ciphertext; value withheld");}
            Assert.False(Directory.Exists(OkxConfiguration.Paths(root,binding).Authority));Assert.False(Directory.Exists(OkxConfiguration.Paths(root,binding).Publication));
            var before=WindowsTestHost.Inventory(root);Assert.Equal("KEY_STORE_WRITE_FAILED_PRESERVED",WindowsOkxProvisioning.PersistAndVerify(config,option,binding,values).Code);WindowsTestHost.SameInventory(before,root);
            WindowsTestHost.Evidence("native-secret-store-reopen",new{maskedFields=3,actualNativeValidation=true,syntheticRoundtrip=true,currentUserDpapi=true,configAndRuntimeReopened=true,authorityCreated=false,networkCalled=false,secureApprovalPositiveVerified=false,scope="production native form and persistence helper, NOT full protected approval chain"});
        }
        finally{ct.Cancel();if(values!=null)foreach(var b in values)CryptographicOperations.ZeroMemory(b);Directory.Delete(root,true);}
    }
    [Fact,Trait("Gate","EnrollmentSource")]
    public async Task Secret_cancel_and_partial_config_failure_preserve_existing_state()
    {
        Assert.True(OperatingSystem.IsWindows());string root=WindowsTestHost.NewRoot();using(var admitted=WindowsPrivateData.Open(root,AppContext.BaseDirectory)){}
        using var ct=new CancellationTokenSource(TimeSpan.FromSeconds(20));var option=Option();var binding=Binding(option);
        byte[][] values=Enumerable.Range(0,3).Select(_=>Encoding.UTF8.GetBytes(Convert.ToHexString(RandomNumberGenerator.GetBytes(32)))).ToArray();
        try
        {
            var config=new ConfigStore(root);var before=WindowsTestHost.Inventory(root);
            var pending=WindowsOkxProvisioning.ShowSecretsAsync(binding,ct.Token);var w=await WindowsNativeUi.Window(Environment.ProcessId,"APEX — lokalny klucz OKX");WindowsNativeUi.Cancel(w);
            var cancelled=await pending.WaitAsync(TimeSpan.FromSeconds(5));Assert.Equal("USER_CANCELLED",cancelled.Code);Assert.Null(cancelled.Values);WindowsTestHost.SameInventory(before,root);
            using(var locked=new FileStream(config.Path_,FileMode.Open,FileAccess.Read,FileShare.Read))Assert.Equal("CONFIG_WRITE_FAILED_KEY_PRESERVED",WindowsOkxProvisioning.PersistAndVerify(config,option,binding,values).Code);
            Assert.True(File.Exists(OkxConfiguration.Paths(root,binding).Credentials));Assert.Empty(OkxConfiguration.Bind(config.Config.OkxNaval));
            var partial=WindowsTestHost.Inventory(root);Assert.Equal("KEY_STORE_WRITE_FAILED_PRESERVED",WindowsOkxProvisioning.PersistAndVerify(config,option,binding,values).Code);WindowsTestHost.SameInventory(partial,root);
            WindowsTestHost.Evidence("native-enrollment-cancel-partial",new{cancelNoWrites=true,ciphertextPreservedAfterConfigFailure=true,noOverwrite=true,approvalBypassed=false,scope="native form and persistence helper; no protected-positive claim"});
        }
        finally{ct.Cancel();foreach(var b in values)CryptographicOperations.ZeroMemory(b);Directory.Delete(root,true);}
    }
}
