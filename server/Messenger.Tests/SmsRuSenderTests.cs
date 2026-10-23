using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Messenger.Api;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Messenger.Tests;

[Trait("Category", "Unit")]
public sealed class SmsRuSenderTests
{
    private const string Phone = "+79001234567";
    private const string Accepted = """{"status":"OK","status_code":100,"sms":{"79001234567":{"status":"OK","status_code":100,"sms_id":"test-1"}}}""";
    private static readonly IConfiguration Config = new ConfigurationBuilder().AddInMemoryCollection(
        new Dictionary<string, string?> { ["Auth:Sms:SmsRu:ApiKey"] = "SYNTHETIC-NOT-A-CREDENTIAL" }).Build();

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("""{"status":100,"status_code":100}""")]
    [InlineData("""{"status":"OK","status_code":"100"}""")]
    [InlineData("""{"status":"ERROR","status_code":500}""")]
    [InlineData("""{"status":"ERROR","status_code":999}""")]
    [InlineData("""{"status":"ERROR","status_code":200,"sms":{}}""")]
    [InlineData("""{"status":"ERROR","status_code":200,"status":"OK"}""")]
    [InlineData("""{"status":"OK","status_code":100,"sms":{"79001234568":{"status":"OK","status_code":100,"sms_id":"1"}}}""")]
    [InlineData("""{"status":"OK","status_code":100,"sms":{"79001234567":null}}""")]
    [InlineData("""{"status":"OK","status_code":100,"sms":{"79001234567":{"status":true,"status_code":100}}}""")]
    [InlineData("""{"status":"OK","status_code":100,"sms":{"79001234567":{"status":"OK","status_code":103,"sms_id":"1"}}}""")]
    [InlineData("""{"status":"OK","status_code":100,"sms":{"79001234567":{"status":"OK","status_code":100,"sms_id":""}}}""")]
    [InlineData("""{"status":"OK","status_code":100,"sms":{"79001234567":{"status":"OK","status_code":100,"sms_id":42}}}""")]
    [InlineData("""{"status":"OK","status_code":100,"sms":{"79001234567":{"status":"OK","status_code":100,"sms_id":"a\nb"}}}""")]
    [InlineData("""{"status":"OK","status_code":100,"sms":{"79001234567":{"status":"ERROR","status_code":207,"sms_id":"1"}}}""")]
    [InlineData("""{"status":"OK","status_code":100,"sms":{"79001234567":{"status":"OK","status_code":100,"status_code":207,"sms_id":"1"}}}""")]
    [InlineData("""{"status":"OK","status_code":100,"sms":{"79001234567":{"status":"OK","status_code":100,"sms_id":"1"},"79001234567":{"status":"ERROR","status_code":207}}}""")]
    public void MalformedContradictoryOrUnboundConfirmationIsUnknown(string response)
    {
        using var json = JsonDocument.Parse(response);
        Assert.Equal(SmsResult.Unknown, SmsRuSender.Interpret(json.RootElement, Phone[1..]));
    }

    [Fact]
    public async Task UnknownLengthBodyIsReadOnlyToLimitPlusOneAndDisposed()
    {
        var stream = new ProbeStream(SmsRuSender.MaxResponseBytes * 100);
        using var handler = new Handler(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) });
        using var client = new HttpClient(handler);
        Assert.Equal(SmsResult.Unknown, await new SmsRuSender(client, Config).Send(Phone, "synthetic", default));
        Assert.Equal(SmsRuSender.MaxResponseBytes + 1, stream.BytesRead);
        Assert.True(stream.Disposed);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task DeclaredOversizedBodyIsNotRead()
    {
        var stream = new ProbeStream(1);
        using var handler = new Handler(() =>
        {
            var content = new StreamContent(stream);
            content.Headers.ContentLength = SmsRuSender.MaxResponseBytes + 1;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });
        using var client = new HttpClient(handler);
        Assert.Equal(SmsResult.Unknown, await new SmsRuSender(client, Config).Send(Phone, "synthetic", default));
        Assert.Equal(0, stream.BytesRead);
        Assert.True(stream.Disposed);
    }

    [Fact]
    public async Task TransportReadFailureIsUnknownWithoutRetry()
    {
        var stream = new ProbeStream(1, fail: true);
        using var handler = new Handler(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) });
        using var client = new HttpClient(handler);
        Assert.Equal(SmsResult.Unknown, await new SmsRuSender(client, Config).Send(Phone, "synthetic", default));
        Assert.True(stream.Disposed);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task DeadlineAlsoCoversStalledBodyAfterHeaders()
    {
        var stream = new ProbeStream(1, stall: true);
        using var handler = new Handler(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) });
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var elapsed = Stopwatch.StartNew();
        Assert.Equal(SmsResult.Unknown, await new SmsRuSender(client, Config).Send(Phone, "synthetic", default));
        Assert.InRange(elapsed.Elapsed.TotalSeconds, 14, 25);
        Assert.True(stream.Disposed);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task CallerCancellationAlsoInterruptsBody()
    {
        var stream = new ProbeStream(1, stall: true);
        using var handler = new Handler(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) });
        using var client = new HttpClient(handler);
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        Assert.Equal(SmsResult.Unknown, await new SmsRuSender(client, Config).Send(Phone, "synthetic", cancel.Token));
        Assert.True(stream.Disposed);
    }

    [Fact]
    public async Task FixedEndpointOverridesClientBaseAddress()
    {
        using var handler = new Handler(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Accepted) });
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://invalid.example/") };
        Assert.Equal(SmsResult.Accepted, await new SmsRuSender(client, Config).Send(Phone, "synthetic", default));
        Assert.Equal("https://sms.ru/sms/send", handler.Url!.AbsoluteUri);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData("79001234567")]
    [InlineData("+++79001234567")]
    [InlineData("+79001234567,+79001234568")]
    [InlineData("+０９００１２３４５６７")]
    [InlineData("+00012345")]
    [InlineData("+1234567890123456")]
    public async Task NonCanonicalOrMultipleRecipientsFailBeforeNetwork(string phone)
    {
        using var handler = new Handler(() => throw new InvalidOperationException("Must not send"));
        using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<ArgumentException>(() => new SmsRuSender(client, Config).Send(phone, "synthetic", default));
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData(307)]
    [InlineData(429)]
    [InlineData(500)]
    public async Task HttpFailureIsUnknownAndNeverRetried(int status)
    {
        using var handler = new Handler(() => new HttpResponseMessage((HttpStatusCode)status));
        using var client = new HttpClient(handler);
        Assert.Equal(SmsResult.Unknown, await new SmsRuSender(client, Config).Send(Phone, "synthetic", default));
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task ExcessiveJsonDepthIsUnknown()
    {
        var body = Accepted[..^1] + ",\"extra\":" + new string('[', 10) + "0" + new string(']', 10) + "}";
        using var handler = new Handler(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        using var client = new HttpClient(handler);
        Assert.Equal(SmsResult.Unknown, await new SmsRuSender(client, Config).Send(Phone, "synthetic", default));
    }

    [Fact]
    public async Task ProductionTransportDoesNotReplayFormOn307()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        var sinkCalls = 0;
        app.MapPost("/redirect", context =>
        {
            context.Response.StatusCode = 307;
            context.Response.Headers.Location = "/sink";
            return Task.CompletedTask;
        });
        app.MapPost("/sink", () => { Interlocked.Increment(ref sinkCalls); return "unexpected"; });
        await app.StartAsync();
        try
        {
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var handler = SmsRuSender.CreateHttpHandler();
            handler.UseProxy = false; // Loopback fixture only; production configuration is unchanged.
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
            using var response = await client.PostAsync(address + "/redirect", new FormUrlEncodedContent(
                new Dictionary<string, string> { ["api_id"] = "SYNTHETIC-NOT-A-CREDENTIAL" }));
            Assert.Equal(HttpStatusCode.TemporaryRedirect, response.StatusCode);
            Assert.Equal(0, Volatile.Read(ref sinkCalls));
        }
        finally { await app.StopAsync(); }
    }

    private sealed class Handler(Func<HttpResponseMessage> response) : HttpMessageHandler
    {
        public int Calls;
        public Uri? Url;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            Url = request.RequestUri;
            return Task.FromResult(response());
        }
    }

    private sealed class ProbeStream(int length, bool fail = false, bool stall = false) : Stream
    {
        public int BytesRead;
        public bool Disposed;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (fail) throw new IOException("Synthetic read failure");
            if (stall) await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            var read = Math.Min(buffer.Length, length - BytesRead);
            buffer.Span[..read].Fill((byte)' ');
            BytesRead += read;
            return read;
        }
        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
