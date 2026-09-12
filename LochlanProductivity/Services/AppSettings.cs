using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

        // Sticky quick-add group selection (task input color dots).
        // Persists so the check-off is not redone between tasks.
        public List<string> StickyBlockedGroupIds { get; set; } = new();

        // PERSONAL vs PUBLIC: set false on your own strict build to
        // make the daily plan un-disableable. Public GitHub builds
        // keep true so others can turn the prompt off in Settings.
        public const bool AllowDisablingDailyPrompt = true;

        // Daily plan prompt switch (public builds). False = no prompt
        // and no missing-plan lock. Ignored when the lockdown above
        // is false.
        public bool DailyPromptEnabled { get; set; } = true;

        public const string DefaultDailyPromptMessage =
            "Focus is locked until you add at least one task for today.";

        // Custom text shown in the daily plan dialog.
        public string DailyPromptMessage { get; set; } =
            DefaultDailyPromptMessage;

        // When true, the Long-term list is replaced by a free-writing
        // sticky-note area.
        public bool UseStickyNoteMode { get; set; } = false;

        // When true, youtube-related CDN/media domains are NOT blocked
        // via hosts so embedded players keep working while
        // youtube.com itself remains blocked for direct navigation.
        // Hosts files cannot distinguish navigation vs iframe, so
        // the main youtube.com block stays but nocookie + CDN stays
        // resolvable.
        public bool AllowYouTubeEmbeds { get; set; } = true;

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
                                KillBrowsersOnEngage,

                            AllowYouTubeEmbeds =
                                AllowYouTubeEmbeds,

                            StickyBlockedGroupIds =
                                StickyBlockedGroupIds.ToList(),

                            DailyPromptEnabled =
                                DailyPromptEnabled,

                            DailyPromptMessage =
                                DailyPromptMessage,

                            UseStickyNoteMode =
                                UseStickyNoteMode
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

                if (loaded.StickyBlockedGroupIds != null)
                {
                    StickyBlockedGroupIds =
                        loaded.StickyBlockedGroupIds
                            .Where(id => !string.IsNullOrWhiteSpace(id))
                            .Select(id => id.Trim())
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToList();
                }

                DailyPromptEnabled =
                    loaded.DailyPromptEnabled;

                if (!string.IsNullOrWhiteSpace(loaded.DailyPromptMessage))
                {
                    DailyPromptMessage =
                        loaded.DailyPromptMessage!;
                }

                UseStickyNoteMode =
                    loaded.UseStickyNoteMode;

                // New field defaults to true for embeds; old files
                // missing the property deserve the same default.
                // System.Text.Json leaves bool as false when missing,
                // so treat a missing-file upgrade via file-not-found
                // above; here we just honor what was saved. If the
                // file existed but lacked the property, keep true.
                if (File.ReadAllText(saveFilePath)
                        .Contains("AllowYouTubeEmbeds", StringComparison.OrdinalIgnoreCase))
                {
                    AllowYouTubeEmbeds =
                        loaded.AllowYouTubeEmbeds;
                }
                else
                {
                    // Upgrade path: existing installs get embeds allowed
                    // (the user's requested behaviour).
                    AllowYouTubeEmbeds = true;
                }
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

            public bool AllowYouTubeEmbeds { get; set; } = true;

            public List<string>? StickyBlockedGroupIds { get; set; }

            public bool DailyPromptEnabled { get; set; } = true;

            public string? DailyPromptMessage { get; set; }

            public bool UseStickyNoteMode { get; set; } = false;
        }
    }
}
