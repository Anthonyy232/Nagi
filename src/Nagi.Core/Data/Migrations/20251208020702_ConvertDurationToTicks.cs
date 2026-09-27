using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nagi.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class ConvertDurationToTicks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "DurationTicks",
                table: "Songs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.Sql("UPDATE Songs SET DurationTicks = nagi_duration_ticks(Duration);");
            migrationBuilder.DropColumn(name: "Duration", table: "Songs");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<TimeSpan>(
                name: "Duration",
                table: "Songs",
                type: "TEXT",
                nullable: false,
                defaultValue: new TimeSpan(0, 0, 0, 0, 0));

            migrationBuilder.Sql("UPDATE Songs SET Duration = nagi_duration_text(DurationTicks);");
            migrationBuilder.DropColumn(name: "DurationTicks", table: "Songs");
        }
    }
}
