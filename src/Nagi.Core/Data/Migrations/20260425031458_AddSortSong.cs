using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nagi.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSortSong : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PrimaryArtistSortName",
                table: "Songs",
                type: "TEXT",
                maxLength: 500,
                nullable: false,
                defaultValue: "",
                collation: "NOCASE");

            migrationBuilder.AddColumn<string>(
                name: "SortTitle",
                table: "Songs",
                type: "TEXT",
                maxLength: 500,
                nullable: false,
                defaultValue: "",
                collation: "NOCASE");

            migrationBuilder.AlterColumn<string>(
                name: "SortName",
                table: "Artists",
                type: "TEXT",
                maxLength: 500,
                nullable: false,
                collation: "NOCASE",
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldMaxLength: 500);

            migrationBuilder.AlterColumn<string>(
                name: "SortTitle",
                table: "Albums",
                type: "TEXT",
                maxLength: 500,
                nullable: false,
                collation: "NOCASE",
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldMaxLength: 500);

            migrationBuilder.AlterColumn<string>(
                name: "PrimaryArtistSortName",
                table: "Albums",
                type: "TEXT",
                maxLength: 500,
                nullable: false,
                collation: "NOCASE",
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldMaxLength: 500);

            migrationBuilder.CreateIndex(
                name: "IX_Songs_PrimaryArtistSortName",
                table: "Songs",
                column: "PrimaryArtistSortName");

            migrationBuilder.CreateIndex(
                name: "IX_Songs_SortTitle",
                table: "Songs",
                column: "SortTitle");

            migrationBuilder.CreateIndex(
                name: "IX_Artists_SortName",
                table: "Artists",
                column: "SortName");

            migrationBuilder.CreateIndex(
                name: "IX_Albums_PrimaryArtistSortName",
                table: "Albums",
                column: "PrimaryArtistSortName");

            migrationBuilder.CreateIndex(
                name: "IX_Albums_SortTitle",
                table: "Albums",
                column: "SortTitle");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Songs_PrimaryArtistSortName",
                table: "Songs");

            migrationBuilder.DropIndex(
                name: "IX_Songs_SortTitle",
                table: "Songs");

            migrationBuilder.DropIndex(
                name: "IX_Artists_SortName",
                table: "Artists");

            migrationBuilder.DropIndex(
                name: "IX_Albums_PrimaryArtistSortName",
                table: "Albums");

            migrationBuilder.DropIndex(
                name: "IX_Albums_SortTitle",
                table: "Albums");

            migrationBuilder.DropColumn(
                name: "PrimaryArtistSortName",
                table: "Songs");

            migrationBuilder.DropColumn(
                name: "SortTitle",
                table: "Songs");

            migrationBuilder.AlterColumn<string>(
                name: "SortName",
                table: "Artists",
                type: "TEXT",
                maxLength: 500,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldMaxLength: 500,
                oldCollation: "NOCASE");

            migrationBuilder.AlterColumn<string>(
                name: "SortTitle",
                table: "Albums",
                type: "TEXT",
                maxLength: 500,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldMaxLength: 500,
                oldCollation: "NOCASE");

            migrationBuilder.AlterColumn<string>(
                name: "PrimaryArtistSortName",
                table: "Albums",
                type: "TEXT",
                maxLength: 500,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldMaxLength: 500,
                oldCollation: "NOCASE");
        }
    }
}
