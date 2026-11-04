using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using TianWen.Lib.Devices.Guider;

namespace TianWen.OpenPHD2MultiScope;

/// <summary>
/// Speaks PHD2's event-monitoring protocol to every connected imaging program and drives the one
/// real PHD2 through TianWen's <see cref="IGuider"/>.
/// <list type="bullet">
/// <item>Every request gets a reply (a result or a JSON-RPC error), so no client waits on a timeout.</item>
/// <item>Commands that would disturb the other programs' exposures (dither, stop, pause, loop,
/// disconnect) go through a <see cref="SharedCommandBarrier{TArgs}"/>: they act once every
/// connected program asked. Commands that do not (guide, unpause, every query) act at once.</item>
/// <item>Guider events go through one queue, so every client sees them in PHD2's order.</item>
/// <item>If PHD2 goes away, every client is closed and the proxy reconnects.</item>
/// </list>
/// </summary>
internal sealed class ProxyServer
{
    private static readonly TimeSpan StopCaptureTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan LoopTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan GuiderPollInterval = TimeSpan.FromSeconds(1);

    private readonly IGuider _guider;
    private readonly ProxyOptions _options;
    private readonly ILogger _logger;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<int, ClientConnection> _clients = new();
    private readonly Channel<GuiderNotification> _notifications = Channel.CreateUnbounded<GuiderNotification>(new UnboundedChannelOptions { SingleReader = true });
    private readonly SharedCommand<DitherArgs> _dither;
    private readonly SharedCommand<bool> _stopCapture;
    private readonly SharedCommand<bool> _loop;
    private readonly SharedCommand<bool> _pause;
    private readonly SharedCommand<bool> _disconnectEquipment;
    private readonly ISharedCommand[] _sharedCommands;
    private int _nextClientId;
    private string _appState = "Stopped";
    private double _pixelScale = double.NaN;
    private int _guideFrame;
    private int _ready;
    private CancellationToken _stopping;

    public ProxyServer(IGuider guider, ProxyOptions options, ILogger logger, TimeProvider timeProvider)
    {
        _guider = guider;
        _options = options;
        _logger = logger;
        _timeProvider = timeProvider;

        _dither = new("dither", options.SharedCommandTimeout,
            (args, ct) => _guider.DitherAsync(args.Amount, args.Settle.Pixels, args.Settle.Time, args.Settle.Timeout, args.RaOnly, ct));
        _stopCapture = new("stop_capture", options.SharedCommandTimeout, (_, ct) => _guider.StopCaptureAsync(StopCaptureTimeout, ct));
        _loop = new("loop", options.SharedCommandTimeout, async (_, ct) =>
        {
            if (!await _guider.LoopAsync(LoopTimeout, ct).ConfigureAwait(false))
            {
                throw new GuiderException($"PHD2 did not start looping within {LoopTimeout.TotalSeconds:0} s");
            }
        });
        _pause = new("set_paused", options.SharedCommandTimeout, (_, ct) => _guider.PauseAsync(ct));
        _disconnectEquipment = new("set_connected", options.SharedCommandTimeout, (_, ct) => _guider.DisconnectEquipmentAsync(ct));
        _sharedCommands = [_dither, _stopCapture, _loop, _pause, _disconnectEquipment];

        _guider.GuiderStateChangedEvent += (_, e) => _notifications.Writer.TryWrite(new GuiderNotification.StateChanged(e.Event, e.AppState));
        _guider.GuideCorrectionEvent += (_, e) =>
        {
            if (e.RaError is { } ra && e.DecError is { } dec)
            {
                _notifications.Writer.TryWrite(new GuiderNotification.GuideStep(ra, dec, e.RaCorrectionMs, e.DecCorrectionMs));
            }
        };
        _guider.GuidingErrorEvent += (_, e) => _logger.LogError(e.Exception, "PHD2 reported an error: {ErrorMessage}", e.Message);
    }

    internal IReadOnlyCollection<int> ConnectedClientIds => [.. _clients.Keys];

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        _stopping = cancellationToken;

        using var listener = new TcpListener(_options.ListenAddress, _options.ListenPort);
        listener.Start();
        _logger.LogInformation("Listening on {ListenAddress}:{ListenPort} for PHD2 clients, relaying to PHD2 instance {Phd2Instance} on {Phd2Host}",
            _options.ListenAddress, _options.ListenPort, _options.Phd2Instance, _options.Phd2Host);

        var pump = PumpNotificationsAsync(cancellationToken);
        var accept = AcceptClientsAsync(listener, cancellationToken);
        var sharedTimeouts = ReevaluateSharedCommandsAsync(cancellationToken);
        try
        {
            await SuperviseGuiderAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            listener.Stop();
            CloseAllClients("the proxy is stopping");
            _notifications.Writer.TryComplete();
            await IgnoreCancellation(pump).ConfigureAwait(false);
            await IgnoreCancellation(accept).ConfigureAwait(false);
            await IgnoreCancellation(sharedTimeouts).ConfigureAwait(false);
            if (_guider.Connected)
            {
                await _guider.DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Keeps the link to PHD2 up: connect, watch, and on loss drop every client and reconnect.</summary>
    private async Task SuperviseGuiderAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await _guider.ConnectAsync(cancellationToken).ConfigureAwait(false);
                Volatile.Write(ref _pixelScale, double.NaN);
                await WaitForGreetingAsync(cancellationToken).ConfigureAwait(false);
                Volatile.Write(ref _ready, 1);
                _logger.LogInformation("Connected to {GuiderInfo}, state {AppState}", _guider.DriverInfo, Volatile.Read(ref _appState));
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning("Cannot reach PHD2 instance {Phd2Instance} on {Phd2Host} ({Reason}); retrying in {ReconnectDelay}",
                    _options.Phd2Instance, _options.Phd2Host, ex.Message, _options.ReconnectDelay);
                await Task.Delay(_options.ReconnectDelay, _timeProvider, cancellationToken).ConfigureAwait(false);
                continue;
            }

            while (_guider.Connected && !cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(GuiderPollInterval, _timeProvider, cancellationToken).ConfigureAwait(false);
            }

            Volatile.Write(ref _ready, 0);
            if (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning("Lost the connection to PHD2; closing every client and reconnecting");
                CloseAllClients("PHD2 went away");
            }
        }
    }

    /// <summary>
    /// PHD2 greets a new connection with its Version and AppState events, which the driver reads
    /// on its own thread: until they arrive it knows neither, and a client greeted in that window
    /// would be told the wrong state. Wait for them, briefly.
    /// </summary>
    private async Task WaitForGreetingAsync(CancellationToken cancellationToken)
    {
        var deadline = _timeProvider.GetUtcNow() + TimeSpan.FromSeconds(5);
        while (true)
        {
            var (appState, _) = await _guider.GetStatusAsync(cancellationToken).ConfigureAwait(false);
            var hasVersion = _guider.DriverInfo?.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length > 1;
            if (appState is { Length: > 0 } and not "Unknown" && hasVersion)
            {
                Volatile.Write(ref _appState, appState);
                return;
            }
            if (_timeProvider.GetUtcNow() >= deadline)
            {
                _logger.LogWarning("PHD2 did not report its version and state within 5 s; continuing with state {AppState}", Volatile.Read(ref _appState));
                return;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(100), _timeProvider, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task AcceptClientsAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient tcpClient;
            try
            {
                tcpClient = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (SocketException ex) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning("Accepting a client failed: {Reason}", ex.Message);
                continue;
            }

            tcpClient.NoDelay = true;
            var client = new ClientConnection(Interlocked.Increment(ref _nextClientId), tcpClient, _options.ClientWriteTimeout, _logger);

            if (Volatile.Read(ref _ready) == 0 || !_guider.Connected)
            {
                // PHD2 itself would refuse the connection; closing at once lets the client retry.
                _logger.LogWarning("Refusing client {Client}: not connected to PHD2", client);
                await client.DisposeAsync().ConfigureAwait(false);
                continue;
            }

            _ = ServeClientAsync(client, cancellationToken);
        }
    }

    private async Task ServeClientAsync(ClientConnection client, CancellationToken cancellationToken)
    {
        try
        {
            // What PHD2 sends a new client before anything else.
            var version = _guider.DriverInfo?.Split(' ', StringSplitOptions.RemoveEmptyEntries) ?? [];
            await SendEventAsync(client, new VersionEvent(version.ElementAtOrDefault(1) ?? "Unknown", version.ElementAtOrDefault(2) ?? ""), cancellationToken).ConfigureAwait(false);
            await SendEventAsync(client, new AppStateEvent(Volatile.Read(ref _appState)), cancellationToken).ConfigureAwait(false);

            // Only now does it receive broadcasts, so nothing can arrive before the greeting.
            Register(client);

            using var reader = new LineReader(client.Stream);
            while (!cancellationToken.IsCancellationRequested && !client.IsClosed
                && await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                if (await HandleLineAsync(client, line, cancellationToken).ConfigureAwait(false) is { } reply)
                {
                    await client.SendAsync(reply, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or InvalidDataException)
        {
            if (!client.IsClosed)
            {
                _logger.LogInformation("Client {Client} dropped: {Reason}", client, ex.Message);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Client {Client} failed", client);
        }
        finally
        {
            _clients.TryRemove(client.Id, out _);
            await client.DisposeAsync().ConfigureAwait(false);
            _logger.LogInformation("Client {Client} disconnected ({ClientCount} connected)", client, _clients.Count);

            // A round may have been waiting only for this client.
            if (!cancellationToken.IsCancellationRequested)
            {
                ReevaluateSharedCommands();
            }
        }
    }

    /// <summary>Makes a client one of those a shared command waits for, and one that receives broadcasts.</summary>
    internal void Register(ClientConnection client)
    {
        _clients[client.Id] = client;
        _logger.LogInformation("Client {Client} connected ({ClientCount} connected)", client, _clients.Count);
    }

    /// <summary>Handles one request line and returns the reply line, or <see langword="null"/> for a notification.</summary>
    internal async Task<byte[]?> HandleLineAsync(ClientConnection client, string line, CancellationToken cancellationToken)
    {
        if (!RpcRequest.TryParse(line, out var request, out var parseError, out var idForError) || request is null)
        {
            _logger.LogWarning("Client {Client} sent a line that is not a JSON-RPC request ({Error}): {Line}", client, parseError?.Message, Truncate(line));
            return RpcResponse.Error(idForError, parseError ?? RpcError.InvalidRequest);
        }

        using (request)
        {
            byte[] reply;
            try
            {
                reply = await DispatchAsync(client, request, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("{Method} from client {Client} failed: {Reason}", request.Method, client, ex.Message);
                reply = RpcResponse.Error(request.Id, RpcError.Failed(ex.Message));
            }

            return request.IsNotification ? null : reply;
        }
    }

    private async Task<byte[]> DispatchAsync(ClientConnection client, RpcRequest request, CancellationToken ct)
    {
        var id = request.Id;
        switch (request.Method)
        {
            case "get_app_state":
                {
                    var (appState, _) = await _guider.GetStatusAsync(ct).ConfigureAwait(false);
                    var state = appState is { Length: > 0 } and not "Unknown" ? appState : Volatile.Read(ref _appState);
                    return RpcResponse.Result(id, w => w.WriteStringValue(state));
                }

            case "get_connected":
                return RpcResponse.Result(id, w => w.WriteBooleanValue(_guider.Connected));

            case "set_connected":
                if (!request.TryGetParam("connected", 0, out var connectedParam) || connectedParam.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    return RpcResponse.Error(id, RpcError.InvalidParams("set_connected takes one boolean"));
                }
                if (connectedParam.GetBoolean())
                {
                    await _guider.ConnectEquipmentAsync(ct).ConfigureAwait(false);
                    return RpcResponse.Result(id, 0);
                }
                return await RequestSharedAsync(client, request, _disconnectEquipment, true, ct).ConfigureAwait(false);

            case "get_pixel_scale":
                {
                    var scale = await _guider.PixelScaleAsync(ct).ConfigureAwait(false);
                    return RpcResponse.Result(id, w => w.WriteNumberValue(scale));
                }

            case "get_exposure":
                {
                    var exposure = await _guider.ExposureTimeAsync(ct).ConfigureAwait(false);
                    return RpcResponse.Result(id, w => w.WriteNumberValue((int)Math.Round(exposure.TotalMilliseconds)));
                }

            case "get_camera_frame_size":
                return await _guider.CameraFrameSizeAsync(ct).ConfigureAwait(false) is var (width, height)
                    ? RpcResponse.Result(id, w => { w.WriteStartArray(); w.WriteNumberValue(width); w.WriteNumberValue(height); w.WriteEndArray(); })
                    : RpcResponse.Error(id, RpcError.Failed("PHD2 did not report its camera frame size"));

            case "get_profiles":
                {
                    var profiles = await _guider.GetEquipmentProfilesAsync(ct).ConfigureAwait(false);
                    return RpcResponse.Result(id, w =>
                    {
                        w.WriteStartArray();
                        for (var i = 0; i < profiles.Count; i++)
                        {
                            WriteProfile(w, i + 1, profiles[i]);
                        }
                        w.WriteEndArray();
                    });
                }

            case "get_profile":
                {
                    var profiles = await _guider.GetEquipmentProfilesAsync(ct).ConfigureAwait(false);
                    var active = await _guider.GetActiveProfileNameAsync(ct).ConfigureAwait(false);
                    var index = active is null ? -1 : profiles.ToList().IndexOf(active);
                    return active is null
                        ? RpcResponse.Error(id, RpcError.Failed("PHD2 has no active profile"))
                        : RpcResponse.Result(id, w => WriteProfile(w, index + 1, active));
                }

            case "guide":
                {
                    if (!TryReadSettle(request, "settle", 0, out var settle, out var settleError))
                    {
                        return RpcResponse.Error(id, settleError);
                    }
                    // Guiding is shared, but starting it disturbs nobody: when PHD2 already guides,
                    // it only settles again.
                    await _guider.GuideAsync(settle.Pixels, settle.Time, settle.Timeout, ct).ConfigureAwait(false);
                    return RpcResponse.Result(id, 0);
                }

            case "dither":
                {
                    if (!request.TryGetParam("amount", 0, out var amountParam) || !amountParam.TryGetDouble(out var amount) || amount <= 0)
                    {
                        return RpcResponse.Error(id, RpcError.InvalidParams("dither needs a positive amount"));
                    }
                    var raOnly = request.TryGetParam("raOnly", 1, out var raOnlyParam) && raOnlyParam.ValueKind is JsonValueKind.True;
                    if (!TryReadSettle(request, "settle", 2, out var settle, out var settleError))
                    {
                        return RpcResponse.Error(id, settleError);
                    }
                    return await RequestSharedAsync(client, request, _dither, new DitherArgs(amount, raOnly, settle), ct).ConfigureAwait(false);
                }

            case "loop":
                return await RequestSharedAsync(client, request, _loop, true, ct).ConfigureAwait(false);

            case "stop_capture":
                return await RequestSharedAsync(client, request, _stopCapture, true, ct).ConfigureAwait(false);

            case "set_paused":
                {
                    if (!request.TryGetParam("paused", 0, out var pausedParam) || pausedParam.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    {
                        return RpcResponse.Error(id, RpcError.InvalidParams("set_paused takes a boolean"));
                    }
                    if (pausedParam.GetBoolean())
                    {
                        return await RequestSharedAsync(client, request, _pause, true, ct).ConfigureAwait(false);
                    }
                    await _guider.UnpauseAsync(ct).ConfigureAwait(false);
                    return RpcResponse.Result(id, 0);
                }

            default:
                _logger.LogWarning("Client {Client} called {Method}, which the proxy does not support", client, request.Method);
                return RpcResponse.Error(id, RpcError.MethodNotFound(request.Method));
        }
    }

    private async Task<byte[]> RequestSharedAsync<TArgs>(ClientConnection client, RpcRequest request, SharedCommand<TArgs> command, TArgs args, CancellationToken ct)
    {
        var decision = command.Barrier.Request(client.Id, args, ConnectedClientIds, _timeProvider.GetUtcNow());
        switch (decision)
        {
            case { Outcome: BarrierOutcome.Fire, Args: { } fireArgs }:
                LogFire(command.Barrier.Command, decision);
                // The reply to the program that completed the round reports what PHD2 said.
                await command.RunAsync(fireArgs, ct).ConfigureAwait(false);
                return RpcResponse.Result(request.Id, 0);

            case { Outcome: BarrierOutcome.Duplicate }:
                _logger.LogInformation("Client {Client} asked for {Command} again; still waiting for {Waiting} of {Needed} clients",
                    client, command.Barrier.Command, decision.Needed - decision.Requested, decision.Needed);
                return RpcResponse.Result(request.Id, 0);

            default:
                // Acknowledge at once, as PHD2 does: the client then waits for the events (SettleDone
                // after a dither) that arrive when the round fires.
                _logger.LogInformation("Client {Client} asked for {Command}: {Requested} of {Needed} clients have asked",
                    client, command.Barrier.Command, decision.Requested, decision.Needed);
                return RpcResponse.Result(request.Id, 0);
        }
    }

    private void ReevaluateSharedCommands()
    {
        var connected = ConnectedClientIds;
        var now = _timeProvider.GetUtcNow();
        foreach (var command in _sharedCommands)
        {
            command.Reevaluate(connected, now, this);
        }
    }

    private async Task ReevaluateSharedCommandsAsync(CancellationToken cancellationToken)
    {
        if (_options.SharedCommandTimeout is null)
        {
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), _timeProvider);
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            ReevaluateSharedCommands();
        }
    }

    private void LogFire<TArgs>(string command, BarrierDecision<TArgs> decision)
    {
        if (decision.TimedOut)
        {
            _logger.LogWarning("{Command}: only {Requested} of {Needed} clients asked within {Timeout}; acting anyway",
                command, decision.Requested, decision.Needed, _options.SharedCommandTimeout);
        }
        else
        {
            _logger.LogInformation("{Command}: all {Requested} clients asked; acting now", command, decision.Requested);
        }
    }

    /// <summary>Fires a round that completed without a request in hand (a client left, or the timeout passed).</summary>
    private void FireInBackground<TArgs>(SharedCommand<TArgs> command, BarrierDecision<TArgs> decision, TArgs args)
    {
        LogFire(command.Barrier.Command, decision);
        _ = Task.Run(async () =>
        {
            try
            {
                await command.RunAsync(args, _stopping).ConfigureAwait(false);
            }
            catch (Exception ex) when (!_stopping.IsCancellationRequested)
            {
                _logger.LogError(ex, "{Command} failed", command.Barrier.Command);
            }
        });
    }

    private async Task PumpNotificationsAsync(CancellationToken cancellationToken)
    {
        await foreach (var notification in _notifications.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                if (await ToEventAsync(notification, cancellationToken).ConfigureAwait(false) is { } @event)
                {
                    await BroadcastAsync(@event, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not relay {Notification}", notification);
            }
        }
    }

    private async ValueTask<PhdEvent?> ToEventAsync(GuiderNotification notification, CancellationToken cancellationToken)
    {
        switch (notification)
        {
            case GuiderNotification.GuideStep step:
                {
                    var scale = Volatile.Read(ref _pixelScale);
                    if (!(scale > 0))
                    {
                        scale = await _guider.PixelScaleAsync(cancellationToken).ConfigureAwait(false);
                        Volatile.Write(ref _pixelScale, scale);
                    }
                    return scale > 0 ? ToGuideStep(step, scale, Interlocked.Increment(ref _guideFrame)) : null;
                }

            case GuiderNotification.StateChanged(var name, var appState):
                if (appState is { Length: > 0 } and not "Unknown")
                {
                    Volatile.Write(ref _appState, appState);
                }

                if (name is "ConfigurationChange")
                {
                    // The guide camera or focal length may have changed; read the scale again on the next step.
                    Volatile.Write(ref _pixelScale, double.NaN);
                }

                switch (name)
                {
                    case "AppState":
                        return new AppStateEvent(Volatile.Read(ref _appState));

                    case "Settling" or "SettleDone":
                        if (await _guider.GetSettleProgressAsync(cancellationToken).ConfigureAwait(false) is not { } progress)
                        {
                            return null;
                        }
                        return name is "SettleDone"
                            ? new SettleDoneEvent(progress.Status, progress.Error is { Length: > 0 } error ? error : null)
                            : new SettlingEvent(progress.Distance, progress.Time, progress.SettleTime, progress.StarLocked);

                    case "GuideStep" or "Version":
                        // GuideStep comes through GuideCorrectionEvent with its errors; a client got Version on connect.
                        return null;

                    default:
                        if (SimpleEvent.Relayed.Contains(name))
                        {
                            return new SimpleEvent(name);
                        }
                        _logger.LogDebug("Not relaying PHD2 event {EventName}", name);
                        return null;
                }

            default:
                return null;
        }
    }

    /// <summary>
    /// TianWen reports a step's errors in arcseconds; PHD2's GuideStep carries guide-camera pixels,
    /// so divide by the scale PHD2 reports (TianWen multiplied by that same scale).
    /// </summary>
    internal static GuideStepEvent ToGuideStep(GuiderNotification.GuideStep step, double arcsecPerPixel, int frame) => new(frame, step.RaErrorArcsec / arcsecPerPixel, step.DecErrorArcsec / arcsecPerPixel)
    {
        RADuration = step.RaPulseMs is 0 ? null : (int)Math.Round(Math.Abs(step.RaPulseMs)),
        RADirection = step.RaPulseMs switch { > 0 => "West", < 0 => "East", _ => null },
        DECDuration = step.DecPulseMs is 0 ? null : (int)Math.Round(Math.Abs(step.DecPulseMs)),
        DECDirection = step.DecPulseMs switch { > 0 => "North", < 0 => "South", _ => null },
    };

    private async Task BroadcastAsync(PhdEvent @event, CancellationToken cancellationToken)
    {
        var line = Serialize(@event);
        var clients = _clients.Values.ToArray();
        var sends = new Task<bool>[clients.Length];
        for (var i = 0; i < clients.Length; i++)
        {
            sends[i] = clients[i].SendAsync(line, cancellationToken).AsTask();
        }
        // SendAsync never throws for a client's own failure; it closes that client instead, and
        // its read loop then removes it.
        await Task.WhenAll(sends).ConfigureAwait(false);
    }

    private ValueTask<bool> SendEventAsync(ClientConnection client, PhdEvent @event, CancellationToken cancellationToken)
        => client.SendAsync(Serialize(@event), cancellationToken);

    internal static byte[] Serialize(PhdEvent @event)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(@event, PhdEventJsonContext.Default.GetTypeInfo(@event.GetType())
            ?? throw new InvalidOperationException($"{@event.GetType().Name} is not registered in {nameof(PhdEventJsonContext)}"));
        var line = new byte[json.Length + 2];
        json.CopyTo(line, 0);
        line[^2] = (byte)'\r';
        line[^1] = (byte)'\n';
        return line;
    }

    private void CloseAllClients(string reason)
    {
        foreach (var client in _clients.Values)
        {
            _logger.LogInformation("Closing client {Client}: {Reason}", client, reason);
            client.Close();
        }
    }

    private static bool TryReadSettle(RpcRequest request, string name, int position, out SettleArgs settle, out RpcError error)
    {
        settle = default;
        error = RpcError.InvalidParams($"{request.Method} needs a settle object with pixels, time and timeout");
        if (!request.TryGetParam(name, position, out var element) || element.ValueKind is not JsonValueKind.Object
            || !element.TryGetProperty("pixels", out var pixels) || !pixels.TryGetDouble(out var pixelsValue)
            || !element.TryGetProperty("time", out var time) || !time.TryGetDouble(out var timeValue)
            || !element.TryGetProperty("timeout", out var timeout) || !timeout.TryGetDouble(out var timeoutValue)
            || pixelsValue <= 0 || timeValue < 0 || timeoutValue <= 0)
        {
            return false;
        }

        settle = new SettleArgs(pixelsValue, timeValue, timeoutValue);
        return true;
    }

    private static void WriteProfile(Utf8JsonWriter writer, int id, string name)
    {
        writer.WriteStartObject();
        writer.WriteNumber("id", id);
        writer.WriteString("name", name);
        writer.WriteEndObject();
    }

    private static string Truncate(string line) => line.Length <= 200 ? line : string.Concat(line.AsSpan(0, 200), "...");

    private static async Task IgnoreCancellation(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private interface ISharedCommand
    {
        void Reevaluate(IReadOnlyCollection<int> connected, DateTimeOffset now, ProxyServer server);
    }

    private sealed class SharedCommand<TArgs>(string name, TimeSpan? timeout, Func<TArgs, CancellationToken, ValueTask> run) : ISharedCommand
    {
        public SharedCommandBarrier<TArgs> Barrier { get; } = new(name, timeout);

        public ValueTask RunAsync(TArgs args, CancellationToken ct) => run(args, ct);

        public void Reevaluate(IReadOnlyCollection<int> connected, DateTimeOffset now, ProxyServer server)
        {
            if (Barrier.Reevaluate(connected, now) is { Outcome: BarrierOutcome.Fire, Args: { } args } decision)
            {
                server.FireInBackground(this, decision, args);
            }
        }
    }

    internal abstract record GuiderNotification
    {
        public sealed record StateChanged(string Name, string AppState) : GuiderNotification;
        /// <summary>Errors in arcseconds and signed pulses in ms (positive West, North), as TianWen reports them.</summary>
        public sealed record GuideStep(double RaErrorArcsec, double DecErrorArcsec, double RaPulseMs, double DecPulseMs) : GuiderNotification;
    }
}

internal readonly record struct SettleArgs(double Pixels, double Time, double Timeout);

internal sealed record DitherArgs(double Amount, bool RaOnly, SettleArgs Settle);
