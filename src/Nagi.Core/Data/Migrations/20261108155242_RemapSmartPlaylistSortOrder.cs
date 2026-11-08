using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nagi.Core.Data.Migrations
{
    /// <summary>
    ///     Smart playlists stored their sort as a copy of the song sort enum that listed artist before album and track
    ///     number after date added. They now store the song sort itself, so the saved numbers move to its members.
    /// </summary>
    public partial class RemapSmartPlaylistSortOrder : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE SmartPlaylists SET SortOrder = CASE SortOrder
                    WHEN 2 THEN 4 WHEN 3 THEN 5 WHEN 4 THEN 2 WHEN 5 THEN 3
                    WHEN 6 THEN 8 WHEN 7 THEN 9 WHEN 8 THEN 10 WHEN 9 THEN 11
                    WHEN 10 THEN 12 WHEN 11 THEN 13 WHEN 12 THEN 14 WHEN 13 THEN 15
                    WHEN 14 THEN 6 WHEN 15 THEN 7
                    ELSE SortOrder
                END;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE SmartPlaylists SET SortOrder = CASE SortOrder
                    WHEN 4 THEN 2 WHEN 5 THEN 3 WHEN 2 THEN 4 WHEN 3 THEN 5
                    WHEN 8 THEN 6 WHEN 9 THEN 7 WHEN 10 THEN 8 WHEN 11 THEN 9
                    WHEN 12 THEN 10 WHEN 13 THEN 11 WHEN 14 THEN 12 WHEN 15 THEN 13
                    WHEN 6 THEN 14 WHEN 7 THEN 15 WHEN 23 THEN 0
                    ELSE SortOrder
                END;
                """);
        }
    }
}
