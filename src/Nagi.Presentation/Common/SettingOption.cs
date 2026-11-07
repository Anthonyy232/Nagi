namespace Nagi.Presentation.Common;

/// <summary>
///     A choice for a setting, with the name it is shown with.
/// </summary>
public sealed record SettingOption<T>(T Value, string DisplayName)
{
    public static IReadOnlyList<SettingOption<T>> For(IEnumerable<T> values, Func<T, string> getName) =>
        values.Select(value => new SettingOption<T>(value, getName(value))).ToList();
}
