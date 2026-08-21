using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using LochlanProductivity;

namespace LochlanProductivity.Services
{
    public class AppGroupManager
    {
        // ============================================================
        // GROUP STORAGE
        // ============================================================

        private readonly List<AppGroup> groups = new();

        private readonly string saveFilePath =
            Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.UserProfile),
                "LochlanProductivityData",
                "appgroups.json");

        // ============================================================
        // CONSTRUCTOR
        // ============================================================

        public AppGroupManager()
        {
            CreateDefaultGroups();
            Load();
        }

        // IMPORTANT:
        // MainWindow currently needs to be able to Clear(), Add(),
        // Remove(), and AddRange() this collection.
        public List<AppGroup> Groups => groups;

        // ============================================================
        // DEFAULT GROUPS
        // ============================================================

        private void CreateDefaultGroups()
        {
            groups.Clear();

            // --------------------------------------------------------
            // GAMES
            // --------------------------------------------------------

            AppGroup games = new AppGroup
            {
                Id = "games",
                Name = "Games",
                Description = "Games and game launchers"
            };

            games.Apps.Add(
                new BlockedApp
                {
                    Name = "Steam",
                    ExecutablePath = "steam.exe"
                });

            games.Apps.Add(
                new BlockedApp
                {
                    Name = "MTG Arena",
                    ExecutablePath = "MTGA.exe"
                });

            games.Apps.Add(
                new BlockedApp
                {
                    Name = "Minecraft",
                    ExecutablePath = "MinecraftLauncher.exe"
                });

            groups.Add(games);

            // --------------------------------------------------------
            // SOCIAL
            // --------------------------------------------------------

            AppGroup social = new AppGroup
            {
                Id = "social",
                Name = "Social",
                Description = "Social and communication applications"
            };

            social.Apps.Add(
                new BlockedApp
                {
                    Name = "Discord",
                    ExecutablePath = "Discord.exe"
                });

            groups.Add(social);
        }

        // ============================================================
        // FIND GROUP
        // ============================================================

        public AppGroup? GetGroup(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
                return null;

            return groups.FirstOrDefault(
                group =>
                    group.Id.Equals(
                        id,
                        StringComparison.OrdinalIgnoreCase));
        }

        // ============================================================
        // CREATE GROUP
        // ============================================================

        public AppGroup CreateGroup(
            string name,
            string description = "")
        {
            name = name.Trim();
            description = description.Trim();

            string baseId = MakeId(name);

            string id = baseId;

            int number = 2;

            while (GetGroup(id) != null)
            {
                id = $"{baseId}-{number}";
                number++;
            }

            AppGroup group = new AppGroup
            {
                Id = id,
                Name = name,
                Description = description
            };

            groups.Add(group);

            Save();

            return group;
        }

        // ============================================================
        // DELETE GROUP
        // ============================================================

        public bool DeleteGroup(string id)
        {
            AppGroup? group = GetGroup(id);

            if (group == null)
                return false;

            // Don't allow the built-in Games group to accidentally
            // disappear.
            //
            // This isn't strictly required, but Games is the default
            // group used by new tasks.
            if (group.Id.Equals(
                "games",
                StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            groups.Remove(group);

            Save();

            return true;
        }

        // ============================================================
        // RENAME GROUP
        // ============================================================

        public bool RenameGroup(
            string id,
            string newName)
        {
            if (string.IsNullOrWhiteSpace(newName))
                return false;

            AppGroup? group = GetGroup(id);

            if (group == null)
                return false;

            bool duplicate =
                groups.Any(
                    existing =>
                        existing != group &&
                        existing.Name.Equals(
                            newName.Trim(),
                            StringComparison.OrdinalIgnoreCase));

            if (duplicate)
                return false;

            group.Name = newName.Trim();

            Save();

            return true;
        }

        // ============================================================
        // UPDATE GROUP DESCRIPTION
        // ============================================================

        public bool SetDescription(
            string id,
            string description)
        {
            AppGroup? group = GetGroup(id);

            if (group == null)
                return false;

            group.Description =
                description?.Trim() ?? "";

            Save();

            return true;
        }

        // ============================================================
        // ADD APP TO GROUP
        // ============================================================

        public bool AddAppToGroup(
            string groupId,
            BlockedApp app)
        {
            if (app == null)
                return false;

            if (string.IsNullOrWhiteSpace(
                app.ExecutablePath))
            {
                return false;
            }

            AppGroup? group =
                GetGroup(groupId);

            if (group == null)
                return false;

            bool exists =
                group.Apps.Any(
                    existing =>
                        PathsEqual(
                            existing.ExecutablePath,
                            app.ExecutablePath));

            if (exists)
                return false;

            group.Apps.Add(
                new BlockedApp
                {
                    Id = Guid.NewGuid().ToString(),

                    Name =
                        app.Name,

                    ExecutablePath =
                        app.ExecutablePath
                });

            Save();

            return true;
        }

        // ============================================================
        // REMOVE APP FROM GROUP
        // ============================================================

        public bool RemoveAppFromGroup(
            string groupId,
            string executablePath)
        {
            AppGroup? group =
                GetGroup(groupId);

            if (group == null)
                return false;

            BlockedApp? app =
                group.Apps.FirstOrDefault(
                    existing =>
                        PathsEqual(
                            existing.ExecutablePath,
                            executablePath));

            if (app == null)
                return false;

            group.Apps.Remove(app);

            Save();

            return true;
        }

        // ============================================================
        // CHECK WHETHER APP IS IN GROUP
        // ============================================================

        public bool GroupContainsApp(
            string groupId,
            string executablePath)
        {
            AppGroup? group =
                GetGroup(groupId);

            if (group == null)
                return false;

            return group.Apps.Any(
                app =>
                    PathsEqual(
                        app.ExecutablePath,
                        executablePath));
        }

        // ============================================================
        // SAVE
        // ============================================================

        public void Save()
        {
            try
            {
                string? directory =
                    Path.GetDirectoryName(
                        saveFilePath);

                if (string.IsNullOrWhiteSpace(directory))
                    return;

                Directory.CreateDirectory(
                    directory);

                JsonSerializerOptions options =
                    new JsonSerializerOptions
                    {
                        WriteIndented = true
                    };

                string json =
                    JsonSerializer.Serialize(
                        groups,
                        options);

                File.WriteAllText(
                    saveFilePath,
                    json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"Failed to save app groups: {ex}");
            }
        }

        // ============================================================
        // LOAD
        // ============================================================

        private void Load()
        {
            try
            {
                if (!File.Exists(saveFilePath))
                {
                    Save();
                    return;
                }

                string json =
                    File.ReadAllText(
                        saveFilePath);

                List<AppGroup>? loaded =
                    JsonSerializer.Deserialize<
                        List<AppGroup>>(json);

                if (loaded == null)
                    return;

                // ----------------------------------------------------
                // Merge saved groups into the defaults.
                // ----------------------------------------------------

                foreach (AppGroup loadedGroup in loaded)
                {
                    if (loadedGroup == null)
                        continue;

                    // Make sure Apps is never null.
                    loadedGroup.Apps ??=
                        new List<BlockedApp>();

                    AppGroup? existing =
                        GetGroup(
                            loadedGroup.Id);

                    if (existing == null)
                    {
                        groups.Add(
                            loadedGroup);

                        continue;
                    }

                    existing.Name =
                        loadedGroup.Name;

                    existing.Description =
                        loadedGroup.Description;

                    existing.Apps =
                        loadedGroup.Apps;
                }

                // ----------------------------------------------------
                // Remove duplicate applications from every group.
                // ----------------------------------------------------

                foreach (AppGroup group in groups)
                {
                    List<BlockedApp> uniqueApps =
                        new List<BlockedApp>();

                    foreach (BlockedApp app in group.Apps)
                    {
                        if (string.IsNullOrWhiteSpace(
                            app.ExecutablePath))
                        {
                            continue;
                        }

                        // Give older saved applications an ID.
                        if (string.IsNullOrWhiteSpace(app.Id))
                        {
                            app.Id = Guid.NewGuid().ToString();
                        }

                        bool alreadyExists =
                            uniqueApps.Any(
                                existing =>
                                    PathsEqual(
                                        existing.ExecutablePath,
                                        app.ExecutablePath));

                        if (!alreadyExists)
                        {
                            uniqueApps.Add(
                                app);
                        }
                    }

                    group.Apps =
                        uniqueApps;
                }

                Save();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"Failed to load app groups: {ex}");
            }
        }

        // ============================================================
        // ID GENERATION
        // ============================================================

        private string MakeId(
            string name)
        {
            string id =
                new string(
                    name
                        .ToLowerInvariant()
                        .Where(
                            character =>
                                char.IsLetterOrDigit(
                                    character) ||
                                character == ' ')
                        .ToArray())
                    .Trim()
                    .Replace(
                        " ",
                        "-");

            if (string.IsNullOrWhiteSpace(id))
            {
                id = "group";
            }

            return id;
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
                string firstFull =
                    Path.GetFullPath(first);

                string secondFull =
                    Path.GetFullPath(second);

                return firstFull.Equals(
                    secondFull,
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