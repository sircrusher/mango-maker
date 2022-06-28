using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MangoMaker
{
	public class Startup
    {
        public IConfiguration Configuration { get; }
        public Startup(IConfiguration configuration)
        {
            Configuration = configuration;
        }

        public void ConfigureServices(IServiceCollection services)
        {
            services.AddControllers();
            services.AddLogging();
            services.AddMemoryCache();
            services.AddSingleton<HedgeService>();
            services.AddHostedService(p => p.GetRequiredService<HedgeService>());
            services.AddSingleton<MakerService>();
            services.AddHostedService(p => p.GetRequiredService<MakerService>());
        }

        public void Configure(IApplicationBuilder app)
        {
            app.UseForwardedHeaders(new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto });
            app.UseRouting();
            app.UseEndpoints(endpoints => endpoints.MapControllers());
        }
    }
}
