using System;
using System.IO;
using System.Text.Json;

namespace LochlanProductivity.Services
{
    // ============================================================
    // APP SETTINGS (machine-local preferences)
    //
    // Atomic JSON persistence following the DailyPrompt pattern.
    // Stored under %LocalAppData% so packaged (MSIX) and unpackaged
    // runs each keep their own view - these are per-machine prefs.
    // ============================================================

    public class AppSettingsManager
    {
        private readonly string saveFilePath =
            Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData),
                "LochlanProductivity",
                "settings.json");

        public bool KillBrowsersOnEngage { get; set; } = false;

        public AppSettingsManager()
        {
            Load();
        }

        public void Save()
        {
            try
            {
                string? directory =
                    Path.GetDirectoryName(saveFilePath);

                if (directory == null)
                    return;

                Directory.CreateDirectory(directory);

                string json =
                    JsonSerializer.Serialize(
                        new SettingsData
                        {
                            KillBrowsersOnEngage =
                                KillBrowsersOnEngage
                        },
                        new JsonSerializerOptions
                        {
                            WriteIndented = true
                        });

                string tempFile = saveFilePath + ".tmp";

                File.WriteAllText(tempFile, json);

                File.Move(tempFile, saveFilePath, true);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"Failed to save settings: {ex}");
            }
        }

        private void Load()
        {
            try
            {
                if (!File.Exists(saveFilePath))
                    return;

                string json =
                    File.ReadAllText(saveFilePath);

                SettingsData? loaded =
                    JsonSerializer.Deserialize<SettingsData>(
                        json,
                        new JsonSerializerOptions
                        {
                            PropertyNameCaseInsensitive = true
                        });

                if (loaded == null)
                    return;

                KillBrowsersOnEngage =
                    loaded.KillBrowsersOnEngage;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"Failed to load settings: {ex}");

                // Corrupt file: keep defaults, never overwrite here.
            }
        }

        private class SettingsData
        {
            public bool KillBrowsersOnEngage { get; set; }
        }
    }
}
