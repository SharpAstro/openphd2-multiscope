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
var sharedState = new SharedState();
var serializerOptions = new JsonSerializerOptions { WriteIndented = false };
var phdEventJsonContent = new PHDEventJsonContext(serializerOptions);

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
    guider.Driver.DeviceConnectedEvent += GuiderDriver_DeviceConnectedEvent;
    guider.Driver.GuiderStateChangedEvent += GuiderDriver_GuiderStateChangedEvent;
    guider.Driver.GuidingErrorEvent += GuiderDriver_GuidingErrorEvent;

    await guider.Driver.ConnectAsync(cts.Token);
}
else
{
    throw new InvalidOperationException("Could not connect to guider");
}

void GuiderDriver_DeviceConnectedEvent(object? sender, DeviceConnectedEventArgs e)
{
    external.AppLogger.LogInformation("Guider {DeviceName} {YesOrNo}", (sender as IGuider)?.Name, e.Connected ? "connected" : "disconnected");
}

void GuiderDriver_GuidingErrorEvent(object? sender, GuidingErrorEventArgs e)
{
    external.AppLogger.LogError(e.Exception, "Guider {DeviceName} encountered error {ErrorMessage}", e.Device.DisplayName, e.Message);
}

async void GuiderDriver_GuiderStateChangedEvent(object? sender, GuiderStateChangedEventArgs e)
{
    if (e.AppState is { Length: > 0 } state && state is not SharedState.UnknownState && sharedState.SetState(state) is { } orig && orig != state)
    {
        external.AppLogger.LogInformation("Guider in state = {NewState} (was {OrigState}) on event = {EventName}", state, orig ?? "Unknown", e.Event);
    }
    else
    {
        external.AppLogger.LogInformation("Event = {EventName}", e.Event);
    }

    if (startListenerSem.CurrentCount == 0)
    {
        startListenerSem.Release();
    }
    else
    {
        switch (e.Event)
        {
            case "ConfigurationChange":
                await BroadcastEventAsync(new ConfigurationChangeEvent(), cts.Token);
                break;

            case "Calibrating":
                await BroadcastEventAsync(new CalibratingEvent(), cts.Token);
                break;

            case "CalibrationComplete":
                await BroadcastEventAsync(new CalibrationCompleteEvent(), cts.Token);
                break;

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

            case "LockPositionSet":
                await BroadcastEventAsync(new LockPositionSetEvent(), cts.Token);
                break;

            case "LockPositionLost":
                await BroadcastEventAsync(new LockPositionLostEvent(), cts.Token);
                break;

            case "AppState":
                await BroadcastEventAsync(new AppStateEvent(e.AppState ?? "Unknown"), cts.Token);
                break;

            case "Paused":
                await BroadcastEventAsync(new PausedEvent(), cts.Token);
                break;

            case "StarLost":
                await BroadcastEventAsync(new StarLostEvent(), cts.Token);
                break;

            case "LoopingExposures":
                await BroadcastEventAsync(new LoopingExposuresEvent(), cts.Token);
                break;

            case "StarSelected":
                await BroadcastEventAsync(new StarSelectedEvent(), cts.Token);
                break;

            case "StartGuiding":
                await BroadcastEventAsync(new StartGuidingEvent(), cts.Token);
                break;

            case "GuidingStopped":
                await BroadcastEventAsync(new GuidingStoppedEvent(), cts.Token);
                break;

            case "LoopingExposuresStopped":
                await BroadcastEventAsync(new LoopingExposuresStoppedEvent(), cts.Token);
                break;

            default:
                external.AppLogger.LogWarning("Unhandled event = {EventName}", e.Event);
                break;
        }
    }
}

using var listener = new TcpListener(IPAddress.Loopback, 4410);

await startListenerSem.WaitAsync(cts.Token);
listener.Start();

while (!cts.IsCancellationRequested && guider.Driver.Connected)
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

    var appState = sharedState.AppState;
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
        while (guider.Driver.Connected && !cts.IsCancellationRequested && (line = await reader.ReadLineAsync(cts.Token)) != null)
        {
            var ditherCmd = JsonSerializer.Deserialize(line, PHDJsonRPCJsonContext.Default.DitherRPC);
            if (ditherCmd?.Params is { } @params)
            {
                external.AppLogger.LogInformation("Recieved dithering command {@Params} from {RemoteEndpoint}", @params, endPoint);

                var count = clients.Count;
                var prev = sharedState.IncrementDitherReceived();
                if (prev == count)
                {
                    if (sharedState.AllDitherReceived(count))
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
            else
            {
                external.AppLogger.LogWarning("Received unknown command {Line} from {RemoteEndpoint}", line, endPoint);
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
    var failedClients = new ConcurrentBag<EndPoint>();
    await Parallel.ForEachAsync(clients, cancellationToken, async (kv, cancellationToken) =>
    {
        try
        {
            await SendEventAsync(kv.Value.Stream, @event, kv.Key, cancellationToken);
        }
        catch (Exception ex)
        {
            external.AppLogger.LogError(ex, "Error while boradcasting event {EventTYpe} to {ClientEndpoint}", typeof(TEvent), kv.Key);
            failedClients.Add(kv.Key);
        }
    });

    foreach (var failedClient in failedClients)
    {
        _ = clients.TryRemove(failedClient, out _);
    }
}

async Task SendEventAsync<TEvent>(Stream stream, TEvent @event, EndPoint endPoint, CancellationToken cancellationToken) where TEvent : PHDEvent
{
    await JsonSerializer.SerializeAsync(stream, @event, typeof(TEvent), phdEventJsonContent, cancellationToken);
    await stream.WriteAsync(CRLF, cancellationToken);
}

class SharedState
{
    internal const string UnknownState = "Unknown";

    private string _appState = UnknownState;
    private int _ditherReceived = 0;

    public int IncrementDitherReceived() => Interlocked.Increment(ref _ditherReceived);

    public bool AllDitherReceived(int count) => Interlocked.CompareExchange(ref _ditherReceived, 0, count) == count;

    internal string AppState => Interlocked.CompareExchange(ref _appState, UnknownState, UnknownState);

    internal string SetState(string state) => Interlocked.Exchange(ref _appState, state);
}

[JsonSerializable(typeof(PHDEvent))]
[JsonSerializable(typeof(ConfigurationChangeEvent))]
[JsonSerializable(typeof(CalibratingEvent))]
[JsonSerializable(typeof(CalibrationCompleteEvent))]
[JsonSerializable(typeof(GuideStepEvent))]
[JsonSerializable(typeof(GuidingDitheredEvent))]
[JsonSerializable(typeof(SettleBeginEvent))]
[JsonSerializable(typeof(SettleDoneEvent))]
[JsonSerializable(typeof(SettlingEvent))]
[JsonSerializable(typeof(LockPositionSetEvent))]
[JsonSerializable(typeof(LockPositionLostEvent))]
[JsonSerializable(typeof(AppStateEvent))]
[JsonSerializable(typeof(PausedEvent))]
[JsonSerializable(typeof(StarLostEvent))]
[JsonSerializable(typeof(LoopingExposuresEvent))]
[JsonSerializable(typeof(StarSelectedEvent))]
[JsonSerializable(typeof(StartGuidingEvent))]
[JsonSerializable(typeof(GuidingStoppedEvent))]
[JsonSerializable(typeof(LoopingExposuresStoppedEvent))]
[JsonSerializable(typeof(VersionEvent))]
internal partial class PHDEventJsonContext : JsonSerializerContext
{
}

internal record PHDEvent(string Event, string? Host = "localhost", int MsgVersion = 1, int? Inst = 1);

internal record VersionEvent(string PHPVersion, string PHPSubVer, bool OverlapSupport = true) : PHDEvent("Version");
internal record AppStateEvent(string State) : PHDEvent("AppState");
internal record LoopingExposuresEvent() : PHDEvent("LoopingExposures");
internal record LoopingExposuresStoppedEvent() : PHDEvent ("LoopingExposuresStopped");
internal record PausedEvent() : PHDEvent("Paused");
internal record StarLostEvent() : PHDEvent("StarLost");
internal record StartCalibrationEvent() : PHDEvent("StartCalibration");
internal record ConfigurationChangeEvent() :PHDEvent("ConfigurationChangeEvent");
internal record CalibratingEvent() : PHDEvent("Calibrating");
internal record CalibrationCompleteEvent() : PHDEvent("CalibrationComplete");
internal record StarSelectedEvent() : PHDEvent("StarSelected");
internal record StartGuidingEvent() : PHDEvent("StartGuiding");
internal record LockPositionSetEvent() : PHDEvent("LockPositionSet");
internal record LockPositionLostEvent() : PHDEvent("LockPositionLost");
internal record GuidingStoppedEvent() : PHDEvent("GuidingStopped");
internal record GuideStepEvent(double RADistanceRaw, double DECDistanceRaw) : PHDEvent("GuideStep");
internal record SettleDoneEvent(int Status, string Error, int TotalFrames, int DroppedFrames) : PHDEvent("SettleDone");
internal record SettleBeginEvent() : PHDEvent("SettleBegin");
internal record GuidingDitheredEvent(/* double dx, double dy */) : PHDEvent("GuidingDithered");
internal record SettlingEvent(double Distance, double Time, double SettleTime, bool StarLocked) : PHDEvent("Settling");

[JsonSerializable(typeof(DitherRPC))]
[JsonSerializable(typeof(DitherParams))]
[JsonSerializable(typeof(SettleArg))]
internal partial class PHDJsonRPCJsonContext : JsonSerializerContext
{
}

// {"method":"dither","params":{"amount":20.0,"settle":{"pixels":2.0,"time":5.0,"timeout":80.0},"raOnly":true},"id":1}
internal record DitherRPC([property: JsonPropertyName("id")] int Id, [property:JsonPropertyName("method")] string Method, [property: JsonPropertyName("params")] DitherParams Params);

internal record DitherParams([property: JsonPropertyName("amount")] double Amount, [property: JsonPropertyName("settle")] SettleArg Settle, [property: JsonPropertyName("raOnly")] bool RaOnly);
internal record SettleArg([property: JsonPropertyName("pixels")] double Pixels, [property: JsonPropertyName("time")] double Time, [property: JsonPropertyName("timeout")] double Timeout);