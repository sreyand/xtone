# Library scale checks

This isolated console harness creates a cached local library of small PCM WAV
files, then checks loading, unchanged refresh, folder grouping and session saves.
It does not read the personal library, play audio, measure high-resolution codec
throughput, or simulate a network drive. Fixture setup is excluded from reported
timings. All fixtures are removed from the harness's own `fixtures/` directory.

Build and run on Windows with the .NET 8 SDK:

```powershell
dotnet build tests/XTONE.LibraryScaleTests -c Release
./tests/XTONE.LibraryScaleTests/bin/Release/net8.0-windows/XTONE.LibraryScaleTests.exe 25000
```

The optional second argument writes a JSON result to that exact path. The default
count is 10,000; the supported explicit range is 1–100,000. Timings depend on the
machine and filesystem. Do not treat one run as a universal performance budget.

The checks require zero source metadata reads on cached load/refresh, no metadata
file rewrite or revision advance for unchanged refresh, bounded progress reports,
correct folder membership, and a small independent session write.
