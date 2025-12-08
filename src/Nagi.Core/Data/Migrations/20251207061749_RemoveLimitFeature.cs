using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nagi.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class RemoveLimitFeature : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LimitBy",
                table: "SmartPlaylists");

            migrationBuilder.DropColumn(
                name: "LiveUpdating",
                table: "SmartPlaylists");

            migrationBuilder.DropColumn(
                name: "SongLimit",
                table: "SmartPlaylists");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "LimitBy",
                table: "SmartPlaylists",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "LiveUpdating",
                table: "SmartPlaylists",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "SongLimit",
                table: "SmartPlaylists",
                type: "INTEGER",
                nullable: true);
        }
    }
}
