using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace LochlanProductivity.Services
{
    public class AppBlockingService
    {
        // ============================================================
        // MONITORING STATE
        // ============================================================

        // Manual Focus Mode.
        public bool IsMonitoring { get; private set; }

        // Controlled by ScheduleManager/MainWindow.
        public bool IsScheduledMonitoring { get; private set; }

        // True when either manual or scheduled blocking is active.
        public bool IsBlockingActive =>
            IsMonitoring ||
            IsScheduledMonitoring;

        // ============================================================
        // PROCESS SNAPSHOT CACHE
        //
        // A full Process.GetProcesses() enumeration (plus MainModule
        // probes) is the most expensive thing here, and the tick
        // calls both CheckBlockedApps and EnforceBlocking. Cache one
        // lightweight snapshot per ~2s so a whole tick shares it.
        // Only IDs/names/paths are cached - live Process objects
        // are always re-opened by ID and re-verified by path, so a
        // recycled PID can never cause a wrong kill.
        // ============================================================

        private readonly object snapshotLock = new();

        private DateTime snapshotTakenUtc = DateTime.MinValue;

        private List<ProcessSnapshot> cachedSnapshot = new();

        private static readonly TimeSpan SnapshotTtl =
            TimeSpan.FromSeconds(2);

        private sealed class ProcessSnapshot
        {
            public int Id;

            public string Name = "";

            public string? Path;
        }

        private List<ProcessSnapshot> GetProcessSnapshot()
        {
            lock (snapshotLock)
            {
                if (DateTime.UtcNow - snapshotTakenUtc < SnapshotTtl)
                {
                    return cachedSnapshot;
                }

                List<ProcessSnapshot> fresh = new();

                Process[] processes;

                try
                {
                    processes = Process.GetProcesses();
                }
                catch
                {
                    return cachedSnapshot;
                }

                foreach (Process process in processes)
                {
                    try
                    {
                        if (process.HasExited)
                        {
                            continue;
                        }

                        string name;

                        try
                        {
                            name = process.ProcessName;
                        }
                        catch
                        {
                            continue;
                        }

                        if (string.IsNullOrWhiteSpace(name))
                        {
                            continue;
                        }

                        string? path = null;

                        try
                        {
                            path = process.MainModule?.FileName;
                        }
                        catch
                        {
                        }

                        fresh.Add(
                            new ProcessSnapshot
                            {
                                Id = process.Id,
                                Name = name,
                                Path = path
                            });
                    }
                    catch
                    {
                    }
                    finally
                    {
                        try { process.Dispose(); } catch { }
                    }
                }

                cachedSnapshot = fresh;
                snapshotTakenUtc = DateTime.UtcNow;

                return cachedSnapshot;
            }
        }

        // ============================================================
        // GRACEFUL-CLOSE TRACKING
        //
        // CloseMainWindow + WaitForExit used to block the UI thread
        // up to 1s per process per tick (frozen cursor). Now: first
        // sighting sends the close request and returns immediately;
        // later ticks Kill only if still alive past the grace period.
        // StartTime is stored so a recycled PID is never mistaken
        // for the original process.
        // ============================================================

        private readonly Dictionary<int, (DateTime StartTime, DateTime RequestedAt)> closeGrace =
            new();

        private static readonly TimeSpan CloseGracePeriod =
            TimeSpan.FromMilliseconds(1500);

        // ============================================================
        // MANUAL MONITORING
        // ============================================================

        public void StartMonitoring()
        {
            IsMonitoring = true;
        }

        public void StopMonitoring()
        {
            IsMonitoring = false;
        }

        // ============================================================
        // SCHEDULED MONITORING
        // ============================================================

        public void SetScheduledMonitoring(
            bool active)
        {
            IsScheduledMonitoring = active;
        }

        // ============================================================
        // CHECK BLOCKED APPS
        //
        // Callers provide the effective blocked apps for each
        // incomplete task (groups expanded via PolicyManager).
        // This keeps the service read-only - no task mutation.
        // ============================================================

        public List<BlockedAppStatus> CheckBlockedApps(
            IReadOnlyDictionary<
                LochlanProductivity.TodoTask,
                IReadOnlyList<BlockedApp>> blockedAppsPerTask)
        {
            List<BlockedAppStatus> statuses =
                new();

            if (!IsBlockingActive)
                return statuses;

            foreach (
                KeyValuePair<
                    LochlanProductivity.TodoTask,
                    IReadOnlyList<BlockedApp>> pair
                in blockedAppsPerTask)
            {
                LochlanProductivity.TodoTask task = pair.Key;

                if (task.IsCompleted)
                    continue;

                foreach (
                    BlockedApp app
                    in pair.Value)
                {
                    List<Process> processes =
                        GetApplicationProcesses(app);

                    bool isRunning =
                        processes.Count > 0;

                    foreach (
                        Process process
                        in processes)
                    {
                        process.Dispose();
                    }

                    statuses.Add(
                        new BlockedAppStatus
                        {
                            App = app,

                            Task = task,

                            IsRunning =
                                isRunning
                        });
                }
            }

            return statuses;
        }

        // ============================================================
        // ENFORCE BLOCKING
        //
        // Same contract as CheckBlockedApps: the caller supplies the
        // effective blocked apps per task so this method never has
        // to mutate task objects.
        // ============================================================

        public List<BlockedAppStatus> EnforceBlocking(
            IReadOnlyDictionary<
                LochlanProductivity.TodoTask,
                IReadOnlyList<BlockedApp>> blockedAppsPerTask)
        {
            List<BlockedAppStatus> blockedApps =
                new();

            if (!IsBlockingActive)
                return blockedApps;

            foreach (
                KeyValuePair<
                    LochlanProductivity.TodoTask,
                    IReadOnlyList<BlockedApp>> pair
                in blockedAppsPerTask)
            {
                LochlanProductivity.TodoTask task = pair.Key;

                if (task.IsCompleted)
                    continue;

            foreach (
                BlockedApp app
                in pair.Value)
                {
                    List<Process> processes =
                        GetApplicationProcesses(app);

                    foreach (
                        Process process
                        in processes)
                    {
                        try
                        {
                            if (process.HasExited)
                            {
                                closeGrace.Remove(process.Id);
                                continue;
                            }

                            DateTime procStart;

                            try
                            {
                                procStart = process.StartTime;
                            }
                            catch
                            {
                                // Can't verify identity: ask nicely
                                // once per tick, never wait.
                                try { process.CloseMainWindow(); }
                                catch { }

                                blockedApps.Add(
                                    new BlockedAppStatus
                                    {
                                        App = app,

                                        Task = task,

                                        IsRunning = true
                                    });

                                continue;
                            }

                            if (closeGrace.TryGetValue(
                                process.Id,
                                out var grace) &&
                                grace.StartTime == procStart)
                            {
                                // Already asked: kill only if still
                                // alive past the grace period.
                                if (DateTime.UtcNow - grace.RequestedAt >=
                                    CloseGracePeriod)
                                {
                                    try
                                    {
                                        if (!process.HasExited)
                                            process.Kill();
                                    }
                                    catch { }

                                    closeGrace.Remove(process.Id);
                                }

                                blockedApps.Add(
                                    new BlockedAppStatus
                                    {
                                        App = app,

                                        Task = task,

                                        IsRunning = true
                                    });

                                continue;
                            }

                            try
                            {
                                process.CloseMainWindow();
                            }
                            catch { }

                            closeGrace[process.Id] =
                                (procStart, DateTime.UtcNow);

                            blockedApps.Add(
                                new BlockedAppStatus
                                {
                                    App = app,

                                    Task = task,

                                    IsRunning = true
                                });
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine(
                                $"Could not close {app.Name}: {ex.Message}");
                        }
                        finally
                        {
                            process.Dispose();
                        }
                    }
                }

            PruneCloseGrace();
            }

            return blockedApps;
        }

        private void PruneCloseGrace()
        {
            if (closeGrace.Count < 500)
                return;

            DateTime cutoff =
                DateTime.UtcNow.AddMinutes(-5);

            List<int> stale =
                closeGrace
                    .Where(pair => pair.Value.RequestedAt < cutoff)
                    .Select(pair => pair.Key)
                    .ToList();

            foreach (int id in stale)
            {
                closeGrace.Remove(id);
            }
        }

        // ============================================================
        // DETECT RUNNING APPLICATIONS
        // ============================================================

        public List<DetectedApplication>
            GetRunningApplications()
        {
            List<DetectedApplication> applications =
                new();

            Process[] processes;

            try
            {
                processes =
                    Process.GetProcesses();
            }
            catch
            {
                return applications;
            }

            foreach (
                Process process
                in processes)
            {
                try
                {
                    if (process.HasExited)
                        continue;

                    string? executablePath = null;

                    try
                    {
                        executablePath =
                            process.MainModule?.FileName;
                    }
                    catch
                    {
                    }

                    if (string.IsNullOrWhiteSpace(
                        executablePath))
                    {
                        continue;
                    }

                    string normalizedPath;

                    try
                    {
                        normalizedPath =
                            Path.GetFullPath(
                                executablePath);
                    }
                    catch
                    {
                        continue;
                    }

                    string processName =
                        Path.GetFileNameWithoutExtension(
                            normalizedPath);

                    if (string.IsNullOrWhiteSpace(
                        processName))
                    {
                        continue;
                    }

                    if (IsWindowsSystemProcess(
                        processName,
                        normalizedPath))
                    {
                        continue;
                    }

                    bool hasVisibleWindow =
                        process.MainWindowHandle !=
                        IntPtr.Zero;

                    bool isInProgramFiles =
                        IsProgramFilesPath(
                            normalizedPath);

                    bool isInSteam =
                        normalizedPath.Contains(
                            @"\Steam\",
                            StringComparison.OrdinalIgnoreCase);

                    if (!hasVisibleWindow &&
                        !isInProgramFiles &&
                        !isInSteam)
                    {
                        continue;
                    }

                    string friendlyName =
                        GetFriendlyApplicationName(
                            processName,
                            normalizedPath);

                    applications.Add(
                        new DetectedApplication
                        {
                            Name =
                                friendlyName,

                            ExecutablePath =
                                normalizedPath
                        });
                }
                catch
                {
                }
                finally
                {
                    process.Dispose();
                }
            }

            return applications
                .GroupBy(
                    app =>
                        app.ExecutablePath,
                    StringComparer.OrdinalIgnoreCase)
                .Select(
                    group =>
                        group.First())
                .OrderBy(
                    app =>
                        app.Name,
                    StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        // ============================================================
        // FRIENDLY APPLICATION NAMES
        // ============================================================

        private string GetFriendlyApplicationName(
            string processName,
            string executablePath)
        {
            return processName.ToLowerInvariant() switch
            {
                "steam" =>
                    "Steam",

                "mtga" =>
                    "MTG Arena",

                "discord" =>
                    "Discord",

                "chrome" =>
                    "Google Chrome",

                "msedge" =>
                    "Microsoft Edge",

                "firefox" =>
                    "Mozilla Firefox",

                "spotify" =>
                    "Spotify",

                "devenv" =>
                    "Visual Studio",

                "code" =>
                    "Visual Studio Code",

                "minecraftlauncher" =>
                    "Minecraft Launcher",

                "minecraft" =>
                    "Minecraft",

                "epicgameslauncher" =>
                    "Epic Games Launcher",

                "riotclientservices" =>
                    "Riot Client",

                "battle.net" =>
                    "Battle.net",

                _ =>
                    MakeFriendlyProcessName(
                        processName)
            };
        }

        private string MakeFriendlyProcessName(
            string processName)
        {
            if (string.IsNullOrWhiteSpace(
                processName))
            {
                return "Unknown application";
            }

            string name =
                processName
                    .Replace("_", " ")
                    .Replace("-", " ");

            return System.Globalization.CultureInfo
                .CurrentCulture
                .TextInfo
                .ToTitleCase(name);
        }

        // ============================================================
        // PROGRAM FILES CHECK
        // ============================================================

        private bool IsProgramFilesPath(
            string path)
        {
            string programFiles =
                Environment.GetFolderPath(
                    Environment.SpecialFolder.ProgramFiles);

            string programFilesX86 =
                Environment.GetFolderPath(
                    Environment.SpecialFolder.ProgramFilesX86);

            return
                path.StartsWith(
                    programFiles,
                    StringComparison.OrdinalIgnoreCase)
                ||
                path.StartsWith(
                    programFilesX86,
                    StringComparison.OrdinalIgnoreCase);
        }

        // ============================================================
        // WINDOWS SYSTEM PROCESS FILTER
        // ============================================================

        private bool IsWindowsSystemProcess(
            string processName,
            string executablePath)
        {
            string windowsDirectory =
                Environment.GetFolderPath(
                    Environment.SpecialFolder.Windows);

            if (executablePath.StartsWith(
                windowsDirectory,
                StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            string[] systemProcesses =
            {
                "System",
                "Idle",
                "Registry",
                "smss",
                "csrss",
                "wininit",
                "services",
                "lsass",
                "svchost",
                "fontdrvhost",
                "dwm",
                "conhost",
                "winlogon",
                "RuntimeBroker",
                "SearchHost",
                "StartMenuExperienceHost",
                "ShellExperienceHost",
                "TextInputHost",
                "WmiPrvSE",
                "taskhostw",
                "sihost",
                "ctfmon",
                "dllhost",
                "audiodg",
                "spoolsv",
                "MsMpEng",
                "SecurityHealthService"
            };

            return systemProcesses.Any(
                name =>
                    name.Equals(
                        processName,
                        StringComparison.OrdinalIgnoreCase));
        }

        // ============================================================
        // BROWSER TERMINATION
        //
        // Hosts-file blocking cannot touch connections a browser has
        // already opened or the entries in its private DNS cache.
        // Killing the known browsers when enforcement engages closes
        // those escape hatches - a fresh launch resolves through the
        // blocked hosts file.
        // ============================================================

        private static readonly string[] KnownBrowserProcessNames =
        {
            "chrome",
            "msedge",
            "firefox",
            "brave",
            "opera",
            "vivaldi"
        };

        public int KillKnownBrowsers()
        {
            int killed = 0;

            foreach (
                string processName
                in KnownBrowserProcessNames)
            {
                Process[] processes;

                try
                {
                    processes =
                        Process.GetProcessesByName(processName);
                }
                catch
                {
                    continue;
                }

                foreach (
                    Process process
                    in processes)
                {
                    try
                    {
                        if (process.HasExited)
                            continue;

                        process.Kill(
                            entireProcessTree: true);

                        killed++;
                    }
                    catch
                    {
                    }
                    finally
                    {
                        process.Dispose();
                    }
                }
            }

            return killed;
        }

        // ============================================================
        // FIND SPECIFIC APPLICATION
        // ============================================================

        private List<Process> GetApplicationProcesses(
            BlockedApp app)
        {
            List<Process> matchingProcesses =
                new();

            if (string.IsNullOrWhiteSpace(
                app.ExecutablePath))
            {
                return matchingProcesses;
            }

            bool isFullPath =
                Path.IsPathRooted(
                    app.ExecutablePath);

            string? expectedPath = null;

            if (isFullPath)
            {
                try
                {
                    expectedPath =
                        Path.GetFullPath(
                            app.ExecutablePath);
                }
                catch
                {
                    return matchingProcesses;
                }
            }

            if (isFullPath && expectedPath != null)
            {
                // Full paths share the tick's cached snapshot instead
                // of enumerating all processes per app. Candidates are
                // re-opened by ID and re-verified by live path.
                foreach (ProcessSnapshot entry in GetProcessSnapshot())
                {
                    if (string.IsNullOrWhiteSpace(entry.Path))
                        continue;

                    string normalizedEntry;

                    try
                    {
                        normalizedEntry =
                            Path.GetFullPath(entry.Path);
                    }
                    catch
                    {
                        continue;
                    }

                    if (!normalizedEntry.Equals(
                        expectedPath,
                        StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    Process? candidate = null;

                    try
                    {
                        candidate =
                            Process.GetProcessById(entry.Id);

                        if (candidate.HasExited)
                        {
                            candidate.Dispose();
                            continue;
                        }

                        string? actualPath = null;

                        try
                        {
                            actualPath =
                                candidate.MainModule?.FileName;
                        }
                        catch
                        {
                        }

                        if (string.IsNullOrWhiteSpace(actualPath))
                        {
                            candidate.Dispose();
                            continue;
                        }

                        string normalizedActual;

                        try
                        {
                            normalizedActual =
                                Path.GetFullPath(actualPath);
                        }
                        catch
                        {
                            candidate.Dispose();
                            continue;
                        }

                        if (normalizedActual.Equals(
                            expectedPath,
                            StringComparison.OrdinalIgnoreCase))
                        {
                            matchingProcesses.Add(candidate);
                        }
                        else
                        {
                            candidate.Dispose();
                        }
                    }
                    catch
                    {
                        try { candidate?.Dispose(); } catch { }
                    }
                }

                return matchingProcesses;
            }

            // Bare names (steam.exe) resolve cheaply by name.
            string processName =
                Path.GetFileNameWithoutExtension(
                    app.ExecutablePath);

            Process[] processes;

            try
            {
                processes =
                    Process.GetProcessesByName(
                        processName);
            }
            catch
            {
                return matchingProcesses;
            }

            foreach (
                Process process
                in processes)
            {
                try
                {
                    if (process.HasExited)
                    {
                        process.Dispose();
                        continue;
                    }

                    matchingProcesses.Add(
                        process);
                }
                catch
                {
                    process.Dispose();
                }
            }

            return matchingProcesses;
        }
    }

    // ================================================================
    // BLOCKED APP STATUS
    // ================================================================

    public class BlockedAppStatus
    {
        public LochlanProductivity.BlockedApp App { get; set; } =
            new();

        public LochlanProductivity.TodoTask Task { get; set; } =
            new();

        public bool IsRunning { get; set; }
    }

    // ================================================================
    // DETECTED APPLICATION
    // ================================================================

    public class DetectedApplication
    {
        public string Name { get; set; } = "";

        public string ExecutablePath { get; set; } = "";
    }
}