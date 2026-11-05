using Microsoft.Extensions.DependencyInjection;
using TianWen.Lib.Devices;
using TianWen.Lib.Extensions;
using TianWen.Lib.Devices.OpenPHD2;

namespace TianWen.OpenPHD2MultiScope;

/// <summary>
/// What the proxy needs from TianWen.Lib, in one place so that Program.cs and the composition test
/// cannot drift apart: a registration that resolves only when the program runs is a failure the
/// unit tests, which hand ProxyServer a substitute guider, would never see.
/// </summary>
internal static class ProxyComposition
{
    /// <summary>The TianWen.Lib services the PHD2 driver depends on. Logging is the caller's.</summary>
    public static IServiceCollection AddProxyServices(this IServiceCollection services)
        => services.AddExternal().AddPHD2();

    /// <summary>
    /// The PHD2 instance the user named, addressed directly rather than discovered: discovery would
    /// pick whichever PHD2 answers first.
    /// </summary>
    public static OpenPHD2GuiderDevice GuiderDevice(ProxyOptions options)
        => new(DeviceType.Guider, $"{options.Phd2Host}/{options.Phd2Instance}", $"PHD2 #{options.Phd2Instance} on {options.Phd2Host}");
}
