using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nagi.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMusicServers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsLovedSyncPending",
                table: "Songs",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "RemoteCoverArtId",
                table: "Songs",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RemoteHash",
                table: "Songs",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RemoteId",
                table: "Songs",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsSubmittedToServer",
                table: "ListenHistory",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "MusicServers",
                columns: table => new
                {
                    FolderId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    BaseUrl = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    UserName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false, collation: "NOCASE"),
                    UserId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    UsesApiKey = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MusicServers", x => x.FolderId);
                    table.ForeignKey(
                        name: "FK_MusicServers_Folders_FolderId",
                        column: x => x.FolderId,
                        principalTable: "Folders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MusicServers");

            migrationBuilder.DropColumn(
                name: "IsLovedSyncPending",
                table: "Songs");

            migrationBuilder.DropColumn(
                name: "RemoteCoverArtId",
                table: "Songs");

            migrationBuilder.DropColumn(
                name: "RemoteHash",
                table: "Songs");

            migrationBuilder.DropColumn(
                name: "RemoteId",
                table: "Songs");

            migrationBuilder.DropColumn(
                name: "IsSubmittedToServer",
                table: "ListenHistory");
        }
    }
}
