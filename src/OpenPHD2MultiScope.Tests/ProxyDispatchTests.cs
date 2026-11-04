using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using TianWen.Lib.Devices.Guider;
using Xunit;

namespace TianWen.OpenPHD2MultiScope.Tests;

public sealed class ProxyDispatchTests : IAsyncDisposable
{
    private const string Dither = """{"method":"dither","params":{"amount":5,"raOnly":false,"settle":{"pixels":1.5,"time":10,"timeout":60}},"id":1}""";

    private readonly IGuider _guider = Substitute.For<IGuider>();
    private readonly ProxyServer _server;
    private readonly List<IAsyncDisposable> _disposables = [];
    private int _nextId;

    public ProxyDispatchTests()
    {
        _guider.Connected.Returns(true);
        var options = new ProxyOptions("localhost", 1, IPAddress.Loopback, 0, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(1));
        _server = new ProxyServer(_guider, options, NullLogger.Instance, TimeProvider.System);
    }

    [Fact]
    public async Task EveryRequestGetsAReplyWithItsId()
    {
        _guider.GetStatusAsync(Arg.Any<CancellationToken>()).Returns(new ValueTask<(string?, double)>(("Guiding", 0.3)));
        var client = await ConnectAsync();

        var reply = await HandleAsync(client, """{"method":"get_app_state","id":42}""");

        reply.GetProperty("id").GetInt32().ShouldBe(42);
        reply.GetProperty("result").GetString().ShouldBe("Guiding");
    }

    [Fact]
    public async Task AnUnsupportedMethodIsAnErrorNotSilence()
    {
        var client = await ConnectAsync();

        var reply = await HandleAsync(client, """{"method":"find_star","id":3}""");

        reply.GetProperty("error").GetProperty("code").GetInt32().ShouldBe(-32601);
        reply.GetProperty("id").GetInt32().ShouldBe(3);
    }

    [Fact]
    public async Task AMalformedLineIsAnErrorAndTheClientStaysUsable()
    {
        var client = await ConnectAsync();

        (await HandleAsync(client, "{not json")).GetProperty("error").GetProperty("code").GetInt32().ShouldBe(-32700);
        (await HandleAsync(client, """{"method":"get_connected","id":2}""")).GetProperty("result").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task ANotificationGetsNoReply()
    {
        var client = await ConnectAsync();
        _server.Register(client);

        (await _server.HandleLineAsync(client, """{"method":"stop_capture"}""", TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    [Fact]
    public async Task DitherWaitsForEveryConnectedClientThenDithersOnce()
    {
        var first = await ConnectAsync();
        var second = await ConnectAsync();
        _server.Register(first);
        _server.Register(second);

        (await HandleAsync(first, Dither)).GetProperty("result").GetInt32().ShouldBe(0);
        await _guider.DidNotReceiveWithAnyArgs().DitherAsync(default, default, default, default, default, Arg.Any<CancellationToken>());

        (await HandleAsync(second, Dither)).GetProperty("result").GetInt32().ShouldBe(0);
        await _guider.Received(1).DitherAsync(5, 1.5, 10, 60, false, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ADitherPhd2RefusesIsReportedToTheClientThatCompletedTheRound()
    {
        _guider.DitherAsync(default, default, default, default, default, Arg.Any<CancellationToken>())
            .ReturnsForAnyArgs(ValueTask.FromException(new GuiderException("not guiding")));
        var client = await ConnectAsync();
        _server.Register(client);

        var reply = await HandleAsync(client, Dither);

        reply.GetProperty("error").GetProperty("message").GetString().ShouldBe("not guiding");
    }

    [Fact]
    public async Task OneClientStoppingDoesNotStopGuidingForTheOthers()
    {
        var first = await ConnectAsync();
        var second = await ConnectAsync();
        _server.Register(first);
        _server.Register(second);

        await HandleAsync(first, """{"method":"stop_capture","id":1}""");

        await _guider.DidNotReceiveWithAnyArgs().StopCaptureAsync(default, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GuidingStartsAtOnceForAnyClient()
    {
        var first = await ConnectAsync();
        var second = await ConnectAsync();
        _server.Register(first);
        _server.Register(second);

        var reply = await HandleAsync(first, """{"method":"guide","params":[{"pixels":1.5,"time":10,"timeout":60},false],"id":5}""");

        reply.GetProperty("result").GetInt32().ShouldBe(0);
        await _guider.Received(1).GuideAsync(1.5, 10, 60, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("""{"method":"dither","params":{"amount":5},"id":1}""")]
    [InlineData("""{"method":"dither","params":{"amount":-1,"settle":{"pixels":1.5,"time":10,"timeout":60}},"id":1}""")]
    [InlineData("""{"method":"guide","params":{},"id":1}""")]
    [InlineData("""{"method":"set_paused","params":["yes"],"id":1}""")]
    [InlineData("""{"method":"set_connected","id":1}""")]
    public async Task BadParamsAreAnInvalidParamsError(string line)
    {
        var client = await ConnectAsync();
        _server.Register(client);

        (await HandleAsync(client, line)).GetProperty("error").GetProperty("code").GetInt32().ShouldBe(-32602);
    }

    private async Task<JsonElement> HandleAsync(ClientConnection client, string line)
    {
        var reply = await _server.HandleLineAsync(client, line, TestContext.Current.CancellationToken);
        reply.ShouldNotBeNull();
        var text = Encoding.UTF8.GetString(reply);
        text.ShouldEndWith("\r\n");
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }

    /// <summary>A real loopback connection, so a <see cref="ClientConnection"/> has a socket.</summary>
    private async Task<ClientConnection> ConnectAsync()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var remote = new TcpClient();
        _disposables.Add(new AsyncDisposableAdapter(remote));
        var accept = listener.AcceptTcpClientAsync(TestContext.Current.CancellationToken);
        await remote.ConnectAsync((IPEndPoint)listener.LocalEndpoint, TestContext.Current.CancellationToken);
        var client = new ClientConnection(Interlocked.Increment(ref _nextId), await accept, TimeSpan.FromSeconds(5), NullLogger.Instance);
        _disposables.Add(client);
        return client;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var disposable in _disposables)
        {
            await disposable.DisposeAsync();
        }
    }

    private sealed class AsyncDisposableAdapter(IDisposable inner) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            inner.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
