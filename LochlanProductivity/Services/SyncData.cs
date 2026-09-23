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

        // Website removal tombstones so a deletion on one computer
        // is not resurrected by the other's stale list. Missing on
        // old syncdata.json files.
        public List<string> RemovedBlockedSites { get; set; } =
            new();

        // ============================================================
        // DAILY PROMPT
        //
        // LastPromptDate from DailyPromptManager, synced so a daily
        // plan added on one computer suppresses the prompt on the
        // other. Nullable for backwards compat with old syncdata.json.
        // ============================================================

        public DateTime? LastDailyPromptDate { get; set; }

        // Long-term sticky note (free-writing alternative to the
        // Long-term list). Newest timestamp wins; missing on old
        // syncdata.json files.
        public string LongTermNote { get; set; } = "";

        public DateTime? LongTermNoteModified { get; set; }

        // Planning pages (ordered step lists). Whole-page
        // newest-wins merge; missing on old syncdata.json files.
        public List<PlanPage> PlanPages { get; set; } =
            new();
    }
}