using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using System;
using System.IO;

namespace Nagi.Core.Data;

/// <summary>
/// This factory is used by the Entity Framework Core tools (e.g., for creating migrations)
/// at design time. It provides a way to create a DbContext instance without relying on the
/// main application's dependency injection container.
/// </summary>
public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<MusicDbContext> {
    public MusicDbContext CreateDbContext(string[] args) {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var appDataRoot = Path.Combine(localAppData, "Nagi");
        var databasePath = Path.Combine(appDataRoot, "nagi.db");
        Directory.CreateDirectory(appDataRoot);
        var optionsBuilder = new DbContextOptionsBuilder<MusicDbContext>();
        optionsBuilder.UseSqlite($"Data Source={databasePath}");
        return new MusicDbContext(optionsBuilder.Options);
    }
}