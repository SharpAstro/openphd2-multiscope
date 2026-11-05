using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TianWen.Lib.Devices;
using TianWen.Lib.Devices.Guider;
using TianWen.Lib.Devices.OpenPHD2;
using TianWen.Lib.Extensions;
using TianWen.OpenPHD2MultiScope;

if (args.Any(a => a is "--help" or "-h" or "/?"))
{
    Console.WriteLine(ProxyOptions.Usage);
    return 0;
}

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = args, DisableDefaults = true });
builder.Configuration.AddCommandLine(args);

if (!ProxyOptions.TryParse(builder.Configuration, out var options, out var error))
{
    Console.Error.WriteLine(error);
    Console.Error.WriteLine();
    Console.Error.WriteLine(ProxyOptions.Usage);
    return 2;
}

builder.Services
    .AddLogging(logging => logging.AddSimpleConsole(console =>
    {
        console.SingleLine = true;
        console.IncludeScopes = false;
        console.TimestampFormat = "HH:mm:ss ";
    }))
    .AddExternal()
    .AddPHD2();

using var host = builder.Build();
await host.StartAsync();

var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("openphd2-multiscope");
var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();

// Addressed directly rather than discovered: discovery would pick whichever PHD2 answers first,
// and the user named one.
var device = new OpenPHD2GuiderDevice(DeviceType.Guider, $"{options.Phd2Host}/{options.Phd2Instance}", $"PHD2 #{options.Phd2Instance} on {options.Phd2Host}");
if (!device.TryInstantiateDriver<IGuider>(host.Services, out var guider))
{
    logger.LogCritical("TianWen.Lib could not create a PHD2 driver for {Device}", device);
    return 1;
}

await using (guider)
{
    try
    {
        await new ProxyServer(guider, options, logger, TimeProvider.System).RunAsync(lifetime.ApplicationStopping);
    }
    catch (OperationCanceledException) when (lifetime.ApplicationStopping.IsCancellationRequested)
    {
    }
    catch (Exception ex)
    {
        logger.LogCritical(ex, "The proxy stopped");
        return 1;
    }
}

await host.StopAsync();
return 0;
