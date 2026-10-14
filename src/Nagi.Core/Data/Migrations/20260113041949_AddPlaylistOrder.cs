using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nagi.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPlaylistOrder : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "Order",
                table: "PlaylistSongs",
                type: "REAL",
                nullable: false,
                defaultValue: 0.0);

            // Older app versions may already have applied FoldersTable without a backup.
            migrationBuilder.Sql("""
                CREATE TABLE IF NOT EXISTS __NagiPlaylistOrderBackup (PlaylistId TEXT, SongId TEXT, "Order" REAL);
                UPDATE PlaylistSongs SET "Order" = backup."Order"
                FROM __NagiPlaylistOrderBackup AS backup
                WHERE PlaylistSongs.PlaylistId = backup.PlaylistId AND PlaylistSongs.SongId = backup.SongId;
                DROP TABLE __NagiPlaylistOrderBackup;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_PlaylistSongs_PlaylistId_Order",
                table: "PlaylistSongs",
                columns: new[] { "PlaylistId", "Order" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
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
        }
    }
}
