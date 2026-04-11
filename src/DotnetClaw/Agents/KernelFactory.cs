using DotnetClaw.Config;
using DotnetClaw.Jobby;
using DotnetClaw.Plugins;
using DotnetClaw.Browser;
using DotnetClaw.Workspace;
using DotnetClaw.Workflowy.Plugin;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;

namespace DotnetClaw.Agents;

/// <summary>
/// Builds and configures the <see cref="Kernel"/> instance used by the agent.
///
/// Provider selection is controlled by the DOTNETCLAW_PROVIDER environment variable:
///   openai    → OpenAI (default, uses OPENAI_API_KEY)
///   azure     → Azure OpenAI (uses AZURE_OPENAI_* env vars)
///   anthropic → Anthropic Claude (uses ANTHROPIC_API_KEY)
/// </summary>
public static class KernelFactory
{
    public static Kernel Build(
        IServiceProvider services,
        DotnetClawOptions options,
        ILoggerFactory loggerFactory)
    {
        var builder = Kernel.CreateBuilder();
        builder.Services.AddSingleton(loggerFactory);

        // ── LLM Provider ────────────────────────────────────────────────────
        var provider = (Environment.GetEnvironmentVariable("DOTNETCLAW_PROVIDER") ?? "openai").ToLowerInvariant();

        switch (provider)
        {
            case "azure":
                ConfigureAzureOpenAI(builder, options);
                break;

            case "anthropic":
                ConfigureAnthropic(builder, options);
                break;

            default: // openai
                ConfigureOpenAI(builder, options);
                break;
        }

        // ── Register plugins (skills) ────────────────────────────────────────
        builder.Plugins.AddFromObject(
            services.GetRequiredService<ShellPlugin>(), "Shell");

        builder.Plugins.AddFromObject(
            services.GetRequiredService<FileSystemPlugin>(), "FileSystem");

        builder.Plugins.AddFromObject(
            services.GetRequiredService<DotnetPlugin>(), "Dotnet");

        builder.Plugins.AddFromObject(
            services.GetRequiredService<WorkspacePlugin>(), "Workspace");

        builder.Plugins.AddFromObject(
            services.GetRequiredService<CursorPlugin>(), "Cursor");

        builder.Plugins.AddFromObject(
            services.GetRequiredService<TelegramPlugin>(), "Telegram");

        builder.Plugins.AddFromObject(
            services.GetRequiredService<BrowserPlugin>(), "Browser");

        builder.Plugins.AddFromObject(
            services.GetRequiredService<McpPlugin>(), "Mcp");

        builder.Plugins.AddFromObject(
            services.GetRequiredService<JobbyPlugin>(), "Jobby");

        builder.Plugins.AddFromObject(
            services.GetRequiredService<WorkflowyPlugin>(), "Workflowy");

        // NOTE: Mcp_{serverName} plugins from live MCP servers are loaded later by
        // McpKernelLoader.LoadAsync(), called from ClawAgentLoop.InitialiseAsync().
        // They cannot be registered here because IMcpClient.AsKernelPluginAsync is async.

        return builder.Build();
    }

    // ── Provider helpers ─────────────────────────────────────────────────────

    private static void ConfigureOpenAI(IKernelBuilder builder, DotnetClawOptions options)
    {
        var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY")
            ?? throw new InvalidOperationException(
                "OPENAI_API_KEY environment variable is not set. " +
                "Set it or switch provider via DOTNETCLAW_PROVIDER.");

        builder.AddOpenAIChatCompletion(options.ModelId, apiKey);
    }

    private static void ConfigureAzureOpenAI(IKernelBuilder builder, DotnetClawOptions options)
    {
        var endpoint = Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT")
            ?? throw new InvalidOperationException("AZURE_OPENAI_ENDPOINT is not set.");
        var apiKey = Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY")
            ?? throw new InvalidOperationException("AZURE_OPENAI_API_KEY is not set.");
        var deployment = Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT") ?? options.ModelId;

        builder.AddAzureOpenAIChatCompletion(deployment, endpoint, apiKey);
    }

    private static void ConfigureAnthropic(IKernelBuilder builder, DotnetClawOptions options)
    {
        // Anthropic support via community connector or custom HTTP client.
        // Swap this out for the official SK Anthropic connector when available.
        throw new NotImplementedException(
            "Anthropic provider is not yet wired up. " +
            "Install a community SK connector or implement ITextGenerationService.");
    }
}
