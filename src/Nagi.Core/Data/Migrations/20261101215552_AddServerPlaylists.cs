using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nagi.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddServerPlaylists : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Playlists_Name",
                table: "Playlists");

            migrationBuilder.AddColumn<string>(
                name: "RemoteId",
                table: "Playlists",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ServerFolderId",
                table: "Playlists",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Playlists_Name",
                table: "Playlists",
                column: "Name",
                unique: true,
                filter: "\"ServerFolderId\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Playlists_ServerFolderId_RemoteId",
                table: "Playlists",
                columns: new[] { "ServerFolderId", "RemoteId" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Playlists_Folders_ServerFolderId",
                table: "Playlists",
                column: "ServerFolderId",
                principalTable: "Folders",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Playlists_Folders_ServerFolderId",
                table: "Playlists");

            migrationBuilder.DropIndex(
                name: "IX_Playlists_Name",
                table: "Playlists");

            migrationBuilder.DropIndex(
                name: "IX_Playlists_ServerFolderId_RemoteId",
                table: "Playlists");

            migrationBuilder.DropColumn(
                name: "RemoteId",
                table: "Playlists");

            migrationBuilder.DropColumn(
                name: "ServerFolderId",
                table: "Playlists");

            migrationBuilder.CreateIndex(
                name: "IX_Playlists_Name",
                table: "Playlists",
                column: "Name",
                unique: true);
        }
    }
}
