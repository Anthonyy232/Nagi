using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nagi.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class OptimizeSongTitleSortIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Songs_SortTitle",
                table: "Songs");

            migrationBuilder.DropIndex(
                name: "IX_Songs_Title",
                table: "Songs");

            migrationBuilder.CreateIndex(
                name: "IX_Songs_SortTitle_PrimaryArtistSortName_Id",
                table: "Songs",
                columns: new[] { "SortTitle", "PrimaryArtistSortName", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_Songs_Title_PrimaryArtistName_Id",
                table: "Songs",
                columns: new[] { "Title", "PrimaryArtistName", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Songs_SortTitle_PrimaryArtistSortName_Id",
                table: "Songs");

            migrationBuilder.DropIndex(
                name: "IX_Songs_Title_PrimaryArtistName_Id",
                table: "Songs");

            migrationBuilder.CreateIndex(
                name: "IX_Songs_SortTitle",
                table: "Songs",
                column: "SortTitle");

            migrationBuilder.CreateIndex(
                name: "IX_Songs_Title",
                table: "Songs",
                column: "Title");
        }
    }
}
