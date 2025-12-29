namespace Nagi.Core.Models;

/// <summary>
///     Represents a predefined equalizer preset with a display name and gain values for each band.
/// </summary>
/// <param name="Name">The display name of the preset (e.g., "Bass Boost", "Treble Boost").</param>
/// <param name="Gains">An array of gain values in decibels for each equalizer band.</param>
public record EqualizerPreset(string Name, float[] Gains);
