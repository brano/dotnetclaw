using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DotnetClaw.Gateway;

// ============================================================================
//  GatewayExtensions — WebSocket gateway registration helpers
// ============================================================================

public static class GatewayExtensions
{
    /// <summary>
    /// Registers WebSocket gateway services with the DI container.
    /// Call this from <c>Program.cs</c> before <c>builder.Build()</c>.
    /// </summary>
    public static IServiceCollection AddGateway(this IServiceCollection services)
    {
        services.AddSingleton<GatewayConnectionManager>();
        services.AddSingleton<GatewayWebSocketHandler>();
        services
            .AddSingleton<TelegramGatewayAdapter>()
            .AddHostedService(sp => sp.GetRequiredService<TelegramGatewayAdapter>());
        return services;
    }

    /// <summary>
    /// Enables WebSockets and maps the gateway endpoint at the path defined
    /// in <see cref="GatewayOptions"/>.
    /// Call this after <c>builder.Build()</c>.
    /// </summary>
    public static WebApplication MapGateway(this WebApplication app)
    {
        var options = app.Services.GetRequiredService<IOptions<GatewayOptions>>().Value;
        var log     = app.Services
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger(typeof(GatewayExtensions).FullName!);

        if (!options.Enabled)
        {
            log.LogInformation("WebSocket Gateway is disabled (Gateway:Enabled = false).");
            return app;
        }

        app.UseWebSockets();

        var handler = app.Services.GetRequiredService<GatewayWebSocketHandler>();
        app.Map(options.Path, (HttpContext context) => handler.HandleAsync(context));

        log.LogInformation(
            "WebSocket Gateway mapped at '{Path}' on port {Port}",
            options.Path, options.Port);

        return app;
    }
}
