using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nagi.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddArtistImageModified : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ImageModifiedUtc",
                table: "Artists",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ImageModifiedUtc",
                table: "Artists");
        }
    }
}
