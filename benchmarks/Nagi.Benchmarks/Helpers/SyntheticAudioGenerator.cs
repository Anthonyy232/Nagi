using ATL;
using System.Diagnostics;

namespace Nagi.Benchmarks.Helpers;

public static class SyntheticAudioGenerator
{
    public static void GenerateLibrary(string rootPath, int songCount)
    {
        Directory.CreateDirectory(rootPath);
        var samplePath = Path.Combine(rootPath, "sample.mp3");
        var startInfo = new ProcessStartInfo("ffmpeg") { UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in new[] { "-v", "error", "-f", "lavfi", "-i", "anullsrc=r=44100:cl=stereo",
                     "-t", "2", "-c:a", "libmp3lame", samplePath })
            startInfo.ArgumentList.Add(argument);
        using var process = Process.Start(startInfo)!;
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException("FFmpeg could not generate the benchmark audio.");
        var sampleBytes = File.ReadAllBytes(samplePath);
        File.Delete(samplePath);

        for (int i = 0; i < songCount; i++)
        {
            int albumIndex = i / 10;
            int trackIndex = (i % 10) + 1;
            string albumName = $"Benchmark Album {albumIndex:D4}";
            string artistName = $"Benchmark Artist {albumIndex / 5:D2}";
            string filePath = Path.Combine(rootPath, artistName, albumName, $"Track {trackIndex:D2}.mp3");

            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            File.WriteAllBytes(filePath, sampleBytes);

            var track = new Track(filePath);
            track.Title = $"Benchmark Track {i:D5}";
            track.Artist = artistName;
            track.Album = albumName;
            track.TrackNumber = trackIndex;
            track.Genre = "Benchmark";
            if (!track.Save())
                throw new InvalidOperationException($"Could not tag benchmark audio: {filePath}");
        }
    }
}
