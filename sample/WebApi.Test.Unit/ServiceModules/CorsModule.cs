using EasilyNET.AutoDependencyInjection.Contexts;
using EasilyNET.AutoDependencyInjection.Modules;

namespace WebApi.Test.Unit.ServiceModules;

/// <summary>
/// 配置跨域服务及中间件
/// </summary>
internal sealed class CorsModule : AppModule
{
    /// <inheritdoc />
    public override void ConfigureServices(ConfigureServicesContext context)
    {
        var config = context.Services.GetConfiguration();
        var allow = config["AllowedHosts"] ?? "*";
        context.Services.AddCors(c => c.AddPolicy("AllowedHosts", s => s.WithOrigins(allow.Split(",")).AllowAnyMethod().AllowAnyHeader()));
    }

    /// <inheritdoc />
    public override void ApplicationInitialization(ApplicationContext context)
    {
        var app = context.GetApplicationHost() as IApplicationBuilder;
        app?.UseCors("AllowedHosts");
    }
}