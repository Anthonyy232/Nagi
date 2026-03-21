using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nagi.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class UpdateMusicModel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_ListenHistory_ListenTimestampUtc_SongId",
                table: "ListenHistory",
                columns: new[] { "ListenTimestampUtc", "SongId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ListenHistory_ListenTimestampUtc_SongId",
                table: "ListenHistory");
        }
    }
}
