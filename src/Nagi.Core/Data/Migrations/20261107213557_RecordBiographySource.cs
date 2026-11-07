using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nagi.Core.Data.Migrations
{
    /// <summary>
    ///     Biographies from Wikipedia used to end with its license credit, in the UI language of the time. The source is
    ///     recorded now and the artist view shows the credit in the current language, so the stored credit is removed.
    /// </summary>
    public partial class RecordBiographySource : Migration
    {
        private const string EnglishCredit = "From Wikipedia, under the CC BY-SA license.";

        // Every wording the credit had in any locale of a release that added it (2.6.0 and later).
        private static readonly string[] Credits =
        [
            "A Wikipédiából, CC BY-SA licenc alatt.",
            "Allikas: Vikipeedia, CC BY-SA litsentsi alusel.",
            "Aus Wikipedia, unter der Lizenz CC BY-SA.",
            "Da Wikipédia, ao abrigo da licença CC BY-SA.",
            "Da Wikipedia, con licenza CC BY-SA.",
            "Da Wikipédia, sob a licença CC BY-SA.",
            "Dari Wikipedia, di bawah lisensi CC BY-SA.",
            "Daripada Wikipedia, di bawah lesen CC BY-SA.",
            "De la Viquipèdia, amb llicència CC BY-SA.",
            "De pe Wikipedia, sub licența CC BY-SA.",
            "De Wikipedia, bajo la licencia CC BY-SA.",
            "Extrait de Wikipédia, sous licence CC BY-SA.",
            "Fra Wikipedia under CC BY-SA-licensen.",
            "Fra Wikipedia, under CC BY-SA-lisensen.",
            "Från Wikipedia, under licensen CC BY-SA.",
            "From Wikipedia, under the CC BY-SA license.",
            "Iš Vikipedijos, pagal CC BY-SA licenciją.",
            "Kutoka Wikipedia, chini ya leseni ya CC BY-SA.",
            "Lähde: Wikipedia, CC BY-SA -lisenssi.",
            "Mula sa Wikipedia, sa ilalim ng lisensyang CC BY-SA.",
            "Nga Wikipedia, sipas licencës CC BY-SA.",
            "Ó Vicipéid, faoin gceadúnas CC BY-SA.",
            "S Wikipedije, pod licencom CC BY-SA.",
            "Từ Wikipedia, theo giấy phép CC BY-SA.",
            "Van Wikipedia, onder de CC BY-SA-licentie.",
            "Van Wikipedia, onder die CC BY-SA-lisensie.",
            "Vikipedi'den, CC BY-SA lisansı altında.",
            "Z Wikipedie, pod licencí CC BY-SA.",
            "Z Wikipédie, pod licenciou CC BY-SA.",
            "Z Wikipedii, na licencji CC BY-SA.",
            "Από τη Βικιπαίδεια, με άδεια CC BY-SA.",
            "З Вікіпедії, за ліцензією CC BY-SA.",
            "Из Википедии, по лицензии CC BY-SA.",
            "От Уикипедия, под лиценза CC BY-SA.",
            "Са Википедије, под лиценцом CC BY-SA.",
            "מתוך ויקיפדיה, תחת רישיון CC BY-SA.",
            "برگرفته از ویکی‌پدیا، تحت مجوز CC BY-SA.",
            "من ويكيبيديا، بموجب ترخيص CC BY-SA.",
            "विकिपीडिया से, CC BY-SA लाइसेंस के तहत।",
            "উইকিপিডিয়া থেকে, CC BY-SA লাইসেন্সের অধীনে।",
            "ਵਿਕੀਪੀਡੀਆ ਤੋਂ, CC BY-SA ਲਾਇਸੈਂਸ ਅਧੀਨ।",
            "விக்கிப்பீடியாவிலிருந்து, CC BY-SA உரிமத்தின் கீழ்.",
            "จากวิกิพีเดีย ภายใต้สัญญาอนุญาต CC BY-SA",
            "출처: 위키백과, CC BY-SA 라이선스에 따라 이용됩니다.",
            "內容取自維基百科，依 CC BY-SA 授權條款使用。",
            "内容来自维基百科，依据 CC BY-SA 许可协议使用。",
            "出典：ウィキペディア（CC BY-SA ライセンス）",
        ];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BiographySource",
                table: "Artists",
                type: "TEXT",
                maxLength: 100,
                nullable: true);

            foreach (var credit in Credits)
            {
                var suffix = CreditSuffix(credit);
                migrationBuilder.Sql($"""
                    UPDATE Artists
                    SET Biography = substr(Biography, 1, length(Biography) - length({suffix})), BiographySource = 'wikipedia'
                    WHERE substr(Biography, -length({suffix})) = {suffix};
                    """);
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Older versions show the biography as stored, so the credit goes back in the text.
            migrationBuilder.Sql($"UPDATE Artists SET Biography = Biography || {CreditSuffix(EnglishCredit)} WHERE BiographySource = 'wikipedia';");

            migrationBuilder.DropColumn(
                name: "BiographySource",
                table: "Artists");
        }

        private static string CreditSuffix(string credit) => $"char(10) || char(10) || '{credit.Replace("'", "''")}'";
    }
}
