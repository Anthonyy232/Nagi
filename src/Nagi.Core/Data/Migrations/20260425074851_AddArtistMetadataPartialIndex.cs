using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nagi.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddArtistMetadataPartialIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_Artists_MetadataLastCheckedUtc_NullOnly",
                table: "Artists",
                column: "MetadataLastCheckedUtc",
                filter: "\"MetadataLastCheckedUtc\" IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Artists_MetadataLastCheckedUtc_NullOnly",
                table: "Artists");
        }
    }
}
