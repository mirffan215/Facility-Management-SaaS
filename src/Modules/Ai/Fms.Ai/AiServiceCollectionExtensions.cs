using Fms.Ai.Persistence;
using Fms.Ai.Services;
using Fms.BuildingBlocks.Persistence;
using Fms.BuildingBlocks.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Fms.Ai;

public static class AiServiceCollectionExtensions
{
    public static IServiceCollection AddFmsAi(this IServiceCollection services, string connectionString)
    {
        services.AddFmsPendingWrites();
        services.AddDbContext<AiDbContext>((sp, options) => options
            .UseSqlServer(connectionString, AiDbContextOptions.Sql)
            .AddInterceptors(new TenantSessionContextInterceptor(sp.GetRequiredService<ITenantContext>())));

        services.AddOptions<AiOptions>().BindConfiguration("Ai");
        services.AddSingleton<IAiProvider>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<AiOptions>>();
            return string.Equals(options.Value.Provider, "Claude", StringComparison.OrdinalIgnoreCase)
                ? new ClaudeProvider(options, sp.GetRequiredService<ILogger<ClaudeProvider>>())
                : new NoAiProvider();
        });
        services.AddScoped<AiSettingsService>();
        services.AddScoped<AiAssistService>();
        services.AddHealthChecks().AddDbContextCheck<AiDbContext>("ai-db", tags: ["ready"]);
        return services;
    }
}
