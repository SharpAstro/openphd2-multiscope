using System.Globalization;
using System.Net;
using Microsoft.Extensions.Configuration;

namespace TianWen.OpenPHD2MultiScope;

internal sealed record ProxyOptions(
    string Phd2Host,
    uint Phd2Instance,
    IPAddress ListenAddress,
    int ListenPort,
    TimeSpan? SharedCommandTimeout,
    TimeSpan ClientWriteTimeout,
    TimeSpan ReconnectDelay)
{
    /// <summary>PHD2 instance N listens on 4400 + N - 1; the proxy defaults to ten above PHD2's first port.</summary>
    public const int DefaultListenPort = 4410;

    public const string Usage = """
        openphd2-multiscope: lets several imaging programs share one PHD2, dithering only once all of them ask.

        Point every imaging program at this proxy's port instead of PHD2's (4400).

        Options:
          --phd2-host <host>         Host PHD2 runs on (default: localhost)
          --phd2-instance <n>        PHD2 instance number (default: 1, i.e. port 4400)
          --listen <address>         Address to listen on (default: 127.0.0.1; 0.0.0.0 for every interface)
          --port <port>              Port to listen on (default: 4410)
          --shared-timeout <s>       Seconds a dither/stop/pause/loop waits for the other programs to ask
                                     before acting anyway (default: 0, wait for all of them)
          --help                     Show this text
        """;

    public static bool TryParse(IConfiguration configuration, out ProxyOptions options, out string? error)
    {
        options = new ProxyOptions("localhost", 1, IPAddress.Loopback, DefaultListenPort, null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(5));
        error = null;

        if (configuration["phd2-host"] is { Length: > 0 } host)
        {
            options = options with { Phd2Host = host };
        }

        if (configuration["phd2-instance"] is { } instanceText)
        {
            if (!uint.TryParse(instanceText, NumberStyles.None, CultureInfo.InvariantCulture, out var instance) || instance is 0 or > 100)
            {
                error = $"--phd2-instance must be a number from 1 to 100, not '{instanceText}'";
                return false;
            }
            options = options with { Phd2Instance = instance };
        }

        if (configuration["listen"] is { } listenText)
        {
            if (!IPAddress.TryParse(listenText, out var address))
            {
                error = $"--listen must be an IP address, not '{listenText}'";
                return false;
            }
            options = options with { ListenAddress = address };
        }

        if (configuration["port"] is { } portText)
        {
            if (!int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port is < 1 or > 65535)
            {
                error = $"--port must be a number from 1 to 65535, not '{portText}'";
                return false;
            }
            options = options with { ListenPort = port };
        }

        if (configuration["shared-timeout"] is { } timeoutText)
        {
            if (!double.TryParse(timeoutText, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) || seconds < 0 || !double.IsFinite(seconds))
            {
                error = $"--shared-timeout must be a number of seconds, 0 or more, not '{timeoutText}'";
                return false;
            }
            options = options with { SharedCommandTimeout = seconds > 0 ? TimeSpan.FromSeconds(seconds) : null };
        }

        if (IsLocal(options.Phd2Host) && options.ListenPort == Phd2Port(options.Phd2Instance))
        {
            error = $"--port {options.ListenPort} is PHD2's own port for instance {options.Phd2Instance}; choose another";
            return false;
        }

        return true;
    }

    public static int Phd2Port(uint instance) => 4400 + (int)instance - 1;

    private static bool IsLocal(string host) => host is "localhost" or "127.0.0.1" or "::1" || string.Equals(host, Environment.MachineName, StringComparison.OrdinalIgnoreCase);
}
