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

            migrationBuilder.CreateIndex(
                name: "IX_PlaylistSongs_PlaylistId_Order",
                table: "PlaylistSongs",
                columns: new[] { "PlaylistId", "Order" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PlaylistSongs_PlaylistId_Order",
                table: "PlaylistSongs");

            migrationBuilder.DropColumn(
                name: "Order",
                table: "PlaylistSongs");
        }
    }
}
