using System;
using System.Collections.Generic;

namespace LochlanProductivity.Services
{
    public class SyncData
    {
        // ============================================================
        // SYNC INFORMATION
        // ============================================================

        public int Version { get; set; } = 1;

        public DateTime LastModified { get; set; } =
            DateTime.UtcNow;

        // ============================================================
        // TASKS
        // ============================================================

        public List<LochlanProductivity.TodoTask> Tasks { get; set; } =
            new();

        // ============================================================
        // APP GROUPS
        // ============================================================

        public List<AppGroup> AppGroups { get; set; } =
            new();

        // ============================================================
        // BLOCKING SCHEDULES
        // ============================================================

        public List<BlockingSchedule> BlockingSchedules { get; set; } =
            new();

        // ============================================================
        // BLOCKED WEBSITES
        //
        // Merged as a UNION across computers: adding a site anywhere
        // blocks it everywhere. Deletions do NOT propagate yet (a
        // removed site can return after the other computer syncs)
        // - acceptable for an always-growing blocklist.
        // ============================================================

        public List<string> BlockedSites { get; set; } =
            new();
    }
}