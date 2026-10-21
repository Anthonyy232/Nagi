using FluentAssertions;
using Microsoft.Data.Sqlite;
using Nagi.Presentation.Services;
using Xunit;

namespace Nagi.Presentation.Tests;

public class AppDataResetTests
{
    [Fact]
    public void DeleteDatabaseFiles_ReleasesPooledDatabaseAndRemovesJournals()
    {
        var directory = Directory.CreateTempSubdirectory("Nagi-reset-");
        var path = Path.Combine(directory.FullName, "nagi.db");
        try
        {
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                   { DataSource = path, Pooling = true }.ToString()))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "PRAGMA journal_mode=WAL; CREATE TABLE SavedData (Value TEXT); INSERT INTO SavedData VALUES ('old');";
                command.ExecuteNonQuery();
            }
            File.WriteAllText(path + "-journal", "old rollback journal");

            AppDataReset.DeleteDatabaseFiles(path);

            Directory.GetFiles(directory.FullName).Should().BeEmpty();
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            directory.Delete(true);
        }
    }

    [Fact]
    public void DeleteDatabaseFiles_KeepsJournalsWhenDatabaseCannotBeDeleted()
    {
        var directory = Directory.CreateTempSubdirectory("Nagi-reset-");
        var path = Path.Combine(directory.FullName, "nagi.db");
        try
        {
            File.WriteAllText(path, "locked database");
            foreach (var suffix in new[] { "-wal", "-shm", "-journal" })
                File.WriteAllText(path + suffix, suffix);
            using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None))
                AppDataReset.DeleteDatabaseFiles(path);

            File.ReadAllText(path).Should().Be("locked database");
            foreach (var suffix in new[] { "-wal", "-shm", "-journal" })
                File.ReadAllText(path + suffix).Should().Be(suffix);
        }
        finally { directory.Delete(true); }
    }
}
