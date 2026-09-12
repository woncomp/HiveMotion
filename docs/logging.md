# Logging and Diagnostics

HiveMotion writes diagnostics to per-day log files. There is no built-in log viewer —
viewing is delegated to external tools so that observing the log never perturbs the
process being measured.

## Log files

- Location: `%LOCALAPPDATA%\HiveMotion\Logs\hivemotion-YYYY-MM-DD.log`
- Encoding: UTF-8 (no BOM), one entry per line.
- Line format: `MMdd HH:mm:ss.fff L [CHANNEL] message` where `L` is `I`/`W`/`E` and
  `CHANNEL` is `DEFAULT` or `ACTIVATION`.
- Retention: the newest 7 daily files are kept; older ones are deleted at startup and on
  date rollover.

The app keeps the active file open with `FileShare.ReadWrite`, so external tools can read
and tail it concurrently without locking conflicts.

## Verbose logging

Info/Warning entries are only produced while verbose logging is on (manage center →
Diagnostics → Verbose logging). Errors are always logged. Keep verbose off for normal use.

## Watching the log with external tools

Zero-install, built into Windows (PowerShell):

```powershell
Get-Content "$env:LOCALAPPDATA\HiveMotion\Logs\hivemotion-$(Get-Date -Format 'yyyy-MM-dd').log" -Wait -Tail 100
```

Filter to activation timing only:

```powershell
Get-Content "$env:LOCALAPPDATA\HiveMotion\Logs\hivemotion-$(Get-Date -Format 'yyyy-MM-dd').log" -Wait -Tail 100 |
    Select-String '\[ACTIVATION\]'
```

GUI options for high-performance tailing, filtering, and highlighting:

- **klogg** (recommended) — designed for fast tailing of large files, with filtered views
  and highlight rules; well suited to watching activation checkpoints live.
- **LogExpert** — Windows-native tailing viewer with columnizers and highlighting.

## Design notes (performance)

- Producers (including the low-level keyboard hook) only capture a `Stopwatch` timestamp
  and push a small struct onto a lock-free bounded queue (capacity 256; new entries are
  dropped when full, except errors which evict the oldest entry).
- All formatting, wall-clock conversion, and file I/O run on a dedicated background
  writer thread, batched per wake-up: one write + one flush per batch.
- Timestamps are monotonic `Stopwatch` ticks converted to wall time on the writer via a
  startup anchor, so queue latency never skews recorded times.
- Directory creation and old-log trimming happen once per file open (startup and date
  rollover), not per entry.
