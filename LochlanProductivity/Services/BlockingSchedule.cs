using System;
using System.Collections.Generic;
using System.Linq;

namespace LochlanProductivity.Services
{
    public class BlockingSchedule
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();

        public string Name { get; set; } = "";

        public bool IsEnabled { get; set; } = true;

        // Days on which this schedule is active.
        public List<DayOfWeek> Days { get; set; } = new();

        // Local time.
        public TimeSpan StartTime { get; set; } =
            new TimeSpan(18, 0, 0);

        // Local time.
        public TimeSpan EndTime { get; set; } =
            new TimeSpan(22, 0, 0);

        // Minutes before StartTime to send a warning toast.
        // 0 = no warning.
        public int WarnMinutesBefore { get; set; } = 15;

        // Used by SyncManager to merge changes between computers.
        public DateTime LastModified { get; set; } = DateTime.UtcNow;

        // ============================================================
        // NEXT START
        //
        // Next upcoming StartTime on a listed day (for warnings).
        // Searches today + 7 days so long lead times still resolve.
        // ============================================================

        public DateTime? GetNextStart(DateTime localNow)
        {
            if (!IsEnabled || Days == null || Days.Count == 0)
                return null;

            for (int offset = 0; offset < 8; offset++)
            {
                DateTime day =
                    localNow.Date.AddDays(offset);

                if (!Days.Contains(day.DayOfWeek))
                    continue;

                DateTime candidate =
                    day.Add(StartTime);

                if (candidate > localNow)
                    return candidate;
            }

            return null;
        }

        // ============================================================
        // CHECK WHETHER SCHEDULE IS ACTIVE
        // ============================================================

        public bool IsActive()
        {
            return IsActive(DateTime.Now);
        }

        public bool IsActive(DateTime localTime)
        {
            if (!IsEnabled)
                return false;

            if (Days == null || Days.Count == 0)
                return false;

            DayOfWeek currentDay =
                localTime.DayOfWeek;

            TimeSpan currentTime =
                localTime.TimeOfDay;

            // --------------------------------------------------------
            // NORMAL SAME-DAY SCHEDULE
            // Example: 6 PM -> 10 PM
            // --------------------------------------------------------

            if (StartTime < EndTime)
            {
                if (!Days.Contains(currentDay))
                    return false;

                return currentTime >= StartTime &&
                       currentTime < EndTime;
            }

            // --------------------------------------------------------
            // EXACT SAME TIME
            //
            // Treat this as a schedule covering the entire selected
            // day.
            // --------------------------------------------------------

            if (StartTime == EndTime)
            {
                return Days.Contains(currentDay);
            }

            // --------------------------------------------------------
            // OVERNIGHT SCHEDULE
            //
            // Example:
            // Monday 10 PM -> Tuesday 7 AM
            // --------------------------------------------------------

            if (currentTime >= StartTime)
            {
                return Days.Contains(currentDay);
            }

            DayOfWeek previousDay =
                currentDay == DayOfWeek.Sunday
                    ? DayOfWeek.Saturday
                    : currentDay - 1;

            return Days.Contains(previousDay) &&
                   currentTime < EndTime;
        }

        // ============================================================
        // DESCRIPTION
        // ============================================================

        public string GetDaysDescription()
        {
            if (Days == null || Days.Count == 0)
                return "No days selected";

            if (Days.Count == 7)
                return "Every day";

            DayOfWeek[] orderedDays =
            {
                DayOfWeek.Monday,
                DayOfWeek.Tuesday,
                DayOfWeek.Wednesday,
                DayOfWeek.Thursday,
                DayOfWeek.Friday,
                DayOfWeek.Saturday,
                DayOfWeek.Sunday
            };

            List<string> names = new();

            foreach (DayOfWeek day in orderedDays)
            {
                if (Days.Contains(day))
                {
                    names.Add(
                        day switch
                        {
                            DayOfWeek.Monday => "Mon",
                            DayOfWeek.Tuesday => "Tue",
                            DayOfWeek.Wednesday => "Wed",
                            DayOfWeek.Thursday => "Thu",
                            DayOfWeek.Friday => "Fri",
                            DayOfWeek.Saturday => "Sat",
                            DayOfWeek.Sunday => "Sun",
                            _ => ""
                        });
                }
            }

            return string.Join(", ", names);
        }

        public string GetTimeDescription()
        {
            DateTime start =
                DateTime.Today.Add(StartTime);

            DateTime end =
                DateTime.Today.Add(EndTime);

            return
                $"{start.ToString("h:mm tt")} - " +
                $"{end.ToString("h:mm tt")}";
        }
    }
}