using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nagi.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class ListenHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "TotalListenTimeTicks",
                table: "Songs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<Guid>(
                name: "ContextId",
                table: "ListenHistory",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ContextType",
                table: "ListenHistory",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "EndReason",
                table: "ListenHistory",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<long>(
                name: "ListenDurationTicks",
                table: "ListenHistory",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateIndex(
                name: "IX_Songs_TotalListenTimeTicks",
                table: "Songs",
                column: "TotalListenTimeTicks");

            migrationBuilder.CreateIndex(
                name: "IX_ListenHistory_ContextType_ContextId",
                table: "ListenHistory",
                columns: new[] { "ContextType", "ContextId" });

            migrationBuilder.CreateIndex(
                name: "IX_ListenHistory_EndReason",
                table: "ListenHistory",
                column: "EndReason");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Songs_TotalListenTimeTicks",
                table: "Songs");

            migrationBuilder.DropIndex(
                name: "IX_ListenHistory_ContextType_ContextId",
                table: "ListenHistory");

            migrationBuilder.DropIndex(
                name: "IX_ListenHistory_EndReason",
                table: "ListenHistory");

            migrationBuilder.DropColumn(
                name: "TotalListenTimeTicks",
                table: "Songs");

            migrationBuilder.DropColumn(
                name: "ContextId",
                table: "ListenHistory");

            migrationBuilder.DropColumn(
                name: "ContextType",
                table: "ListenHistory");

            migrationBuilder.DropColumn(
                name: "EndReason",
                table: "ListenHistory");

            migrationBuilder.DropColumn(
                name: "ListenDurationTicks",
                table: "ListenHistory");
        }
    }
}
