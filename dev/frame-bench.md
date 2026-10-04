# Frame benchmark (`--frame-bench`)

How to measure Historia's frame times and compare two builds. Frame timing can't be a build
gate, because on a shared runner it measures the runner. It is a local A/B run against a
recorded baseline, repeated whenever the render path changes.

## Quick start

1. Publish the two builds to compare (A = baseline, B = candidate):

   ```powershell
   dotnet publish Historia.App -c Release -r win-x64 --self-contained false -o <dir-A>
   # check out / build the candidate, then:
   dotnet publish Historia.App -c Release -r win-x64 --self-contained false -o <dir-B>
   ```

2. Close every Historia window, leave the display awake, and keep the machine otherwise idle.
3. From the repo root:

   ```powershell
   .\dev\frame-bench.ps1 -A <dir-A> -B <dir-B> -Out <results-dir> -Runs 5
   ```

4. Read `<results-dir>\summary.md`.

A full run (two repos, `all` and `terminal`, 5 runs) is 48 launches of about 100 s each, so
roughly 80 minutes.

## Prerequisites

- **Bench repositories** at `C:\_Testing\linux` and `C:\_Testing\chromium` (override with
  `-Repos`). Each must be checked out at the HEAD pinned in `frame-bench.json`; a launch on any
  other HEAD exits with code 5 and the script stops. The harness never fetches, resets or writes
  to these repositories, and neither should you.
- **No Historia running.** The script refuses to start while one is open, and never stops one.
  A launch also refuses (exit 3) if another instance holds the single-instance lock.
- **Display on, window visible.** Frames are timed off the compositor. If the monitor sleeps
  (power-plan idle timeout), Windows throttles presentation to a few frames per second and every
  run is reported invalid. For a long run, keep the display awake (for example a process that
  calls `SetThreadExecutionState(ES_CONTINUOUS | ES_DISPLAY_REQUIRED)` for the duration).

## What the pieces do

### `dev/frame-bench.ps1`: the driver

- For each repository and each mode (`all`, then `terminal`):
  1. One warm-up launch per build, written to `<Out>\warmup\A|B` and not summarised.
  2. `-Runs` measured launches per build, **alternating A, B, A, B**, so clock and thermal drift
     land on both builds equally. Results go to `<Out>\A` and `<Out>\B`.
- Retries a failed launch once; a second failure is listed in `failed-launches.txt`.
- Logs every launch to `<Out>\launches.csv`, with stderr under `<Out>\logs\`.
- Ends by running build A with `--frame-bench-summarize`, which writes `<Out>\summary.md`.
- Parameters: `-A`, `-B`, `-Out` (required); `-Runs` (5), `-Seconds` per workload (20), `-Seed`
  (1), `-Repos`, `-Data` (the isolated app-data folder), `-Config` (defaults to
  `dev\frame-bench.json`).

### `dev/frame-bench.json`: pins

Per repository: the `head` commit the repo must be at, and the `diff` (commit + path) the
diff-scroll workload opens. Re-pin in its own commit when a bench repo moves.

### `Historia.exe --frame-bench <all|terminal>`: one launch

```text
Historia.exe --frame-bench <all|terminal> --repo <path> --out <dir> --data <dir> --run <n>
             [--seconds 20] [--seed 1] [--config <frame-bench.json>]
```

Code: `Historia.App/Diagnostics/FrameBench/`. `FrameBenchRunner` drives the launch:

1. Before Avalonia starts it takes the single-instance lock and points every persisted path at
   `--data`, so your own settings, caches and logs are never touched.
2. Pins the window to 1600×1000 device pixels at (40, 40) on the primary monitor.
3. Checks the repo's HEAD against the pin, then opens the repo as the only tab.
4. Waits for the full load, then for background work to go quiet: three one-second windows in a
   row where Historia and its children use under 5% of one core.
5. Before each workload it waits 3 s and forces a GC, then records the workload.
6. Writes `<repo>-<workload>-<avalonia>-r<run>.csv` (per frame) and `.json` (summary, validity,
   machine) plus a `.log`, and exits with the run's code. A 6-minute watchdog bounds the launch.

Git calls go through `FrameBenchGitGuard`. It refuses any subcommand that would write to the
repository or reach the network, without running git, and counts and names what was asked.

### Workloads (`Workloads.cs`)

| Mode | Workload | What it does |
|---|---|---|
| `all` | `graph-scroll` | Scrolls the commit graph down from the top at 3,000 px/s through the wheel's scroll path. |
| `all` | `graph-jump` | Every 0.25 s, jumps the graph to a seeded-random offset among the loaded rows, so it lands on cold rows. |
| `all` | `diff-scroll` | Opens the pinned file diff and scrolls it at 3,000 px/s. |
| `terminal` | `terminal` | Types a loop into the repo shell that echoes 200,000 numbered lines, and records until it ends. |

`all` runs its three workloads in that order in one launch. A run must cover at least 90% of
the planned distance or jump count.

### Recording (`FrameRecorder.cs`)

It chains `TopLevel.RequestAnimationFrame`, so each callback marks one composited frame. On
each frame it records:

- the frame interval;
- the graph's render time, from the `FrameBench.Active` probe in `CommitGraphControl.Render`;
- the GC counters.

Then it steps the workload.

A run is **valid** only if all of these hold:

- it recorded at least half the display's refresh rate in frames over the recorded time;
- the workload covered its range;
- background work had settled before recording;
- nothing threw.

Refused git requests are reported but don't invalidate a run.

### Statistics and summary

- `Historia.Core/Diagnostics/FrameStats.cs` turns the frames into a summary:
  - frame-interval percentiles;
  - frames over the 22.2 ms ceiling;
  - graph render percentiles;
  - allocation rate;
  - gen0/1/2 counts and GC pause.

  It also compares A with B per metric. It is unit-tested in `FrameStatsTests`.
- **Change** is `(median B − median A) / median A`. The **noise band** is the larger side's
  spread, `(max − min) / median`, and never less than 5%. A change counts only beyond the band.
- `--frame-bench-summarize <out> [--runs n]` reads every JSON under `<out>\A` and `<out>\B`, compares
  each (repo, workload) cell, and writes `summary.md`. With `--runs`, a cell with fewer valid
  runs than that on either side is invalid.
- The summary's verdict answers "is B worth adopting over A?":
  - **Win:** on a graph-scroll or graph-jump cell, frame p99, render p99 or frames over the
    ceiling improve by at least 10%, or GC pause improves by at least 25%.
  - **Regression:** on any cell, frame p99, max frame or frames over the ceiling worsen by at
    least 10%.
  - **Outcome:**
    - any regression → **Skip**;
    - a win on every repo (at least two repos) → **Proceed**;
    - a win on some repos only → **Inconclusive**;
    - no win → **Skip**.

  A change's own plan may set stricter absolute thresholds; check the cells against those too.

## Reading the results

- A **max-frame-only regression** flag is known noise. Re-run that cell once and record both
  runs. Never re-run until a result passes.
- A cell marked **invalid** usually means too few frames. Check that the display was awake and
  the window unoccluded, then read the launch's `.log` (it records the load and settle times and
  each workload's frame count).
- To find *why* a cell is slow, see the switch-experiment recipe in the design docs (developer
  panel and diagnostics chapter). It uses an uncommitted scratch build with env-var switches,
  variants run interleaved, and stack samples.

## Exit codes

| Code | Meaning |
|---|---|
| 0 | Every workload valid |
| 1 | Failure (exception) |
| 2 | Usage error |
| 3 | Another Historia instance is running |
| 4 | Watchdog timeout (launch exceeded 6 minutes) |
| 5 | Repository not at its pinned HEAD |
| 6 | Ran, but at least one workload was invalid |
