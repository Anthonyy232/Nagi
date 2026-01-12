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

            migrationBuilder.CreateIndex(
                name: "IX_PlaylistSongs_PlaylistId_Order",
                table: "PlaylistSongs",
                columns: new[] { "PlaylistId", "Order" });
        }
    }
}
