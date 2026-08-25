using System;
using System.Collections.Generic;

namespace LochlanProductivity.Services
{
    public class AppGroup
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();

        public string Name { get; set; } = "";

        public string Description { get; set; } = "";

        // Used by SyncManager to merge changes between computers.
        public DateTime LastModified { get; set; } = DateTime.UtcNow;

        public List<LochlanProductivity.BlockedApp> Apps { get; set; } = new();
    }
}