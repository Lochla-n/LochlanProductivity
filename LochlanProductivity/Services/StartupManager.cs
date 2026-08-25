using System;
using System.Threading.Tasks;
using Microsoft.Win32;
using Windows.ApplicationModel;

namespace LochlanProductivity.Services
{
    // ============================================================
    // STARTUP REGISTRATION
    //
    // Two modes:
    //
    // 1. Packaged (MSIX):
    //    Uses the windows.startupTask extension declared in
    //    Package.appxmanifest (TaskId "LochlanProductivityStartup")
    //    through the StartupTask API.
    //
    // 2. Unpackaged (dotnet run / published exe):
    //    Falls back to the classic HKCU Run key so blocking still
    //    survives reboots in dev runs.
    // ============================================================

    public class StartupManager
    {
        private const string StartupTaskId =
            "LochlanProductivityStartup";

        private const string RunKeyPath =
            @"Software\Microsoft\Windows\CurrentVersion\Run";

        private const string RunValueName =
            "LochlanProductivity";

        public bool IsPackaged { get; }

        public StartupManager()
        {
            IsPackaged = DetectIsPackaged();
        }

        private static bool DetectIsPackaged()
        {
            try
            {
                return Package.Current != null;
            }
            catch
            {
                return false;
            }
        }

        // ============================================================
        // STATE
        // ============================================================

        public async Task<StartupState> GetStateAsync()
        {
            if (IsPackaged)
            {
                try
                {
                    StartupTask task =
                        await StartupTask.GetAsync(
                            StartupTaskId);

                    return task.State switch
                    {
                        StartupTaskState.Enabled =>
                            StartupState.Enabled,

                        StartupTaskState.EnabledByPolicy =>
                            StartupState.Enabled,

                        StartupTaskState.DisabledByUser =>
                            StartupState.DisabledByUser,

                        StartupTaskState.DisabledByPolicy =>
                            StartupState.DisabledByPolicy,

                        _ =>
                            StartupState.Disabled
                    };
                }
                catch
                {
                    // Packaged API failed (e.g. running the exe
                    // directly without identity) - fall back to
                    // the registry below.
                }
            }

            return IsRunKeySet()
                ? StartupState.Enabled
                : StartupState.Disabled;
        }

        // ============================================================
        // ENABLE / DISABLE
        // ============================================================

        public async Task<bool> SetEnabledAsync(
            bool enable)
        {
            if (IsPackaged)
            {
                try
                {
                    StartupTask task =
                        await StartupTask.GetAsync(
                            StartupTaskId);

                    if (enable)
                    {
                        if (task.State ==
                            StartupTaskState.Disabled)
                        {
                            StartupTaskState result =
                                await task.RequestEnableAsync();

                            return result ==
                                StartupTaskState.Enabled;
                        }

                        return task.State ==
                            StartupTaskState.Enabled;
                    }
                    else
                    {
                        if (task.State ==
                            StartupTaskState.Enabled)
                        {
                            task.Disable();
                        }

                        return true;
                    }
                }
                catch
                {
                    // Fall back to the registry below.
                }
            }

            return SetRunKey(enable);
        }

        // ============================================================
        // REGISTRY FALLBACK (unpackaged)
        // ============================================================

        private bool IsRunKeySet()
        {
            try
            {
                using RegistryKey? key =
                    Registry.CurrentUser.OpenSubKey(
                        RunKeyPath);

                return key?.GetValue(RunValueName) != null;
            }
            catch
            {
                return false;
            }
        }

        private bool SetRunKey(bool enable)
        {
            try
            {
                using RegistryKey? key =
                    Registry.CurrentUser.OpenSubKey(
                        RunKeyPath,
                        true);

                if (key == null)
                    return false;

                if (enable)
                {
                    string? executablePath =
                        Environment.ProcessPath;

                    if (string.IsNullOrWhiteSpace(
                        executablePath))
                    {
                        return false;
                    }

                    key.SetValue(
                        RunValueName,
                        $"\"{executablePath}\"");
                }
                else
                {
                    if (key.GetValue(RunValueName) != null)
                    {
                        key.DeleteValue(
                            RunValueName,
                            false);
                    }
                }

                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    public enum StartupState
    {
        Disabled,
        Enabled,
        DisabledByUser,
        DisabledByPolicy
    }
}
