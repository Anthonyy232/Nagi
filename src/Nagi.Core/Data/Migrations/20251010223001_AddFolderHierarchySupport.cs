using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nagi.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddFolderHierarchySupport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DirectoryPath",
                table: "Songs",
                type: "TEXT",
                nullable: false,
                defaultValue: "",
                collation: "NOCASE");

            migrationBuilder.AddColumn<Guid>(
                name: "ParentFolderId",
                table: "Folders",
                type: "TEXT",
                nullable: true);

            migrationBuilder.Sql(@"
                UPDATE Songs 
                SET DirectoryPath = FilePath
                WHERE DirectoryPath = '' OR DirectoryPath IS NULL
            ");

            migrationBuilder.CreateIndex(
                name: "IX_Songs_DirectoryPath",
                table: "Songs",
                column: "DirectoryPath");

            migrationBuilder.CreateIndex(
                name: "IX_Songs_FolderId_DirectoryPath",
                table: "Songs",
                columns: new[] { "FolderId", "DirectoryPath" });

            migrationBuilder.CreateIndex(
                name: "IX_Folders_ParentFolderId",
                table: "Folders",
                column: "ParentFolderId");

            migrationBuilder.AddForeignKey(
                name: "FK_Folders_Folders_ParentFolderId",
                table: "Folders",
                column: "ParentFolderId",
                principalTable: "Folders",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Folders_Folders_ParentFolderId",
                table: "Folders");

            migrationBuilder.DropIndex(
                name: "IX_Songs_DirectoryPath",
                table: "Songs");

            migrationBuilder.DropIndex(
                name: "IX_Songs_FolderId_DirectoryPath",
                table: "Songs");

            migrationBuilder.DropIndex(
                name: "IX_Folders_ParentFolderId",
                table: "Folders");

            migrationBuilder.DropColumn(
                name: "DirectoryPath",
                table: "Songs");

            migrationBuilder.DropColumn(
                name: "ParentFolderId",
                table: "Folders");
        }
    }
}
