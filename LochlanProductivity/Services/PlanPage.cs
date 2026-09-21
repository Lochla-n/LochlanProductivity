using System;
using System.Collections.Generic;

namespace LochlanProductivity.Services
{
    // A planning page: an ordered list of dateless, non-blocking
    // steps for breaking down a bigger goal ("learn to draw" ->
    // perspective, figure drawing, ...). Order IS the list order.
    // Whole-page newest-wins sync (see SyncManager.MergePlanPages).
    public class PlanPage
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();

        public string Title { get; set; } = "";

        public bool IsDeleted { get; set; } = false;

        public DateTime LastModified { get; set; } = DateTime.UtcNow;

        public List<PlanStep> Steps { get; set; } = new();
    }

    public class PlanStep
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();

        public string Title { get; set; } = "";

        public bool IsCompleted { get; set; } = false;
    }
}
