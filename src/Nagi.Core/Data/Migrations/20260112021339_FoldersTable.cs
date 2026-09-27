using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nagi.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class FoldersTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The next migration reintroduces Order as REAL. Keep values across the schema gap.
            migrationBuilder.Sql("""
                CREATE TABLE __NagiPlaylistOrderBackup AS
                SELECT PlaylistId, SongId, "Order" FROM PlaylistSongs;
                """);

            migrationBuilder.DropIndex(
                name: "IX_PlaylistSongs_PlaylistId_Order",
                table: "PlaylistSongs");

            migrationBuilder.DropColumn(
                name: "Order",
                table: "PlaylistSongs");

            migrationBuilder.CreateIndex(
                name: "IX_Songs_Year",
                table: "Songs",
                column: "Year");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Songs_Year",
                table: "Songs");

            migrationBuilder.AddColumn<int>(
                name: "Order",
                table: "PlaylistSongs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.Sql("""
                CREATE TABLE IF NOT EXISTS __NagiPlaylistOrderBackup (PlaylistId TEXT, SongId TEXT, "Order" REAL);
                UPDATE PlaylistSongs SET "Order" = CAST(backup."Order" AS INTEGER)
                FROM __NagiPlaylistOrderBackup AS backup
                WHERE PlaylistSongs.PlaylistId = backup.PlaylistId AND PlaylistSongs.SongId = backup.SongId;
                DROP TABLE __NagiPlaylistOrderBackup;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_PlaylistSongs_PlaylistId_Order",
                table: "PlaylistSongs",
                columns: new[] { "PlaylistId", "Order" });
        }
    }
}
