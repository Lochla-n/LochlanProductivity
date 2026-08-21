using System;
using System.IO;
using System.Text.Json;

namespace LochlanProductivity
{
    public class DailyPromptManager
    {
        private readonly string saveFilePath =
            Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData),
                "LochlanProductivity",
                "daily-prompt.json");

        private DailyPromptData data =
            new DailyPromptData();

        public DailyPromptManager()
        {
            Load();
        }

        // ============================================================
        // CHECK WHETHER TODAY'S PROMPT HAS BEEN SHOWN
        // ============================================================

        public bool ShouldShowPrompt()
        {
            return data.LastPromptDate.Date != DateTime.Now.Date;
        }

        // ============================================================
        // MARK TODAY'S PROMPT AS SHOWN
        // ============================================================

        public void MarkPromptShown()
        {
            data.LastPromptDate =
                DateTime.Now.Date;

            Save();
        }

        // ============================================================
        // SAVE
        // ============================================================

        private void Save()
        {
            try
            {
                string? directory =
                    Path.GetDirectoryName(saveFilePath);

                if (directory == null)
                    return;

                Directory.CreateDirectory(directory);

                JsonSerializerOptions options =
                    new JsonSerializerOptions
                    {
                        WriteIndented = true
                    };

                string json =
                    JsonSerializer.Serialize(
                        data,
                        options);

                string tempFile =
                    saveFilePath + ".tmp";

                File.WriteAllText(
                    tempFile,
                    json);

                // Replace the old file only after the new file
                // has been successfully written.
                File.Move(
                    tempFile,
                    saveFilePath,
                    true);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"Failed to save daily prompt data: {ex}");
            }
        }

        // ============================================================
        // LOAD
        // ============================================================

        private void Load()
        {
            try
            {
                if (!File.Exists(saveFilePath))
                    return;

                string json =
                    File.ReadAllText(saveFilePath);

                DailyPromptData? loaded =
                    JsonSerializer.Deserialize<DailyPromptData>(
                        json,
                        new JsonSerializerOptions
                        {
                            PropertyNameCaseInsensitive = true
                        });

                if (loaded != null)
                {
                    data = loaded;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"Failed to load daily prompt data: {ex}");

                // Do NOT save here.
                //
                // If the file is broken, we don't want to overwrite
                // it with a new empty file.
                data = new DailyPromptData();
            }
        }
    }

    public class DailyPromptData
    {
        public DateTime LastPromptDate { get; set; }
    }
}