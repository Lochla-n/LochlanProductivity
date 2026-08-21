using System;
using System.Collections.Generic;

namespace LochlanProductivity.Services
{
    public class AppGroup
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();

        public string Name { get; set; } = "";

        public string Description { get; set; } = "";

        public List<LochlanProductivity.BlockedApp> Apps { get; set; } = new();
    }
}