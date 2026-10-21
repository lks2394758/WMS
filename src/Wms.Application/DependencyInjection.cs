using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Wms.Application.Auth;
using Wms.Application.Catalog;
using Wms.Application.Inventory;
using Wms.Application.Persistence.InMemory;
using Wms.Persistence.Abstractions;

namespace Wms.Application;

public static class DependencyInjection
{
    /// <summary>Use cases. The host must also register an <see cref="IAccessTokenIssuer"/> and a persistence provider.</summary>
    public static IServiceCollection AddWmsApplication(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IPasswordHasher>(new Pbkdf2PasswordHasher());
        services.AddScoped<AuthService>();
        services.AddScoped<CatalogQueries>();
        services.AddScoped<InventoryQueries>();
        services.AddScoped<InventoryCommandService>();
        return services;
    }

    /// <summary>
    /// API v2 use cases (B1: identity and seeding). The host must also register a v2 persistence provider
    /// (<c>AddWmsV2SqlitePersistence</c>). Independent of <see cref="AddWmsApplication"/>; a v2 host registers only this.
    /// </summary>
    public static IServiceCollection AddWmsV2Application(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IPasswordHasher>(new Pbkdf2PasswordHasher());
        services.AddScoped<V2.Auth.V2AuthService>();
        services.AddScoped<V2.V2Seeder>();
        services.AddScoped<V2.Common.V2CatalogWriter>();
        services.AddScoped<V2.Catalog.V2UnitService>();
        services.AddScoped<V2.Catalog.V2CategoryService>();
        services.AddScoped<V2.Catalog.V2ProductService>();
        services.AddScoped<V2.Admin.V2BinService>();
        services.AddScoped<V2.Admin.V2UserService>();
        services.AddScoped<V2.Admin.V2SettingsService>();
        services.AddScoped<V2.Inventory.V2InventoryService>();
        services.AddScoped<V2.Inventory.V2TransactionQueries>();
        services.AddScoped<V2.Inventory.V2SerialService>();
        services.AddScoped<V2.Counts.V2CountService>();
        services.AddScoped<V2.Monitoring.V2MonitoringService>();
        return services;
    }

    /// <summary>
    /// The in-memory provider (explicit test/development option, <c>Persistence:Provider = InMemory</c>). One shared
    /// instance backs every port so a committed write is visible to every reader. Data is lost on restart.
    /// </summary>
    public static IServiceCollection AddWmsInMemoryPersistence(this IServiceCollection services)
    {
        services.AddSingleton<InMemoryWmsStore>();
        services.AddSingleton<ICatalogReader>(sp => sp.GetRequiredService<InMemoryWmsStore>());
        services.AddSingleton<IInventoryReader>(sp => sp.GetRequiredService<InMemoryWmsStore>());
        services.AddSingleton<IInventoryStore>(sp => sp.GetRequiredService<InMemoryWmsStore>());
        services.AddSingleton<IUserStore>(sp => sp.GetRequiredService<InMemoryWmsStore>());
        services.AddSingleton<IRefreshTokenStore>(sp => sp.GetRequiredService<InMemoryWmsStore>());
        services.AddSingleton<IAuditLog>(sp => sp.GetRequiredService<InMemoryWmsStore>());
        services.AddSingleton<ISeedStore>(sp => sp.GetRequiredService<InMemoryWmsStore>());
        return services;
    }
}
