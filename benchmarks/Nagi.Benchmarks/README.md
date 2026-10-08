# Library benchmarks

Run from the repository root with the SDK required by `global.json` and FFmpeg on `PATH`:

```powershell
dotnet run --project benchmarks/Nagi.Benchmarks -c Release -- --filter "*Library*" --job Short --iterationCount 7 --warmupCount 2 --launchCount 1 --exporters json
```

Use `--filter "*LibraryQueryBenchmarks*"` or `--filter "*LibraryScanBenchmarks*"` to select one suite. BenchmarkDotNet writes timing, managed allocation, CSV and JSON reports under `BenchmarkDotNet.Artifacts`.

| Suite | Sizes | Work measured |
| --- | --- | --- |
| LibraryScanBenchmarks | 100, 500, 10,000 songs | Initial indexing and unchanged rescans of real tagged MP3 files through ATL, the filesystem and SQLite. |
| LibraryQueryBenchmarks | 10,000, 100,000 songs | First and last 100-song pages, secondary-artist search, a search with no matches, and a smart playlist with two AND rules. |

Query fixtures contain ten tracks per album, fifty tracks per primary artist, two genres, secondary credits on one percent of songs, and 4 KB of lyrics on twenty percent. They use the production EF model and denormalization interceptor. Audio files are unnecessary for the query suite. Fixture generation and result verification are outside the measured methods; scan iterations also verify indexed counts and valid durations.

Both suites use temporary file-backed SQLite databases. Connection pooling is disabled for isolated cleanup, so absolute query timings include opening a connection and may differ from the application's pooled connections. Scan setup warms the audio files and database; `InitialScan` clears library rows rather than the operating system's filesystem cache. Its managed allocation figure is cumulative allocation per scan, not peak resident memory.

Online metadata, artwork processing and ReplayGain analysis are disabled or substituted. These benchmarks measure scanning and database queries; they do not measure WinUI layout, scrolling, network services or background loudness analysis.

For a code regression comparison, run the same harness and synthetic datasets against both revisions, hold the SDK and dependency versions constant, and run sequentially on the same machine. Short jobs can reveal large changes, but small differences require longer repeated runs. Keep correctness checks enabled: a scan or search returning fewer rows is not a valid speed improvement.
