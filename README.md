# Lochlan Productivity

A strict Windows focus app: finish your tasks, or your distractions stay blocked. Free and open source (MIT).

![License: MIT](https://img.shields.io/badge/License-MIT-green.svg)

## What it does

- **Today's Tasks** — add what you must do. While anything is incomplete, Focus Mode stays locked.
- **App blocking** — assign app groups (or individual EXEs) per task. Blocked apps are closed automatically until their task is done. Adding apps is always allowed; removing them requires all tasks complete.
- **Website blocking** — blocked sites resolve to `0.0.0.0` via the Windows hosts file while enforcing, with browser secure-DNS disabled so the block can't be bypassed. Embedded YouTube can stay allowed for tutorials.
- **Daily plan prompt** — once a day the app asks for today's task before anything unlocks (customizable message, can be switched off in Settings).
- **Schedules** — auto-engage Focus Mode on a timetable (e.g. school nights), with a custom **warning toast** N minutes before each block starts.
- **Recurring + future tasks** — daily/weekly repeats plus one-shot future planning with a calendar view.
- **Long-term notes** — checklist or free-writing sticky-note mode, synced.
- **Sync** — two-way merge across your computers through any shared folder (OneDrive, Syncthing, …).
- **Themes** — Frost (light), Tapestry (dark botanical), and fully custom colors.

## Requirements

- Windows 10 version 1809+ or Windows 11, x64.
- **Administrator approval once**: website blocking edits the hosts file, so Windows will ask for permission when blocking first engages (via a pre-approved scheduled task after that).
- For the `.exe` build: [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) + [Windows App SDK runtime](https://learn.microsoft.com/windows/apps/windows-app-sdk/downloads) (the MSIX build carries its dependencies).

## Install

**Option A — plain exe (simplest):** download `LochlanProductivity-win-x64.zip` from [Releases](../../releases), unzip anywhere, run `LochlanProductivity.exe`.

**Option B — MSIX (Start Menu + cleaner uninstall):** download the `.msixbundle`, right-click → Install. If Windows warns about an untrusted certificate, install the bundled `.cer` into *Local Machine → Trusted People* first, or run `Add-AppxPackage path\to\package.msixbundle`.

On first launch the app shows exactly what it will do to your system (close apps, hosts file, scheduled task, registry, startup entry). Declining quits.

## Sync between computers

Single-machine works with zero setup. To sync, point both computers at the **same shared folder** via `… → Sync Folder`:

- **Easiest: OneDrive.** Pick a folder inside your OneDrive (same Microsoft account on both PCs). Propagation takes tens of seconds.
- **Fastest/private: Syncthing.** Share a folder between your machines, then pick it. Propagation takes ~1 second.
- Dropbox / Google Drive folders work the same way.

Merges are newest-wins per item. Editing the *same* task on both PCs inside one sync window can lose one side's edit.

## Backup

`… → Export Backup…` saves everything (tasks, groups, schedules, sites, notes) to a JSON file. `… → Import Backup…` restores it after confirmation. Do this before anything drastic.

## Uninstall

1. In the app: `… → Settings → Cleanup → Remove blocking leftovers` (clears the hosts section, DNS policies, helper task, startup entry).
2. Uninstall the app (or delete the exe folder).
3. Manual fallback: delete the `LochlanProductivityHosts` scheduled task (`schtasks /Delete /TN LochlanProductivityHosts /F`), the `LochlanProductivity` value under `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`, and the marked `# >>> LochlanProductivity Block >>>` section in `C:\Windows\System32\drivers\etc\hosts`.

## Privacy

No telemetry, no accounts, no network calls except your own sync folder. Everything lives on your disk:

| Data | Location |
|---|---|
| Tasks, groups, settings, notes | `%LocalAppData%\LochlanProductivity\` |
| Shared sync file | Your chosen sync folder (`syncdata.json`) |
| Blocked-sites list | `%LocalAppData%\LochlanProductivity\blocked-sites.json` |
| Diagnostics | `%LocalAppData%\LochlanProductivity\webblock.log` |

Task titles sync verbatim to whoever shares your sync folder — don't sync it anywhere you wouldn't paste the titles.

## Build from source

```powershell
dotnet build LochlanProductivity.slnx
# Run (unpackaged):
dotnet run --project LochlanProductivity/LochlanProductivity.csproj
# Publish a self-contained exe:
dotnet publish LochlanProductivity/LochlanProductivity.csproj -c Release -r win-x64
# MSIX: Visual Studio → Package & Publish
```

No test project; `dotnet build` is the check. See `AGENTS.md` for architecture notes, gotchas (XAML compiler quirks, strict-mode guards), and conventions.

## Contributing

Issues and pull requests welcome. Please keep the strict-mode rule intact: nothing may weaken blocking while tasks are incomplete, except the explicitly additive paths (adding apps/checks).

## License

MIT — see [LICENSE](LICENSE).
