using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;

namespace Scar.Utilities
{
    public static class HostUtilities
    {
        public static IHost BuildAndRunHost(string[] args, Func<IServiceCollection, IServiceCollection> configureServices)
        {
            _ = configureServices ?? throw new ArgumentNullException(nameof(configureServices));

            Log.Logger = new LoggerConfiguration()
                .WriteTo.Console(outputTemplate: "{Message:lj}{NewLine}").MinimumLevel.Information()
                .CreateLogger();

            var host = Host.CreateDefaultBuilder(args)
                .ConfigureServices(services =>
                {
                    services.Configure<ConsoleLifetimeOptions>(options => options.SuppressStatusMessages = true);
                    configureServices(services);
                })
                .ConfigureLogging(
                    logging =>
                    {
                        logging.ClearProviders().AddSerilog();
                    })
                .Build();
            host.RunAsync();
            return host;
        }
    }
}
