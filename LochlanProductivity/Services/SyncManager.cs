using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace LochlanProductivity.Services
{
    // ============================================================
    // SYNC MANAGER
    //
    // Designed for Syncthing (or OneDrive, or any folder-sync
    // tool) between the desktop and laptop:
    //
    // 1. The shared syncdata.json lives in a configurable folder.
    //    Auto-detection order:
    //        saved setting
    //          > ~/Syncthing/LochlanProductivity (if it has data)
    //          > legacy OneDrive path (if it has data)
    //          > ~/Syncthing/LochlanProductivity (default target)
    //
    // 2. All writes are atomic (.tmp + File.Move) so the sync tool
    //    never propagates a half-written JSON file to the other
    //    computer.
    //
    // 3. Sync Now performs a two-way merge keyed on Id with
    //    LastModified (newest wins per item). Known limitation:
    //    there are no deletion tombstones yet, so an item deleted
    //    on one computer can be resurrected from a stale snapshot
    //    on the other until both have synced once.
    // ============================================================

    public class SyncManager
    {
        // ============================================================
        // CANDIDATE FOLDERS
        // ============================================================

        private static readonly string syncthingFolder =
            Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.UserProfile),
                "Syncthing",
                "LochlanProductivity");

        private static readonly string legacyOneDriveFolder =
            Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.UserProfile),
                "OneDrive",
                "OneDrive - Personal",
                "LochlanProductivity");

        private readonly string settingsDirectory =
            Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData),
                "LochlanProductivity");

        private string SettingsFilePath =>
            Path.Combine(settingsDirectory, "sync-config.json");

        private string? configuredSyncFolder;

        public SyncManager()
        {
            LoadConfig();
        }

        // ============================================================
        // SYNC LOCATION
        // ============================================================

        public string SyncFolder => ResolveSyncFolder();

        public string SyncFilePath =>
            Path.Combine(SyncFolder, "syncdata.json");

        public void SetSyncFolder(string folder)
        {
            configuredSyncFolder =
                string.IsNullOrWhiteSpace(folder)
                    ? null
                    : folder.Trim();

            SaveConfig();
        }

        private string ResolveSyncFolder()
        {
            if (!string.IsNullOrWhiteSpace(configuredSyncFolder))
            {
                return configuredSyncFolder!;
            }

            if (File.Exists(
                Path.Combine(syncthingFolder, "syncdata.json")))
            {
                return syncthingFolder;
            }

            if (File.Exists(
                Path.Combine(legacyOneDriveFolder, "syncdata.json")))
            {
                return legacyOneDriveFolder;
            }

            return syncthingFolder;
        }

        // ============================================================
        // CREATE SYNC DATA
        // ============================================================

        public SyncData CreateSyncData(
            IEnumerable<LochlanProductivity.TodoTask> tasks,
            IEnumerable<AppGroup> appGroups,
            IEnumerable<BlockingSchedule> blockingSchedules)
        {
            return new SyncData
            {
                Version = 1,

                LastModified = DateTime.UtcNow,

                Tasks =
                    new List<LochlanProductivity.TodoTask>(
                        tasks),

                AppGroups =
                    new List<AppGroup>(
                        appGroups),

                BlockingSchedules =
                    new List<BlockingSchedule>(
                        blockingSchedules)
            };
        }

        // ============================================================
        // SAVE SYNC DATA (atomic)
        // ============================================================

        public async Task<bool> SaveSyncDataAsync(
            SyncData data)
        {
            try
            {
                string syncFilePath = SyncFilePath;

                string? directory =
                    Path.GetDirectoryName(syncFilePath);

                if (string.IsNullOrWhiteSpace(directory))
                    return false;

                Directory.CreateDirectory(directory);

                data.LastModified = DateTime.UtcNow;

                JsonSerializerOptions options =
                    new JsonSerializerOptions
                    {
                        WriteIndented = true
                    };

                string json =
                    JsonSerializer.Serialize(data, options);

                string tempFilePath = syncFilePath + ".tmp";

                await File.WriteAllTextAsync(tempFilePath, json);

                // Atomic replace: readers and the sync tool only ever
                // see a complete file.
                File.Move(tempFilePath, syncFilePath, true);

                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"Failed to save sync data: {ex}");

                return false;
            }
        }

        // ============================================================
        // LOAD SYNC DATA
        // ============================================================

        public async Task<SyncData?> LoadSyncDataAsync()
        {
            try
            {
                string syncFilePath = SyncFilePath;

                if (!File.Exists(syncFilePath))
                    return null;

                string json =
                    await File.ReadAllTextAsync(syncFilePath);

                if (string.IsNullOrWhiteSpace(json))
                    return null;

                SyncData? data =
                    JsonSerializer.Deserialize<SyncData>(json);

                if (data == null)
                    return null;

                data.Tasks ??=
                    new List<LochlanProductivity.TodoTask>();

                data.AppGroups ??=
                    new List<AppGroup>();

                data.BlockingSchedules ??=
                    new List<BlockingSchedule>();

                return data;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"Failed to load sync data: {ex}");

                return null;
            }
        }

        public bool HasSyncData()
        {
            return File.Exists(SyncFilePath);
        }

        public async Task<DateTime?> GetLastModifiedAsync()
        {
            SyncData? data =
                await LoadSyncDataAsync();

            return data?.LastModified;
        }

        // ============================================================
        // UPLOAD CURRENT DATA (first-run bootstrap)
        // ============================================================

        public async Task<bool> UploadCurrentDataAsync(
            List<LochlanProductivity.TodoTask> tasks,
            AppGroupManager groupManager,
            ScheduleManager scheduleManager)
        {
            SyncData data =
                CreateSyncData(
                    tasks,
                    groupManager.Groups,
                    scheduleManager.Schedules);

            return await SaveSyncDataAsync(data);
        }

        // ============================================================
        // FULL TWO-WAY MERGE
        // ============================================================

        public async Task<SyncResult> SyncNowAsync(
            List<LochlanProductivity.TodoTask> tasks,
            AppGroupManager groupManager,
            ScheduleManager scheduleManager)
        {
            try
            {
                SyncData? sharedData =
                    await LoadSyncDataAsync();

                // ----------------------------------------------------
                // FIRST COMPUTER - no shared file yet, upload local.
                // ----------------------------------------------------

                if (sharedData == null)
                {
                    bool uploaded =
                        await UploadCurrentDataAsync(
                            tasks,
                            groupManager,
                            scheduleManager);

                    return new SyncResult
                    {
                        Success = uploaded,

                        Message = uploaded
                            ? "Created the shared sync file from this computer."
                            : $"Could not create {SyncFilePath}."
                    };
                }

                // ----------------------------------------------------
                // MERGE SHARED INTO LOCAL (newest wins per item).
                // ----------------------------------------------------

                int taskChanges =
                    MergeTasks(sharedData.Tasks, tasks);

                int groupChanges =
                    MergeGroups(sharedData.AppGroups, groupManager);

                int scheduleChanges =
                    MergeSchedules(
                        sharedData.BlockingSchedules,
                        scheduleManager);

                // ----------------------------------------------------
                // WRITE THE MERGED SNAPSHOT BACK so the other
                // computer picks up this side's newer items too.
                // ----------------------------------------------------

                SyncData merged =
                    CreateSyncData(
                        tasks,
                        groupManager.Groups,
                        scheduleManager.Schedules);

                bool saved =
                    await SaveSyncDataAsync(merged);

                return new SyncResult
                {
                    Success = saved,

                    TaskChanges = taskChanges,

                    GroupChanges = groupChanges,

                    ScheduleChanges = scheduleChanges,

                    Message = saved
                        ? $"{taskChanges} task(s), {groupChanges} group(s), {scheduleChanges} schedule(s) updated."
                        : $"Could not write {SyncFilePath}."
                };
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"Sync failed: {ex}");

                return new SyncResult
                {
                    Success = false,
                    Message = ex.Message
                };
            }
        }

        // ============================================================
        // MERGE HELPERS
        // ============================================================

        private int MergeTasks(
            List<LochlanProductivity.TodoTask> incoming,
            List<LochlanProductivity.TodoTask> local)
        {
            int changes = 0;

            foreach (
                LochlanProductivity.TodoTask incomingTask
                in incoming)
            {
                if (incomingTask == null ||
                    string.IsNullOrWhiteSpace(incomingTask.Id))
                {
                    continue;
                }

                LochlanProductivity.TodoTask? existing =
                    local.FirstOrDefault(
                        task =>
                            task.Id.Equals(
                                incomingTask.Id,
                                StringComparison.OrdinalIgnoreCase));

                if (existing == null)
                {
                    local.Add(incomingTask);

                    changes++;

                    continue;
                }

                if (incomingTask.LastModified > existing.LastModified)
                {
                    CopyTaskInto(existing, incomingTask);

                    changes++;
                }
            }

            return changes;
        }

        private void CopyTaskInto(
            LochlanProductivity.TodoTask target,
            LochlanProductivity.TodoTask source)
        {
            target.Title = source.Title;
            target.IsCompleted = source.IsCompleted;
            target.IsDeleted = source.IsDeleted;
            target.LastModified = source.LastModified;

            target.Priority = source.Priority;
            target.DueTimeOfDay = source.DueTimeOfDay;

            target.BlockedGroups =
                new List<string>(source.BlockedGroups ?? new());

            target.BlockedApps =
                new List<BlockedApp>(source.BlockedApps ?? new());

            target.IsRecurring = source.IsRecurring;
            target.Recurrence = source.Recurrence;
            target.RecurrenceInterval = source.RecurrenceInterval;
            target.RecurrenceDays =
                new List<DayOfWeek>(source.RecurrenceDays ?? new());
            target.DueDate = source.DueDate;
            target.LastCompletedDate = source.LastCompletedDate;
        }

        private int MergeGroups(
            List<AppGroup> incoming,
            AppGroupManager groupManager)
        {
            int changes = 0;

            foreach (AppGroup incomingGroup in incoming)
            {
                if (incomingGroup == null ||
                    string.IsNullOrWhiteSpace(incomingGroup.Id))
                {
                    continue;
                }

                AppGroup? existing =
                    groupManager.GetGroup(incomingGroup.Id);

                if (existing == null)
                {
                    incomingGroup.Apps ??= new List<BlockedApp>();

                    groupManager.Groups.Add(incomingGroup);

                    changes++;

                    continue;
                }

                if (incomingGroup.LastModified >
                    existing.LastModified)
                {
                    existing.Name = incomingGroup.Name;
                    existing.Description = incomingGroup.Description;
                    existing.Apps =
                        new List<BlockedApp>(
                            incomingGroup.Apps ?? new());
                    existing.LastModified =
                        incomingGroup.LastModified;

                    changes++;
                }
            }

            groupManager.Save();

            return changes;
        }

        private int MergeSchedules(
            List<BlockingSchedule> incoming,
            ScheduleManager scheduleManager)
        {
            int changes = 0;

            List<BlockingSchedule> merged =
                scheduleManager.Schedules.ToList();

            foreach (BlockingSchedule incomingSchedule in incoming)
            {
                if (incomingSchedule == null ||
                    string.IsNullOrWhiteSpace(incomingSchedule.Id))
                {
                    continue;
                }

                BlockingSchedule? existing =
                    merged.FirstOrDefault(
                        schedule =>
                            schedule.Id.Equals(
                                incomingSchedule.Id,
                                StringComparison.OrdinalIgnoreCase));

                if (existing == null)
                {
                    incomingSchedule.Days ??= new List<DayOfWeek>();

                    merged.Add(incomingSchedule);

                    changes++;

                    continue;
                }

                if (incomingSchedule.LastModified >
                    existing.LastModified)
                {
                    existing.Name = incomingSchedule.Name;
                    existing.IsEnabled = incomingSchedule.IsEnabled;
                    existing.StartTime = incomingSchedule.StartTime;
                    existing.EndTime = incomingSchedule.EndTime;
                    existing.Days =
                        new List<DayOfWeek>(
                            incomingSchedule.Days ?? new());
                    existing.LastModified =
                        incomingSchedule.LastModified;

                    changes++;
                }
            }

            scheduleManager.ReplaceSchedules(merged);

            return changes;
        }

        // ============================================================
        // CONFIG PERSISTENCE (atomic, DailyPrompt pattern)
        // ============================================================

        private void LoadConfig()
        {
            try
            {
                if (!File.Exists(SettingsFilePath))
                    return;

                string json =
                    File.ReadAllText(SettingsFilePath);

                SyncConfig? config =
                    JsonSerializer.Deserialize<SyncConfig>(
                        json,
                        new JsonSerializerOptions
                        {
                            PropertyNameCaseInsensitive = true
                        });

                configuredSyncFolder =
                    string.IsNullOrWhiteSpace(config?.SyncFolder)
                        ? null
                        : config!.SyncFolder!.Trim();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"Failed to load sync config: {ex}");
            }
        }

        private void SaveConfig()
        {
            try
            {
                Directory.CreateDirectory(settingsDirectory);

                string json =
                    JsonSerializer.Serialize(
                        new SyncConfig
                        {
                            SyncFolder = configuredSyncFolder
                        },
                        new JsonSerializerOptions
                        {
                            WriteIndented = true
                        });

                string tempFile = SettingsFilePath + ".tmp";

                File.WriteAllText(tempFile, json);

                File.Move(tempFile, SettingsFilePath, true);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"Failed to save sync config: {ex}");
            }
        }

        private class SyncConfig
        {
            public string? SyncFolder { get; set; }
        }
    }

    public class SyncResult
    {
        public bool Success { get; set; }

        public string Message { get; set; } = "";

        public int TaskChanges { get; set; }

        public int GroupChanges { get; set; }

        public int ScheduleChanges { get; set; }
    }
}
