using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nagi.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddIndexToDatabase : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_Songs_AlbumId_DiscNumber_TrackNumber",
                table: "Songs",
                columns: new[] { "AlbumId", "DiscNumber", "TrackNumber" });

            migrationBuilder.CreateIndex(
                name: "IX_Songs_DateAddedToLibrary",
                table: "Songs",
                column: "DateAddedToLibrary");

            migrationBuilder.CreateIndex(
                name: "IX_Songs_IsLoved",
                table: "Songs",
                column: "IsLoved");

            migrationBuilder.CreateIndex(
                name: "IX_Songs_PlayCount",
                table: "Songs",
                column: "PlayCount");

            migrationBuilder.CreateIndex(
                name: "IX_ListenHistory_IsScrobbled",
                table: "ListenHistory",
                column: "IsScrobbled");

            migrationBuilder.CreateIndex(
                name: "IX_Albums_Year",
                table: "Albums",
                column: "Year");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Songs_AlbumId_DiscNumber_TrackNumber",
                table: "Songs");

            migrationBuilder.DropIndex(
                name: "IX_Songs_DateAddedToLibrary",
                table: "Songs");

            migrationBuilder.DropIndex(
                name: "IX_Songs_IsLoved",
                table: "Songs");

            migrationBuilder.DropIndex(
                name: "IX_Songs_PlayCount",
                table: "Songs");

            migrationBuilder.DropIndex(
                name: "IX_ListenHistory_IsScrobbled",
                table: "ListenHistory");

            migrationBuilder.DropIndex(
                name: "IX_Albums_Year",
                table: "Albums");
        }
    }
}
