using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nagi.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddReplayGainFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "ReplayGainTrackGain",
                table: "Songs",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "ReplayGainTrackPeak",
                table: "Songs",
                type: "REAL",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ReplayGainTrackGain",
                table: "Songs");

            migrationBuilder.DropColumn(
                name: "ReplayGainTrackPeak",
                table: "Songs");
        }
    }
}
