using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nagi.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMultiArtistAndAlbumArtistDenorm : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Albums_Artists_ArtistId",
                table: "Albums");

            migrationBuilder.DropForeignKey(
                name: "FK_Songs_Artists_ArtistId",
                table: "Songs");

            migrationBuilder.AddColumn<string>(
                name: "ArtistName",
                table: "Songs",
                type: "TEXT",
                maxLength: 1000,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "PrimaryArtistName",
                table: "Songs",
                type: "TEXT",
                maxLength: 500,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ArtistName",
                table: "Albums",
                type: "TEXT",
                maxLength: 1000,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "PrimaryArtistName",
                table: "Albums",
                type: "TEXT",
                maxLength: 500,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "AlbumArtists",
                columns: table => new
                {
                    AlbumId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ArtistId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Order = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AlbumArtists", x => new { x.AlbumId, x.ArtistId });
                    table.ForeignKey(
                        name: "FK_AlbumArtists_Albums_AlbumId",
                        column: x => x.AlbumId,
                        principalTable: "Albums",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AlbumArtists_Artists_ArtistId",
                        column: x => x.ArtistId,
                        principalTable: "Artists",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SongArtists",
                columns: table => new
                {
                    SongId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ArtistId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Order = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SongArtists", x => new { x.SongId, x.ArtistId });
                    table.ForeignKey(
                        name: "FK_SongArtists_Artists_ArtistId",
                        column: x => x.ArtistId,
                        principalTable: "Artists",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SongArtists_Songs_SongId",
                        column: x => x.SongId,
                        principalTable: "Songs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            // --- DATA MIGRATION START ---
            
            // Migrate Songs -> SongArtists
            migrationBuilder.Sql(@"
                INSERT INTO SongArtists (SongId, ArtistId, [Order])
                SELECT Id, ArtistId, 0 FROM Songs WHERE ArtistId IS NOT NULL
            ");

            // Populate denormalized Song fields
            migrationBuilder.Sql(@"
                UPDATE Songs
                SET 
                    ArtistName = (SELECT Name FROM Artists WHERE Id = Songs.ArtistId),
                    PrimaryArtistName = (SELECT Name FROM Artists WHERE Id = Songs.ArtistId)
                WHERE ArtistId IS NOT NULL
            ");

            // Migrate Albums -> AlbumArtists
            migrationBuilder.Sql(@"
                INSERT INTO AlbumArtists (AlbumId, ArtistId, [Order])
                SELECT Id, ArtistId, 0 FROM Albums WHERE ArtistId IS NOT NULL
            ");

            // Populate denormalized Album fields
            migrationBuilder.Sql(@"
                UPDATE Albums
                SET 
                    ArtistName = (SELECT Name FROM Artists WHERE Id = Albums.ArtistId),
                    PrimaryArtistName = (SELECT Name FROM Artists WHERE Id = Albums.ArtistId)
                WHERE ArtistId IS NOT NULL
            ");

            // Set defaults for anything else (Unknown Artist)
            migrationBuilder.Sql(@"
                UPDATE Songs SET ArtistName = 'Unknown Artist', PrimaryArtistName = 'Unknown Artist' WHERE ArtistName = '' OR ArtistName IS NULL;
                UPDATE Albums SET ArtistName = 'Unknown Artist', PrimaryArtistName = 'Unknown Artist' WHERE ArtistName = '' OR ArtistName IS NULL;
            ");

            // --- DATA MIGRATION END ---

            migrationBuilder.DropIndex(
                name: "IX_Songs_ArtistId",
                table: "Songs");

            migrationBuilder.DropIndex(
                name: "IX_Albums_ArtistId",
                table: "Albums");

            migrationBuilder.DropIndex(
                name: "IX_Albums_Title_ArtistId",
                table: "Albums");

            migrationBuilder.DropColumn(
                name: "ArtistId",
                table: "Songs");

            migrationBuilder.DropColumn(
                name: "ArtistId",
                table: "Albums");

            migrationBuilder.CreateIndex(
                name: "IX_Songs_ArtistName",
                table: "Songs",
                column: "ArtistName");

            migrationBuilder.CreateIndex(
                name: "IX_Songs_PrimaryArtistName",
                table: "Songs",
                column: "PrimaryArtistName");

            migrationBuilder.CreateIndex(
                name: "IX_Albums_ArtistName",
                table: "Albums",
                column: "ArtistName");

            migrationBuilder.CreateIndex(
                name: "IX_Albums_PrimaryArtistName",
                table: "Albums",
                column: "PrimaryArtistName");

            migrationBuilder.CreateIndex(
                name: "IX_AlbumArtists_AlbumId_Order",
                table: "AlbumArtists",
                columns: new[] { "AlbumId", "Order" });

            migrationBuilder.CreateIndex(
                name: "IX_AlbumArtists_ArtistId",
                table: "AlbumArtists",
                column: "ArtistId");

            migrationBuilder.CreateIndex(
                name: "IX_SongArtists_ArtistId",
                table: "SongArtists",
                column: "ArtistId");

            migrationBuilder.CreateIndex(
                name: "IX_SongArtists_SongId_Order",
                table: "SongArtists",
                columns: new[] { "SongId", "Order" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AlbumArtists");

            migrationBuilder.DropTable(
                name: "SongArtists");

            migrationBuilder.DropIndex(
                name: "IX_Songs_ArtistName",
                table: "Songs");

            migrationBuilder.DropIndex(
                name: "IX_Songs_PrimaryArtistName",
                table: "Songs");

            migrationBuilder.DropIndex(
                name: "IX_Albums_ArtistName",
                table: "Albums");

            migrationBuilder.DropIndex(
                name: "IX_Albums_PrimaryArtistName",
                table: "Albums");

            migrationBuilder.DropColumn(
                name: "ArtistName",
                table: "Songs");

            migrationBuilder.DropColumn(
                name: "PrimaryArtistName",
                table: "Songs");

            migrationBuilder.DropColumn(
                name: "ArtistName",
                table: "Albums");

            migrationBuilder.DropColumn(
                name: "PrimaryArtistName",
                table: "Albums");

            migrationBuilder.AddColumn<Guid>(
                name: "ArtistId",
                table: "Songs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ArtistId",
                table: "Albums",
                type: "TEXT",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.CreateIndex(
                name: "IX_Songs_ArtistId",
                table: "Songs",
                column: "ArtistId");

            migrationBuilder.CreateIndex(
                name: "IX_Albums_ArtistId",
                table: "Albums",
                column: "ArtistId");

            migrationBuilder.CreateIndex(
                name: "IX_Albums_Title_ArtistId",
                table: "Albums",
                columns: new[] { "Title", "ArtistId" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Albums_Artists_ArtistId",
                table: "Albums",
                column: "ArtistId",
                principalTable: "Artists",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_Songs_Artists_ArtistId",
                table: "Songs",
                column: "ArtistId",
                principalTable: "Artists",
                principalColumn: "Id");
        }
    }
}
