using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.DependencyInjection;
using Nagi.Data;

namespace Nagi;

/// <summary>
/// This factory is used by the EF Core command-line tools (e.g., for migrations)
/// to create a DbContext instance. It configures only the necessary services,
/// avoiding any dependencies on the WinUI UI framework.
/// </summary>
public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<MusicDbContext> {
    public MusicDbContext CreateDbContext(string[] args) {
        var services = new ServiceCollection();
        App.ConfigureCoreServices(services);
        var serviceProvider = services.BuildServiceProvider();
        var dbContextFactory = serviceProvider.GetRequiredService<IDbContextFactory<MusicDbContext>>();
        return dbContextFactory.CreateDbContext();
    }
}