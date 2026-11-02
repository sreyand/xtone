# Large-library presentation checks

This isolated WPF harness applies synthetic cached metadata at 10,000 and 25,000 tracks to the real MainWindow. It does not scan a personal library, decode these synthetic paths, play audio, or measure first-time FLAC/ALAC import speed. A nonexistent fixture root deliberately exercises cached offline-library presentation.

It verifies that the full collection is retained, Artists/Music list/grid containers are virtualized, only the latest rapidly changed search is published, and clearing search cannot be overwritten by an older query. It records total snapshot-apply time, 100 interface refresh calls, latest-search completion (including the 150 ms debounce), dispatcher-timer intervals throughout the workload, and realized row counts.

Run from the project folder:

```powershell
dotnet run --project tests/XTONE.LargeLibraryTests/XTONE.LargeLibraryTests.csproj -c Release
```

The JSON report is written beside the executable as `large-library-results.json`. Timings include JIT/font/layout warmup and concurrent host load. Dispatcher samples span the complete workload, including view changes; they are not audio latency or per-frame guarantees. Assertions check behavior and bounded materialized rows rather than fragile machine-specific timing limits.

An initial local run observed 7.7 ms for 100 cached interface refreshes at both sizes and 189 ms for the latest 25,000-track search including debounce. The 25,000-track snapshot apply took about 1,005 ms including background preparation; workload dispatcher p95 was 60 ms with a 211 ms maximum. The colder 10,000-track workload had higher layout intervals (p95 168 ms, maximum 491 ms). These results identify remaining cold-layout costs as well as improved steady-state work; they do not claim all large-library UI operations meet a 16 ms frame budget.
