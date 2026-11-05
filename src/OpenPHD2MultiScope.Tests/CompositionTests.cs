using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using TianWen.Lib.Devices.Guider;
using Xunit;

namespace TianWen.OpenPHD2MultiScope.Tests;

/// <summary>
/// Builds the services exactly as Program.cs does and creates the real PHD2 driver from them. The
/// other tests hand ProxyServer a substitute guider, so a registration TianWen.Lib's driver needs
/// but the proxy does not add (tianwen #1985) would otherwise only fail when the program starts.
/// </summary>
public sealed class CompositionTests
{
    [Fact]
    public async Task TheProgramsServicesCreateThePhd2Driver()
    {
        await using var services = new ServiceCollection().AddLogging().AddProxyServices().BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        var options = new ProxyOptions("localhost", 1, IPAddress.Loopback, 4410, null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(5));

        ProxyComposition.GuiderDevice(options).TryInstantiateDriver<IGuider>(services, out var guider).ShouldBeTrue();

        await using (guider)
        {
            guider.ShouldNotBeNull();
            guider.Connected.ShouldBeFalse();
        }
    }
}
