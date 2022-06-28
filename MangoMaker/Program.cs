using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;

namespace MangoMaker
{
    class Program
    {
        public static bool FlatFreeze { get; set; }
        public static bool ShuttingDown { get; set; }

        static async Task Main(string[] args)
        {
            ShuttingDown = false;
            FlatFreeze = true;

            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Information()
                .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Error)
                .MinimumLevel.Override("System", LogEventLevel.Error)
                .MinimumLevel.Override("Websocket.Client.WebsocketClient", LogEventLevel.Fatal)
                .Enrich.FromLogContext()
                .WriteTo.RollingFile("session.log", outputTemplate: "{Timestamp:o} [{Level:u3}] [{SourceContext}] {Message}{NewLine}{Exception}")
                .WriteTo.Console(outputTemplate: "{Timestamp:o} [{Level:u3}] [{SourceContext}] {Message}{NewLine}{Exception}")
                .CreateLogger();

            AppConfig.Configuration = new ConfigurationBuilder()
                .AddJsonFile("appsettings.json", false)
                .Build();

            await Host.CreateDefaultBuilder(args)
                .UseSystemd()
                .ConfigureLogging((context, logging) =>
                {
                    logging.ClearProviders();
                    logging.AddDebug();
                    logging.AddConfiguration(context.Configuration.GetSection("Logging"));
                    logging.AddSerilog();
                })
                .ConfigureWebHost(builder => builder
                    .UseKestrel()
                    .UseUrls($"http://*:{AppConfig.Configuration["ApiPort"]}")
                    .UseStartup<Startup>())
                .Build()
                .RunAsync();
        }
    }
}