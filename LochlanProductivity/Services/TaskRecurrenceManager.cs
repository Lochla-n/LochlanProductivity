using System;
using System.Collections.Generic;
using System.Linq;

namespace LochlanProductivity.Services
{
    public class TaskRecurrenceManager
    {
        // ============================================================
        // CHECK WHETHER A TASK IS CURRENTLY DUE
        // ============================================================

        public bool IsDue(TodoTask task)
        {
            if (task.IsLongTerm)
                return false;

            if (!task.IsRecurring)
                return !task.IsCompleted && task.DueDate.Date <= DateTime.Today;

            return task.DueDate.Date <= DateTime.Today;
        }

        // ============================================================
        // CHECK WHETHER A TASK IS OVERDUE
        // ============================================================

        public bool IsOverdue(TodoTask task)
        {
            if (!task.IsRecurring)
                return false;

            return
                !task.IsCompleted &&
                task.DueDate.Date < DateTime.Today;
        }

        // ============================================================
        // COMPLETE A RECURRING TASK
        // ============================================================

        public void CompleteTask(TodoTask task)
        {
            if (!task.IsRecurring)
            {
                task.IsCompleted = true;
                task.LastModified = DateTime.UtcNow;
                return;
            }

            // Mark done but keep the current DueDate so the task stays
            // visible (checked) in Today for the rest of the day. The
            // next occurrence is computed lazily by
            // UpdateRecurringTasksIfDue once this date has passed.
            task.LastCompletedDate =
                DateTime.Today;

            task.IsCompleted = true;

            task.LastModified = DateTime.UtcNow;
        }

        // ============================================================
        // PREPARE RECURRING TASKS FOR TODAY
        // ============================================================

        public void UpdateRecurringTasks(
            IEnumerable<TodoTask> tasks)
        {
            UpdateRecurringTasksIfDue(tasks);
        }

        // Returns true when at least one task advanced to its next
        // occurrence. A completed task only advances once its due
        // date has fully passed AND it was completed on an earlier
        // day — so checking something off keeps it visible (marked)
        // for the rest of today, then it resurfaces unmarked on its
        // next due date.
        public bool UpdateRecurringTasksIfDue(
            IEnumerable<TodoTask> tasks)
        {
            bool changed = false;

            DateTime today = DateTime.Today;

            foreach (TodoTask task in tasks)
            {
                if (!task.IsRecurring)
                    continue;

                if (!task.IsCompleted)
                    continue;

                if (task.DueDate.Date >= today)
                    continue;

                if (task.LastCompletedDate != null &&
                    task.LastCompletedDate.Value.Date >= today)
                {
                    continue;
                }

                task.DueDate =
                    CalculateNextDueDate(task);

                task.IsCompleted = false;

                task.LastModified = DateTime.UtcNow;

                changed = true;
            }

            return changed;
        }

        // ============================================================
        // CALCULATE NEXT DUE DATE
        //
        // Fast-forwards past missed cycles so a task that was not
        // completed for several days resurfaces with a due date in
        // the future instead of staying stuck in the past.
        // ============================================================

        public DateTime CalculateNextDueDate(
            TodoTask task)
        {
            DateTime next =
                AdvanceOnce(
                    task,
                    task.DueDate.Date);

            int guard = 0;

            while (next.Date <= DateTime.Today &&
                   guard++ < 3650)
            {
                DateTime advanced =
                    AdvanceOnce(task, next);

                // Protect against non-advancing recurrence settings.
                if (advanced.Date <= next.Date)
                {
                    advanced = next.Date.AddDays(1);
                }

                next = advanced;
            }

            return next;
        }

        private DateTime AdvanceOnce(
            TodoTask task,
            DateTime fromDate)
        {
            switch (task.Recurrence)
            {
                case RecurrenceType.Daily:

                    return fromDate.AddDays(1);

                case RecurrenceType.EveryNDays:

                    int interval =
                        Math.Max(
                            1,
                            task.RecurrenceInterval);

                    return fromDate.AddDays(
                        interval);

                case RecurrenceType.WeeklyDays:

                    return GetNextWeeklyDate(
                        fromDate,
                        task.RecurrenceDays);

                default:

                    return fromDate;
            }
        }

        // ============================================================
        // NEXT SELECTED WEEKDAY
        // ============================================================

        private DateTime GetNextWeeklyDate(
            DateTime currentDate,
            List<DayOfWeek> days)
        {
            if (days == null ||
                days.Count == 0)
            {
                return currentDate.AddDays(7);
            }

            HashSet<DayOfWeek> selectedDays =
                days.ToHashSet();

            for (int i = 1; i <= 7; i++)
            {
                DateTime candidate =
                    currentDate.AddDays(i);

                if (selectedDays.Contains(
                    candidate.DayOfWeek))
                {
                    return candidate;
                }
            }

            return currentDate.AddDays(7);
        }

        // ============================================================
        // DESCRIPTION
        // ============================================================

        public string GetRecurrenceDescription(
            TodoTask task)
        {
            if (!task.IsRecurring)
                return "";

            switch (task.Recurrence)
            {
                case RecurrenceType.Daily:

                    return "Every day";

                case RecurrenceType.EveryNDays:

                    if (task.RecurrenceInterval == 1)
                        return "Every day";

                    return
                        $"Every {task.RecurrenceInterval} days";

                case RecurrenceType.WeeklyDays:

                    if (task.RecurrenceDays.Count == 0)
                        return "Weekly";

                    return string.Join(
                        ", ",
                        task.RecurrenceDays
                            .OrderBy(GetDayOrder)
                            .Select(
                                GetShortDayName));

                default:

                    return "";
            }
        }

        private int GetDayOrder(
            DayOfWeek day)
        {
            return day switch
            {
                DayOfWeek.Monday => 0,
                DayOfWeek.Tuesday => 1,
                DayOfWeek.Wednesday => 2,
                DayOfWeek.Thursday => 3,
                DayOfWeek.Friday => 4,
                DayOfWeek.Saturday => 5,
                DayOfWeek.Sunday => 6,
                _ => 7
            };
        }

        private string GetShortDayName(
            DayOfWeek day)
        {
            return day switch
            {
                DayOfWeek.Monday => "Mon",
                DayOfWeek.Tuesday => "Tue",
                DayOfWeek.Wednesday => "Wed",
                DayOfWeek.Thursday => "Thu",
                DayOfWeek.Friday => "Fri",
                DayOfWeek.Saturday => "Sat",
                DayOfWeek.Sunday => "Sun",
                _ => ""
            };
        }
    }
}