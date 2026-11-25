using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nagi.Core.Data.Migrations;

/// <inheritdoc />
public partial class _20261125071358_AddSongTagsAndPinnedViews : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "Tags",
            table: "Songs",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<bool>(
            name: "IsPinned",
            table: "SmartPlaylists",
            type: "INTEGER",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<string>(
            name: "SortTag",
            table: "SmartPlaylists",
            type: "TEXT",
            maxLength: 200,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "TagName",
            table: "SmartPlaylistRules",
            type: "TEXT",
            maxLength: 200,
            nullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "Tags",
            table: "Songs");

        migrationBuilder.DropColumn(
            name: "IsPinned",
            table: "SmartPlaylists");

        migrationBuilder.DropColumn(
            name: "SortTag",
            table: "SmartPlaylists");

        migrationBuilder.DropColumn(
            name: "TagName",
            table: "SmartPlaylistRules");
    }
}
