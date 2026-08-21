using System;
using System.Collections.Generic;
using System.Linq;

namespace LochlanProductivity.Services
{
    public class PolicyManager
    {
        private readonly AppGroupManager groupManager;

        public PolicyManager(
            AppGroupManager groupManager)
        {
            this.groupManager =
                groupManager;
        }

        // ============================================================
        // GROUP ACCESS
        // ============================================================

        public IReadOnlyList<AppGroup> GetGroups()
        {
            return groupManager.Groups;
        }

        public AppGroup? GetGroup(
            string groupId)
        {
            return groupManager.GetGroup(
                groupId);
        }

        // ============================================================
        // DEFAULT TASK POLICY
        // ============================================================

        public void ApplyDefaultPolicy(
            TodoTask task)
        {
            if (task == null)
                return;

            if (!task.BlockedGroups.Contains(
                "games",
                StringComparer.OrdinalIgnoreCase))
            {
                task.BlockedGroups.Add(
                    "games");
            }

            ExpandTaskGroups(task);
        }

        // ============================================================
        // EXPAND GROUPS
        // ============================================================

        public void ExpandTaskGroups(
            TodoTask task)
        {
            if (task == null)
                return;

            foreach (string groupId
                in task.BlockedGroups)
            {
                AppGroup? group =
                    GetGroup(groupId);

                if (group == null)
                    continue;

                foreach (BlockedApp app
                    in group.Apps)
                {
                    bool alreadyExists =
                        task.BlockedApps.Any(
                            existing =>
                                PathsEqual(
                                    existing.ExecutablePath,
                                    app.ExecutablePath));

                    if (alreadyExists)
                        continue;

                    task.BlockedApps.Add(
                        new BlockedApp
                        {
                            Name =
                                app.Name,

                            ExecutablePath =
                                app.ExecutablePath
                        });
                }
            }
        }

        // ============================================================
        // EFFECTIVE BLOCKED APPS
        // ============================================================

        public List<BlockedApp>
            GetEffectiveBlockedApps(
                TodoTask task)
        {
            List<BlockedApp> result =
                new();

            if (task == null)
                return result;

            foreach (string groupId
                in task.BlockedGroups)
            {
                AppGroup? group =
                    GetGroup(groupId);

                if (group == null)
                    continue;

                foreach (BlockedApp app
                    in group.Apps)
                {
                    AddIfMissing(
                        result,
                        app);
                }
            }

            foreach (BlockedApp app
                in task.BlockedApps)
            {
                AddIfMissing(
                    result,
                    app);
            }

            return result;
        }

        private void AddIfMissing(
            List<BlockedApp> apps,
            BlockedApp app)
        {
            bool exists =
                apps.Any(
                    existing =>
                        PathsEqual(
                            existing.ExecutablePath,
                            app.ExecutablePath));

            if (exists)
                return;

            apps.Add(
                new BlockedApp
                {
                    Name =
                        app.Name,

                    ExecutablePath =
                        app.ExecutablePath
                });
        }

        // ============================================================
        // POLICY EVALUATION
        // ============================================================

        public bool ShouldBlockTask(
            TodoTask task)
        {
            if (task == null)
                return false;

            if (task.IsCompleted)
                return false;

            return task.BlockedGroups.Count > 0 ||
                   task.BlockedApps.Count > 0;
        }

        public List<BlockedApp>
            GetAppsToBlock(
                IEnumerable<TodoTask> tasks)
        {
            List<BlockedApp> result =
                new();

            foreach (TodoTask task in tasks)
            {
                if (!ShouldBlockTask(task))
                    continue;

                foreach (BlockedApp app
                    in GetEffectiveBlockedApps(task))
                {
                    AddIfMissing(
                        result,
                        app);
                }
            }

            return result;
        }

        // ============================================================
        // PATH COMPARISON
        // ============================================================

        private bool PathsEqual(
            string first,
            string second)
        {
            if (string.IsNullOrWhiteSpace(first) ||
                string.IsNullOrWhiteSpace(second))
            {
                return false;
            }

            try
            {
                return System.IO.Path.GetFullPath(first)
                    .Equals(
                        System.IO.Path.GetFullPath(second),
                        StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return first.Equals(
                    second,
                    StringComparison.OrdinalIgnoreCase);
            }
        }
    }
}