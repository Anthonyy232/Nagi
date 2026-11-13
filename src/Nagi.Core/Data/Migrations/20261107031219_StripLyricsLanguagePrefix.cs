using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nagi.Core.Data.Migrations
{
    /// <summary>
    ///     Lyrics read from tags used to keep the language prefix ATL puts on them, as in "eng||", and the lyrics page
    ///     stripped it for display. The tag reader now strips it, so stored lyrics lose it too.
    /// </summary>
    public partial class StripLyricsLanguagePrefix : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE Songs SET Lyrics = CASE
                    WHEN Lyrics GLOB '[A-Za-z][A-Za-z]||*' THEN substr(Lyrics, 5)
                    ELSE substr(Lyrics, 6)
                END
                WHERE Lyrics GLOB '[A-Za-z][A-Za-z]||*' OR Lyrics GLOB '[A-Za-z][A-Za-z][A-Za-z]||*';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The prefix only named the language and isn't needed to read the lyrics, so it isn't put back.
        }
    }
}
