using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Guider;
using TianWen.Lib.Extensions;
using TianWen.Lib.Sequencing;

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = args, DisableDefaults = true });
builder.Services
    .AddLogging(builder =>
    {
        builder.AddSimpleConsole(options =>
        {
            options.SingleLine = true;
            options.IncludeScopes = false;
        });
    })
    .AddExternal()
    .AddPHD2()
    .AddDeviceManager();

var startListenerSem = new SemaphoreSlim(0);
string? appState = null;
var serializerOptions = new JsonSerializerOptions { WriteIndented = false };
var ditherReceived = 0;

using var host = builder.Build();

await host.StartAsync();

var services = host.Services;
var external = services.GetRequiredService<IExternal>();
var deviceManager = services.GetRequiredService<ICombinedDeviceManager>();
var lifetime = services.GetRequiredService<IHostApplicationLifetime>();
using var cts = CancellationTokenSource.CreateLinkedTokenSource(lifetime.ApplicationStopping);
var clients = new ConcurrentDictionary<EndPoint, (Task WorkerTask, NetworkStream Stream, TcpClient Client)>();
var CRLF = "\r\n"u8.ToArray();

await deviceManager.DiscoverAsync(cts.Token);

var device = deviceManager.RegisteredDevices(DeviceType.DedicatedGuiderSoftware).FirstOrDefault();
Guider guider;
if (device is GuiderDevice guiderDevice)
{
    guider = new Guider(guiderDevice, external);
    await guider.Driver.ConnectAsync(cts.Token);
    guider.Driver.GuiderStateChangedEvent += GuiderDriver_GuiderStateChangedEvent;
}
else
{
    throw new InvalidOperationException("Could not connect to guider");
}

async void GuiderDriver_GuiderStateChangedEvent(object? sender, GuiderStateChangedEventArgs e)
{
    if (e.AppState is { } state)
    {
        var orig = Interlocked.Exchange(ref appState, state);
        if (orig != state)
        {
            Console.WriteLine("Guider in state = {0} on event = {1}", state, e.Event);
        }
    }
    else
    {
        Console.WriteLine("Event = {0}", e.Event);
    }

    if (startListenerSem.CurrentCount == 0)
    {
        startListenerSem.Release();
    }
    else
    {
        switch (e.Event)
        {
            case "GuideStep":
                if (await guider.Driver.GetStatsAsync(cts.Token) is { LastRaErr: { } ra, LastDecErr: { } dec } stats)
                {
                    await BroadcastEventAsync(new GuideStepEvent(ra, dec), cts.Token);
                }
                break;

            case "GuidingDithered":
                await BroadcastEventAsync(new GuidingDitheredEvent(), cts.Token);
                break;

            case "SettleBegin":
                await BroadcastEventAsync(new SettleBeginEvent(), cts.Token);
                break;

            case "Settling":
            case "SettleDone":
                if (await guider.Driver.GetSettleProgressAsync(cts.Token) is { } settleProgress)
                {
                    if (e.Event is "SettleDone")
                    {
                        await BroadcastEventAsync(new SettleDoneEvent(settleProgress.Status, settleProgress.Error ?? "", 0, 0), cts.Token);
                    }
                    else if (e.Event is "Settling")
                    {
                        await BroadcastEventAsync(new SettlingEvent(settleProgress.Distance, settleProgress.Time, settleProgress.SettleTime, settleProgress.StarLocked), cts.Token);
                    }
                }
                break;
        }
    }
}

using var listener = new TcpListener(IPAddress.Loopback, 4410);

await startListenerSem.WaitAsync(cts.Token);
listener.Start();

while (!cts.IsCancellationRequested)
{
    var client = await listener.AcceptTcpClientAsync(cts.Token);

    if (client.Client.RemoteEndPoint is { } remoteEndpoint)
    {
        external.AppLogger.LogInformation("Incoming connection from: {RemoteEndpoint}", remoteEndpoint);

        var stream = client.GetStream();
        clients[remoteEndpoint] = (ClientWorkerAsync(stream, remoteEndpoint), stream, client);
    }
}

await host.WaitForShutdownAsync();

async Task ClientWorkerAsync(NetworkStream stream, EndPoint endPoint)
{
    using var reader = new StreamReader(stream, Encoding.UTF8);

    var version = guider.Driver.DriverInfo?.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1) ?? [];

    await SendEventAsync(stream, new VersionEvent(version.FirstOrDefault() ?? "Unknown", version.LastOrDefault() ?? "Unknown"), endPoint, cts.Token);

    await (appState switch
    {
        "Stopped" => SendEventAsync(stream, new LoopingExposuresStoppedEvent(), endPoint, cts.Token), // PHD is idle
        "Selected" => SendEventAsync(stream, new StarSelectedEvent(), endPoint, cts.Token), // A star is selected but PHD is neither looping exposures, calibrating, or guiding
        "Calibrating" => SendEventAsync(stream, new StartCalibrationEvent(), endPoint, cts.Token),  // PHD is calibrating
        "Guiding" => SendEventAsync(stream, new StartGuidingEvent(), endPoint, cts.Token), // PHD is guiding
        "LostLock" => SendEventAsync(stream, new StarLostEvent(), endPoint, cts.Token), // PHD is guiding, but the frame was dropped
        "Paused" => SendEventAsync(stream, new PausedEvent(), endPoint, cts.Token), // PHD is paused
        "Looping" => SendEventAsync(stream, new LoopingExposuresEvent(), endPoint, cts.Token), // PHD is looping exposures
        _ => throw new InvalidOperationException($"Unknown PHD2 state {appState}")
    });

    await SendEventAsync(stream, new AppStateEvent(appState ?? "Unknown"), endPoint, cts.Token);

    try
    {
        string? line;
        while (!cts.IsCancellationRequested && (line = await reader.ReadLineAsync()) != null)
        {
            Console.WriteLine("<< {0} from {1}", line, endPoint);
            var ditherCmd = JsonSerializer.Deserialize<DitherRPC>(line, serializerOptions);
            if (ditherCmd?.Params is { } @params)
            {
                var count = clients.Count;
                var prev = Interlocked.Increment(ref ditherReceived);
                if (prev == count)
                {
                    if (Interlocked.CompareExchange(ref ditherReceived, 0, count) == count)
                    {
                        external.AppLogger.LogInformation("All {ClientCOunt} connected clients issued a dither command, actually dither now", count);
                        await guider.Driver.DitherAsync(@params.Amount, @params.Settle.Pixels, @params.Settle.Time, @params.Settle.Timeout, @params.RaOnly, cts.Token);
                    }
                    else
                    {
                        external.AppLogger.LogWarning("Dithering already triggered in another thread");
                    }
                }
                else
                {
                    external.AppLogger.LogInformation("Only {DitherIssued} of {ClientCount} connected clients issued a dither command, ignoring", prev, count);
                }
            }
        }
    }
    finally
    {
        external.AppLogger.LogInformation("Client {ClientEndpoint} disconnected", endPoint);
        if (clients.TryRemove(endPoint, out var clientInfo) && clientInfo is { Client: var client })
        {
            client.Close();
        }
    }
}

async Task BroadcastEventAsync<TEvent>(TEvent @event, CancellationToken cancellationToken) where TEvent : PHDEvent
{
    await Parallel.ForEachAsync(clients, cancellationToken, async (kv, cancellationToken) =>
    {
        await SendEventAsync(kv.Value.Stream, @event, kv.Key, cancellationToken);
    });
}

async Task SendEventAsync<TEvent>(Stream stream, TEvent @event, EndPoint endPoint, CancellationToken cancellationToken) where TEvent : PHDEvent
{
    await JsonSerializer.SerializeAsync(stream, @event, serializerOptions, cancellationToken);
    await stream.WriteAsync(CRLF, cancellationToken);
}

record PHDEvent(string Event, string? Host = "localhost", int MsgVersion = 1, int? Inst = 1);

record VersionEvent(string PHPVersion, string PHPSubVer, bool OverlapSupport = true) : PHDEvent("Version");
record AppStateEvent(string State) : PHDEvent("AppState");
record LoopingExposuresEvent() : PHDEvent("LoopingExposures");
record LoopingExposuresStoppedEvent() : PHDEvent ("LoopingExposuresStopped");
record PausedEvent() : PHDEvent("Paused");
record StarLostEvent() : PHDEvent("StarLost");
record StartCalibrationEvent() : PHDEvent("StartCalibration");
record StarSelectedEvent() : PHDEvent("StarSelected");
record StartGuidingEvent() : PHDEvent("StartGuiding");
record LockPositionSetEvent() : PHDEvent("LockPositionSet");
record GuideStepEvent(double RADistanceRaw, double DECDistanceRaw) : PHDEvent("GuideStep");
record SettleDoneEvent(int Status, string Error, int TotalFrames, int DroppedFrames) : PHDEvent("SettleDone");
record SettleBeginEvent() : PHDEvent("SettleBegin");
record GuidingDitheredEvent(/* double dx, double dy */) : PHDEvent("GuidingDithered");
record SettlingEvent(double Distance, double Time, double SettleTime, bool StarLocked) : PHDEvent("Settling");


// {"method":"dither","params":{"amount":20.0,"settle":{"pixels":2.0,"time":5.0,"timeout":80.0},"raOnly":true},"id":1}
record DitherRPC([property: JsonPropertyName("id")] int Id, [property:JsonPropertyName("method")] string Method, [property: JsonPropertyName("params")] DitherParams Params);

record DitherParams([property: JsonPropertyName("amount")] double Amount, [property: JsonPropertyName("settle")] SettleArg Settle, [property: JsonPropertyName("raOnly")] bool RaOnly);
record SettleArg([property: JsonPropertyName("pixels")] double Pixels, [property: JsonPropertyName("time")] double Time, [property: JsonPropertyName("timeout")] double Timeout);