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
    //          > Syncthing config.xml folders (first is
    //            ~/LochlanProductivityData)
    //          > ~/Syncthing/LochlanProductivity (legacy)
    //          > OneDrive/LochlanProductivity (legacy)
    //
    // 2. All writes are atomic (.tmp + File.Move) so the sync tool
    //    never propagates a half-written JSON file to the other
    //    computer.
    //
    // 3. Sync Now performs a two-way merge keyed on Id with
    //    LastModified (newest wins per item). Deletions use
    //    soft-delete tombstones (TodoTask.IsDeleted) kept 30 days
    //    so they propagate; raw tasks.json Syncthing conflicts
    //    (tasks.sync-conflict*.json) are merged on next load.
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
            string? previousFolder =
                string.IsNullOrWhiteSpace(configuredSyncFolder)
                    ? null
                    : configuredSyncFolder;

            // Remember where the file currently lives before changing.
            string? previousFile = null;

            if (previousFolder == null)
            {
                // No explicit config: file is in the resolved folder.
                try { previousFile = SyncFilePath; } catch { }
            }
            else
            {
                previousFile =
                    Path.Combine(previousFolder, "syncdata.json");
            }

            configuredSyncFolder =
                string.IsNullOrWhiteSpace(folder)
                    ? null
                    : folder.Trim();

            SaveConfig();

            // If the new folder has no file but the old one did,
            // copy it so no data is lost on a folder change.
            try
            {
                string newFile = SyncFilePath;

                if (!File.Exists(newFile) &&
                    previousFile != null &&
                    File.Exists(previousFile))
                {
                    Directory.CreateDirectory(
                        Path.GetDirectoryName(newFile)!);

                    File.Copy(previousFile, newFile);
                }
            }
            catch
            {
            }
        }

        private string ResolveSyncFolder()
        {
            if (!string.IsNullOrWhiteSpace(configuredSyncFolder))
            {
                return configuredSyncFolder!;
            }

            List<string> syncthingFolders =
                GetSyncthingFolderCandidates();

            // Prefer a Syncthing folder that already has data.
            foreach (string folder in syncthingFolders)
            {
                if (File.Exists(Path.Combine(folder, "syncdata.json")))
                {
                    return folder;
                }
            }

            // No Syncthing folder has data yet: use the first
            // Syncthing folder so the shared file actually syncs.
            // If the legacy OneDrive file exists, migrate it.
            if (syncthingFolders.Count > 0)
            {
                string target = syncthingFolders[0];

                TryMigrateLegacySyncData(target);

                return target;
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

        private void TryMigrateLegacySyncData(string targetFolder)
        {
            try
            {
                string targetFile =
                    Path.Combine(targetFolder, "syncdata.json");

                if (File.Exists(targetFile))
                    return;

                string legacyFile =
                    Path.Combine(legacyOneDriveFolder, "syncdata.json");

                if (!File.Exists(legacyFile))
                    return;

                Directory.CreateDirectory(targetFolder);

                File.Copy(legacyFile, targetFile);
            }
            catch
            {
            }
        }

        private List<string> GetSyncthingFolderCandidates()
        {
            List<string> candidates = new();

            foreach (string configPath in new[]
            {
                Path.Combine(
                    Environment.GetFolderPath(
                        Environment.SpecialFolder.LocalApplicationData),
                    "Syncthing",
                    "config.xml"),

                Path.Combine(
                    Environment.GetFolderPath(
                        Environment.SpecialFolder.ApplicationData),
                    "Syncthing",
                    "config.xml")
            })
            {
                try
                {
                    if (!File.Exists(configPath))
                        continue;

                    string xml =
                        File.ReadAllText(configPath);

                    int searchFrom = 0;

                    while (true)
                    {
                        int folderIndex =
                            xml.IndexOf(
                                "<folder ",
                                searchFrom,
                                StringComparison.OrdinalIgnoreCase);

                        if (folderIndex < 0)
                            break;

                        int pathIndex =
                            xml.IndexOf(
                                "path=\"",
                                folderIndex,
                                StringComparison.OrdinalIgnoreCase);

                        if (pathIndex < 0)
                        {
                            searchFrom = folderIndex + 8;
                            continue;
                        }

                        int valueStart = pathIndex + 6;

                        int valueEnd =
                            xml.IndexOf('"', valueStart);

                        if (valueEnd < 0)
                            break;

                        string folderPath =
                            xml.Substring(valueStart, valueEnd - valueStart);

                        if (!string.IsNullOrWhiteSpace(folderPath) &&
                            !candidates.Contains(
                                folderPath,
                                StringComparer.OrdinalIgnoreCase))
                        {
                            candidates.Add(folderPath);
                        }

                        searchFrom = valueEnd + 1;
                    }
                }
                catch
                {
                }
            }

            return candidates;
        }

        // ============================================================
        // CREATE SYNC DATA
        // ============================================================

        public SyncData CreateSyncData(
            IEnumerable<LochlanProductivity.TodoTask> tasks,
            IEnumerable<AppGroup> appGroups,
            IEnumerable<BlockingSchedule> blockingSchedules,
            IEnumerable<string> blockedSites,
            DateTime? lastDailyPromptDate = null,
            string? longTermNote = null,
            DateTime? longTermNoteModified = null)
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
                        blockingSchedules),

                BlockedSites =
                    new List<string>(
                        blockedSites),

                LastDailyPromptDate =
                    lastDailyPromptDate?.Date,

                LongTermNote =
                    longTermNote ?? "",

                LongTermNoteModified =
                    longTermNoteModified
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

                data.BlockedSites ??=
                    new List<string>();

                data.LongTermNote ??= "";

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
            ScheduleManager scheduleManager,
            BlockedSitesManager blockedSites,
            DateTime? lastDailyPromptDate = null,
            string? longTermNote = null,
            DateTime? longTermNoteModified = null)
        {
            SyncData data =
                CreateSyncData(
                    tasks,
                    groupManager.Groups,
                    scheduleManager.Schedules,
                    blockedSites.Domains,
                    lastDailyPromptDate,
                    longTermNote,
                    longTermNoteModified);

            return await SaveSyncDataAsync(data);
        }

        // ============================================================
        // FULL TWO-WAY MERGE
        // ============================================================

        // Persistent sync diagnostics (%LocalAppData%/
        // LochlanProductivity/sync.log). Release builds have no
        // debugger, so merges and failures must be visible on disk.
        public static void Log(string message)
        {
            try
            {
                string directory =
                    Path.Combine(
                        Environment.GetFolderPath(
                            Environment.SpecialFolder.LocalApplicationData),
                        "LochlanProductivity");

                Directory.CreateDirectory(directory);

                string path =
                    Path.Combine(directory, "sync.log");

                try
                {
                    if (File.Exists(path) &&
                        new FileInfo(path).Length > 262144)
                    {
                        File.Delete(path);
                    }
                }
                catch
                {
                }

                File.AppendAllText(
                    path,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
            }
            catch
            {
            }
        }

        public async Task<SyncResult> SyncNowAsync(
            List<LochlanProductivity.TodoTask> tasks,
            AppGroupManager groupManager,
            ScheduleManager scheduleManager,
            BlockedSitesManager blockedSites,
            DateTime? localDailyPromptDate = null,
            string? localLongTermNote = null,
            DateTime? localLongTermNoteModified = null)
        {
            try
            {
                Log(
                    $"sync start: local={tasks.Count} " +
                    $"folder={SyncFolder}");

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
                            scheduleManager,
                            blockedSites,
                            localDailyPromptDate,
                            localLongTermNote,
                            localLongTermNoteModified);

                    return new SyncResult
                    {
                        Success = uploaded,

                        Message = uploaded
                            ? "Created the shared sync file from this computer."
                            : $"Could not create {SyncFilePath}.",

                        MergedDailyPromptDate = localDailyPromptDate?.Date,

                        MergedNoteText = localLongTermNote,
                        MergedNoteModified = localLongTermNoteModified
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

                int siteChanges =
                    MergeBlockedSites(
                        sharedData.BlockedSites,
                        blockedSites);

                // Daily prompt: newest date wins. If the other
                // computer already did today's plan, this computer
                // should not prompt again.
                DateTime? remotePromptDate =
                    sharedData.LastDailyPromptDate?.Date;

                DateTime? localPromptDate =
                    localDailyPromptDate?.Date;

                DateTime? mergedPromptDate = null;

                if (remotePromptDate != null &&
                    localPromptDate != null)
                {
                    mergedPromptDate =
                        remotePromptDate > localPromptDate
                            ? remotePromptDate
                            : localPromptDate;
                }
                else
                {
                    mergedPromptDate =
                        remotePromptDate ?? localPromptDate;
                }

                bool dailyPromptChanged =
                    remotePromptDate != null &&
                    (localPromptDate == null ||
                     remotePromptDate > localPromptDate);

                // Long-term sticky note: newest timestamp wins.
                DateTime? remoteNoteModified =
                    sharedData.LongTermNoteModified;

                string remoteNoteText =
                    sharedData.LongTermNote ?? "";

                string localNoteText = localLongTermNote ?? "";

                string mergedNoteText = localNoteText;
                DateTime? mergedNoteModified = localLongTermNoteModified;

                if (remoteNoteModified != null &&
                    (localLongTermNoteModified == null ||
                     remoteNoteModified > localLongTermNoteModified))
                {
                    mergedNoteModified = remoteNoteModified;

                    if (!remoteNoteText.Equals(
                        localNoteText,
                        StringComparison.Ordinal))
                    {
                        mergedNoteText = remoteNoteText;
                    }
                }

                bool noteChanged =
                    remoteNoteModified != null &&
                    (localLongTermNoteModified == null ||
                     remoteNoteModified > localLongTermNoteModified) &&
                    !remoteNoteText.Equals(
                        localNoteText,
                        StringComparison.Ordinal);

                // ----------------------------------------------------
                // WRITE THE MERGED SNAPSHOT BACK so the other
                // computer picks up this side's newer items too.
                // ----------------------------------------------------

                SyncData merged =
                    CreateSyncData(
                        tasks,
                        groupManager.Groups,
                        scheduleManager.Schedules,
                        blockedSites.Domains,
                        mergedPromptDate,
                        mergedNoteText,
                        mergedNoteModified);

                bool saved =
                    await SaveSyncDataAsync(merged);

                Log(
                    $"sync merged: tasks={taskChanges} " +
                    $"groups={groupChanges} " +
                    $"schedules={scheduleChanges} " +
                    $"sites={siteChanges} " +
                    $"prompt={dailyPromptChanged} " +
                    $"note={noteChanged} saved={saved}");

                return new SyncResult
                {
                    Success = saved,

                    TaskChanges = taskChanges,

                    GroupChanges = groupChanges,

                    ScheduleChanges = scheduleChanges,

                    SiteChanges = siteChanges,

                    DailyPromptChanged = dailyPromptChanged,

                    MergedDailyPromptDate = mergedPromptDate,

                    NoteChanged = noteChanged,

                    MergedNoteText = mergedNoteText,

                    MergedNoteModified = mergedNoteModified,

                    Message = saved
                        ? $"{taskChanges} task(s), {groupChanges} group(s), {scheduleChanges} schedule(s), {siteChanges} site(s) updated."
                        : $"Could not write {SyncFilePath}."
                };
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"Sync failed: {ex}");

                Log($"sync FAILED: {ex.Message}");

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

                // Deletions are tombstones — they must stick. If either
                // side is already deleted, keep it deleted and never
                // resurrect from the other side's newer edit.
                if (existing.IsDeleted)
                {
                    // Keep tombstone, but bump LastModified so the
                    // deletion propagates if incoming is newer.
                    if (incomingTask.LastModified > existing.LastModified)
                    {
                        existing.LastModified = incomingTask.LastModified;
                        changes++;
                    }
                    continue;
                }

                if (incomingTask.IsDeleted && !existing.IsDeleted)
                {
                    existing.IsDeleted = true;
                    existing.LastModified = incomingTask.LastModified > existing.LastModified
                        ? incomingTask.LastModified
                        : DateTime.UtcNow;
                    changes++;
                    continue;
                }

                if (incomingTask.LastModified > existing.LastModified)
                {
                    CopyTaskInto(existing, incomingTask);
                    changes++;
                }
                else if (incomingTask.LastModified == existing.LastModified)
                {
                    // Equal timestamp but diverged state (same-second
                    // edits on two machines). Preserve completions and
                    // deletions — if either side is completed/deleted,
                    // the result should be.
                    bool needsUpdate = false;

                    if (incomingTask.IsCompleted != existing.IsCompleted)
                    {
                        if (incomingTask.IsCompleted)
                        {
                            existing.IsCompleted = true;
                            needsUpdate = true;
                        }
                        // If local is completed and incoming is not,
                        // keep local completed — no update needed.
                    }

                    if (incomingTask.IsDeleted != existing.IsDeleted)
                    {
                        if (incomingTask.IsDeleted)
                        {
                            existing.IsDeleted = true;
                            needsUpdate = true;
                        }
                    }

                    if (incomingTask.IsLongTerm != existing.IsLongTerm)
                    {
                        existing.IsLongTerm = incomingTask.IsLongTerm;
                        needsUpdate = true;
                    }

                    // Title/priority/order etc should still converge
                    // to incoming when equal but different — newest
                    // writer wins.
                    if (incomingTask.Title != existing.Title ||
                        incomingTask.Priority != existing.Priority)
                    {
                        existing.Title = incomingTask.Title;
                        existing.Priority = incomingTask.Priority;
                        needsUpdate = true;
                    }

                    if (incomingTask.SortOrder != existing.SortOrder)
                    {
                        existing.SortOrder = incomingTask.SortOrder;
                        needsUpdate = true;
                    }

                    if (needsUpdate)
                    {
                        existing.LastModified = DateTime.UtcNow;
                        changes++;
                    }
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
            target.IsLongTerm = source.IsLongTerm;
            target.SortOrder = source.SortOrder;
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

                if (incomingGroup.LastModified >=
                    existing.LastModified)
                {
                    if (incomingGroup.LastModified == existing.LastModified &&
                        incomingGroup.Name == existing.Name &&
                        incomingGroup.Description == existing.Description &&
                        string.Equals(
                            incomingGroup.Color ?? "",
                            existing.Color ?? "",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    existing.Name = incomingGroup.Name;
                    existing.Description = incomingGroup.Description;
                    existing.Color = incomingGroup.Color ?? "";
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

                if (incomingSchedule.LastModified >=
                    existing.LastModified)
                {
                    if (incomingSchedule.LastModified == existing.LastModified &&
                        incomingSchedule.Name == existing.Name &&
                        incomingSchedule.IsEnabled == existing.IsEnabled &&
                        incomingSchedule.WarnMinutesBefore ==
                            existing.WarnMinutesBefore)
                    {
                        continue;
                    }

                    existing.Name = incomingSchedule.Name;
                    existing.IsEnabled = incomingSchedule.IsEnabled;
                    existing.StartTime = incomingSchedule.StartTime;
                    existing.EndTime = incomingSchedule.EndTime;
                    existing.WarnMinutesBefore =
                        incomingSchedule.WarnMinutesBefore;
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

        private int MergeBlockedSites(
            List<string> incoming,
            BlockedSitesManager blockedSites)
        {
            // Union semantics: a site added on either computer ends
            // up blocking on both. See SyncData.BlockedSites.
            return blockedSites.Absorb(incoming);
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

        public int SiteChanges { get; set; }

        public bool DailyPromptChanged { get; set; }

        public DateTime? MergedDailyPromptDate { get; set; }

        public bool NoteChanged { get; set; }

        public string? MergedNoteText { get; set; }

        public DateTime? MergedNoteModified { get; set; }
    }
}
