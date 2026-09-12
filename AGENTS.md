# LochlanProductivity - Agent Instructions

## Project Overview
WinUI 3 / Windows App SDK desktop productivity app (`net8.0-windows10.0.19041.0`, `UseWinUI=true`, `Microsoft.WindowsAppSDK 1.8.260811000`). Focus/anti-distraction: tasks block apps until completed. MSIX packaging enabled.

## Structure
- `LochlanProductivity.slnx` - Solution file
- `LochlanProductivity/App.xaml.cs:7` - App entry, single-instance mutex (`Local\LochlanProductivity.SingleInstance`, relaunch exits), `AppWindow_Closing` hides window instead of exiting to keep blocking alive, `ExitApplication:52` + `ReleaseSingleInstanceLock` for actual quit
- `LochlanProductivity/MainWindow.xaml:4` / `MainWindow.xaml.cs:18` - Main window, all UI logic, task CRUD, blocking UI, dialogs built in code-behind
- `LochlanProductivity/DailyPromptManager.cs:7` - Daily prompt persistence to `%LocalAppData%/LochlanProductivity/daily-prompt.json`
- `LochlanProductivity/Services/` - Core domain:
  - `AppBlockingService.cs` (`Class1.cAppBlockingService.cs`) - process monitoring/killing. `CheckBlockedApps`/`EnforceBlocking` take an effective-apps map (`IReadOnlyDictionary<TodoTask, IReadOnlyList<BlockedApp>>`) and never mutate tasks
  - `TrayIconManager.cs` - Shell_NotifyIcon P/Invoke tray icon (Open/Exit), one instance per process
  - `AppGroup.cs` / `AppGroupManager.cs` - app groups (stamped `LastModified` on mutations); default Games/Social groups are empty shells (no hardcoded apps), `availableApps` in `MainWindow.xaml.cs:105` starts empty and is seeded from saved groups/tasks + picker/detect
  - `PolicyManager.cs` - expands groups into effective blocked apps via `GetEffectiveBlockedApps` (pure)
  - `ScheduleManager.cs` / `BlockingSchedule.cs` - scheduled Focus Mode
  - `TaskRecurrenceManager.cs` - recurrence (Daily/EveryNDays/WeeklyDays); `CalculateNextDueDate` fast-forwards missed cycles; `CompleteTask` advances recurring tasks
  - `SyncManager.cs` / `SyncData.cs` - two-way merge sync keyed on Id + LastModified for Syncthing-style folder sync; folder configurable via `%LocalAppData%/LochlanProductivity/sync-config.json`, auto-detects Syncthing `config.xml` folders (first is `~/LochlanProductivityData` which you share via Syncthing) then `~/Syncthing/LochlanProductivity` then legacy OneDrive path; atomic `.tmp`+Move writes; `SyncData.BlockedSites` syncs the website blocklist as a UNION (additions propagate both ways; deletions do not); startup sync and Sync Now are allowed while tasks are incomplete (remote completions never overwrite a local incomplete)
  - `StartupManager.cs` - start-with-Windows: packaged StartupTask API (`LochlanProductivityStartup` TaskId) with HKCU Run-key fallback when unpackaged
  - `WebsiteBlocker.cs` - `HostsFileBlocker` splices a marked section into the hosts file (domains -> 0.0.0.0); transports in order: direct write → pre-approved scheduled task `LochlanProductivityHosts` (registered once via UAC; runs PowerShell over `~/LochlanProductivityData/hosts-staging.txt`, result in `swap-last-result.txt`) → legacy self-relaunch elevated with `--lp-hostsfile <path>` (handled in `App.OnLaunched` BEFORE the single-instance mutex). `BlockedSitesManager` persists domains to `%LocalAppData%/LochlanProductivity/blocked-sites.json`. Applied/removed only on enforcement-state transitions (`UpdateWebsiteBlockingState`; also called directly from Focus Mode toggle + task checkboxes); stale sections self-heal on launch; diagnostics in `webblock.log`. Each computer applies its own hosts entries - on a new machine, approve the single registration UAC. Raw `tasks.json` Syncthing conflicts (`tasks.sync-conflict*.json`) are merged on next load (newest `LastModified` wins, local incomplete never overwritten)
- `LochlanProductivity/Assets/` - App icons/splash

## Build & Run
```powershell
dotnet build LochlanProductivity.slnx
dotnet build LochlanProductivity/LochlanProductivity.csproj
# Run (WinUI requires Windows, unpackaged: use VS or `dotnet run --project`):
dotnet run --project LochlanProductivity/LochlanProductivity.csproj
# Publish MSIX via Visual Studio Package & Publish menu (HasPackageAndPublishMenu=true)
```
No test project currently exists. Verify with `dotnet build` only.

## Conventions & Gotchas
- **XAML + code-behind only** - no MVVM framework. Dialogs are constructed imperatively in `MainWindow.xaml.cs` (e.g., `ContentDialog`, `StackPanel`, `CheckBox`).
- **Blocking is enforcement-heavy** - `BlockingTimer_Tick:876` every 1s calls `UpdateScheduledBlockingState`, `EnforceBlocking`, `UpdateBlockingStatus`. `HasIncompleteTasks` locks Focus Mode and blocks editing (`OpenBlockedAppsDialog:1351`, `ManageSchedules_Click:1720` deny when incomplete).
- **Duplicate EXP handling** - `EnforceBlocking:909` and `UpdateBlockingStatus:975` deduplicate by `ExecutablePath` (case-insensitive) via `GroupBy`.
- **Persistence** - Tasks: `~/LochlanProductivityData/tasks.json` (`MainWindow.xaml.cs:104`). Daily prompt: atomic write via `.tmp` + `File.Move` in `DailyPromptManager.cs:74`. `Load:97` does NOT overwrite on corrupt file.
- **Additive blocking edits allowed while locked** - `ManageAppGroups_Click`, `EditAppGroupAsync`, and `OpenBlockedAppsDialog` open even with incomplete tasks: new apps/groups/checks are enabled, but removals (per-app Remove, group Delete, unchecking a blocked box via `IsEnabled=false`) are disabled until Focus is off. `OpenEditTaskDialog` stays fully locked.
- **Quick-add group strip** - `TaskGroupStrip` in `MainWindow.xaml` (input row, left of Add): one flat color-dot `ToggleButton` per group (system chrome stripped via per-button `Resources` overrides; full color = on, `0.28` opacity = off, no glyph), color = `AppGroup.Color` if set else stable FNV hash of group Id over a 12-color palette (`GetGroupHex`/`GetGroupColor`), tooltip = name + app count. Selection is sticky across adds and restarts (`AppSettingsManager.StickyBlockedGroupIds`, defaults to Games); `AddTask`/`AddTaskForDailyPromptAsync` apply it via `ApplyStickyBlockingPolicy`. Strip rebuilds at the end of `RefreshTaskList`, so new groups appear automatically.
- **Group colors are user-pickable** - `EditAppGroupAsync` and `CreateNewGroupAsync` both show a 12-swatch picker (`BuildGroupColorPicker`); edit SAVE stamps `LastModified` so renames/colors propagate, and `SyncManager.MergeGroups` merges `Color`.
- **Plan Future Task** - `+ Future` header button (`PlanFutureTask_Click` → `OpenPlanFutureTaskDialogAsync`): title + `BuildGroupDotToggle` strip (defaults to sticky set, local copy) + `CalendarDatePicker` (`MinDate` = today, default tomorrow). One-shot non-recurring `TodoTask`; lands in Coming Up until due.
- **Soft delete / tombstones** - `TodoTask.IsDeleted` keeps removed tasks in `tasks.json` so deletions propagate through sync; UI/enforcement filter via `ActiveTasks`; tombstones older than 30 days are purged in `LoadTasksAsync`.
- **Sync is pull/push on demand** - task mutations do NOT write the shared `syncdata.json`; "Sync Now" merges (Id + `LastModified`, newest wins per item) and writes back, and launch does a silent merge (`SyncOnStartupAsync`) that only refreshes UI when something changed. No auto-sync on mutations.
- **Save after mutations** - every task add/remove/check/uncheck/edit calls `SaveTasksAsync()` and stamps `LastModified = UtcNow` (merge key for sync).
- **Task planning fields** - `TodoTask.Priority` (`TaskPriority` Low/Normal/High) + optional `DueTimeOfDay`; list sorts incomplete-first, then priority desc, then effective deadline; overdue meta line turns OrangeRed. Edit dialog (`OpenEditTaskDialog`) is denied while tasks are incomplete like all other settings.
- **Daily prompt is mandatory** - `DailyPromptManager` tracks `CurrentStreak`/`BestStreak`; `CheckDailyPromptAsync:311` runs once per calendar day (`ShouldShowPrompt` checks `LastPromptDate` != today) and is **required** — no Skip button, Cancel loops, window is forced visible via `ShowMainWindow`, and `HasIncompleteTasks:269` includes `IsDailyPlanMissing` so Focus stays `Locked` (blocking + hosts) until today's task is added, even with zero tasks. `InitializeAsync:154` auto-enables startup via `EnsureStartupEnabledAsync` so the prompt actually appears at logon. `SkipToday` remains in code but is unused.
- **Strict-mode gates** - Sync Now AND startup sync are allowed while tasks are incomplete (remote completions never overwrite a local incomplete); all settings dialogs share the same lock. Browser closing on block engagement is opt-in (`AppSettingsManager.KillBrowsersOnEngage`, default off) - DNS-policy + hosts make it mostly redundant. Chromium secure-DNS is policy-disabled during enforcement (`BuiltInDnsClientEnabled=0` under HKCU Policies; HKCU\Policies is admin-ACLed so un-elevated writes stage through the helper task via `dns-policy-staging.txt`).
- **No tamper seal** - The old `TamperSeal.cs` (HMAC/DPAPI per-machine key on `tasks.json`) was removed: its `.seal` file synced independently of `tasks.json` via Syncthing and used a machine-local DPAPI key, so cross-device sync ALWAYS tripped a false "tampered" lockdown that froze sync and focus. Do not reintroduce a per-machine seal over a syncthing-synced `tasks.json`.

## What to do when helping
- Prefer `dotnet build` to validate, not `dotnet run` (requires UI thread).
- Keep WinUI threading in mind: `DispatcherTimer`, `ContentDialog.ShowAsync()` needs `XamlRoot`.
- Don't delete `bin/ obj/ .vs/ *.csproj.user` - already gitignored.
- When adding blocking logic, update both `EnforceBlocking` and `CheckBlockedApps` paths.
- Keep `DailyPromptManager` atomic-write pattern for any new JSON persistence.

## Do Not
- Don't change `TargetFramework` / `TargetPlatformMinVersion` without checking WindowsAppSDK compatibility.
- Don't remove `AppWindow_Closing` cancel logic - app must stay alive for blocking.
- Don't bypass `IsFocusModeLocked`/`HasIncompleteTasks` guards.
