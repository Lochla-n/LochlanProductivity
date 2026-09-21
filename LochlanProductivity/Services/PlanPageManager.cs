using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace LochlanProductivity.Services
{
    public class PlanPageManager
    {
        private readonly List<PlanPage> pages = new();

        private readonly string saveFilePath =
            Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData),
                "LochlanProductivity",
                "plan-pages.json");

        public PlanPageManager()
        {
            Load();
        }

        public List<PlanPage> Pages => pages;

        public IEnumerable<PlanPage> ActivePages =>
            pages.Where(page => !page.IsDeleted);

        public PlanPage? GetPage(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
                return null;

            return pages.FirstOrDefault(
                page =>
                    page.Id.Equals(
                        id,
                        StringComparison.OrdinalIgnoreCase));
        }

        public PlanPage CreatePage(string title)
        {
            PlanPage page = new PlanPage
            {
                Id = Guid.NewGuid().ToString(),
                Title = string.IsNullOrWhiteSpace(title)
                    ? "Untitled page"
                    : title.Trim(),
                LastModified = DateTime.UtcNow
            };

            pages.Add(page);

            Save();

            return page;
        }

        public void Save()
        {
            try
            {
                string? directory =
                    Path.GetDirectoryName(saveFilePath);

                if (directory != null)
                    Directory.CreateDirectory(directory);

                string json =
                    JsonSerializer.Serialize(
                        pages,
                        new JsonSerializerOptions
                        {
                            WriteIndented = true
                        });

                string tempFile = saveFilePath + ".tmp";

                File.WriteAllText(tempFile, json);

                File.Move(tempFile, saveFilePath, true);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"Failed to save plan pages: {ex}");
            }
        }

        private void Load()
        {
            try
            {
                if (!File.Exists(saveFilePath))
                    return;

                List<PlanPage>? loaded =
                    JsonSerializer.Deserialize<List<PlanPage>>(
                        File.ReadAllText(saveFilePath),
                        new JsonSerializerOptions
                        {
                            PropertyNameCaseInsensitive = true
                        });

                if (loaded == null)
                    return;

                pages.Clear();

                foreach (PlanPage page in loaded)
                {
                    if (page == null ||
                        string.IsNullOrWhiteSpace(page.Id))
                    {
                        continue;
                    }

                    page.Steps ??= new List<PlanStep>();
                    page.Title ??= "";

                    foreach (PlanStep step in page.Steps)
                    {
                        if (string.IsNullOrWhiteSpace(step.Id))
                            step.Id = Guid.NewGuid().ToString();

                        step.Title ??= "";
                    }

                    pages.Add(page);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"Failed to load plan pages: {ex}");
            }
        }
    }
}
