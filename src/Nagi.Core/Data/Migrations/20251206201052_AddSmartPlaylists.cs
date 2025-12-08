using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nagi.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSmartPlaylists : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SmartPlaylists",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false, collation: "NOCASE"),
                    DateCreated = table.Column<DateTime>(type: "TEXT", nullable: false),
                    DateModified = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 5000, nullable: true),
                    CoverImageUri = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    MatchAllRules = table.Column<bool>(type: "INTEGER", nullable: false),
                    SongLimit = table.Column<int>(type: "INTEGER", nullable: true),
                    LimitBy = table.Column<int>(type: "INTEGER", nullable: false),

                    LiveUpdating = table.Column<bool>(type: "INTEGER", nullable: false),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SmartPlaylists", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SmartPlaylistRules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SmartPlaylistId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Field = table.Column<int>(type: "INTEGER", nullable: false),
                    Operator = table.Column<int>(type: "INTEGER", nullable: false),
                    Value = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    SecondValue = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    Order = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SmartPlaylistRules", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SmartPlaylistRules_SmartPlaylists_SmartPlaylistId",
                        column: x => x.SmartPlaylistId,
                        principalTable: "SmartPlaylists",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SmartPlaylistRules_SmartPlaylistId_Order",
                table: "SmartPlaylistRules",
                columns: new[] { "SmartPlaylistId", "Order" });

            migrationBuilder.CreateIndex(
                name: "IX_SmartPlaylists_Name",
                table: "SmartPlaylists",
                column: "Name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SmartPlaylistRules");

            migrationBuilder.DropTable(
                name: "SmartPlaylists");
        }
    }
}
