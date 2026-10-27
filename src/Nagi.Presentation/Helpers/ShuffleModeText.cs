using Nagi.Core.Models;
using Nagi.Presentation.Resources;

namespace Nagi.Presentation.Helpers;

public static class ShuffleModeText
{
    /// <summary>The modes offered as mixes; <see cref="ShuffleMode.Random" /> is the plain shuffle.</summary>
    public static IEnumerable<ShuffleMode> MixModes => Enum.GetValues<ShuffleMode>().Where(mode => mode != ShuffleMode.Random);

    public static string GetName(ShuffleMode mode) => mode switch
    {
        ShuffleMode.MoreVariety => Strings.Listening_MoreVariety,
        ShuffleMode.Rediscover => Strings.Listening_Rediscover,
        ShuffleMode.Unheard => Strings.Listening_Unheard,
        _ => Strings.Listening_Random
    };

    public static string GetDescription(ShuffleMode mode) => mode switch
    {
        ShuffleMode.MoreVariety => Strings.Listening_VarietyDescription,
        ShuffleMode.Rediscover => Strings.Listening_RediscoverDescription,
        ShuffleMode.Unheard => Strings.Listening_UnheardDescription,
        _ => string.Empty
    };

    public static string GetEmptyMixDescription(ShuffleMode mode, bool timed = false)
    {
        var description = GetDescription(mode);
        var guidance = timed ? Strings.Listening_NoTimedMixDescription : Strings.Listening_NoMixDescription;
        return description.Length > 0 ? $"{description}\n\n{guidance}" : guidance;
    }
}
