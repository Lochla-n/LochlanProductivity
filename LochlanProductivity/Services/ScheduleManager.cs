using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace LochlanProductivity.Services
{
    public class ScheduleManager
    {
        private readonly List<BlockingSchedule> schedules = new();

        private readonly string saveFilePath =
            Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData),
                "LochlanProductivity",
                "schedules.json");

        public IReadOnlyList<BlockingSchedule> Schedules =>
            schedules;

        // ============================================================
        // CONSTRUCTOR
        // ============================================================

        public ScheduleManager()
        {
            Load();
        }

        // ============================================================
        // FIND SCHEDULE
        // ============================================================

        public BlockingSchedule? GetSchedule(
            string id)
        {
            return schedules.FirstOrDefault(
                schedule =>
                    schedule.Id.Equals(
                        id,
                        StringComparison.OrdinalIgnoreCase));
        }

        // ============================================================
        // GET ACTIVE SCHEDULES
        // ============================================================

        public List<BlockingSchedule> GetActiveSchedules()
        {
            DateTime now =
                DateTime.Now;

            return GetActiveSchedules(now);
        }

        public List<BlockingSchedule> GetActiveSchedules(
            DateTime localTime)
        {
            return schedules
                .Where(
                    schedule =>
                        schedule.IsActive(localTime))
                .ToList();
        }

        // ============================================================
        // IS ANY SCHEDULE ACTIVE?
        // ============================================================

        public bool IsBlockingScheduledNow()
        {
            return GetActiveSchedules().Count > 0;
        }

        // ============================================================
        // CREATE
        // ============================================================

        public BlockingSchedule CreateSchedule(
            string name,
            IEnumerable<DayOfWeek> days,
            TimeSpan startTime,
            TimeSpan endTime,
            int warnMinutesBefore = 15)
        {
            BlockingSchedule schedule =
                new BlockingSchedule
                {
                    Id =
                        Guid.NewGuid().ToString(),

                    Name =
                        string.IsNullOrWhiteSpace(name)
                            ? "Schedule"
                            : name.Trim(),

                    IsEnabled = true,

                    WarnMinutesBefore =
                        Math.Max(0, warnMinutesBefore),

                    Days =
                        days
                            .Distinct()
                            .ToList(),

                    StartTime =
                        startTime,

                    EndTime =
                        endTime,

                    LastModified = DateTime.UtcNow
                };

            schedules.Add(schedule);

            Save();

            return schedule;
        }

        // ============================================================
        // DELETE
        // ============================================================

        public bool DeleteSchedule(
            string id)
        {
            BlockingSchedule? schedule =
                GetSchedule(id);

            if (schedule == null)
                return false;

            schedules.Remove(schedule);

            Save();

            return true;
        }
        // ============================================================
        // REPLACE ALL SCHEDULES
        // ============================================================

        public void ReplaceSchedules(
            IEnumerable<BlockingSchedule> newSchedules)
        {
            schedules.Clear();

            foreach (BlockingSchedule schedule in newSchedules)
            {
                if (schedule == null)
                    continue;

                if (string.IsNullOrWhiteSpace(schedule.Id))
                {
                    schedule.Id =
                        Guid.NewGuid().ToString();
                }

                schedule.Days ??=
                    new List<DayOfWeek>();

                schedules.Add(schedule);
            }

            Save();
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

                if (directory == null)
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
                        schedules,
                        options);

                File.WriteAllText(
                    saveFilePath,
                    json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"Failed to save schedules: {ex}");
            }
        }

        // ============================================================
        // LOAD
        // ============================================================

        private void Load()
        {
            try
            {
                if (!File.Exists(
                    saveFilePath))
                {
                    return;
                }

                string json =
                    File.ReadAllText(
                        saveFilePath);

                List<BlockingSchedule>? loaded =
                    JsonSerializer.Deserialize<
                        List<BlockingSchedule>>(json);

                if (loaded == null)
                    return;

                schedules.Clear();

                foreach (
                    BlockingSchedule schedule
                    in loaded)
                {
                    if (schedule.Days == null)
                    {
                        schedule.Days =
                            new List<DayOfWeek>();
                    }

                    if (string.IsNullOrWhiteSpace(
                        schedule.Id))
                    {
                        schedule.Id =
                            Guid.NewGuid().ToString();
                    }

                    schedules.Add(schedule);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"Failed to load schedules: {ex}");
            }
        }
    }
}