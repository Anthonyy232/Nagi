using System.Security.Cryptography;
using System.Text;
using Nagi.Presentation.Models;

namespace Nagi.Presentation.Helpers;

/// <summary>
///     The gradient a genre is shown with. It is picked from a curated palette by a hash of the name, so a genre keeps
///     its colors across runs.
/// </summary>
public static class GenreGradients
{
    private static readonly (ArgbColor Start, ArgbColor End)[] Palettes =
    [
        (ArgbColor.FromRgb(142, 45, 226), ArgbColor.FromRgb(74, 0, 224)),
        (ArgbColor.FromRgb(214, 109, 255), ArgbColor.FromRgb(92, 3, 214)),
        (ArgbColor.FromRgb(255, 95, 109), ArgbColor.FromRgb(255, 195, 113)),
        (ArgbColor.FromRgb(221, 94, 221), ArgbColor.FromRgb(115, 54, 242)),
        (ArgbColor.FromRgb(233, 30, 99), ArgbColor.FromRgb(156, 39, 176)),
        (ArgbColor.FromRgb(1, 200, 255), ArgbColor.FromRgb(4, 105, 255)),
        (ArgbColor.FromRgb(33, 150, 243), ArgbColor.FromRgb(3, 169, 244)),
        (ArgbColor.FromRgb(89, 92, 255), ArgbColor.FromRgb(102, 194, 255)),
        (ArgbColor.FromRgb(0, 212, 255), ArgbColor.FromRgb(9, 9, 121)),
        (ArgbColor.FromRgb(43, 88, 118), ArgbColor.FromRgb(78, 67, 118)),
        (ArgbColor.FromRgb(29, 212, 162), ArgbColor.FromRgb(11, 171, 181)),
        (ArgbColor.FromRgb(168, 255, 120), ArgbColor.FromRgb(120, 255, 214)),
        (ArgbColor.FromRgb(0, 150, 136), ArgbColor.FromRgb(76, 175, 80)),
        (ArgbColor.FromRgb(106, 17, 203), ArgbColor.FromRgb(37, 117, 252)),
        (ArgbColor.FromRgb(19, 84, 122), ArgbColor.FromRgb(128, 208, 199)),
        (ArgbColor.FromRgb(255, 107, 107), ArgbColor.FromRgb(255, 159, 67)),
        (ArgbColor.FromRgb(255, 193, 7), ArgbColor.FromRgb(255, 87, 34)),
        (ArgbColor.FromRgb(255, 110, 82), ArgbColor.FromRgb(255, 183, 82)),
        (ArgbColor.FromRgb(241, 39, 17), ArgbColor.FromRgb(245, 175, 25)),
        (ArgbColor.FromRgb(255, 204, 42), ArgbColor.FromRgb(255, 153, 0)),
        (ArgbColor.FromRgb(255, 0, 132), ArgbColor.FromRgb(255, 100, 71)),
        (ArgbColor.FromRgb(255, 225, 109), ArgbColor.FromRgb(255, 109, 143)),
        (ArgbColor.FromRgb(118, 75, 162), ArgbColor.FromRgb(102, 126, 234)),
        (ArgbColor.FromRgb(6, 214, 160), ArgbColor.FromRgb(255, 209, 102))
    ];

    private static readonly (ArgbColor Start, ArgbColor End) Default =
        (ArgbColor.FromRgb(88, 88, 88), ArgbColor.FromRgb(58, 58, 58));

    public static (ArgbColor Start, ArgbColor End) For(string? genreName)
    {
        if (string.IsNullOrEmpty(genreName)) return Default;

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(genreName));
        return Palettes[(int)(BitConverter.ToUInt32(hash, 0) % (uint)Palettes.Length)];
    }
}
