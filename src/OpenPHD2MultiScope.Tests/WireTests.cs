using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Shouldly;
using Xunit;

namespace TianWen.OpenPHD2MultiScope.Tests;

public class LineReaderTests
{
    [Fact]
    public async Task LinesSplitOnLfAndCrLfAndAFinalUnterminatedLineCounts()
    {
        using var reader = new LineReader(new MemoryStream(Encoding.UTF8.GetBytes("one\r\ntwo\nthree")));
        var ct = TestContext.Current.CancellationToken;

        (await reader.ReadLineAsync(ct)).ShouldBe("one");
        (await reader.ReadLineAsync(ct)).ShouldBe("two");
        (await reader.ReadLineAsync(ct)).ShouldBe("three");
        (await reader.ReadLineAsync(ct)).ShouldBeNull();
    }

    [Fact]
    public async Task ALineSplitAcrossReadsIsReassembled()
    {
        var bytes = Encoding.UTF8.GetBytes("""{"method":"loop","id":1}""" + "\r\n");
        using var reader = new LineReader(new TrickleStream(bytes));

        (await reader.ReadLineAsync(TestContext.Current.CancellationToken)).ShouldBe("""{"method":"loop","id":1}""");
    }

    [Fact]
    public async Task ALineOverTheLimitIsRefused()
    {
        using var reader = new LineReader(new MemoryStream(new byte[100]), maxLineBytes: 64);

        await Should.ThrowAsync<InvalidDataException>(async () => await reader.ReadLineAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ALineExactlyAtTheLimitIsRead()
    {
        var line = new string('x', 64);
        using var reader = new LineReader(new MemoryStream(Encoding.UTF8.GetBytes(line + "\n")), maxLineBytes: 64);

        (await reader.ReadLineAsync(TestContext.Current.CancellationToken)).ShouldBe(line);
    }

    /// <summary>Hands out one byte per read, as a slow socket can.</summary>
    private sealed class TrickleStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => base.ReadAsync(buffer[..Math.Min(1, buffer.Length)], cancellationToken);
    }
}

public class PhdEventTests
{
    [Fact]
    public void EventsUsePhd2sPropertyNamesWithEventFirst()
    {
        var json = Encoding.UTF8.GetString(ProxyServer.Serialize(new GuideStepEvent(1, 0.25, -0.5)));

        json.ShouldStartWith("""{"Event":"GuideStep",""");
        json.ShouldEndWith("\r\n");
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("RADistanceRaw").GetDouble().ShouldBe(0.25);
        doc.RootElement.GetProperty("DECDistanceRaw").GetDouble().ShouldBe(-0.5);
        doc.RootElement.TryGetProperty("Timestamp", out _).ShouldBeTrue();
        doc.RootElement.TryGetProperty("Inst", out _).ShouldBeTrue();
    }

    [Fact]
    public void AGuideStepIsInPixelsWithCompassPulses()
    {
        // TianWen reports arcseconds (pixels x scale); PHD2's clients expect the pixels back.
        var step = new ProxyServer.GuiderNotification.GuideStep(RaErrorArcsec: 3.0, DecErrorArcsec: -1.5, RaPulseMs: -120.4, DecPulseMs: 0);

        var e = ProxyServer.ToGuideStep(step, arcsecPerPixel: 6.0, frame: 7);

        e.Frame.ShouldBe(7);
        e.RADistanceRaw.ShouldBe(0.5);
        e.DECDistanceRaw.ShouldBe(-0.25);
        e.RADuration.ShouldBe(120);
        e.RADirection.ShouldBe("East");
        e.DECDuration.ShouldBeNull();
        e.DECDirection.ShouldBeNull();
        using var doc = JsonDocument.Parse(ProxyServer.Serialize(e));
        doc.RootElement.TryGetProperty("DECDirection", out _).ShouldBeFalse();
    }

    [Fact]
    public void TheVersionEventCarriesPhd2sFieldNames()
    {
        using var doc = JsonDocument.Parse(ProxyServer.Serialize(new VersionEvent("2.6.13", "dev4")));

        doc.RootElement.GetProperty("Event").GetString().ShouldBe("Version");
        doc.RootElement.GetProperty("PHDVersion").GetString().ShouldBe("2.6.13");
        doc.RootElement.GetProperty("PHDSubver").GetString().ShouldBe("dev4");
        doc.RootElement.GetProperty("MsgVersion").GetInt32().ShouldBe(1);
    }

    [Fact]
    public void ASimpleEventIsNamedByItsNameAndDoesNotLeakIt()
    {
        using var doc = JsonDocument.Parse(ProxyServer.Serialize(new SimpleEvent("ConfigurationChange")));

        // The old proxy sent "ConfigurationChangeEvent", which no client knows.
        doc.RootElement.GetProperty("Event").GetString().ShouldBe("ConfigurationChange");
        doc.RootElement.TryGetProperty("Name", out _).ShouldBeFalse();
    }

    [Fact]
    public void ASuccessfulSettleOmitsTheError()
    {
        using var doc = JsonDocument.Parse(ProxyServer.Serialize(new SettleDoneEvent(0, null)));

        doc.RootElement.GetProperty("Status").GetInt32().ShouldBe(0);
        doc.RootElement.TryGetProperty("Error", out _).ShouldBeFalse();
    }
}

public class ProxyOptionsTests
{
    private static IConfiguration Args(params string[] args) => new ConfigurationBuilder().AddCommandLine(args).Build();

    [Fact]
    public void TheDefaultsListenOnLoopbackAndWaitForEveryClient()
    {
        ProxyOptions.TryParse(Args(), out var options, out var error).ShouldBeTrue(error);

        options.Phd2Host.ShouldBe("localhost");
        options.Phd2Instance.ShouldBe(1u);
        options.ListenAddress.ShouldBe(IPAddress.Loopback);
        options.ListenPort.ShouldBe(ProxyOptions.DefaultListenPort);
        options.SharedCommandTimeout.ShouldBeNull();
    }

    [Fact]
    public void EveryOptionIsRead()
    {
        ProxyOptions.TryParse(Args("--phd2-host", "rig.local", "--phd2-instance", "2", "--listen", "0.0.0.0", "--port", "4500", "--shared-timeout", "90"),
            out var options, out var error).ShouldBeTrue(error);

        options.Phd2Host.ShouldBe("rig.local");
        options.Phd2Instance.ShouldBe(2u);
        options.ListenAddress.ShouldBe(IPAddress.Any);
        options.ListenPort.ShouldBe(4500);
        options.SharedCommandTimeout.ShouldBe(TimeSpan.FromSeconds(90));
    }

    [Theory]
    [InlineData("--port", "0")]
    [InlineData("--port", "70000")]
    [InlineData("--port", "abc")]
    [InlineData("--phd2-instance", "0")]
    [InlineData("--listen", "not-an-ip")]
    [InlineData("--shared-timeout", "-1")]
    [InlineData("--shared-timeout", "NaN")]
    public void ABadValueIsRefusedNamingTheOption(string option, string value)
    {
        ProxyOptions.TryParse(Args(option, value), out _, out var error).ShouldBeFalse();
        error.ShouldNotBeNull();
        error.ShouldContain(option);
    }

    [Fact]
    public void ListeningOnPhd2sOwnPortIsRefused()
    {
        ProxyOptions.TryParse(Args("--phd2-instance", "2", "--port", "4401"), out _, out var error).ShouldBeFalse();
        error.ShouldNotBeNull();
        error.ShouldContain("PHD2's own port");
    }
}
