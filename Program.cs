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

var cts = new CancellationTokenSource();
var startListenerSem = new SemaphoreSlim(0);
string? appState = null;
var serializerOptions = new JsonSerializerOptions { WriteIndented = false };
var clients = new ConcurrentDictionary<EndPoint, NetworkStream>();
var ditherReceived = 0;

using var host = builder.Build();

var deviceManager = host.Services.GetRequiredService<IDeviceManager<DeviceBase>>();

var guider = new Guider(new GuiderDevice(DeviceType.PHD2, "localhost/1", ""), host.Services.GetRequiredService<IExternal>());

if (guider.Driver.CanAsyncConnect)
{
    await guider.Driver.ConnectAsync();
}
else
{
    guider.Driver.Connect();
}
guider.Driver.GuiderStateChangedEvent += GuiderDriver_GuiderStateChangedEvent;

void GuiderDriver_GuiderStateChangedEvent(object? sender, GuiderStateChangedEventArgs e)
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
                if (guider.Driver.GetStats() is { LastRaErr: { } ra, LastDecErr: { } dec } stats)
                {
                    BroadcastEvent(new GuideStepEvent(ra, dec));
                }
                break;

            case "GuidingDithered":
                BroadcastEvent(new GuidingDitheredEvent());
                break;

            case "SettleBegin":
                BroadcastEvent(new SettleBeginEvent());
                break;

            case "Settling":
                if (guider.Driver.TryGetSettleProgress(out var settleProgress))
                {
                    BroadcastEvent(new SettlingEvent(settleProgress.Distance, settleProgress.Time, settleProgress.SettleTime, settleProgress.StarLocked));
                }
                break;

            case "SettleDone":
                if (guider.Driver.TryGetSettleProgress(out settleProgress))
                {
                    BroadcastEvent(new SettleDoneEvent(settleProgress.Status, settleProgress.Error ?? "", 0, 0));
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

    Console.WriteLine("Incoming connection from: {0}", client.Client.RemoteEndPoint);

    new Thread(Worker).Start(client);
}

void Worker(object? obj)
{
    if (obj is not TcpClient client)
    {
        Console.Error.WriteLine("State is not a TCP client");
        return;
    }

    var endPoint = client.Client.RemoteEndPoint;
    if (endPoint is null)
    {
        Console.Error.WriteLine("Invalid client");
        return;
    }

    using var stream = client.GetStream();
    using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);

    var version = guider.Driver.DriverInfo?.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1) ?? [];

    SendEvent(stream, new VersionEvent(version.FirstOrDefault() ?? "Unknown", version.LastOrDefault() ?? "Unknown"), endPoint);

    switch (appState)
    {
        case "Stopped":     // PHD is idle
            SendEvent(stream, new LoopingExposuresStoppedEvent(), endPoint);
            break;
        case "Selected":    // A star is selected but PHD is neither looping exposures, calibrating, or guiding
            SendEvent(stream, new StarSelectedEvent(), endPoint);
            break;
        case "Calibrating": // PHD is calibrating
            SendEvent(stream, new StartCalibrationEvent(), endPoint);
            break;
        case "Guiding":     // PHD is guiding
            SendEvent(stream, new StartGuidingEvent(), endPoint);
            break;
        case "LostLock":    // PHD is guiding, but the frame was dropped
            SendEvent(stream, new StarLostEvent(), endPoint);
            break;
        case "Paused":      // PHD is paused
            SendEvent(stream, new PausedEvent(), endPoint);
            break;
        case "Looping":     // PHD is looping exposures
            SendEvent(stream, new LoopingExposuresEvent(), endPoint);
            break;
    }

    SendEvent(stream, new AppStateEvent(appState ?? "Unknown"), endPoint);

    clients[endPoint] = stream;

    try
    {
        string? line;
        while (!cts.IsCancellationRequested && (line = reader.ReadLine()) != null)
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
                        Console.WriteLine("All {0} connected clients issued a dither command, actually dither now", count);
                        guider.Driver.Dither(@params.Amount, @params.Settle.Pixels, @params.Settle.Time, @params.Settle.Timeout, @params.RaOnly);
                    }
                    else
                    {
                        Console.WriteLine("Dithering already triggered in another thread");
                    }
                }
                else
                {
                    Console.WriteLine("Only {0} of {1} connected clients issued a dither command, ignoring", prev, count);
                }
            }
        }
    }
    finally
    {
        Console.WriteLine("Client {0} disconnected", endPoint);
        clients.TryRemove(endPoint, out _);
    }
}

void BroadcastEvent<TEvent>(TEvent @event) where TEvent : PHPEvent
{
    foreach (var client in clients)
    {
        SendEvent(client.Value, @event, client.Key);
    }
}

void SendEvent<TEvent>(Stream stream, TEvent @event, EndPoint endPoint) where TEvent : PHPEvent
{
    var json = JsonSerializer.Serialize(@event, serializerOptions);
    var jsonl = json + "\r\n";
    Console.WriteLine(">> {0} to {1}", json, endPoint);
    stream.Write(Encoding.UTF8.GetBytes(jsonl));
}

record PHPEvent(string Event, string? Host = "localhost", int MsgVersion = 1, int? Inst = 1);

record VersionEvent(string PHPVersion, string PHPSubVer, bool OverlapSupport = true) : PHPEvent("Version");
record AppStateEvent(string State) : PHPEvent("AppState");
record LoopingExposuresEvent() : PHPEvent("LoopingExposures");
record LoopingExposuresStoppedEvent() : PHPEvent("LoopingExposuresStopped");
record PausedEvent() : PHPEvent("Paused");
record StarLostEvent() : PHPEvent("StarLost");
record StartCalibrationEvent() : PHPEvent("StartCalibration");
record StarSelectedEvent() : PHPEvent("StarSelected");
record StartGuidingEvent() : PHPEvent("StartGuiding");
record LockPositionSetEvent() : PHPEvent("LockPositionSet");
record GuideStepEvent(double RADistanceRaw, double DECDistanceRaw) : PHPEvent("GuideStep");
record SettleDoneEvent(int Status, string Error, int TotalFrames, int DroppedFrames) : PHPEvent("SettleDone");
record SettleBeginEvent() : PHPEvent("SettleBegin");
record GuidingDitheredEvent(/* double dx, double dy */) : PHPEvent("GuidingDithered");
record SettlingEvent(double Distance, double Time, double SettleTime, bool StarLocked) : PHPEvent("Settling");


// {"method":"dither","params":{"amount":20.0,"settle":{"pixels":2.0,"time":5.0,"timeout":80.0},"raOnly":true},"id":1}
record DitherRPC([property: JsonPropertyName("id")] int Id, [property:JsonPropertyName("method")] string Method, [property: JsonPropertyName("params")] DitherParams Params);

record DitherParams([property: JsonPropertyName("amount")] double Amount, [property: JsonPropertyName("settle")] SettleArg Settle, [property: JsonPropertyName("raOnly")] bool RaOnly);
record SettleArg([property: JsonPropertyName("pixels")] double Pixels, [property: JsonPropertyName("time")] double Time, [property: JsonPropertyName("timeout")] double Timeout);