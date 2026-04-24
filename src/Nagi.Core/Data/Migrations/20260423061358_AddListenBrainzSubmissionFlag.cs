using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nagi.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddListenBrainzSubmissionFlag : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsSubmittedToListenBrainz",
                table: "ListenHistory",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsSubmittedToListenBrainz",
                table: "ListenHistory");
        }
    }
}
