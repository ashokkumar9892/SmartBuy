using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmartBuy.Domain.Suppliers;
using SmartBuy.Infrastructure.Ai;
using SmartBuy.Infrastructure.Persistence;
using SmartBuy.Infrastructure.Services;
using SmartBuy.Infrastructure.Suppliers.Mock;

namespace SmartBuy.Infrastructure;

public sealed class DatabaseOptions
{
    /// <summary>"postgres" (production) or "sqlite" (zero-install local development and tests).</summary>
    public string Provider { get; set; } = "sqlite";
    public string ConnectionString { get; set; } = "Data Source=smartbuy-dev.db";
}

public sealed class JobOptions
{
    /// <summary>Runs the in-process price-alert scheduler. Enable on exactly one instance.</summary>
    public bool PriceAlertSchedulerEnabled { get; set; }
    public int PriceAlertIntervalMinutes { get; set; } = 60;
}

public static class DependencyInjection
{
    public static IServiceCollection AddSmartBuyInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var database = configuration.GetSection("Database").Get<DatabaseOptions>() ?? new DatabaseOptions();
        services.AddDbContext<AppDbContext>(options => ConfigureDatabase(options, database));

        services.Configure<SeedOptions>(configuration.GetSection("Seed"));
        services.Configure<SupplierOptions>(configuration.GetSection("Suppliers"));
        services.Configure<AiOptions>(configuration.GetSection("Ai"));
        services.Configure<SmtpOptions>(configuration.GetSection("Smtp"));
        services.Configure<BillingOptions>(configuration.GetSection("Billing"));
        services.Configure<AppOptions>(configuration.GetSection("App"));
        services.Configure<JobOptions>(configuration.GetSection("Jobs"));

        services.AddMemoryCache();
        services.AddHttpClient();
        services.TryAddTimeProvider();

        services.AddScoped<TenantContext>();
        services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());

        // Supplier adapters. Mock adapters are always registered; live adapters add themselves only when configured.
        services.AddSingleton<MockSupplierControl>();
        foreach (var profile in MockSupplierProfiles.All)
            services.AddSingleton<ISupplierProvider>(sp => new MockSupplierProvider(profile, sp.GetRequiredService<MockSupplierControl>(), sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<ISupplierProviderRegistry, SupplierProviderRegistry>();

        services.AddSingleton<IAiProvider, MockAiProvider>();
        services.AddSingleton<IAiProvider, ClaudeAiProvider>();
        services.AddSingleton<IAiProvider, OpenAiProvider>();
        services.AddScoped<AiGateway>();
        services.AddScoped<CatalogResolver>();

        services.AddSingleton<IPurchaseOrderDeliveryChannel, SmtpPurchaseOrderChannel>();

        services.AddScoped<AuditService>();
        services.AddScoped<EntitlementService>();
        services.AddScoped<SupplierCatalogService>();
        services.AddScoped<ComparisonService>();
        services.AddScoped<CartService>();
        services.AddScoped<PurchaseOrderService>();
        services.AddScoped<AlternativesService>();
        services.AddScoped<PlanExplanationService>();
        services.AddScoped<PriceAlertJob>();
        services.AddScoped<BillingService>();
        services.AddScoped<DataSeeder>();

        services.AddHostedService<PriceAlertScheduler>();
        return services;
    }

    private static void TryAddTimeProvider(this IServiceCollection services)
    {
        if (services.All(s => s.ServiceType != typeof(TimeProvider))) services.AddSingleton(TimeProvider.System);
    }

    public static void ConfigureDatabase(DbContextOptionsBuilder options, DatabaseOptions database)
    {
        if (string.Equals(database.Provider, "postgres", StringComparison.OrdinalIgnoreCase) || string.Equals(database.Provider, "postgresql", StringComparison.OrdinalIgnoreCase))
            options.UseNpgsql(database.ConnectionString, npgsql => npgsql.MigrationsAssembly(typeof(AppDbContext).Assembly.FullName));
        else
            options.UseSqlite(database.ConnectionString);
    }

    /// <summary>PostgreSQL applies EF Core migrations; SQLite (development/test only) creates the schema directly from the model.</summary>
    public static async Task InitializeDatabaseAsync(this IServiceProvider services, CancellationToken ct = default)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        if (db.Database.IsNpgsql()) await db.Database.MigrateAsync(ct);
        else await db.Database.EnsureCreatedAsync(ct);
        await scope.ServiceProvider.GetRequiredService<DataSeeder>().SeedAsync(ct);
    }
}

/// <summary>Used by <c>dotnet ef</c>. Migrations always target PostgreSQL; no database connection is needed to generate them.</summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var connection = Environment.GetEnvironmentVariable("SMARTBUY_MIGRATIONS_CONNECTION") ?? "Host=localhost;Database=smartbuy;Username=smartbuy;Password=smartbuy";
        var builder = new DbContextOptionsBuilder<AppDbContext>();
        DependencyInjection.ConfigureDatabase(builder, new DatabaseOptions { Provider = "postgres", ConnectionString = connection });
        return new AppDbContext(builder.Options, new TenantContext());
    }
}

/// <summary>
/// Simple in-process scheduler for price alerts. Suitable for a single instance; for multiple instances or guaranteed
/// execution, disable it and trigger <c>--run-job price-alerts</c> from Windows Task Scheduler, cron, or a job runner.
/// </summary>
public sealed class PriceAlertScheduler(IServiceScopeFactory scopes, IOptions<JobOptions> options, ILogger<PriceAlertScheduler> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.PriceAlertSchedulerEnabled) return;
        var interval = TimeSpan.FromMinutes(Math.Max(5, options.Value.PriceAlertIntervalMinutes));
        logger.LogInformation("Price alert scheduler running every {Minutes} minutes", interval.TotalMinutes);
        using var timer = new PeriodicTimer(interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await RunOnceAsync(scopes, null, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Price alert job failed");
            }
        }
    }

    public static async Task<AlertJobResult> RunOnceAsync(IServiceScopeFactory scopes, Guid? organizationId, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var tenant = scope.ServiceProvider.GetRequiredService<TenantContext>();
        using var elevated = tenant.Elevate();
        return await scope.ServiceProvider.GetRequiredService<PriceAlertJob>().RunAsync(organizationId, ct);
    }
}
