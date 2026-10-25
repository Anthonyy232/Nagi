using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nagi.Core.Data.Migrations
{
    /// <summary>
    ///     Gives artists still missing a biography or image another lookup, now that Wikipedia and Deezer are sources.
    /// </summary>
    public partial class RecheckIncompleteArtistMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                UPDATE Artists
                SET MetadataLastCheckedUtc = NULL
                WHERE Biography IS NULL OR LocalImageCachePath IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
