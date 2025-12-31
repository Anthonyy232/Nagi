using ATL;
using System.IO;

namespace Nagi.Benchmarks.Helpers;

public static class SyntheticAudioGenerator
{
    // Minimal valid MP3 header for 1 frame of silence (approx 26ms at 44.1kHz)
    private static readonly byte[] MinimalMp3Bytes = new byte[]
    {
        0xFF, 0xFB, 0x10, 0x44, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00
    };

    public static void GenerateLibrary(string rootPath, int songCount)
    {
        if (Directory.Exists(rootPath))
            Directory.Delete(rootPath, true);
        
        Directory.CreateDirectory(rootPath);

        for (int i = 0; i < songCount; i++)
        {
            // Group into albums of 10 songs
            int albumIndex = i / 10;
            int trackIndex = (i % 10) + 1;
            string albumName = $"Benchmark Album {albumIndex:D4}";
            string artistName = $"Benchmark Artist {albumIndex / 5:D2}"; // 1 artist per 5 albums
            string filePath = Path.Combine(rootPath, artistName, albumName, $"Track {trackIndex:D2}.mp3");

            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            File.WriteAllBytes(filePath, MinimalMp3Bytes);

            // Write tags using ATL
            var track = new Track(filePath);
            track.Title = $"Benchmark Track {i:D5}";
            track.Artist = artistName;
            track.Album = albumName;
            track.TrackNumber = trackIndex;
            track.Genre = "Benchmark";
            track.Save();
        }
    }
}
