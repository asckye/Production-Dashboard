# ProductionDashboard · Production Statistics Wall Display

[简体中文](README.md) | **English**

[![CI](https://github.com/asckye/Production-Dashboard/actions/workflows/ci.yml/badge.svg)](https://github.com/asckye/Production-Dashboard/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)
![.NET 10](https://img.shields.io/badge/.NET-10-512BD4)
![Platform: Windows](https://img.shields.io/badge/platform-Windows-0078D6)

A general-purpose production statistics dashboard for discrete-manufacturing lines running 24/7. The PLC raises a "part complete" signal; the PC side handles counting, shift attribution, aggregation, persistence and big-screen visualization, with a full year (365 days) of data available for query and export.

> Adapts to any workshop or line: edit `Display.FactoryName` (top-bar title) and the `Plc` section (PLC addresses) in `appsettings.json`. No code changes needed.
>
> The UI is in Simplified Chinese. UI labels quoted below include an English gloss.

---

## Screenshots

![Dashboard, dark theme](docs/images/dashboard-dark.png)

| Light theme | Team & shift settings |
|---|---|
| ![Dashboard, light theme](docs/images/dashboard-light.png) | ![Team & shift settings](docs/images/shift-config-dark.png) |

> Screenshots use demo data; PLC signals come from the [`tools/plc-sim`](tools/plc-sim) software PLC.

---

## Features

- **Real-time counting**: under 200 ms from PLC signal to on-screen update
- **5 time dimensions**: this hour / today / this week / this month / this year, each with a big number, target percentage and trend comparison
- **Team comparison**: two teams side by side in real time, plus weekly / monthly / yearly totals. Production is attributed to the team, and settlement happens automatically after a shift swap
- **Fully configurable shifts**: 1–N shifts, custom time ranges, cross-midnight rules and effective dates, all editable from the operator UI without touching code
- **Optional PLC handshake mode**: rising-edge detection with debounce by default. Switch to Handshake mode for zero missed counts and crash-safe re-entry (requires one extra DWORD sequence counter on the PLC side)
- **Resume after power loss**: after an industrial-PC power cut, the current shift's running total is restored from SQLite with no data loss
- **Automatic Excel export at end of shift**: each shift produces `Data/Exports/{year}/{date}_{shift}.xlsx`
- **Yearly archiving**: at year rollover, the previous year is exported to `Data/Archives/{year}.xlsx`, a full database backup is taken, and the year is purged from the main database; archive files are never overwritten
- **Watchdog self-recovery**: a UI deadlock or crash is detected within 60 seconds, and the process is killed and restarted
- **Fits any screen**: the UI is designed at 1920×1080 and scales proportionally with the window, so it displays in full from 1280×720 up to 4K
- **Audit trail**: shift changes, PLC state changes and configuration changes are all written to the AuditLog table

---

## Tech stack

| Category | Choice |
|---|---|
| Runtime | .NET 10 LTS |
| UI | WPF + CommunityToolkit.Mvvm |
| Host | Microsoft.Extensions.Hosting (Generic Host) |
| Database | SQLite (WAL mode) + Dapper |
| PLC communication | Sharp7 (S7 protocol, fully managed) |
| Logging | Serilog (console + rolling file) |
| Tests | xUnit + FluentAssertions |
| Deployment | Self-contained single file + ReadyToRun (win-x64) |

---

## Getting started

### Prerequisites

- Windows 10 / 11
- .NET 10 SDK ([download](https://dotnet.microsoft.com/download/dotnet/10.0))
- A reachable Siemens S7 PLC (IP / rack / slot known), or the [`tools/plc-sim`](tools/plc-sim) software PLC if you have no hardware

### Configure the PLC

Edit `src/ProductionDashboard/appsettings.json` and point the `Plc` section at your device:

```json
"Plc": {
  "IpAddress": "192.168.0.10",
  "Rack": 0,
  "Slot": 1,
  "SampleMode": "Edge",
  "CompleteSignalAddress": "DB100.DBX0.0",
  "HeartbeatAddress": "DB100.DBW6",
  "PollIntervalMs": 100
}
```

> On S7-1200 / S7-1500, the DB must have "Optimized block access" turned off so it can be read and written by absolute address. S7-300/400 typically use rack 0 / slot 2.

### Run

```powershell
git clone https://github.com/asckye/Production-Dashboard.git
cd Production-Dashboard
dotnet run --project src/ProductionDashboard
```

On startup the app runs four initialization steps, which you can follow in `logs/app-*.log` under the output folder:

1. SQLite creates the database, applies the initial migration and seeds the default day shift / night shift (白班 / 夜班) and teams A / B (A 班 / B 班)
2. PlcService connects to the PLC; on success the log shows `PLC 已连接` (PLC connected)
3. ShiftManager determines the current shift and activates the matching panel
4. The dashboard window opens and the live numbers and progress bars start updating

Press **Esc** to exit and enter the password (default `1234`; **change it before deployment**). Alt+F4 or closing from the taskbar asks for the password too.

---

## PLC communication: Edge vs Handshake

The two sampling modes are switched in `appsettings.json`.

### Edge mode (default)

```json
"Plc": { "SampleMode": "Edge" }
```

- Rising-edge detection with 200 ms debounce
- The PLC must **hold the complete bit for at least 200 ms**; otherwise the PC may miss it between two polls (100 ms apart)

### Handshake mode (zero missed counts)

```json
"Plc": {
  "SampleMode": "Handshake",
  "CompleteSignalAddress": "DB100.DBX0.0",
  "CompleteSeqAddress": "DB100.DBD2"
}
```

How it works:

- On every completion, the PLC does `DBD2 := DBD2 + 1; DBX0.0 := TRUE;`
- The PC detects the new sequence number, persists the event, then writes 0 to clear the bit
- Even if several PLC completions fall inside one poll window, the sequence difference gives the exact delta (no missed counts)
- If the PC crashes and restarts, it sees bit = 1 with an unchanged sequence number and only clears the bit, so nothing is double-counted (re-entrant)

**The PLC engineer needs to add** a `DB100.DBD2` field (DWORD, in the same DB as the complete bit) plus a few lines of SCL:

```pascal
IF part_complete AND DB100.DBX0.0 = FALSE THEN
    IF DB100.DBD2 >= 16#FFFFFFFF THEN
        DB100.DBD2 := 0;            // wrap explicitly; don't rely on implicit PLC overflow
    ELSE
        DB100.DBD2 := DB100.DBD2 + 1;
    END_IF;
    DB100.DBX0.0 := TRUE;
END_IF;
```

> The heartbeat word `HeartbeatAddress` must not overlap the bytes used by the sequence counter (DBD2 occupies DBB2–DBB5); the example uses `DB100.DBW6`.

---

## Project structure

Three layers; dependencies only point downward (enforced by project references and architecture tests):

```
ProductionDashboard.slnx
├── src/
│   ├── ProductionDashboard/                  ← App layer: WPF UI, ViewModels, watchdog, service wiring (the executable)
│   ├── ProductionDashboard.Infrastructure/   ← SQLite data access and migrations, Siemens S7 PLC, Excel export, DB maintenance
│   └── ProductionDashboard.Core/             ← domain models, shift / counting / statistics rules, service interfaces (no external technology)
├── tests/ProductionDashboard.Tests/          ← tests mirroring src, plus layering rule tests
├── tools/plc-sim/                            ← software PLC (local development / demos)
├── docs/                                     ← architecture guide, screenshots
└── .github/                                  ← CI and release workflows, issue / PR templates
```

**New to the codebase? Start with the [Architecture and Development Guidelines](docs/ARCHITECTURE.en.md)**: where things live, the data flow, coding rules, and step-by-step recipes for common changes (adding a setting, adding a dashboard number, changing the database, supporting another PLC).

---

## Data model

```
ShiftDefinition       shift definition (versioned; config changes don't affect history)
    │
    ▼
ShiftSummary          per-shift summary (one row per shift: actual / target / peak / notes)
    ▲
    │ (aggregated)
    │
ProductionEvent       raw event (one row per PLC completion, includes TeamId)
    │
    ▼ (aggregated per minute)
ProductionMinute      minute buckets (for charts)

Team / ShiftSlotAssignment  team master data + slot ↔ team assignment
AuditLog                    audit trail
```

Capacity estimate (1 part per second, full year):

- ProductionEvent: ~30 million rows, about **1.2 GB**
- ProductionMinute: ~500,000 rows, about **30 MB**
- ShiftSummary: ~1,500 rows (2 shifts × 365 days × ~2 teams)

At year rollover, data is archived to `Data/Archives/{year}.xlsx`, a full database backup `pre-purge-{year}.db` is taken, and the data is removed from the main database. If the same year is archived again (for example, after more data for it arrived), the new files get a timestamp suffix instead of overwriting the earlier archive.

---

## Shift and team configuration

Start the UI → click **⚙ 班组配置** (Team & Shift Settings) in the top bar → edit names / start and end times / target output / which team works the shift / team name and team leader → click **保存并应用** (Save & Apply).

Every shift must have a team (output of a shift without a team cannot be recorded). New shifts get a default team by rotation, and applying a template keeps the original shifts' teams in order; you can change any of them before saving.

Any shift pattern is supported, for example:

- Single shift: 08:00 → 17:00
- Two shifts: 08:00–20:00 + 20:00–08:00 (default)
- Three shifts: 00:00–08:00 + 08:00–16:00 + 16:00–24:00
- Four-team three-shift rotation or anything custom. Shifts must not overlap (saving is blocked); gaps are allowed (for example, a single-shift line that is idle at night), and output during a gap is not credited to any shift

**Cross-midnight rule**: a night shift from 20:00 to 08:00 the next day is attributed to its **end date** by default (`Shift.AttributionRule = EndDate`). So "today's night shift" means the one that ended this morning, which matches how shop-floor staff usually talk about it.

**Shift comparison**: the two comparison cards on the dashboard show the first two shifts by display order (day + night by default). With three or more shifts, the others are still counted, and the running shift appears in the hero number and the top bar's current-shift badge.

**Shift swap**: performed by a logged-in team leader and audited. In the top bar, click **🔑 班长登录** (Leader Login) → **⇄ 倒班切换** (Swap Shifts) → confirm swapping day ↔ night. Teams keep their own running totals, so production stays attributed to the right team.

---

## Tests

```powershell
dotnet test
```

All **279 tests pass**. Main coverage:

| Module | Coverage |
|---|---|
| ShiftCalculator / ShiftDefinition / ShiftCoverage / ComparedShifts | same-day / cross-midnight / 1–N shifts / attribution rules (incl. ending at 24:00) / shift length / edge cases / minute-by-minute coverage check (gaps, overlaps) / picking the compared shifts |
| ProductionPeriods / target rules | "this hour" at the local full hour (incl. half-hour time zones) / weeks start on Monday / month and year rollover / week, month and year targets |
| CountingService / MinuteAggregator / shift settlement | attribution by signal timestamp / shift change / team attribution after a swap / retrying failed writes / per-minute rollup with catch-up / settling shifts missed during downtime / Excel export |
| TeamAssignment / AuthService | team assignment and swaps / leader login / idle timeout / lockout |
| Repository integration / yearly archive / database maintenance | CRUD + time windows + peak hour for each repository / transactional shift-config save / purge safeguards, never overwriting an earlier archive / VACUUM scheduling (real SQLite) |
| S7Address / HandshakeDecoder / EdgeDetector / ReconnectBackoff | S7 address parsing / sequence wrap-around / crash re-entry / counter reset / rising-edge debounce / re-aligning after reconnect / reconnect backoff |
| ViewModel presentation logic / UI scaling | trend text / bar normalization / leader detection / shift editor helpers / proportional scaling from 1920×1080 |
| AlertThrottle | alert throttling: first report passes, repeats within the interval are suppressed, only one concurrent caller passes |
| Layering rules (LayeringTests) | Core has no WPF / database / PLC dependency; ViewModels never call repositories |

---

## Build and deployment

### Download

The [Releases](https://github.com/asckye/Production-Dashboard/releases) page provides a win-x64 single-file package, built automatically by GitHub Actions when a release is published. Unzip it, edit `appsettings.json` for the site (PLC IP, factory name, passwords), and double-click `ProductionDashboard.exe`. No .NET installation is needed.

### Development build

```powershell
dotnet build
```

### Single-file publish (for the industrial PC)

```powershell
dotnet publish src/ProductionDashboard -c Release -p:PublishProfile=win-x64 -o publish
```

Output:

- `publish/ProductionDashboard.exe`: about **80 MB**, a self-contained single file; the target machine does not need .NET 10 installed
- `publish/appsettings.json`: production config (set the PLC IP, exit password and factory name for the site before deploying)

Copy the whole `publish/` folder to the target machine and double-click the exe.

### Watchdog

On startup the main program spawns a `--watchdog` child process that monitors the UI heartbeat (`runtime/heartbeat.json`). The heartbeat is written by a **DispatcherTimer on the UI thread**, so a UI deadlock stops the heartbeat and the watchdog notices.

- Heartbeat not updated for `HeartbeatTimeoutMs` (60 seconds by default) → kill the main program, wait 3 seconds for handles to be released, restart; if the main program can't be killed, no restart, so two instances never run at once
- Graceful exit → the main program writes `gracefulExit=true` and the watchdog exits
- Watchdog logs go to `logs/watchdog-yyyy-MM-dd.log`, separate from the main program's logs

To disable it, add `"Watchdog": { "Enabled": false }` to `appsettings.json`.

---

## Configuration reference

By design, `appsettings.json` **only holds deployment-specific and security-sensitive fields**. Every other default lives in `Configuration/*Options.cs`; to override one, add it to the JSON. Types and ranges are validated with `[Range]` / `[Required]` and similar attributes, so bad values fail fast at startup.

The current minimal `appsettings.json`:

```json
{
  "Serilog": { ... },
  "Plc": {
    "IpAddress": "192.168.0.10",
    "Rack": 0,
    "Slot": 1,
    "SampleMode": "Edge",
    "CompleteSignalAddress": "DB100.DBX0.0",
    "CompleteSeqAddress": "DB100.DBD2",
    "HeartbeatAddress": "DB100.DBW6",
    "PollIntervalMs": 100
  },
  "Display": {
    "FactoryName": "示例车间 | 1 号产线",
    "ExitPassword": "1234",
    "LeaderPassword": "leader"
  }
}
```

To override any other default (turning off fullscreen, changing backup retention days, switching the PLC `SampleMode` to Handshake, and so on), add the field under the matching section. See the XML doc comments on each Options class for the full field list.

The week / month / year target rules can be adjusted in the `Targets` section (working days by default):

```json
"Targets": {
  "WorkingDaysPerWeek": 5,
  "WorkingDaysPerMonth": 22
}
```

Daily target = sum of all shift targets; weekly = daily × `WorkingDaysPerWeek`; monthly = daily × `WorkingDaysPerMonth`; yearly = monthly × 12. Lines running 24/7 can use 7 / 30. The hourly target is the current shift's target ÷ the shift's length.

> Passwords can also be supplied through environment variables, e.g. `Display__ExitPassword` and `Display__LeaderPassword`, so strong passwords never need to be committed to the config file.

---

## Known limitations

- **Shift-config versioning**: the `ShiftDefinition` table already has `EffectiveFrom` / `EffectiveTo` and soft delete works, but the UI has no history view. Changes take effect immediately; past data was stored with the targets in force at the time.
- **Yield rate**: defects are not tracked yet, and the yield metric is hidden in the UI. Extend `ProductionEvent` with a `Quality` field if you need it.
- **PLC Handshake mode**: requires the PLC program to add a sequence DWORD (e.g. `DB100.DBD2`) before switching. The default Edge mode needs no PLC program changes.
- **Siemens S7 only**: the communication layer is built on Sharp7. To support another PLC brand, implement `IPlcService` (see `Plc/PlcServiceBase.cs`).

---

## Contributing

Issues and pull requests are welcome! Before you start, please read:

- [Architecture and Development Guidelines](docs/ARCHITECTURE.en.md): layering rules, where things live, recipes for common changes
- [Contributing guide](CONTRIBUTING.md): dev environment, running without a PLC, code conventions, commit style
- [Code of Conduct](CODE_OF_CONDUCT.md)
- [Security policy](SECURITY.md): report vulnerabilities privately, not in public issues
- [Changelog](CHANGELOG.md)

---

## License

[MIT](LICENSE)
