using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DotnetClaw.Gateway;

// ============================================================================
//  GatewayExtensions — SignalR hub registration helpers
// ============================================================================

public static class GatewayExtensions
{
    /// <summary>
    /// Registers SignalR and the <see cref="GatewayHub"/> with the DI container.
    /// Call this from <c>Program.cs</c> before <c>builder.Build()</c>.
    /// </summary>
    public static IServiceCollection AddGateway(this IServiceCollection services)
    {
        services.AddSignalR();
        services
            .AddSingleton<TelegramGatewayAdapter>()
            .AddHostedService(sp => sp.GetRequiredService<TelegramGatewayAdapter>());
        return services;
    }

    /// <summary>
    /// Maps the <see cref="GatewayHub"/> at the path defined in <see cref="GatewayOptions"/>.
    /// Call this after <c>builder.Build()</c>.
    /// </summary>
    public static IEndpointRouteBuilder MapGateway(this IEndpointRouteBuilder app)
    {
        var options = app.ServiceProvider.GetRequiredService<IOptions<GatewayOptions>>().Value;
        var log     = app.ServiceProvider
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger(typeof(GatewayExtensions).FullName!);

        if (!options.Enabled)
        {
            log.LogInformation("WebSocket Gateway is disabled (Gateway:Enabled = false).");
            return app;
        }

        app.MapHub<GatewayHub>(options.Path);
        log.LogInformation(
            "SignalR Gateway mapped at '{Path}' on port {Port}",
            options.Path, options.Port);

        return app;
    }
}
