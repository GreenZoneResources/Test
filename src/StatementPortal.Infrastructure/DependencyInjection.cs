using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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

        services.AddHttpClient<IStatementServiceClient, StatementServiceClient>()
            .AddStandardResilienceHandler();

        return services;
    }
}
