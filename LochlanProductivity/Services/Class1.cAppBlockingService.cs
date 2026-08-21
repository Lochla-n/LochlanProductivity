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
        // ============================================================

        public List<BlockedAppStatus> CheckBlockedApps(
            IEnumerable<LochlanProductivity.TodoTask> tasks)
        {
            List<BlockedAppStatus> statuses =
                new();

            if (!IsBlockingActive)
                return statuses;

            foreach (
                LochlanProductivity.TodoTask task
                in tasks)
            {
                if (task.IsCompleted)
                    continue;

                foreach (
                    BlockedApp app
                    in task.BlockedApps)
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
        // ============================================================

        public List<BlockedAppStatus> EnforceBlocking(
            IEnumerable<LochlanProductivity.TodoTask> tasks)
        {
            List<BlockedAppStatus> blockedApps =
                new();

            if (!IsBlockingActive)
                return blockedApps;

            foreach (
                LochlanProductivity.TodoTask task
                in tasks)
            {
                if (task.IsCompleted)
                    continue;

                foreach (
                    BlockedApp app
                    in task.BlockedApps)
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
                                continue;

                            process.CloseMainWindow();

                            if (!process.WaitForExit(1000))
                            {
                                process.Kill();
                            }

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
            }

            return blockedApps;
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

            Process[] processes;

            try
            {
                if (!isFullPath)
                {
                    string processName =
                        Path.GetFileNameWithoutExtension(
                            app.ExecutablePath);

                    processes =
                        Process.GetProcessesByName(
                            processName);
                }
                else
                {
                    processes =
                        Process.GetProcesses();
                }
            }
            catch
            {
                return matchingProcesses;
            }

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
                    expectedPath = null;
                }
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

                    if (!isFullPath)
                    {
                        matchingProcesses.Add(
                            process);

                        continue;
                    }

                    string? actualPath = null;

                    try
                    {
                        actualPath =
                            process.MainModule?.FileName;
                    }
                    catch
                    {
                    }

                    if (!string.IsNullOrWhiteSpace(
                        actualPath) &&
                        expectedPath != null)
                    {
                        string normalizedActualPath =
                            Path.GetFullPath(
                                actualPath);

                        if (normalizedActualPath.Equals(
                            expectedPath,
                            StringComparison.OrdinalIgnoreCase))
                        {
                            matchingProcesses.Add(
                                process);

                            continue;
                        }
                    }

                    process.Dispose();
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