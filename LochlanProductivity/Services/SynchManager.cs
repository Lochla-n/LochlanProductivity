using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace LochlanProductivity.Services
{
    public class SyncManager
    {
        // ============================================================
        // LOCAL SYNC FILE
        // ============================================================

        private readonly string syncFilePath =
            Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.UserProfile),
                "OneDrive",
                "OneDrive - Personal",
                "LochlanProductivity",
                "syncdata.json");

        // ============================================================
        // CREATE SYNC DATA
        // ============================================================

        public SyncData CreateSyncData(
            IEnumerable<TodoTask> tasks,
            IEnumerable<AppGroup> appGroups,
            IEnumerable<BlockingSchedule> blockingSchedules)
        {
            return new SyncData
            {
                Version = 1,

                LastModified = DateTime.UtcNow,

                Tasks = new List<TodoTask>(tasks),

                AppGroups = new List<AppGroup>(appGroups),

                BlockingSchedules =
                    new List<BlockingSchedule>(
                        blockingSchedules)
            };
        }

        // ============================================================
        // SAVE SYNC DATA
        // ============================================================

        public async Task SaveSyncDataAsync(
            SyncData data)
        {
            try
            {
                string? directory =
                    Path.GetDirectoryName(
                        syncFilePath);

                if (string.IsNullOrWhiteSpace(directory))
                    return;

                Directory.CreateDirectory(
                    directory);

                data.LastModified =
                    DateTime.UtcNow;

                JsonSerializerOptions options =
                    new JsonSerializerOptions
                    {
                        WriteIndented = true
                    };

                string json =
                    JsonSerializer.Serialize(
                        data,
                        options);

                await File.WriteAllTextAsync(
                    syncFilePath,
                    json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"Failed to save sync data: {ex}");
            }
        }

        // ============================================================
        // LOAD SYNC DATA
        // ============================================================

        public async Task<SyncData?> LoadSyncDataAsync()
        {
            try
            {
                if (!File.Exists(syncFilePath))
                    return null;

                string json =
                    await File.ReadAllTextAsync(
                        syncFilePath);

                SyncData? data =
                    JsonSerializer.Deserialize<SyncData>(
                        json);

                if (data == null)
                    return null;

                // Make sure older sync files don't produce null lists.
                data.Tasks ??=
                    new List<TodoTask>();

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

        // ============================================================
        // CHECK WHETHER SYNC DATA EXISTS
        // ============================================================

        public bool HasSyncData()
        {
            return File.Exists(
                syncFilePath);
        }

        // ============================================================
        // GET LAST MODIFIED TIME
        // ============================================================

        public async Task<DateTime?> GetLastModifiedAsync()
        {
            SyncData? data =
                await LoadSyncDataAsync();

            return data?.LastModified;
        }

        // ============================================================
        // GET LOCAL SYNC DATA
        // ============================================================

        public async Task<SyncData?> GetLocalSyncDataAsync()
        {
            return await LoadSyncDataAsync();
        }

        // ============================================================
        // REPLACE SCHEDULES FROM SYNC DATA
        // ============================================================

        public void ApplySchedules(
            SyncData data,
            ScheduleManager scheduleManager)
        {
            if (data == null)
                return;

            if (data.BlockingSchedules == null)
                return;

            // ScheduleManager intentionally exposes its collection
            // as IReadOnlyList, so we use its replacement method.
            scheduleManager.ReplaceSchedules(
                data.BlockingSchedules);
        }

        public async Task<bool> SyncNowAsync(
    List<TodoTask> tasks,
    AppGroupManager groupManager,
    ScheduleManager scheduleManager)
        {
            try
            {
                // ------------------------------------------------------------
                // Load the shared OneDrive sync file
                // ------------------------------------------------------------

                SyncData? cloudData =
                    await LoadSyncDataAsync();

                // ------------------------------------------------------------
                // Nothing exists in OneDrive yet.
                // Create the first sync file.
                // ------------------------------------------------------------

                if (cloudData == null)
                {
                    SyncData newData =
                        CreateSyncData(
                            tasks,
                            groupManager.Groups,
                            scheduleManager.Schedules);

                    await SaveSyncDataAsync(newData);

                    return true;
                }

                // ------------------------------------------------------------
                // For now, use the newest copy.
                //
                // Later we'll make this smarter so changes from both
                // computers can be merged instead of choosing one copy.
                // ------------------------------------------------------------

                DateTime cloudModified =
                    cloudData.LastModified;

                DateTime localModified =
                    File.Exists(syncFilePath)
                        ? File.GetLastWriteTimeUtc(syncFilePath)
                        : DateTime.MinValue;

                // ------------------------------------------------------------
                // Load the shared data.
                // ------------------------------------------------------------

                ApplySyncData(
                    cloudData,
                    tasks,
                    groupManager,
                    scheduleManager);

                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"Sync failed: {ex}");

                return false;
            }
        }

        // ============================================================
        // APPLY SYNC DATA (cloud -> local)
        // ============================================================
        private void ApplySyncData(
            SyncData data,
            List<TodoTask> tasks,
            AppGroupManager groupManager,
            ScheduleManager scheduleManager)
        {
            if (data == null)
                return;

            // Tasks: caller passed the live list, so mutate it in place
            // rather than reassigning.
            tasks.Clear();

            if (data.Tasks != null)
                tasks.AddRange(data.Tasks);

            // App groups: Groups is a plain mutable List<AppGroup>, so
            // update it directly and persist via the manager's own Save().
            if (data.AppGroups != null)
            {
                groupManager.Groups.Clear();
                groupManager.Groups.AddRange(data.AppGroups);
                groupManager.Save();
            }

            // Blocking schedules: reuse the existing helper.
            ApplySchedules(data, scheduleManager);
        }
    }
}