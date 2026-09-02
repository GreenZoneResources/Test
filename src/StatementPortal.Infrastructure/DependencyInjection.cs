using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StatementPortal.Application.Audit;
using StatementPortal.Application.Permissions;
using StatementPortal.Application.Statements;
using StatementPortal.Infrastructure.Auth;
using StatementPortal.Infrastructure.Persistence;
using StatementPortal.Infrastructure.Sso;
using StatementPortal.Infrastructure.StatementService;

namespace StatementPortal.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddStatementPortalInfrastructure(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.AddHttpContextAccessor();
        services.AddMemoryCache();

        services.AddDbContext<StatementPortalDbContext>(options =>
            options.UseSqlServer(
                configuration.GetConnectionString("StatementPortal"),
                sql => sql.EnableRetryOnFailure(maxRetryCount: 3)));

        services.AddScoped<IAuditWriter, AuditRepository>();
        services.AddScoped<IAuditQueryService, AuditRepository>();

        services.AddScoped<Application.Common.ICurrentUserContext, CurrentUserContext>();
        services.AddScoped<IStatementRequestService, StatementRequestService>();

        services.AddScoped<IValidator<SingleStatementRequestDto>, SingleStatementRequestValidator>();
        services.AddScoped<IValidator<BulkStatementRequestDto>, BulkStatementRequestValidator>();

        services.Configure<SsoOptions>(configuration.GetSection(SsoOptions.SectionName));
        services.Configure<StatementServiceOptions>(configuration.GetSection(StatementServiceOptions.SectionName));

        services.AddScoped<IPermissionService, PermissionService>();

        // Named/typed clients get the standard resilience handler (retry with
        // jitter, circuit breaker, and an overall timeout) so a slow or flaky
        // downstream never cascades into thread-pool starvation on the portal.
        services.AddHttpClient<ISsoClient, SsoClient>()
            .AddStandardResilienceHandler();

        // Dedicated named client for the service-account login call itself —
        // kept separate from the main Statement Service client so the auth
        // handler below never ends up trying to authenticate its own login
        // request.
        services.AddHttpClient("StatementServiceAuth")
            .AddStandardResilienceHandler();

        // Registered as a singleton deliberately: its cached token and
        // refresh lock must be shared across every request in the process.
        // AddHttpClient<TClient, TImpl>() registers the typed client as
        // transient by convention, which would silently defeat the caching
        // (a fresh, empty cache on every resolution) — so this is built by
        // hand from IHttpClientFactory instead of using that pattern here.
        services.AddSingleton<IStatementServiceAuthenticator>(sp =>
        {
            var httpClient = sp.GetRequiredService<IHttpClientFactory>().CreateClient("StatementServiceAuth");
            return new StatementServiceAuthenticator(
                httpClient,
                sp.GetRequiredService<IOptions<StatementServiceOptions>>(),
                sp.GetRequiredService<ILogger<StatementServiceAuthenticator>>());
        });

        services.AddTransient<StatementServiceAuthHandler>();

        var statementServiceClientBuilder = services.AddHttpClient<IStatementServiceClient, StatementServiceClient>();
        // Order matters: registering resilience first, then the auth handler,
        // puts the auth handler *inside* the resilience wrapper — so if
        // resilience retries a failed request, the auth handler re-runs on
        // each attempt too (attaching a freshly-refreshed token if needed),
        // rather than only ever running once for the whole retry sequence.
        statementServiceClientBuilder.AddStandardResilienceHandler();
        statementServiceClientBuilder.AddHttpMessageHandler<StatementServiceAuthHandler>();

        return services;
    }
}
