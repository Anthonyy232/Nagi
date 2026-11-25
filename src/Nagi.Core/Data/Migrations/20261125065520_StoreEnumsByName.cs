using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nagi.Core.Data.Migrations
{
    /// <summary>
    ///     Enums move from their member numbers to their names.
    /// </summary>
    public partial class StoreEnumsByName : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "SortOrder",
                table: "SmartPlaylists",
                type: "TEXT",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "INTEGER");

            migrationBuilder.AlterColumn<string>(
                name: "Field",
                table: "SmartPlaylistRules",
                type: "TEXT",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "INTEGER");

            migrationBuilder.AlterColumn<string>(
                name: "Operator",
                table: "SmartPlaylistRules",
                type: "TEXT",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "INTEGER");

            migrationBuilder.AlterColumn<string>(
                name: "Kind",
                table: "MusicServers",
                type: "TEXT",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "INTEGER");

            migrationBuilder.AlterColumn<string>(
                name: "EndReason",
                table: "ListenHistory",
                type: "TEXT",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "INTEGER");

            migrationBuilder.AlterColumn<string>(
                name: "ContextType",
                table: "ListenHistory",
                type: "TEXT",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "INTEGER");

            migrationBuilder.Sql("""
                UPDATE SmartPlaylists SET SortOrder = CASE SortOrder
                        WHEN 0 THEN 'TitleAsc' WHEN 1 THEN 'TitleDesc' WHEN 2 THEN 'AlbumAsc' WHEN 3 THEN 'AlbumDesc'
                        WHEN 4 THEN 'ArtistAsc' WHEN 5 THEN 'ArtistDesc' WHEN 6 THEN 'TrackNumberAsc' WHEN 7 THEN 'TrackNumberDesc'
                        WHEN 8 THEN 'YearAsc' WHEN 9 THEN 'YearDesc' WHEN 10 THEN 'PlayCountAsc' WHEN 11 THEN 'PlayCountDesc'
                        WHEN 12 THEN 'LastPlayedAsc' WHEN 13 THEN 'LastPlayedDesc' WHEN 14 THEN 'DateAddedAsc' WHEN 15 THEN 'DateAddedDesc'
                        WHEN 16 THEN 'DurationAsc' WHEN 17 THEN 'DurationDesc' WHEN 18 THEN 'BpmAsc' WHEN 19 THEN 'BpmDesc'
                        WHEN 20 THEN 'FileCreatedDateAsc' WHEN 21 THEN 'FileCreatedDateDesc' WHEN 22 THEN 'Random' WHEN 23 THEN 'PlaylistOrder'
                        WHEN 24 THEN 'FileModifiedDateAsc' WHEN 25 THEN 'FileModifiedDateDesc'
                    END;
                UPDATE SmartPlaylistRules SET Field = CASE Field
                        WHEN 0 THEN 'Title' WHEN 1 THEN 'Artist' WHEN 2 THEN 'Album' WHEN 3 THEN 'Genre'
                        WHEN 4 THEN 'Composer' WHEN 5 THEN 'Comment' WHEN 6 THEN 'Grouping' WHEN 7 THEN 'PlayCount'
                        WHEN 8 THEN 'SkipCount' WHEN 9 THEN 'Rating' WHEN 10 THEN 'Year' WHEN 11 THEN 'TrackNumber'
                        WHEN 12 THEN 'DiscNumber' WHEN 13 THEN 'Bpm' WHEN 14 THEN 'Duration' WHEN 15 THEN 'Bitrate'
                        WHEN 16 THEN 'SampleRate' WHEN 17 THEN 'IsLoved' WHEN 18 THEN 'HasLyrics' WHEN 19 THEN 'DateAdded'
                        WHEN 20 THEN 'LastPlayed' WHEN 21 THEN 'FileCreatedDate' WHEN 22 THEN 'FileModifiedDate'
                    END,
                    Operator = CASE Operator
                        WHEN 0 THEN 'Is' WHEN 1 THEN 'IsNot' WHEN 2 THEN 'Contains' WHEN 3 THEN 'DoesNotContain'
                        WHEN 4 THEN 'StartsWith' WHEN 5 THEN 'EndsWith' WHEN 6 THEN 'Equals' WHEN 7 THEN 'NotEquals'
                        WHEN 8 THEN 'GreaterThan' WHEN 9 THEN 'LessThan' WHEN 10 THEN 'GreaterThanOrEqual' WHEN 11 THEN 'LessThanOrEqual'
                        WHEN 12 THEN 'IsInRange' WHEN 13 THEN 'IsInTheLast' WHEN 14 THEN 'IsNotInTheLast' WHEN 15 THEN 'IsTrue'
                        WHEN 16 THEN 'IsFalse'
                    END;
                UPDATE MusicServers SET Kind = CASE Kind
                        WHEN 0 THEN 'OpenSubsonic' WHEN 1 THEN 'Jellyfin'
                    END;
                UPDATE ListenHistory SET EndReason = CASE EndReason
                        WHEN 0 THEN 'Finished' WHEN 1 THEN 'Skipped' WHEN 2 THEN 'PausedAndAbandoned'
                    END,
                    ContextType = CASE ContextType
                        WHEN 0 THEN 'Album' WHEN 1 THEN 'Artist' WHEN 2 THEN 'Playlist' WHEN 3 THEN 'SmartPlaylist'
                        WHEN 4 THEN 'Folder' WHEN 5 THEN 'Genre' WHEN 6 THEN 'Search' WHEN 7 THEN 'Library'
                        WHEN 8 THEN 'Transient'
                    END;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE SmartPlaylists SET SortOrder = CASE SortOrder
                        WHEN 'TitleAsc' THEN 0 WHEN 'TitleDesc' THEN 1 WHEN 'AlbumAsc' THEN 2 WHEN 'AlbumDesc' THEN 3
                        WHEN 'ArtistAsc' THEN 4 WHEN 'ArtistDesc' THEN 5 WHEN 'TrackNumberAsc' THEN 6 WHEN 'TrackNumberDesc' THEN 7
                        WHEN 'YearAsc' THEN 8 WHEN 'YearDesc' THEN 9 WHEN 'PlayCountAsc' THEN 10 WHEN 'PlayCountDesc' THEN 11
                        WHEN 'LastPlayedAsc' THEN 12 WHEN 'LastPlayedDesc' THEN 13 WHEN 'DateAddedAsc' THEN 14 WHEN 'DateAddedDesc' THEN 15
                        WHEN 'DurationAsc' THEN 16 WHEN 'DurationDesc' THEN 17 WHEN 'BpmAsc' THEN 18 WHEN 'BpmDesc' THEN 19
                        WHEN 'FileCreatedDateAsc' THEN 20 WHEN 'FileCreatedDateDesc' THEN 21 WHEN 'Random' THEN 22 WHEN 'PlaylistOrder' THEN 23
                        WHEN 'FileModifiedDateAsc' THEN 24 WHEN 'FileModifiedDateDesc' THEN 25
                    END;
                UPDATE SmartPlaylistRules SET Field = CASE Field
                        WHEN 'Title' THEN 0 WHEN 'Artist' THEN 1 WHEN 'Album' THEN 2 WHEN 'Genre' THEN 3
                        WHEN 'Composer' THEN 4 WHEN 'Comment' THEN 5 WHEN 'Grouping' THEN 6 WHEN 'PlayCount' THEN 7
                        WHEN 'SkipCount' THEN 8 WHEN 'Rating' THEN 9 WHEN 'Year' THEN 10 WHEN 'TrackNumber' THEN 11
                        WHEN 'DiscNumber' THEN 12 WHEN 'Bpm' THEN 13 WHEN 'Duration' THEN 14 WHEN 'Bitrate' THEN 15
                        WHEN 'SampleRate' THEN 16 WHEN 'IsLoved' THEN 17 WHEN 'HasLyrics' THEN 18 WHEN 'DateAdded' THEN 19
                        WHEN 'LastPlayed' THEN 20 WHEN 'FileCreatedDate' THEN 21 WHEN 'FileModifiedDate' THEN 22
                    END,
                    Operator = CASE Operator
                        WHEN 'Is' THEN 0 WHEN 'IsNot' THEN 1 WHEN 'Contains' THEN 2 WHEN 'DoesNotContain' THEN 3
                        WHEN 'StartsWith' THEN 4 WHEN 'EndsWith' THEN 5 WHEN 'Equals' THEN 6 WHEN 'NotEquals' THEN 7
                        WHEN 'GreaterThan' THEN 8 WHEN 'LessThan' THEN 9 WHEN 'GreaterThanOrEqual' THEN 10 WHEN 'LessThanOrEqual' THEN 11
                        WHEN 'IsInRange' THEN 12 WHEN 'IsInTheLast' THEN 13 WHEN 'IsNotInTheLast' THEN 14 WHEN 'IsTrue' THEN 15
                        WHEN 'IsFalse' THEN 16
                    END;
                UPDATE MusicServers SET Kind = CASE Kind
                        WHEN 'OpenSubsonic' THEN 0 WHEN 'Jellyfin' THEN 1
                    END;
                UPDATE ListenHistory SET EndReason = CASE EndReason
                        WHEN 'Finished' THEN 0 WHEN 'Skipped' THEN 1 WHEN 'PausedAndAbandoned' THEN 2
                    END,
                    ContextType = CASE ContextType
                        WHEN 'Album' THEN 0 WHEN 'Artist' THEN 1 WHEN 'Playlist' THEN 2 WHEN 'SmartPlaylist' THEN 3
                        WHEN 'Folder' THEN 4 WHEN 'Genre' THEN 5 WHEN 'Search' THEN 6 WHEN 'Library' THEN 7
                        WHEN 'Transient' THEN 8
                    END;
                """);

            migrationBuilder.AlterColumn<int>(
                name: "SortOrder",
                table: "SmartPlaylists",
                type: "INTEGER",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "TEXT");

            migrationBuilder.AlterColumn<int>(
                name: "Field",
                table: "SmartPlaylistRules",
                type: "INTEGER",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "TEXT");

            migrationBuilder.AlterColumn<int>(
                name: "Operator",
                table: "SmartPlaylistRules",
                type: "INTEGER",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "TEXT");

            migrationBuilder.AlterColumn<int>(
                name: "Kind",
                table: "MusicServers",
                type: "INTEGER",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "TEXT");

            migrationBuilder.AlterColumn<int>(
                name: "EndReason",
                table: "ListenHistory",
                type: "INTEGER",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "TEXT");

            migrationBuilder.AlterColumn<int>(
                name: "ContextType",
                table: "ListenHistory",
                type: "INTEGER",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "TEXT");
        }
    }
}
