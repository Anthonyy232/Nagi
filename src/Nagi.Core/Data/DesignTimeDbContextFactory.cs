using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Data.Sqlite;

namespace Nagi.Core.Data;

public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<MusicDbContext>
{
    public MusicDbContext CreateDbContext(string[] args)
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var appDataRoot = Path.Combine(localAppData, "Nagi");
        var databasePath = Path.Combine(appDataRoot, "nagi.db");
        Directory.CreateDirectory(appDataRoot);
        var optionsBuilder = new DbContextOptionsBuilder<MusicDbContext>();
        optionsBuilder.UseSqlite(new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString());
        return new MusicDbContext(optionsBuilder.Options);
    }
}
