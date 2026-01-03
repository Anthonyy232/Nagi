using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nagi.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMoreMetadataArtist : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "MusicBrainzId",
                table: "Artists",
                type: "TEXT",
                maxLength: 100,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MusicBrainzId",
                table: "Artists");
        }
    }
}
