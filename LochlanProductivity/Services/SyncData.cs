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
    }
}