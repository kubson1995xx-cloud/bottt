using System.Net;
using Apex.Adapters.Okx.Execution;
using Xunit;
namespace Apex.Adapters.Okx.Tests;

public sealed class OkxSerializedDispatchTests
{
    private sealed class Fence : IOkxSerializedDispatchFence
    {
        internal bool Held, Deny;
        internal int Enters, Disposes, BooleanCalls;
        public bool CanDispatch(OkxPreparedRequest r, DateTimeOffset now) { BooleanCalls++; return true; }
        public IDisposable? TryEnterDispatch(OkxPreparedRequest r, DateTimeOffset now)
        { Enters++; if(Deny)return null; Held=true; return new Guard(this); }
        private sealed class Guard(Fence f) : IDisposable
        { public void Dispose(){Assert.True(f.Held);f.Held=false;f.Disposes++;} }
    }
    [Fact]
    public async Task OkxSerializedDispatch_GuardCoversHandlerInvocation()
    {
        using var peer=new OkxExecutionHttpFixture();var fence=new Fence();
        peer.Steps.Enqueue(("POST","/api/v5/trade/order",OkxExecutionHttpFixture.SubmitBody,ct=>{
            Assert.True(fence.Held,"Serialized guard must cover actual synchronous handler invocation");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(OkxExecutionHttpFixture.Ack())});}));
        await using var client=OkxExecutionClient.ForTests(peer,peer.Clock,peer.Scope,peer,peer.Fingerprint,fence,peer.Clock.Delay);
        Assert.Equal(OkxWriteDisposition.Acknowledged,(await client.SubmitAsync(peer.Request(),default)).Disposition);
        Assert.False(fence.Held);Assert.Equal(1,fence.Disposes);Assert.Equal(0,fence.BooleanCalls);peer.Done();
    }
    [Fact]
    public async Task NullLeaseDeniesWithoutBooleanFallback()
    {
        using var peer=new OkxExecutionHttpFixture();var fence=new Fence{Deny=true};
        await using var client=OkxExecutionClient.ForTests(peer,peer.Clock,peer.Scope,peer,peer.Fingerprint,fence,peer.Clock.Delay);
        Assert.Equal(OkxWriteDisposition.ProvenNotSent,(await client.SubmitAsync(peer.Request(),default)).Disposition);
        Assert.Empty(peer.Sent);Assert.Equal(1,fence.Enters);Assert.Equal(0,fence.BooleanCalls);
    }
    [Fact]
    public async Task SynchronousThrowDisposesGuardAndRemainsUnknown()
    {
        using var peer=new OkxExecutionHttpFixture();var fence=new Fence();
        using var handler=new SyncHandler(()=>{Assert.True(fence.Held);throw new HttpRequestException("SYNTHETIC_FAILURE");});
        await using var client=OkxExecutionClient.ForTests(handler,peer.Clock,peer.Scope,peer,peer.Fingerprint,fence,peer.Clock.Delay);
        Assert.Equal(OkxWriteDisposition.Unknown,(await client.SubmitAsync(peer.Request(),default)).Disposition);
        Assert.False(fence.Held);Assert.Equal(1,fence.Disposes);
    }
    [Fact]
    public async Task GuardReleasedBeforeResponseAwait()
    {
        using var peer=new OkxExecutionHttpFixture();var fence=new Fence();
        var response=new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler=new SyncHandler(()=>{Assert.True(fence.Held);return response.Task;});
        await using var client=OkxExecutionClient.ForTests(handler,peer.Clock,peer.Scope,peer,peer.Fingerprint,fence,peer.Clock.Delay);
        var pending=client.SubmitAsync(peer.Request(),default);
        Assert.False(pending.IsCompleted);Assert.False(fence.Held);Assert.Equal(1,fence.Disposes);
        response.SetResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(OkxExecutionHttpFixture.Ack())});
        Assert.Equal(OkxWriteDisposition.Acknowledged,(await pending).Disposition);
    }
    private sealed class SyncHandler(Func<Task<HttpResponseMessage>> invoke) : HttpMessageHandler
    {protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r,CancellationToken ct)=>invoke();}
}
