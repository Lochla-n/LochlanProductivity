using LochlanProductivity.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Windows.ApplicationModel.Appointments;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace LochlanProductivity
{
    public sealed partial class MainWindow : Window
    {
        // ============================================================
        // SERVICES
        // ============================================================

        private readonly SyncManager syncManager = new();

        private readonly TaskRecurrenceManager taskRecurrenceManager = new();

        private readonly AppBlockingService blockingService = new();

        private readonly AppGroupManager groupManager = new();

        private readonly ScheduleManager scheduleManager = new();

        private readonly PolicyManager policyManager;

        private readonly DailyPromptManager dailyPromptManager = new();

        private readonly StartupManager startupManager = new();

        private readonly BlockedSitesManager blockedSiteStore = new();

        private readonly HostsFileBlocker hostsFileBlocker = new();

        // Null = unknown; forces a reconcile on the next tick.
        private bool? websiteBlocksApplied;

        private TrayIconManager? trayIconManager;

        // ============================================================
        // TASKS
        // ============================================================

        private readonly List<TodoTask> tasks = new();

        // ============================================================
        // BLOCKING
        // ============================================================

        private DispatcherTimer? blockingTimer;

        private string? lastBlockedNotification;

        // ============================================================
        // AVAILABLE APPLICATIONS
        // ============================================================

        private readonly List<BlockedApp> availableApps = new()
        {
            new BlockedApp
            {
                Name = "Steam",
                ExecutablePath = "steam.exe"
            },

            new BlockedApp
            {
                Name = "MTG Arena",
                ExecutablePath = "MTGA.exe"
            },

            new BlockedApp
            {
                Name = "Discord",
                ExecutablePath = "Discord.exe"
            },

            new BlockedApp
            {
                Name = "Minecraft",
                ExecutablePath = "MinecraftLauncher.exe"
            }
        };

        // ============================================================
        // SAVE FILES
        // ============================================================

        private readonly string saveDirectory =
            Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.UserProfile),
                "LochlanProductivityData");

        private readonly string saveFilePath =
            Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.UserProfile),
                "LochlanProductivityData",
                "tasks.json");

        // ============================================================
        // CONSTRUCTOR
        // ============================================================

        public MainWindow()
        {
            InitializeComponent();

            policyManager =
                new PolicyManager(groupManager);

            StartBlockingTimer();

            InitializeTrayIcon();

            if (this.Content is FrameworkElement root)
            {
                root.Loaded += MainWindow_ContentLoaded;
            }
        }

        private async void MainWindow_ContentLoaded(
            object sender,
            RoutedEventArgs e)
        {
            if (sender is FrameworkElement root)
            {
                root.Loaded -= MainWindow_ContentLoaded;
            }

            await InitializeAsync();
        }

        private async System.Threading.Tasks.Task InitializeAsync()
        {
            // Load groups first because tasks can reference groups.
            await LoadAppGroupsAsync();

            // Load saved tasks.
            await LoadTasksAsync();

            // Activate recurring tasks that have become due.
            taskRecurrenceManager.UpdateRecurringTasks(
                ActiveTasks.ToList());

            // Display whatever was loaded.
            RefreshTaskList();

            UpdateFocusModeLock();

            UpdateScheduledBlockingState();

            UpdateBlockingStatus();

            await SyncOnStartupAsync();

            await CheckDailyPromptAsync();

            await UpdateStartupToggleButtonAsync();
        }

        // ============================================================
        // STARTUP SYNC (silent)
        //
        // Pulls changes the laptop pushed through Syncthing while
        // this computer was off. Merge-based, so it cannot clobber
        // local edits; only touches UI when something changed.
        // ============================================================

        private async System.Threading.Tasks.Task SyncOnStartupAsync()
        {
            try
            {
                if (!syncManager.HasSyncData())
                    return;

                SyncResult result =
                    await syncManager.SyncNowAsync(
                        tasks,
                        groupManager,
                        scheduleManager);

                if (result.Success &&
                    (result.TaskChanges > 0 ||
                     result.GroupChanges > 0 ||
                     result.ScheduleChanges > 0))
                {
                    RefreshTaskList();

                    UpdateFocusModeLock();

                    UpdateScheduledBlockingState();

                    UpdateBlockingStatus();

                    System.Diagnostics.Debug.WriteLine(
                        $"Startup sync merged: {result.Message}");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"Startup sync failed: {ex}");
            }
        }

        // ============================================================
        // FOCUS MODE LOCK STATE
        // ============================================================

        private IEnumerable<TodoTask> ActiveTasks =>
            tasks.Where(task => !task.IsDeleted);

        private bool HasIncompleteTasks =>
            ActiveTasks.Any(task => !task.IsCompleted);

        private bool IsFocusModeLocked =>
            HasIncompleteTasks;

        private void UpdateFocusModeLock()
        {
            if (HasIncompleteTasks)
            {
                if (!blockingService.IsMonitoring)
                {
                    blockingService.StartMonitoring();
                }

                BlockingButton.Content =
                    "Focus Mode Locked";

                BlockingStatusText.Text =
                    "Focus Mode is locked until all tasks are complete.";

                return;
            }

            BlockingButton.Content =
                blockingService.IsMonitoring
                    ? "Stop Focus Mode"
                    : "Start Focus Mode";

            BlockingStatusText.Text =
                blockingService.IsMonitoring
                    ? "Focus Mode is ON."
                    : "Focus Mode is OFF.";
        }

        // ============================================================
        // DAILY STARTUP PROMPT
        // ============================================================

        private async System.Threading.Tasks.Task CheckDailyPromptAsync()
        {
            // If there are already unfinished tasks, don't ask the user
            // to create another daily task list.
            //
            // This is especially important after restarting the app:
            // saved tasks should simply reappear.
            if (ActiveTasks.Any(task => !task.IsCompleted))
            {
                return;
            }

            // If today's prompt was already shown, don't show it again.
            if (!dailyPromptManager.ShouldShowPrompt())
            {
                return;
            }

            while (true)
            {
                TextBox taskBox =
                    new TextBox
                    {
                        PlaceholderText =
                            "What do you need to get done today?",

                        AcceptsReturn = false
                    };

                StackPanel dialogContent =
                    new StackPanel
                    {
                        Spacing = 10
                    };

                if (dailyPromptManager.CurrentStreak > 0)
                {
                    dialogContent.Children.Add(
                        new TextBlock
                        {
                            Text =
                                $"Streak: {dailyPromptManager.CurrentStreak} day(s) " +
                                $"(best {dailyPromptManager.BestStreak})",

                            FontWeight =
                                Microsoft.UI.Text.FontWeights.SemiBold,

                            Opacity = 0.85
                        });
                }

                dialogContent.Children.Add(
                    new TextBlock
                    {
                        Text =
                            "Before you get started, " +
                            "add at least one thing you " +
                            "need to accomplish today.",

                        TextWrapping =
                            TextWrapping.Wrap,

                        Opacity = 0.8
                    });

                dialogContent.Children.Add(taskBox);

                ContentDialog dialog =
                    new ContentDialog
                    {
                        Title =
                            "Plan Your Day",

                        Content = dialogContent,

                        PrimaryButtonText =
                            "Add Task",

                        SecondaryButtonText =
                            "Skip for today",

                        CloseButtonText =
                            "Cancel",

                        XamlRoot =
                            this.Content.XamlRoot
                    };

                ContentDialogResult result =
                    await dialog.ShowAsync();

                if (result == ContentDialogResult.Secondary)
                {
                    // Skipping is allowed but resets the streak and,
                    // with no tasks, blocking stays off.
                    dailyPromptManager.SkipToday();

                    break;
                }

                if (result != ContentDialogResult.Primary)
                {
                    continue;
                }

                string text =
                    taskBox.Text.Trim();

                if (string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                await AddTaskForDailyPromptAsync(text);

                dailyPromptManager.MarkPromptShown();

                break;
            }
        }

        private async System.Threading.Tasks.Task AddTaskForDailyPromptAsync(
            string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return;

            TodoTask task =
                new TodoTask
                {
                    Title = text.Trim(),
                    IsCompleted = false
                };

            policyManager.ApplyDefaultPolicy(task);

            tasks.Add(task);

            await SaveTasksAsync();

            RefreshTaskList();
        }

        // ============================================================
        // FOCUS MODE
        // ============================================================
        private async void ScheduledTasks_Click(
            object sender,
            RoutedEventArgs e)
        {
            await OpenScheduledTasksDialogAsync();
        }

        private async System.Threading.Tasks.Task OpenScheduledTasksDialogAsync()
        {
            StackPanel panel =
                new StackPanel
                {
                    Spacing = 12
                };

            TextBlock description =
                new TextBlock
                {
                    Text =
                        "Create tasks that automatically become available " +
                        "on a recurring schedule.",

                    TextWrapping =
                        TextWrapping.Wrap,

                    Opacity = 0.7
                };

            panel.Children.Add(description);

            Button newTaskButton =
                new Button
                {
                    Content = "+ New Scheduled Task",

                    HorizontalAlignment =
                        HorizontalAlignment.Left
                };

            panel.Children.Add(newTaskButton);

            StackPanel taskList =
                new StackPanel
                {
                    Spacing = 8
                };

            ScrollViewer scrollViewer =
                new ScrollViewer
                {
                    Content = taskList,

                    Height = 400,

                    VerticalScrollBarVisibility =
                        ScrollBarVisibility.Auto,

                    HorizontalScrollBarVisibility =
                        ScrollBarVisibility.Disabled
                };

            panel.Children.Add(scrollViewer);

            IEnumerable<TodoTask> recurringTasks =
                ActiveTasks.Where(task => task.IsRecurring);

            if (!recurringTasks.Any())
            {
                taskList.Children.Add(
                    new TextBlock
                    {
                        Text =
                            "No scheduled tasks yet.\n\n" +
                            "Click '+ New Scheduled Task' to create one.",

                        TextWrapping =
                            TextWrapping.Wrap,

                        Opacity = 0.65
                    });
            }
            else
            {
                foreach (TodoTask task in recurringTasks)
                {
                    Border card =
                        new Border
                        {
                            Padding =
                                new Thickness(12),

                            CornerRadius =
                                new Microsoft.UI.Xaml.CornerRadius(8)
                        };

                    StackPanel cardContent =
                        new StackPanel
                        {
                            Spacing = 3
                        };

                    cardContent.Children.Add(
                        new TextBlock
                        {
                            Text = task.Title,

                            FontSize = 16,

                            FontWeight =
                                Microsoft.UI.Text.FontWeights.SemiBold
                        });

                    cardContent.Children.Add(
                        new TextBlock
                        {
                            Text =
                                taskRecurrenceManager
                                    .GetRecurrenceDescription(task),

                            FontSize = 12,

                            Opacity = 0.7
                        });

                    cardContent.Children.Add(
                        new TextBlock
                        {
                            Text =
                                $"Next due: {task.DueDate:d}",

                            FontSize = 12,

                            Opacity = 0.7
                        });

                    card.Child = cardContent;

                    taskList.Children.Add(card);
                }
            }

            ContentDialog dialog =
                new ContentDialog
                {
                    Title = "Scheduled Tasks",

                    Content = panel,

                    CloseButtonText = "Close",

                    XamlRoot =
                        this.Content.XamlRoot
                };

            newTaskButton.Click +=
                async (s, args) =>
                {
                    await HideDialogAndWaitClosedAsync(dialog);

                    await CreateScheduledTaskAsync();
                };

            await dialog.ShowAsync();
        }

        private async System.Threading.Tasks.Task CreateScheduledTaskAsync()
        {
            StackPanel content =
                new StackPanel
                {
                    Spacing = 10
                };

            TextBox taskNameBox =
                new TextBox
                {
                    Header = "Task name",

                    PlaceholderText =
                        "Example: Clean cat litter"
                };

            content.Children.Add(taskNameBox);

            ComboBox recurrenceBox =
                new ComboBox
                {
                    Header = "Repeat",

                    HorizontalAlignment =
                        HorizontalAlignment.Stretch
                };

            recurrenceBox.Items.Add("Every day");
            recurrenceBox.Items.Add("Every N days");
            recurrenceBox.Items.Add("Specific days of the week");

            recurrenceBox.SelectedIndex = 0;

            content.Children.Add(recurrenceBox);

            // ------------------------------------------------------------
            // EVERY N DAYS
            // ------------------------------------------------------------

            StackPanel intervalPanel =
                new StackPanel
                {
                    Spacing = 6,

                    Visibility =
                        Visibility.Collapsed
                };

            NumberBox intervalBox =
                new NumberBox
                {
                    Header = "Repeat every",

                    Value = 2,

                    Minimum = 1,

                    //SpinButtonPlacementMode =
                     //   Microsoft.UI.Xaml.Controls.SpinButtonPlacementMode.Compact
                };

            intervalPanel.Children.Add(intervalBox);

            TextBlock intervalDescription =
                new TextBlock
                {
                    Text = "days",

                    Opacity = 0.65
                };

            intervalPanel.Children.Add(
                intervalDescription);

            content.Children.Add(intervalPanel);

            // ------------------------------------------------------------
            // DAYS OF WEEK
            // ------------------------------------------------------------

            StackPanel weeklyPanel =
                new StackPanel
                {
                    Spacing = 4,

                    Visibility =
                        Visibility.Collapsed
                };

            weeklyPanel.Children.Add(
                new TextBlock
                {
                    Text = "Repeat on",

                    FontWeight =
                        Microsoft.UI.Text.FontWeights.SemiBold
                });

            Dictionary<DayOfWeek, CheckBox> dayBoxes =
                new();

            DayOfWeek[] days =
            {
        DayOfWeek.Monday,
        DayOfWeek.Tuesday,
        DayOfWeek.Wednesday,
        DayOfWeek.Thursday,
        DayOfWeek.Friday,
        DayOfWeek.Saturday,
        DayOfWeek.Sunday
    };

            string[] dayNames =
            {
        "Monday",
        "Tuesday",
        "Wednesday",
        "Thursday",
        "Friday",
        "Saturday",
        "Sunday"
    };

            for (int i = 0; i < days.Length; i++)
            {
                CheckBox checkBox =
                    new CheckBox
                    {
                        Content = dayNames[i]
                    };

                dayBoxes.Add(
                    days[i],
                    checkBox);

                weeklyPanel.Children.Add(
                    checkBox);
            }

            content.Children.Add(weeklyPanel);

            // ------------------------------------------------------------
            // FIRST DUE DATE
            // ------------------------------------------------------------

            DatePicker dueDatePicker =
                new DatePicker
                {
                    Header = "First due date",

                    Date = DateTimeOffset.Now
                };

            content.Children.Add(
                dueDatePicker);

            // ------------------------------------------------------------
            // CHANGE OPTIONS WHEN RECURRENCE CHANGES
            // ------------------------------------------------------------

            recurrenceBox.SelectionChanged +=
                (s, args) =>
                {
                    intervalPanel.Visibility =
                        recurrenceBox.SelectedIndex == 1
                            ? Visibility.Visible
                            : Visibility.Collapsed;

                    weeklyPanel.Visibility =
                        recurrenceBox.SelectedIndex == 2
                            ? Visibility.Visible
                            : Visibility.Collapsed;
                };

            ScrollViewer scrollViewer =
                new ScrollViewer
                {
                    Content = content,

                    MaxHeight = 550,

                    VerticalScrollBarVisibility =
                        ScrollBarVisibility.Auto,

                    HorizontalScrollBarVisibility =
                        ScrollBarVisibility.Disabled
                };

            ContentDialog dialog =
                new ContentDialog
                {
                    Title = "Create Scheduled Task",

                    Content = scrollViewer,

                    PrimaryButtonText = "Create",

                    CloseButtonText = "Cancel",

                    XamlRoot =
                        this.Content.XamlRoot
                };

            ContentDialogResult result =
                await dialog.ShowAsync();

            if (result != ContentDialogResult.Primary)
            {
                return;
            }

            string title =
                taskNameBox.Text.Trim();

            if (string.IsNullOrWhiteSpace(title))
            {
                await ShowSimpleMessageAsync(
                    "Please enter a task name.");

                return;
            }

            DateTime dueDate =
                dueDatePicker.Date.Date;

            TodoTask task =
                new TodoTask
                {
                    Title = title,

                    IsRecurring = true,

                    IsCompleted = false,

                    DueDate = dueDate,

                    BlockedGroups = new List<string>(),

                    BlockedApps = new List<BlockedApp>()
                };

            // ------------------------------------------------------------
            // RECURRENCE TYPE
            // ------------------------------------------------------------

            switch (recurrenceBox.SelectedIndex)
            {
                case 0:

                    task.Recurrence =
                        RecurrenceType.Daily;

                    task.RecurrenceInterval = 1;

                    break;

                case 1:

                    task.Recurrence =
                        RecurrenceType.EveryNDays;

                    task.RecurrenceInterval =
                        Math.Max(
                            1,
                            (int)intervalBox.Value);

                    break;

                case 2:

                    task.Recurrence =
                        RecurrenceType.WeeklyDays;

                    task.RecurrenceDays =
                        dayBoxes
                            .Where(
                                pair =>
                                    pair.Value.IsChecked == true)
                            .Select(
                                pair =>
                                    pair.Key)
                            .ToList();

                    if (task.RecurrenceDays.Count == 0)
                    {
                        await ShowSimpleMessageAsync(
                            "Please select at least one day.");

                        return;
                    }

                    break;
            }

            // ------------------------------------------------------------
            // DEFAULT BLOCKING POLICY
            // ------------------------------------------------------------

            policyManager.ApplyDefaultPolicy(task);

            tasks.Add(task);

            await SaveTasksAsync();

            RefreshTaskList();
        }

        private async void BlockingButton_Click(
            object sender,
            RoutedEventArgs e)
        {
            if (HasIncompleteTasks)
            {
                if (!blockingService.IsMonitoring)
                {
                    blockingService.StartMonitoring();
                }

                BlockingButton.Content =
                    "Focus Mode Locked";

                await ShowSimpleMessageAsync(
                    "Focus Mode is locked.\n\n" +
                    "Complete all of your tasks before Focus Mode can be turned off.");

                UpdateBlockingStatus();

                return;
            }

            if (blockingService.IsMonitoring)
            {
                blockingService.StopMonitoring();

                BlockingButton.Content =
                    "Start Focus Mode";

                BlockingStatusText.Text =
                    "Focus Mode is OFF.";

                UpdateBlockingStatus();

                // Route the OFF transition through the helper task
                // immediately instead of waiting for the next tick.
                UpdateWebsiteBlockingState();

                return;
            }

            blockingService.StartMonitoring();

            BlockingButton.Content =
                "Stop Focus Mode";

            BlockingStatusText.Text =
                "Focus Mode is ON.";

            UpdateBlockingStatus();

            UpdateWebsiteBlockingState();
        }

        private void StartBlockingTimer()
        {
            blockingTimer?.Stop();

            blockingTimer =
                new DispatcherTimer
                {
                    Interval =
                        TimeSpan.FromSeconds(1)
                };

            blockingTimer.Tick += BlockingTimer_Tick;

            blockingTimer.Start();
        }

        private void StopBlockingTimer()
        {
            if (blockingTimer == null)
                return;

            blockingTimer.Stop();

            blockingTimer = null;
        }

        private void BlockingTimer_Tick(
            object? sender,
            object e)
        {
            if (HasIncompleteTasks &&
                !blockingService.IsMonitoring)
            {
                blockingService.StartMonitoring();
            }

            UpdateFocusModeLock();

            UpdateScheduledBlockingState();

            EnforceBlocking();

            UpdateBlockingStatus();
        }

        private void UpdateScheduledBlockingState()
        {
            bool scheduleActive =
                scheduleManager.IsBlockingScheduledNow();

            blockingService.SetScheduledMonitoring(
                scheduleActive);
        }

        // ============================================================
        // ENFORCEMENT
        // ============================================================

        private void EnforceBlocking()
        {
            if (!blockingService.IsBlockingActive)
                return;

            Dictionary<TodoTask, IReadOnlyList<BlockedApp>>
                blockedAppsPerTask =
                    BuildEffectiveBlockedAppsMap();

            List<BlockedAppStatus> blockedApps =
                blockingService.EnforceBlocking(
                    blockedAppsPerTask);

            if (blockedApps.Count == 0)
                return;

            BlockedAppStatus blocked =
                blockedApps[0];

            string notificationKey =
                $"{blocked.App.ExecutablePath}|{blocked.Task.Title}";

            if (notificationKey ==
                lastBlockedNotification)
            {
                return;
            }

            lastBlockedNotification =
                notificationKey;

            ShowBlockedAppNotification(
                blocked.App.Name,
                blocked.Task.Title);
        }

        private void ShowBlockedAppNotification(
            string appName,
            string taskName)
        {
            BlockedAppNotificationTitle.Text =
                $"🔒 {appName} blocked";

            BlockedAppNotificationMessage.Text =
                $"Complete \"{taskName}\" to unlock {appName}.";

            BlockedAppNotification.Visibility =
                Visibility.Visible;
        }

        private void HideBlockedAppNotification()
        {
            BlockedAppNotification.Visibility =
                Visibility.Collapsed;

            BlockedAppNotificationTitle.Text = "";

            BlockedAppNotificationMessage.Text = "";

            lastBlockedNotification = null;
        }

        // ============================================================
        // EFFECTIVE BLOCKED APPS MAP
        //
        // Pure per-tick computation: groups are expanded through
        // PolicyManager without ever mutating task objects, so the
        // timer no longer pollutes saved tasks with group apps.
        // ============================================================

        private Dictionary<TodoTask, IReadOnlyList<BlockedApp>>
            BuildEffectiveBlockedAppsMap()
        {
            Dictionary<TodoTask, IReadOnlyList<BlockedApp>> map =
                new();

            foreach (TodoTask task in ActiveTasks)
            {
                if (task.IsCompleted)
                    continue;

                map[task] =
                    policyManager.GetEffectiveBlockedApps(task);
            }

            return map;
        }

        // ============================================================
        // STATUS
        // ============================================================

        private void UpdateBlockingStatus()
        {
            if (!blockingService.IsBlockingActive)
                return;

            Dictionary<TodoTask, IReadOnlyList<BlockedApp>>
                blockedAppsPerTask =
                    BuildEffectiveBlockedAppsMap();

            List<BlockedAppStatus> statuses =
                blockingService.CheckBlockedApps(
                    blockedAppsPerTask);

            // --------------------------------------------------------
            // COUNT UNIQUE APPLICATIONS
            // --------------------------------------------------------
            //
            // The same EXE can be associated with multiple tasks.
            // Only count each executable once.
            //
            // Task 1 -> Steam
            // Task 2 -> Steam
            // Task 3 -> Discord
            //
            // Result:
            // 2 unique applications
            // --------------------------------------------------------

            List<BlockedAppStatus> uniqueStatuses =
                statuses
                    .GroupBy(
                        status =>
                            status.App.ExecutablePath,
                        StringComparer.OrdinalIgnoreCase)
                    .Select(
                        group =>
                            group.First())
                    .ToList();

            int blockedAppCount =
                uniqueStatuses.Count;

            int runningBlockedApps =
                uniqueStatuses.Count(
                    status =>
                        status.IsRunning);

            if (blockedAppCount == 0)
            {
                BlockedAppStatusText.Text =
                    "No incomplete tasks have blocked apps.";

                return;
            }

            if (runningBlockedApps == 0)
            {
                BlockedAppStatusText.Text =
                    $"{blockedAppCount} unique app(s) being monitored.";

                return;
            }

            BlockedAppStatusText.Text =
                $"{runningBlockedApps} blocked app(s) detected " +
                $"({blockedAppCount} unique app(s) monitored).";
        }

        // ============================================================
        // ADD TASK
        // ============================================================

        private void AddTask_Click(
            object sender,
            RoutedEventArgs e)
        {
            AddTask();
        }

        private void TaskInput_KeyDown(
            object sender,
            KeyRoutedEventArgs e)
        {
            if (e.Key ==
                Windows.System.VirtualKey.Enter)
            {
                AddTask();
            }
        }

        private async void AddTask()
        {
            string text =
                TaskInput.Text.Trim();

            if (string.IsNullOrWhiteSpace(text))
                return;

            TodoTask task =
                new TodoTask
                {
                    Title = text,

                    IsCompleted = false,

                    BlockedGroups =
                        new List<string>()
                };

            if (groupManager.Groups.Any(
                group =>
                    group.Id.Equals(
                        "games",
                        StringComparison.OrdinalIgnoreCase)))
            {
                task.BlockedGroups.Add("games");
            }

            policyManager.ApplyDefaultPolicy(task);

            tasks.Add(task);

            TaskInput.Text = "";

            RefreshTaskList();

            await SaveTasksAsync();
        }

        // ============================================================
        // REMOVE TASK
        // ============================================================

        private async void RemoveTask(
            TodoTask task)
        {
            if (!task.IsCompleted)
            {
                await ShowSimpleMessageAsync(
                    "This task cannot be removed while it is incomplete.\n\n" +
                    "Complete the task first.");

                return;
            }

            ContentDialog dialog =
                new ContentDialog
                {
                    Title =
                        "Remove task?",

                    Content =
                        $"Are you sure you want to remove \"{task.Title}\"?",

                    PrimaryButtonText =
                        "Remove",

                    CloseButtonText =
                        "Cancel",

                    DefaultButton =
                        ContentDialogButton.Close,

                    XamlRoot =
                        this.Content.XamlRoot
                };

            ContentDialogResult result =
                await dialog.ShowAsync();

            if (result != ContentDialogResult.Primary)
            {
                return;
            }

            // Soft-delete: the tombstone syncs to other computers
            // so SyncManager can keep deletions consistent.
            task.IsDeleted = true;

            task.LastModified = DateTime.UtcNow;

            lastBlockedNotification = null;

            RefreshTaskList();

            await SaveTasksAsync();

            UpdateFocusModeLock();

            UpdateBlockingStatus();
        }

        // ============================================================
        // TASK LIST
        // ============================================================

        private void RefreshTaskList()
        {
            TaskList.Children.Clear();

            List<TodoTask> orderedTasks = ActiveTasks
                .OrderBy(task => task.IsCompleted)
                .ThenByDescending(task => (int)task.Priority)
                .ThenBy(GetEffectiveDeadline)
                .ThenBy(
                    task => task.Title,
                    StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (TodoTask task in orderedTasks)
            {
                Grid taskRow =
                    new Grid
                    {
                        Padding =
                            new Thickness(12)
                    };

                taskRow.ColumnDefinitions.Add(
                    new ColumnDefinition
                    {
                        Width =
                            new GridLength(
                                1,
                                GridUnitType.Star)
                    });

                taskRow.ColumnDefinitions.Add(
                    new ColumnDefinition
                    {
                        Width =
                            GridLength.Auto
                    });

                taskRow.ColumnDefinitions.Add(
                    new ColumnDefinition
                    {
                        Width =
                            GridLength.Auto
                    });

                taskRow.ColumnDefinitions.Add(
                    new ColumnDefinition
                    {
                        Width =
                            GridLength.Auto
                    });

                // ----------------------------------------------------
                // CHECKBOX
                // ----------------------------------------------------

                TextBlock titleText =
                    new TextBlock
                    {
                        Text =
                            task.Title,

                        FontSize = 16,

                        TextWrapping =
                            TextWrapping.Wrap
                    };

                string metaLine =
                    GetTaskMetaLine(task);

                TextBlock metaText =
                    new TextBlock
                    {
                        Text = metaLine,

                        FontSize = 12,

                        Opacity = 0.7,

                        TextWrapping =
                            TextWrapping.Wrap,

                        Visibility =
                            string.IsNullOrWhiteSpace(metaLine)
                                ? Visibility.Collapsed
                                : Visibility.Visible
                    };

                if (IsTaskOverdue(task))
                {
                    metaText.Opacity = 1;

                    metaText.Foreground =
                        new Microsoft.UI.Xaml.Media.SolidColorBrush(
                            Microsoft.UI.Colors.OrangeRed);
                }

                CheckBox checkBox =
                    new CheckBox
                    {
                        Content =
                            new StackPanel
                            {
                                Spacing = 2,

                                Children =
                                {
                                    titleText,
                                    metaText
                                }
                            },

                        IsChecked =
                            task.IsCompleted,

                        VerticalAlignment =
                            VerticalAlignment.Center
                    };

                checkBox.Checked +=
                    async (sender, e) =>
                    {
                        task.IsCompleted = true;

                        task.LastModified = DateTime.UtcNow;

                        lastBlockedNotification = null;

                        // Recurring tasks advance to their next
                        // occurrence instead of disappearing.
                        if (task.IsRecurring)
                        {
                            taskRecurrenceManager.CompleteTask(task);
                        }

                        await SaveTasksAsync();

                        UpdateFocusModeLock();

                        UpdateBlockingStatus();

                        // Completing the last task (or unchecking one)
                        // flips enforcement - swap websites instantly.
                        UpdateWebsiteBlockingState();

                        RefreshTaskList();
                    };

                checkBox.Unchecked +=
                    async (sender, e) =>
                    {
                        task.IsCompleted = false;

                        task.LastModified = DateTime.UtcNow;

                        lastBlockedNotification = null;

                        blockingService.StartMonitoring();

                        UpdateFocusModeLock();

                        await SaveTasksAsync();

                        UpdateBlockingStatus();

                        RefreshTaskList();
                    };

                Grid.SetColumn(
                    checkBox,
                    0);

                // ----------------------------------------------------
                // EDIT BUTTON
                // ----------------------------------------------------

                Button editButton =
                    new Button
                    {
                        Content =
                            "Edit",

                        VerticalAlignment =
                            VerticalAlignment.Center,

                        Margin =
                            new Thickness(
                                12,
                                0,
                                6,
                                0)
                    };

                editButton.Click +=
                    (sender, e) =>
                    {
                        OpenEditTaskDialog(task);
                    };

                Grid.SetColumn(
                    editButton,
                    1);

                // ----------------------------------------------------
                // BLOCKED APPS BUTTON
                // ----------------------------------------------------

                Button blockedAppsButton =
                    new Button
                    {
                        Content =
                            GetBlockedAppsButtonText(task),

                        VerticalAlignment =
                            VerticalAlignment.Center,

                        Margin =
                            new Thickness(
                                12,
                                0,
                                6,
                                0)
                    };

                blockedAppsButton.Click +=
                    (sender, e) =>
                    {
                        OpenBlockedAppsDialog(task);
                    };

                Grid.SetColumn(
                    blockedAppsButton,
                    2);

                // ----------------------------------------------------
                // REMOVE BUTTON
                // ----------------------------------------------------

                Button removeButton =
                    new Button
                    {
                        Content =
                            "Remove",

                        VerticalAlignment =
                            VerticalAlignment.Center
                    };

                removeButton.Click +=
                    (sender, e) =>
                    {
                        RemoveTask(task);
                    };

                Grid.SetColumn(
                    removeButton,
                    3);

                taskRow.Children.Add(checkBox);

                taskRow.Children.Add(editButton);

                taskRow.Children.Add(blockedAppsButton);

                taskRow.Children.Add(removeButton);

                TaskList.Children.Add(taskRow);
            }
        }

        private string GetBlockedAppsButtonText(
            TodoTask task)
        {
            List<BlockedApp> effectiveApps =
                policyManager
                    .GetEffectiveBlockedApps(task);

            // Deduplicate applications by executable path.
            int count =
                effectiveApps
                    .GroupBy(
                        app =>
                            app.ExecutablePath,
                        StringComparer.OrdinalIgnoreCase)
                    .Count();

            if (count == 0)
                return "No blocked apps";

            if (count == 1)
                return "1 blocked app";

            return $"{count} blocked apps";
        }

        // ============================================================
        // DEADLINE / PRIORITY HELPERS
        // ============================================================

        private DateTime GetEffectiveDeadline(
            TodoTask task)
        {
            return task.DueDate.Date +
                (task.DueTimeOfDay ??
                    new TimeSpan(23, 59, 0));
        }

        private bool IsTaskOverdue(
            TodoTask task)
        {
            return !task.IsCompleted &&
                GetEffectiveDeadline(task) < DateTime.Now;
        }

        private string GetTaskMetaLine(
            TodoTask task)
        {
            if (task.IsCompleted && task.IsRecurring)
            {
                return $"Done · next {task.DueDate:d}";
            }

            List<string> parts = new();

            if (task.IsRecurring)
            {
                parts.Add(
                    taskRecurrenceManager
                        .GetRecurrenceDescription(task));
            }

            if (task.Priority != TaskPriority.Normal)
            {
                parts.Add($"{task.Priority}");
            }

            if (task.DueTimeOfDay != null)
            {
                parts.Add(
                    $"due {GetEffectiveDeadline(task):t}");
            }
            else if (!task.IsRecurring &&
                     task.DueDate.Date != DateTime.Today)
            {
                parts.Add($"due {task.DueDate:d}");
            }

            if (IsTaskOverdue(task))
            {
                parts.Add("OVERDUE");
            }

            return string.Join(" · ", parts);
        }

        // ============================================================
        // EDIT TASK DIALOG
        // ============================================================

        private async void OpenEditTaskDialog(
            TodoTask task)
        {
            if (HasIncompleteTasks)
            {
                await ShowSimpleMessageAsync(
                    "Tasks are locked while other tasks are incomplete.\n\n" +
                    "Complete all tasks before editing.");

                return;
            }

            StackPanel panel =
                new StackPanel
                {
                    Spacing = 10
                };

            TextBox titleBox =
                new TextBox
                {
                    Header = "Task name",

                    Text = task.Title
                };

            panel.Children.Add(titleBox);

            ComboBox priorityBox =
                new ComboBox
                {
                    Header = "Priority",

                    HorizontalAlignment =
                        HorizontalAlignment.Stretch,

                    SelectedIndex = (int)task.Priority
                };

            priorityBox.Items.Add("Low");
            priorityBox.Items.Add("Normal");
            priorityBox.Items.Add("High");

            panel.Children.Add(priorityBox);

            if (task.IsRecurring)
            {
                panel.Children.Add(
                    new TextBlock
                    {
                        Text =
                            taskRecurrenceManager
                                .GetRecurrenceDescription(task) +
                            $", next due {task.DueDate:d}",

                        Opacity = 0.7,

                        TextWrapping =
                            TextWrapping.Wrap
                    });
            }

            DatePicker datePicker =
                new DatePicker
                {
                    Header = "Due date",

                    Date =
                        new DateTimeOffset(task.DueDate.Date)
                };

            panel.Children.Add(datePicker);

            CheckBox hasTimeBox =
                new CheckBox
                {
                    Content = "Set a due time",

                    IsChecked = task.DueTimeOfDay != null
                };

            TimePicker timePicker =
                new TimePicker
                {
                    Header = "Due time",

                    Time =
                        task.DueTimeOfDay ??
                            new TimeSpan(17, 0, 0),

                    IsEnabled = task.DueTimeOfDay != null
                };

            hasTimeBox.Checked +=
                (sender, e) => timePicker.IsEnabled = true;

            hasTimeBox.Unchecked +=
                (sender, e) => timePicker.IsEnabled = false;

            panel.Children.Add(hasTimeBox);

            panel.Children.Add(timePicker);

            ScrollViewer scrollViewer =
                new ScrollViewer
                {
                    Content = panel,

                    MaxHeight = 480,

                    VerticalScrollBarVisibility =
                        ScrollBarVisibility.Auto,

                    HorizontalScrollBarVisibility =
                        ScrollBarVisibility.Disabled
                };

            ContentDialog dialog =
                new ContentDialog
                {
                    Title = $"Edit Task - {task.Title}",

                    Content = scrollViewer,

                    PrimaryButtonText = "Save",

                    CloseButtonText = "Cancel",

                    DefaultButton =
                        ContentDialogButton.Primary,

                    XamlRoot = this.Content.XamlRoot
                };

            ContentDialogResult result =
                await dialog.ShowAsync();

            if (result != ContentDialogResult.Primary)
            {
                return;
            }

            string newTitle =
                titleBox.Text.Trim();

            if (string.IsNullOrWhiteSpace(newTitle))
            {
                await ShowSimpleMessageAsync(
                    "Please enter a task name.");

                return;
            }

            task.Title = newTitle;

            task.Priority =
                (TaskPriority)Math.Max(
                    0,
                    priorityBox.SelectedIndex);

            task.DueDate =
                datePicker.Date.Date;

            task.DueTimeOfDay =
                hasTimeBox.IsChecked == true
                    ? timePicker.Time
                    : null;

            task.LastModified = DateTime.UtcNow;

            RefreshTaskList();

            await SaveTasksAsync();

            UpdateBlockingStatus();
        }

        // ============================================================
        // WEBSITE BLOCKING
        // ============================================================

        private void UpdateWebsiteBlockingState()
        {
            bool desired =
                blockingService.IsBlockingActive &&
                HasIncompleteTasks &&
                blockedSiteStore.Domains.Count > 0;

            if (websiteBlocksApplied == desired)
                return;

            websiteBlocksApplied = desired;

            HostsFileBlocker.Log(
                $"state -> {(desired ? "BLOCKING" : "unblocked")} " +
                $"(enforcing={blockingService.IsBlockingActive}, " +
                $"incompleteTasks={HasIncompleteTasks}, " +
                $"domains={blockedSiteStore.Domains.Count})");

            try
            {
                if (desired)
                {
                    hostsFileBlocker.Apply(
                        blockedSiteStore.Domains);
                }
                else
                {
                    hostsFileBlocker.Remove();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"Website blocking update failed: {ex}");
            }
        }

        private async void BlockedSitesButton_Click(
            object sender,
            RoutedEventArgs e)
        {
            if (HasIncompleteTasks)
            {
                await ShowSimpleMessageAsync(
                    "Blocked website settings are locked while tasks are incomplete.\n\n" +
                    "Complete all tasks before changing settings.");

                return;
            }

            await OpenBlockedSitesDialogAsync();
        }

        private async System.Threading.Tasks.Task
            OpenBlockedSitesDialogAsync()
        {
            StackPanel panel =
                new StackPanel
                {
                    Spacing = 10
                };

            panel.Children.Add(
                new TextBlock
                {
                    Text =
                        "These websites are blocked system-wide " +
                        "while Focus Mode is enforcing incomplete tasks.",

                    TextWrapping = TextWrapping.Wrap,

                    Opacity = 0.75
                });

            TextBox addBox =
                new TextBox
                {
                    PlaceholderText =
                        "example.com"
                };

            Button addButton =
                new Button
                {
                    Content = "Add"
                };

            StackPanel siteList =
                new StackPanel
                {
                    Spacing = 4
                };

            void PopulateSiteList()
            {
                siteList.Children.Clear();

                if (blockedSiteStore.Domains.Count == 0)
                {
                    siteList.Children.Add(
                        new TextBlock
                        {
                            Text = "No websites blocked yet.",

                            Opacity = 0.6
                        });

                    return;
                }

                foreach (string domain in blockedSiteStore.Domains)
                {
                    Grid row =
                        new Grid();

                    row.ColumnDefinitions.Add(
                        new ColumnDefinition
                        {
                            Width =
                                new GridLength(
                                    1,
                                    GridUnitType.Star)
                        });

                    row.ColumnDefinitions.Add(
                        new ColumnDefinition
                        {
                            Width = GridLength.Auto
                        });

                    TextBlock domainText =
                        new TextBlock
                        {
                            Text = domain,

                            VerticalAlignment =
                                VerticalAlignment.Center
                        };

                    Grid.SetColumn(domainText, 0);

                    Button removeButton =
                        new Button
                        {
                            Content = "Remove",

                            Padding =
                                new Thickness(8, 2, 8, 2)
                        };

                    removeButton.Click +=
                        (s, args) =>
                        {
                            blockedSiteStore.Remove(domain);

                            websiteBlocksApplied = null;

                            PopulateSiteList();
                        };

                    Grid.SetColumn(removeButton, 1);

                    row.Children.Add(domainText);

                    row.Children.Add(removeButton);

                    siteList.Children.Add(row);
                }
            }

            PopulateSiteList();

            addButton.Click +=
                (s, args) =>
                {
                    bool added =
                        blockedSiteStore.Add(addBox.Text);

                    if (added)
                    {
                        websiteBlocksApplied = null;

                        addBox.Text = "";

                        PopulateSiteList();
                    }
                };

            ScrollViewer scrollViewer =
                new ScrollViewer
                {
                    Content = panel,

                    MaxHeight = 500,

                    VerticalScrollBarVisibility =
                        ScrollBarVisibility.Auto,

                    HorizontalScrollBarVisibility =
                        ScrollBarVisibility.Disabled
                };

            ContentDialog dialog =
                new ContentDialog
                {
                    Title = "Blocked Websites",

                    Content = scrollViewer,

                    CloseButtonText = "Done",

                    XamlRoot = this.Content.XamlRoot
                };

            void AddToAddRow()
            {
                Grid addRow = new Grid
                {
                    ColumnSpacing = 8
                };

                addRow.ColumnDefinitions.Add(
                    new ColumnDefinition
                    {
                        Width =
                            new GridLength(
                                1,
                                GridUnitType.Star)
                    });

                addRow.ColumnDefinitions.Add(
                    new ColumnDefinition
                    {
                        Width = GridLength.Auto
                    });

                Grid.SetColumn(addBox, 0);

                Grid.SetColumn(addButton, 1);

                addRow.Children.Add(addBox);

                addRow.Children.Add(addButton);

                panel.Children.Add(addRow);
            }

            AddToAddRow();

            panel.Children.Add(siteList);

            panel.Children.Add(
                new TextBlock
                {
                    Text =
                        "Changing the hosts file requires admin rights. " +
                        "If the app is not running elevated, Windows will " +
                        "ask for permission once per block/unblock change.",

                    FontSize = 12,

                    Opacity = 0.55,

                    TextWrapping = TextWrapping.Wrap
                });

            await dialog.ShowAsync();
        }

        // ============================================================
        // TASK BLOCKING DIALOG
        // ============================================================

        private async void OpenBlockedAppsDialog(
            TodoTask task)
        {
            if (HasIncompleteTasks)
            {
                await ShowSimpleMessageAsync(
                    "Blocked app settings are locked while tasks are incomplete.\n\n" +
                    "Complete all tasks before changing blocking settings.");

                return;
            }

            StackPanel panel =
                new StackPanel
                {
                    Spacing = 8
                };

            TextBlock instructions =
                new TextBlock
                {
                    Text =
                        "Choose app groups and individual applications " +
                        "that should be blocked until this task is completed.",

                    TextWrapping =
                        TextWrapping.Wrap,

                    Opacity = 0.75
                };

            panel.Children.Add(
                instructions);

            // --------------------------------------------------------
            // GROUPS
            // --------------------------------------------------------

            TextBlock groupsHeader =
                new TextBlock
                {
                    Text =
                        "App groups",

                    FontWeight =
                        Microsoft.UI.Text.FontWeights.SemiBold,

                    Margin =
                        new Thickness(
                            0,
                            8,
                            0,
                            2)
                };

            panel.Children.Add(
                groupsHeader);

            Dictionary<CheckBox, AppGroup>
                groupCheckBoxes =
                    new();

            foreach (AppGroup group
                in groupManager.Groups)
            {
                bool selected =
                    task.BlockedGroups.Any(
                        groupId =>
                            groupId.Equals(
                                group.Id,
                                StringComparison.OrdinalIgnoreCase));

                CheckBox groupCheckBox =
                    new CheckBox
                    {
                        Content =
                            string.IsNullOrWhiteSpace(
                                group.Description)
                                ? group.Name
                                : $"{group.Name} — " +
                                  group.Description,

                        IsChecked =
                            selected
                    };

                groupCheckBoxes.Add(
                    groupCheckBox,
                    group);

                panel.Children.Add(
                    groupCheckBox);
            }

            if (groupManager.Groups.Count == 0)
            {
                panel.Children.Add(
                    new TextBlock
                    {
                        Text =
                            "No groups have been created yet. " +
                            "Use Manage App Groups to create one.",

                        Opacity = 0.65,

                        TextWrapping =
                            TextWrapping.Wrap
                    });
            }

            // --------------------------------------------------------
            // INDIVIDUAL APPLICATIONS
            // --------------------------------------------------------

            TextBlock appsHeader =
                new TextBlock
                {
                    Text =
                        "Additional applications",

                    FontWeight =
                        Microsoft.UI.Text.FontWeights.SemiBold,

                    Margin =
                        new Thickness(
                            0,
                            10,
                            0,
                            2)
                };

            panel.Children.Add(
                appsHeader);

            StackPanel appList =
                new StackPanel
                {
                    Spacing = 4
                };

            panel.Children.Add(
                appList);

            Dictionary<CheckBox, BlockedApp>
                checkBoxes =
                    new();

            // Apps already covered by any group aren't shown
            // as individual apps.
            List<BlockedApp> groupApps =
                new();

            foreach (AppGroup group
                in groupManager.Groups)
            {
                foreach (BlockedApp app
                    in group.Apps)
                {
                    if (!groupApps.Any(
                        existing =>
                            PathsEqual(
                                existing.ExecutablePath,
                                app.ExecutablePath)))
                    {
                        groupApps.Add(app);
                    }
                }
            }

            foreach (BlockedApp app
                in availableApps)
            {
                bool belongsToGroup =
                    groupApps.Any(
                        groupApp =>
                            PathsEqual(
                                groupApp.ExecutablePath,
                                app.ExecutablePath));

                if (belongsToGroup)
                    continue;

                bool selected =
                    task.BlockedApps.Any(
                        existing =>
                            PathsEqual(
                                existing.ExecutablePath,
                                app.ExecutablePath));

                AddCheckboxIfMissing(
                    app,
                    appList,
                    checkBoxes,
                    selected);
            }

            // --------------------------------------------------------
            // BROWSE FOR EXE
            // --------------------------------------------------------

            Button browseButton =
                new Button
                {
                    Content =
                        "+ Browse for EXE",

                    HorizontalAlignment =
                        HorizontalAlignment.Left,

                    Margin =
                        new Thickness(
                            0,
                            12,
                            0,
                            0)
                };

            browseButton.Click +=
                async (sender, e) =>
                {
                    await AddApplicationFromPickerAsync(
                        appList,
                        checkBoxes);
                };

            panel.Children.Add(
                browseButton);

            // --------------------------------------------------------
            // DETECT
            // --------------------------------------------------------

            Button detectButton =
                new Button
                {
                    Content =
                        "🔍 Detect running applications",

                    HorizontalAlignment =
                        HorizontalAlignment.Left,

                    Margin =
                        new Thickness(
                            0,
                            6,
                            0,
                            0)
                };

            detectButton.Click +=
                (sender, e) =>
                {
                    DetectRunningApplications(
                        appList,
                        checkBoxes);
                };

            panel.Children.Add(
                detectButton);

            // --------------------------------------------------------
            // SCROLLABLE DIALOG CONTENT
            // --------------------------------------------------------

            ScrollViewer dialogScrollViewer =
                new ScrollViewer
                {
                    Content =
                        panel,

                    MaxHeight =
                        600,

                    VerticalScrollBarVisibility =
                        ScrollBarVisibility.Auto,

                    HorizontalScrollBarVisibility =
                        ScrollBarVisibility.Disabled
                };

            ContentDialog dialog =
                new ContentDialog
                {
                    Title =
                        $"Blocked Apps — {task.Title}",

                    Content =
                        dialogScrollViewer,

                    PrimaryButtonText =
                        "Save",

                    CloseButtonText =
                        "Cancel",

                    XamlRoot =
                        this.Content.XamlRoot
                };

            ContentDialogResult result =
                await dialog.ShowAsync();

            if (result !=
                ContentDialogResult.Primary)
            {
                return;
            }

            // --------------------------------------------------------
            // SAVE GROUPS
            // --------------------------------------------------------

            task.BlockedGroups.Clear();

            foreach (
                KeyValuePair<
                    CheckBox,
                    AppGroup> pair
                in groupCheckBoxes)
            {
                if (pair.Key.IsChecked == true)
                {
                    task.BlockedGroups.Add(
                        pair.Value.Id);
                }
            }

            // --------------------------------------------------------
            // SAVE INDIVIDUAL APPS
            // --------------------------------------------------------

            task.BlockedApps.Clear();

            foreach (
                KeyValuePair<
                    CheckBox,
                    BlockedApp> pair
                in checkBoxes)
            {
                if (pair.Key.IsChecked == true)
                {
                    BlockedApp app =
                        pair.Value;

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

            task.LastModified = DateTime.UtcNow;

            RefreshTaskList();

            await SaveTasksAsync();

            UpdateBlockingStatus();
        }

        // ============================================================
        // SCHEDULED BLOCKING
        // ============================================================

        private async void ManageSchedules_Click(
            object sender,
            RoutedEventArgs e)
        {
            if (HasIncompleteTasks)
            {
                await ShowSimpleMessageAsync(
                    "Schedule settings are locked while tasks are incomplete.\n\n" +
                    "Complete all tasks before changing schedules.");

                return;
            }

            while (true)
            {
                bool createNewSchedule = false;

                BlockingSchedule? selectedSchedule = null;

                StackPanel mainPanel =
                    new StackPanel
                    {
                        Spacing = 12
                    };

                TextBlock description =
                    new TextBlock
                    {
                        Text =
                            "Automatically enable Focus Mode during specific " +
                            "times and days.",

                        TextWrapping =
                            TextWrapping.Wrap,

                        Opacity = 0.7
                    };

                mainPanel.Children.Add(
                    description);

                Button newScheduleButton =
                    new Button
                    {
                        Content =
                            "+ New Schedule",

                        HorizontalAlignment =
                            HorizontalAlignment.Left
                    };

                mainPanel.Children.Add(
                    newScheduleButton);

                StackPanel scheduleList =
                    new StackPanel
                    {
                        Spacing = 8
                    };

                ScrollViewer scheduleScrollViewer =
                    new ScrollViewer
                    {
                        Content =
                            scheduleList,

                        Height = 420,

                        VerticalScrollBarVisibility =
                            ScrollBarVisibility.Auto,

                        HorizontalScrollBarVisibility =
                            ScrollBarVisibility.Disabled
                    };

                mainPanel.Children.Add(
                    scheduleScrollViewer);

                bool scheduleCurrentlyActive =
                    scheduleManager.IsBlockingScheduledNow();

                Border statusCard =
                    new Border
                    {
                        Padding =
                            new Thickness(12),

                        CornerRadius =
                            new Microsoft.UI.Xaml.CornerRadius(8),

                        Background =
                            new Microsoft.UI.Xaml.Media.SolidColorBrush(
                                Microsoft.UI.Colors.Transparent)
                    };

                StackPanel statusPanel =
                    new StackPanel
                    {
                        Spacing = 3
                    };

                TextBlock statusTitle =
                    new TextBlock
                    {
                        Text =
                            scheduleCurrentlyActive
                                ? "● A schedule is active"
                                : "○ No schedule is active",

                        FontWeight =
                            Microsoft.UI.Text.FontWeights.SemiBold
                    };

                TextBlock statusDescription =
                    new TextBlock
                    {
                        Text =
                            scheduleCurrentlyActive
                                ? "Scheduled blocking is currently active."
                                : "No scheduled blocking is currently active.",

                        FontSize = 12,

                        Opacity = 0.65
                    };

                statusPanel.Children.Add(
                    statusTitle);

                statusPanel.Children.Add(
                    statusDescription);

                statusCard.Child =
                    statusPanel;

                mainPanel.Children.Add(
                    statusCard);

                foreach (
                    BlockingSchedule schedule
                    in scheduleManager.Schedules)
                {
                    Border card =
                        CreateScheduleCard(
                            schedule,
                            () =>
                            {
                                selectedSchedule =
                                    schedule;
                            });

                    scheduleList.Children.Add(
                        card);
                }

                if (scheduleManager.Schedules.Count == 0)
                {
                    Border emptyCard =
                        new Border
                        {
                            Padding =
                                new Thickness(16),

                            CornerRadius =
                                new Microsoft.UI.Xaml.CornerRadius(8)
                        };

                    TextBlock emptyText =
                        new TextBlock
                        {
                            Text =
                                "No schedules have been created yet.\n\n" +
                                "Click '+ New Schedule' to create one.",

                            TextWrapping =
                                TextWrapping.Wrap,

                            Opacity = 0.65
                        };

                    emptyCard.Child =
                        emptyText;

                    scheduleList.Children.Add(
                        emptyCard);
                }

                ContentDialog dialog =
                    new ContentDialog
                    {
                        Title =
                            "Scheduled Blocking",

                        Content =
                            mainPanel,

                        CloseButtonText =
                            "Close",

                        XamlRoot =
                            this.Content.XamlRoot
                    };

                // Subscribe before ShowAsync so the wait below can
                // never miss the Closed signal.
                System.Threading.Tasks.Task schedulesDialogClosed =
                    WaitDialogClosedAsync(dialog);

                newScheduleButton.Click +=
                    (s, args) =>
                    {
                        createNewSchedule = true;

                        dialog.Hide();
                    };

                await dialog.ShowAsync();

                await schedulesDialogClosed;

                if (createNewSchedule)
                {
                    await CreateScheduleAsync();

                    continue;
                }

                if (selectedSchedule != null)
                {
                    await EditScheduleAsync(
                        selectedSchedule);

                    continue;
                }

                break;
            }
        }

        private Border CreateScheduleCard(
            BlockingSchedule schedule,
            Action editAction)
        {
            Border card =
                new Border
                {
                    Padding =
                        new Thickness(12),

                    CornerRadius =
                        new Microsoft.UI.Xaml.CornerRadius(8)
                };

            Grid grid =
                new Grid();

            grid.ColumnDefinitions.Add(
                new ColumnDefinition
                {
                    Width =
                        new GridLength(
                            1,
                            GridUnitType.Star)
                });

            grid.ColumnDefinitions.Add(
                new ColumnDefinition
                {
                    Width =
                        GridLength.Auto
                });

            StackPanel information =
                new StackPanel
                {
                    Spacing = 3
                };

            TextBlock name =
                new TextBlock
                {
                    Text =
                        schedule.Name,

                    FontSize = 16,

                    FontWeight =
                        Microsoft.UI.Text.FontWeights.SemiBold
                };

            TextBlock days =
                new TextBlock
                {
                    Text =
                        schedule.GetDaysDescription(),

                    FontSize = 12,

                    Opacity = 0.7
                };

            TextBlock time =
                new TextBlock
                {
                    Text =
                        schedule.GetTimeDescription(),

                    FontSize = 13
                };

            TextBlock status =
                new TextBlock
                {
                    Text =
                        schedule.IsEnabled
                            ? "Enabled"
                            : "Disabled",

                    FontSize = 11,

                    Opacity = 0.6
                };

            information.Children.Add(name);
            information.Children.Add(days);
            information.Children.Add(time);
            information.Children.Add(status);

            Grid.SetColumn(
                information,
                0);

            Button editButton =
                new Button
                {
                    Content =
                        "Edit",

                    VerticalAlignment =
                        VerticalAlignment.Center,

                    Margin =
                        new Thickness(
                            10,
                            0,
                            0,
                            0)
                };

            editButton.Click +=
                (sender, args) =>
                {
                    editAction();
                };

            Grid.SetColumn(
                editButton,
                1);

            grid.Children.Add(
                information);

            grid.Children.Add(
                editButton);

            card.Child =
                grid;

            return card;
        }

        // ============================================================
        // CREATE SCHEDULE
        // ============================================================

        private async System.Threading.Tasks.Task CreateScheduleAsync()
        {
            StackPanel content =
                new StackPanel
                {
                    Spacing = 8
                };

            TextBox nameBox =
                new TextBox
                {
                    Header =
                        "Schedule name",

                    PlaceholderText =
                        "Example: School nights"
                };

            content.Children.Add(
                nameBox);

            TextBlock daysHeader =
                new TextBlock
                {
                    Text =
                        "Days",

                    FontWeight =
                        Microsoft.UI.Text.FontWeights.SemiBold,

                    Margin =
                        new Thickness(
                            0,
                            8,
                            0,
                            0)
                };

            content.Children.Add(
                daysHeader);

            Dictionary<DayOfWeek, CheckBox>
                dayBoxes =
                    new();

            DayOfWeek[] days =
            {
                DayOfWeek.Monday,
                DayOfWeek.Tuesday,
                DayOfWeek.Wednesday,
                DayOfWeek.Thursday,
                DayOfWeek.Friday,
                DayOfWeek.Saturday,
                DayOfWeek.Sunday
            };

            string[] dayNames =
            {
                "Monday",
                "Tuesday",
                "Wednesday",
                "Thursday",
                "Friday",
                "Saturday",
                "Sunday"
            };

            for (int i = 0; i < days.Length; i++)
            {
                DayOfWeek day =
                    days[i];

                CheckBox checkBox =
                    new CheckBox
                    {
                        Content =
                            dayNames[i],

                        IsChecked =
                            i < 5
                    };

                dayBoxes.Add(
                    day,
                    checkBox);

                content.Children.Add(
                    checkBox);
            }

            TimePicker startPicker =
                new TimePicker
                {
                    Header =
                        "Start time",

                    Time =
                        new TimeSpan(
                            18,
                            0,
                            0),

                    Margin =
                        new Thickness(
                            0,
                            8,
                            0,
                            0)
                };

            content.Children.Add(
                startPicker);

            TimePicker endPicker =
                new TimePicker
                {
                    Header =
                        "End time",

                    Time =
                        new TimeSpan(
                            22,
                            0,
                            0)
                };

            content.Children.Add(
                endPicker);

            ScrollViewer scrollViewer =
                new ScrollViewer
                {
                    Content =
                        content,

                    MaxHeight =
                        500,

                    VerticalScrollBarVisibility =
                        ScrollBarVisibility.Auto,

                    HorizontalScrollBarVisibility =
                        ScrollBarVisibility.Disabled
                };

            ContentDialog dialog =
                new ContentDialog
                {
                    Title =
                        "Create Schedule",

                    Content =
                        scrollViewer,

                    PrimaryButtonText =
                        "Create",

                    CloseButtonText =
                        "Cancel",

                    XamlRoot =
                        this.Content.XamlRoot
                };

            ContentDialogResult result =
                await dialog.ShowAsync();

            if (result !=
                ContentDialogResult.Primary)
            {
                return;
            }

            string name =
                nameBox.Text.Trim();

            if (string.IsNullOrWhiteSpace(name))
            {
                await ShowSimpleMessageAsync(
                    "Please enter a schedule name.");

                return;
            }

            List<DayOfWeek> selectedDays =
                dayBoxes
                    .Where(
                        pair =>
                            pair.Value.IsChecked == true)
                    .Select(
                        pair =>
                            pair.Key)
                    .ToList();

            if (selectedDays.Count == 0)
            {
                await ShowSimpleMessageAsync(
                    "Please select at least one day.");

                return;
            }

            scheduleManager.CreateSchedule(
                name,
                selectedDays,
                startPicker.Time,
                endPicker.Time);
        }

        // ============================================================
        // EDIT SCHEDULE
        // ============================================================

        private async System.Threading.Tasks.Task EditScheduleAsync(
            BlockingSchedule schedule)
        {
            StackPanel content =
                new StackPanel
                {
                    Spacing = 8
                };

            TextBox nameBox =
                new TextBox
                {
                    Header =
                        "Schedule name",

                    Text =
                        schedule.Name
                };

            content.Children.Add(
                nameBox);

            CheckBox enabledBox =
                new CheckBox
                {
                    Content =
                        "Schedule enabled",

                    IsChecked =
                        schedule.IsEnabled,

                    Margin =
                        new Thickness(
                            0,
                            4,
                            0,
                            0)
                };

            content.Children.Add(
                enabledBox);

            TextBlock daysHeader =
                new TextBlock
                {
                    Text =
                        "Days",

                    FontWeight =
                        Microsoft.UI.Text.FontWeights.SemiBold,

                    Margin =
                        new Thickness(
                            0,
                            8,
                            0,
                            0)
                };

            content.Children.Add(
                daysHeader);

            Dictionary<DayOfWeek, CheckBox>
                dayBoxes =
                    new();

            DayOfWeek[] days =
            {
                DayOfWeek.Monday,
                DayOfWeek.Tuesday,
                DayOfWeek.Wednesday,
                DayOfWeek.Thursday,
                DayOfWeek.Friday,
                DayOfWeek.Saturday,
                DayOfWeek.Sunday
            };

            string[] dayNames =
            {
                "Monday",
                "Tuesday",
                "Wednesday",
                "Thursday",
                "Friday",
                "Saturday",
                "Sunday"
            };

            for (int i = 0; i < days.Length; i++)
            {
                DayOfWeek day =
                    days[i];

                CheckBox checkBox =
                    new CheckBox
                    {
                        Content =
                            dayNames[i],

                        IsChecked =
                            schedule.Days.Contains(day)
                    };

                dayBoxes.Add(
                    day,
                    checkBox);

                content.Children.Add(
                    checkBox);
            }

            TimePicker startPicker =
                new TimePicker
                {
                    Header =
                        "Start time",

                    Time =
                        schedule.StartTime,

                    Margin =
                        new Thickness(
                            0,
                            8,
                            0,
                            0)
                };

            content.Children.Add(
                startPicker);

            TimePicker endPicker =
                new TimePicker
                {
                    Header =
                        "End time",

                    Time =
                        schedule.EndTime
                };

            content.Children.Add(
                endPicker);

            Button deleteButton =
                new Button
                {
                    Content =
                        "Delete Schedule",

                    HorizontalAlignment =
                        HorizontalAlignment.Left,

                    Margin =
                        new Thickness(
                            0,
                            10,
                            0,
                            0)
                };

            content.Children.Add(
                deleteButton);

            bool deleteRequested =
                false;

            ScrollViewer scrollViewer =
                new ScrollViewer
                {
                    Content =
                        content,

                    MaxHeight =
                        500,

                    VerticalScrollBarVisibility =
                        ScrollBarVisibility.Auto,

                    HorizontalScrollBarVisibility =
                        ScrollBarVisibility.Disabled
                };

            ContentDialog dialog =
                new ContentDialog
                {
                    Title =
                        $"Edit Schedule — {schedule.Name}",

                    Content =
                        scrollViewer,

                    PrimaryButtonText =
                        "Save",

                    CloseButtonText =
                        "Cancel",

                    XamlRoot =
                        this.Content.XamlRoot
                };

            deleteButton.Click +=
                (s, args) =>
                {
                    deleteRequested = true;

                    dialog.Hide();
                };

            ContentDialogResult result =
                await dialog.ShowAsync();

            if (deleteRequested)
            {
                bool confirmed =
                    await ShowConfirmationAsync(
                        "Delete Schedule",

                        $"Are you sure you want to delete " +
                        $"\"{schedule.Name}\"?");

                if (confirmed)
                {
                    scheduleManager.DeleteSchedule(
                        schedule.Id);
                }

                return;
            }

            if (result !=
                ContentDialogResult.Primary)
            {
                return;
            }

            string name =
                nameBox.Text.Trim();

            if (string.IsNullOrWhiteSpace(name))
            {
                await ShowSimpleMessageAsync(
                    "Please enter a schedule name.");

                return;
            }

            List<DayOfWeek> selectedDays =
                dayBoxes
                    .Where(
                        pair =>
                            pair.Value.IsChecked == true)
                    .Select(
                        pair =>
                            pair.Key)
                    .ToList();

            if (selectedDays.Count == 0)
            {
                await ShowSimpleMessageAsync(
                    "Please select at least one day.");

                return;
            }

            schedule.Name =
                name;

            schedule.IsEnabled =
                enabledBox.IsChecked == true;

            schedule.Days =
                selectedDays;

            schedule.StartTime =
                startPicker.Time;

            schedule.EndTime =
                endPicker.Time;

            scheduleManager.Save();
        }

        // ============================================================
        // CHECKBOX GENERATION
        // ============================================================

        private void AddCheckboxIfMissing(
            BlockedApp app,
            StackPanel appList,
            Dictionary<CheckBox, BlockedApp> checkBoxes,
            bool checkedByDefault)
        {
            bool alreadyShown =
                checkBoxes.Values.Any(
                    existing =>
                        PathsEqual(
                            existing.ExecutablePath,
                            app.ExecutablePath));

            if (alreadyShown)
                return;

            StackPanel appContent =
                new StackPanel
                {
                    Spacing = 1
                };

            appContent.Children.Add(
                new TextBlock
                {
                    Text =
                        app.Name
                });

            appContent.Children.Add(
                new TextBlock
                {
                    Text =
                        app.ExecutablePath,

                    FontSize = 11,

                    Opacity = 0.55
                });

            CheckBox checkBox =
                new CheckBox
                {
                    Content =
                        appContent,

                    IsChecked =
                        checkedByDefault
                };

            checkBoxes.Add(
                checkBox,
                app);

            appList.Children.Add(
                checkBox);
        }

        // ============================================================
        // BROWSE FOR EXE
        // ============================================================

        private async System.Threading.Tasks.Task
            AddApplicationFromPickerAsync(
                StackPanel appList,
                Dictionary<CheckBox, BlockedApp> checkBoxes)
        {
            FileOpenPicker picker =
                new FileOpenPicker();

            picker.ViewMode =
                PickerViewMode.List;

            picker.SuggestedStartLocation =
                PickerLocationId.ComputerFolder;

            picker.FileTypeFilter.Add(
                ".exe");

            IntPtr hwnd =
                WindowNative.GetWindowHandle(
                    this);

            InitializeWithWindow.Initialize(
                picker,
                hwnd);

            StorageFile? file =
                await picker.PickSingleFileAsync();

            if (file == null)
                return;

            BlockedApp? existing =
                availableApps.FirstOrDefault(
                    app =>
                        PathsEqual(
                            app.ExecutablePath,
                            file.Path));

            BlockedApp app;

            if (existing != null)
            {
                app = existing;
            }
            else
            {
                app =
                    new BlockedApp
                    {
                        Name =
                            Path.GetFileNameWithoutExtension(
                                file.Name),

                        ExecutablePath =
                            file.Path
                    };

                availableApps.Add(app);
            }

            AddCheckboxIfMissing(
                app,
                appList,
                checkBoxes,
                true);
        }

        // ============================================================
        // DETECT RUNNING APPLICATIONS
        // ============================================================

        private void DetectRunningApplications(
            StackPanel appList,
            Dictionary<CheckBox, BlockedApp> checkBoxes)
        {
            List<DetectedApplication>
                detectedApplications =
                    blockingService
                        .GetRunningApplications();

            if (detectedApplications.Count == 0)
            {
                appList.Children.Add(
                    new TextBlock
                    {
                        Text =
                            "No accessible running applications were detected.",

                        Opacity = 0.7,

                        Margin =
                            new Thickness(
                                0,
                                8,
                                0,
                                0),

                        TextWrapping =
                            TextWrapping.Wrap
                    });

                return;
            }

            List<DetectedApplication>
                uniqueApplications =
                    detectedApplications
                        .GroupBy(
                            app =>
                                app.ExecutablePath,
                            StringComparer.OrdinalIgnoreCase)
                        .Select(
                            group =>
                                group.First())
                        .OrderBy(
                            app =>
                                app.Name)
                        .ToList();

            TextBlock detectedHeader =
                new TextBlock
                {
                    Text =
                        "Detected running applications:",

                    FontWeight =
                        Microsoft.UI.Text.FontWeights.SemiBold,

                    Margin =
                        new Thickness(
                            0,
                            10,
                            0,
                            4)
                };

            appList.Children.Add(
                detectedHeader);

            foreach (
                DetectedApplication detected
                in uniqueApplications)
            {
                BlockedApp? existing =
                    availableApps.FirstOrDefault(
                        app =>
                            PathsEqual(
                                app.ExecutablePath,
                                detected.ExecutablePath));

                BlockedApp app;

                if (existing != null)
                {
                    app = existing;
                }
                else
                {
                    app =
                        new BlockedApp
                        {
                            Name =
                                detected.Name,

                            ExecutablePath =
                                detected.ExecutablePath
                        };

                    availableApps.Add(app);
                }

                AddCheckboxIfMissing(
                    app,
                    appList,
                    checkBoxes,
                    false);
            }
        }

        // ============================================================
        // APP GROUP MANAGEMENT
        // ============================================================

        private async void ManageAppGroups_Click(
            object sender,
            RoutedEventArgs e)
        {
            if (HasIncompleteTasks)
            {
                await ShowSimpleMessageAsync(
                    "App group settings are locked while tasks are incomplete.\n\n" +
                    "Complete all tasks before changing app groups.");

                return;
            }

            while (true)
            {
                AppGroup? selectedGroup = null;

                bool createNewGroup = false;

                StackPanel content =
                    new StackPanel
                    {
                        Spacing = 12
                    };

                content.Children.Add(
                    new TextBlock
                    {
                        Text =
                            "Create groups of applications that can be assigned " +
                            "to tasks. For example, put all games into a Games group.",

                        TextWrapping =
                            TextWrapping.Wrap,

                        Opacity = 0.75
                    });

                StackPanel groupList =
                    new StackPanel
                    {
                        Spacing = 8
                    };

                ScrollViewer groupScroll =
                    new ScrollViewer
                    {
                        Content =
                            groupList,

                        Height = 400,

                        VerticalScrollBarVisibility =
                            ScrollBarVisibility.Auto
                    };

                content.Children.Add(
                    groupScroll);

                ContentDialog dialog =
                    new ContentDialog
                    {
                        Title =
                            "Manage App Groups",

                        Content =
                            content,

                        CloseButtonText =
                            "Close",

                        XamlRoot =
                            this.Content.XamlRoot
                    };

                // Subscribe before ShowAsync so the wait below can
                // never miss the Closed signal.
                System.Threading.Tasks.Task groupsDialogClosed =
                    WaitDialogClosedAsync(dialog);

                // ----------------------------------------------------
                // GROUP ROWS
                // ----------------------------------------------------

                foreach (AppGroup group
                    in groupManager.Groups)
                {
                    Grid row =
                        new Grid
                        {
                            Padding =
                                new Thickness(8)
                        };

                    row.ColumnDefinitions.Add(
                        new ColumnDefinition
                        {
                            Width =
                                new GridLength(
                                    1,
                                    GridUnitType.Star)
                        });

                    row.ColumnDefinitions.Add(
                        new ColumnDefinition
                        {
                            Width =
                                GridLength.Auto
                        });

                    StackPanel info =
                        new StackPanel
                        {
                            Spacing = 2
                        };

                    info.Children.Add(
                        new TextBlock
                        {
                            Text =
                                group.Name,

                            FontSize = 16,

                            FontWeight =
                                Microsoft.UI.Text.FontWeights.SemiBold
                        });

                    info.Children.Add(
                        new TextBlock
                        {
                            Text =
                                $"{group.Apps.Count} application(s)" +
                                (string.IsNullOrWhiteSpace(
                                    group.Description)
                                    ? ""
                                    : $" — {group.Description}"),

                            FontSize = 12,

                            Opacity = 0.65,

                            TextWrapping =
                                TextWrapping.Wrap
                        });

                    Grid.SetColumn(
                        info,
                        0);

                    Button editButton =
                        new Button
                        {
                            Content =
                                "Edit",

                            Margin =
                                new Thickness(
                                    8,
                                    0,
                                    0,
                                    0)
                        };

                    Grid.SetColumn(
                        editButton,
                        1);

                    editButton.Click +=
                        (s, args) =>
                        {
                            selectedGroup =
                                group;

                            dialog.Hide();
                        };

                    row.Children.Add(info);

                    row.Children.Add(editButton);

                    groupList.Children.Add(row);
                }

                if (groupManager.Groups.Count == 0)
                {
                    groupList.Children.Add(
                        new TextBlock
                        {
                            Text =
                                "No app groups exist yet.",

                            Opacity = 0.65,

                            Margin =
                                new Thickness(4)
                        });
                }

                // ----------------------------------------------------
                // NEW GROUP
                // ----------------------------------------------------

                Button newGroupButton =
                    new Button
                    {
                        Content =
                            "+ New Group",

                        HorizontalAlignment =
                            HorizontalAlignment.Left
                    };

                newGroupButton.Click +=
                    (s, args) =>
                    {
                        createNewGroup = true;

                        dialog.Hide();
                    };

                content.Children.Add(
                    newGroupButton);

                await dialog.ShowAsync();

                await groupsDialogClosed;

                if (selectedGroup != null)
                {
                    await EditAppGroupAsync(
                        selectedGroup);

                    continue;
                }

                if (createNewGroup)
                {
                    await CreateNewGroupAsync();

                    continue;
                }

                break;
            }

            RefreshTaskList();
        }

        // ============================================================
        // CREATE GROUP
        // ============================================================

        private async System.Threading.Tasks.Task
            CreateNewGroupAsync()
        {
            StackPanel content =
                new StackPanel
                {
                    Spacing = 8
                };

            TextBox nameBox =
                new TextBox
                {
                    Header =
                        "Group name",

                    PlaceholderText =
                        "Example: Games"
                };

            TextBox descriptionBox =
                new TextBox
                {
                    Header =
                        "Description",

                    PlaceholderText =
                        "Example: Games and entertainment"
                };

            content.Children.Add(
                nameBox);

            content.Children.Add(
                descriptionBox);

            ContentDialog dialog =
                new ContentDialog
                {
                    Title =
                        "Create App Group",

                    Content =
                        content,

                    PrimaryButtonText =
                        "Create",

                    CloseButtonText =
                        "Cancel",

                    XamlRoot =
                        this.Content.XamlRoot
                };

            ContentDialogResult result =
                await dialog.ShowAsync();

            if (result !=
                ContentDialogResult.Primary)
            {
                return;
            }

            string name =
                nameBox.Text.Trim();

            if (string.IsNullOrWhiteSpace(name))
            {
                await ShowSimpleMessageAsync(
                    "The group name cannot be empty.");

                return;
            }

            bool duplicate =
                groupManager.Groups.Any(
                    group =>
                        group.Name.Equals(
                            name,
                            StringComparison.OrdinalIgnoreCase));

            if (duplicate)
            {
                await ShowSimpleMessageAsync(
                    "A group with that name already exists.");

                return;
            }

            groupManager.CreateGroup(
                name,
                descriptionBox.Text.Trim());

            await SaveAppGroupsAsync();
        }

        // ============================================================
        // EDIT GROUP
        // ============================================================

        private async System.Threading.Tasks.Task
            EditAppGroupAsync(
                AppGroup group)
        {
            while (true)
            {
                BlockedApp? appToRemove = null;

                bool addApplication = false;

                bool scanRequested = false;

                bool deleteGroup = false;

                StackPanel content =
                    new StackPanel
                    {
                        Spacing = 10
                    };

                TextBox nameBox =
                    new TextBox
                    {
                        Header =
                            "Group name",

                        Text =
                            group.Name
                    };

                TextBox descriptionBox =
                    new TextBox
                    {
                        Header =
                            "Description",

                        Text =
                            group.Description,

                        AcceptsReturn = true,

                        TextWrapping =
                            TextWrapping.Wrap,

                        Height = 70
                    };

                content.Children.Add(
                    nameBox);

                content.Children.Add(
                    descriptionBox);

                content.Children.Add(
                    new TextBlock
                    {
                        Text =
                            "Applications",

                        FontSize = 16,

                        FontWeight =
                            Microsoft.UI.Text.FontWeights.SemiBold,

                        Margin =
                            new Thickness(
                                0,
                                8,
                                0,
                                2)
                    });

                StackPanel appList =
                    new StackPanel
                    {
                        Spacing = 4
                    };

                // ----------------------------------------------------
                // APPLICATIONS
                // ----------------------------------------------------

                foreach (BlockedApp app
                    in group.Apps.ToList())
                {
                    Grid row =
                        new Grid
                        {
                            Padding =
                                new Thickness(6)
                        };

                    row.ColumnDefinitions.Add(
                        new ColumnDefinition
                        {
                            Width =
                                new GridLength(
                                    1,
                                    GridUnitType.Star)
                        });

                    row.ColumnDefinitions.Add(
                        new ColumnDefinition
                        {
                            Width =
                                GridLength.Auto
                        });

                    StackPanel info =
                        new StackPanel
                        {
                            Spacing = 1
                        };

                    info.Children.Add(
                        new TextBlock
                        {
                            Text =
                                app.Name,

                            FontSize = 14
                        });

                    info.Children.Add(
                        new TextBlock
                        {
                            Text =
                                app.ExecutablePath,

                            FontSize = 11,

                            Opacity = 0.55,

                            TextWrapping =
                                TextWrapping.Wrap
                        });

                    Grid.SetColumn(
                        info,
                        0);

                    Button removeButton =
                        new Button
                        {
                            Content =
                                "Remove"
                        };

                    Grid.SetColumn(
                        removeButton,
                        1);

                    removeButton.Click +=
                        (s, args) =>
                        {
                            appToRemove = app;
                        };

                    row.Children.Add(info);

                    row.Children.Add(removeButton);

                    appList.Children.Add(row);
                }

                if (group.Apps.Count == 0)
                {
                    appList.Children.Add(
                        new TextBlock
                        {
                            Text =
                                "No applications in this group.",

                            Opacity = 0.65,

                            Margin =
                                new Thickness(4)
                        });
                }

                // FIX:
                // This was accidentally written as Scontent.Children.Add(...)
                // It must be content.Children.Add(...).

                ScrollViewer groupAppsScrollViewer =
                    new ScrollViewer
                    {
                        Content =
                            appList,

                        MaxHeight =
                            300,

                        VerticalScrollBarVisibility =
                            ScrollBarVisibility.Auto,

                        HorizontalScrollBarVisibility =
                            ScrollBarVisibility.Disabled
                    };

                content.Children.Add(
                    groupAppsScrollViewer);

                // ----------------------------------------------------
                // ADD APPLICATION / BROWSE
                // ----------------------------------------------------

                StackPanel appButtons =
                    new StackPanel
                    {
                        Orientation =
                            Orientation.Horizontal,

                        Spacing = 8
                    };

                Button addButton =
                    new Button
                    {
                        Content =
                            "+ Add EXE"
                    };

                Button scanButton =
                    new Button
                    {
                        Content =
                            "🔍 Scan Running Apps"
                    };

                appButtons.Children.Add(
                    addButton);

                appButtons.Children.Add(
                    scanButton);

                content.Children.Add(
                    appButtons);

                // ----------------------------------------------------
                // DELETE GROUP
                // ----------------------------------------------------

                Button deleteButton =
                    new Button
                    {
                        Content =
                            "Delete Group",

                        HorizontalAlignment =
                            HorizontalAlignment.Left,

                        Margin =
                            new Thickness(
                                0,
                                8,
                                0,
                                0)
                    };

                content.Children.Add(
                    deleteButton);

                // ----------------------------------------------------
                // DIALOG SCROLL VIEWER
                // ----------------------------------------------------

                ScrollViewer dialogScrollViewer =
                    new ScrollViewer
                    {
                        Content =
                            content,

                        MaxHeight =
                            600,

                        VerticalScrollBarVisibility =
                            ScrollBarVisibility.Auto,

                        HorizontalScrollBarVisibility =
                            ScrollBarVisibility.Disabled
                    };

                ContentDialog dialog =
                    new ContentDialog
                    {
                        Title =
                            $"Edit Group — {group.Name}",

                        Content =
                            dialogScrollViewer,

                        PrimaryButtonText =
                            "Save",

                        CloseButtonText =
                            "Cancel",

                        XamlRoot =
                            this.Content.XamlRoot
                    };

                // Subscribe before ShowAsync so the wait below can
                // never miss the Closed signal.
                System.Threading.Tasks.Task editDialogClosed =
                    WaitDialogClosedAsync(dialog);

                // ----------------------------------------------------
                // BUTTON EVENTS
                // ----------------------------------------------------

                addButton.Click +=
                    (s, args) =>
                    {
                        addApplication = true;

                        dialog.Hide();
                    };

                scanButton.Click +=
                    (s, args) =>
                    {
                        scanRequested = true;

                        dialog.Hide();
                    };

                deleteButton.Click +=
                    (s, args) =>
                    {
                        deleteGroup = true;

                        dialog.Hide();
                    };

                foreach (UIElement element
                    in appList.Children)
                {
                    if (element is Grid row)
                    {
                        foreach (UIElement child
                            in row.Children)
                        {
                            if (child is Button button &&
                                button.Content?.ToString() ==
                                "Remove")
                            {
                                button.Click +=
                                    (s, args) =>
                                    {
                                        dialog.Hide();
                                    };
                            }
                        }
                    }
                }

                // ----------------------------------------------------
                // SHOW
                // ----------------------------------------------------

                ContentDialogResult result =
                    await dialog.ShowAsync();

                // The dialog is closing but not yet closed - wait so
                // the next dialog cannot race it. The task was created
                // before ShowAsync so Closed cannot be missed.
                await editDialogClosed;

                // ----------------------------------------------------
                // SCAN RUNNING APPLICATIONS
                // ----------------------------------------------------

                if (scanRequested)
                {
                    await ScanRunningAppsForGroupAsync(group);

                    continue;
                }

                // ----------------------------------------------------
                // DELETE
                // ----------------------------------------------------

                if (deleteGroup)
                {
                    bool confirmed =
                        await ShowConfirmationAsync(
                            "Delete Group",

                            $"Are you sure you want to delete " +
                            $"\"{group.Name}\"?");

                    if (confirmed)
                    {
                        groupManager.DeleteGroup(
                            group.Id);

                        await SaveAppGroupsAsync();

                        return;
                    }

                    continue;
                }

                // ----------------------------------------------------
                // ADD APPLICATION
                // ----------------------------------------------------

                if (addApplication)
                {
                    await AddApplicationToGroupAsync(
                        group);

                    continue;
                }

                // ----------------------------------------------------
                // REMOVE APPLICATION
                // ----------------------------------------------------

                if (appToRemove != null)
                {
                    groupManager.RemoveAppFromGroup(
                        group.Id,
                        appToRemove.ExecutablePath);

                    await SaveAppGroupsAsync();

                    continue;
                }

                // ----------------------------------------------------
                // CANCEL
                // ----------------------------------------------------

                if (result !=
                    ContentDialogResult.Primary)
                {
                    return;
                }

                // ----------------------------------------------------
                // SAVE
                // ----------------------------------------------------

                string newName =
                    nameBox.Text.Trim();

                if (string.IsNullOrWhiteSpace(newName))
                {
                    await ShowSimpleMessageAsync(
                        "The group name cannot be empty.");

                    continue;
                }

                bool duplicate =
                    groupManager.Groups.Any(
                        existing =>
                            existing != group &&
                            existing.Name.Equals(
                                newName,
                                StringComparison.OrdinalIgnoreCase));

                if (duplicate)
                {
                    await ShowSimpleMessageAsync(
                        "Another group already has that name.");

                    continue;
                }

                group.Name =
                    newName;

                group.Description =
                    descriptionBox.Text.Trim();

                await SaveAppGroupsAsync();

                return;
            }
        }

        // ============================================================
        // ADD APPLICATION TO GROUP
        // ============================================================
        private async System.Threading.Tasks.Task ShowSaveLocationAsync()
        {
            await ShowSimpleMessageAsync(
                $"Save directory:\n{saveDirectory}\n\n" +
                $"Task file:\n{saveFilePath}\n\n" +
                $"File exists:\n{File.Exists(saveFilePath)}");
        }

        private async System.Threading.Tasks.Task
    AddApplicationToGroupAsync(
        AppGroup group)
        {
            FileOpenPicker picker =
                new FileOpenPicker
                {
                    ViewMode =
                        PickerViewMode.List,

                    SuggestedStartLocation =
                        PickerLocationId.ComputerFolder
                };

            picker.FileTypeFilter.Add(".exe");

            IntPtr hwnd =
                WindowNative.GetWindowHandle(this);

            InitializeWithWindow.Initialize(
                picker,
                hwnd);

            StorageFile? file =
                await picker.PickSingleFileAsync();

            if (file == null)
                return;

            await AddExecutableToGroupAsync(
                group,
                file.Name,
                file.Path);
        }

        private async System.Threading.Tasks.Task
    AddExecutableToGroupAsync(
        AppGroup group,
        string name,
        string executablePath)
        {
            bool alreadyExists =
                group.Apps.Any(
                    app =>
                        PathsEqual(
                            app.ExecutablePath,
                            executablePath));

            if (alreadyExists)
            {
                await ShowSimpleMessageAsync(
                    "That application is already in this group.");

                return;
            }

            BlockedApp? existing =
                availableApps.FirstOrDefault(
                    app =>
                        PathsEqual(
                            app.ExecutablePath,
                            executablePath));

            BlockedApp app;

            if (existing != null)
            {
                app = existing;
            }
            else
            {
                app =
                    new BlockedApp
                    {
                        Name = name,
                        ExecutablePath = executablePath
                    };

                availableApps.Add(app);
            }

            groupManager.AddAppToGroup(
                group.Id,
                app);

            await SaveAppGroupsAsync();
        }
        private async System.Threading.Tasks.Task


    ScanRunningAppsForGroupAsync(
        AppGroup group)
        {
            try
            {
            List<DetectedApplication>
                detectedApplications =
                    blockingService
                        .GetRunningApplications();

            if (detectedApplications.Count == 0)
            {
                await ShowSimpleMessageAsync(
                    "No accessible running applications were detected.");

                return;
            }

            List<DetectedApplication>
                uniqueApplications =
                    detectedApplications
                        .GroupBy(
                            app =>
                                app.ExecutablePath,
                            StringComparer.OrdinalIgnoreCase)
                        .Select(
                            detectedGroup =>
                                detectedGroup.First())
                        .OrderBy(
                            app =>
                                app.Name)
                        .ToList();

            StackPanel panel =
                new StackPanel
                {
                    Spacing = 4
                };

            Dictionary<CheckBox, DetectedApplication>
                checkBoxToApplication =
                    new();

            foreach (
                DetectedApplication detected
                in uniqueApplications)
            {
                CheckBox checkBox =
                    new CheckBox
                    {
                        Content =
                            new StackPanel
                            {
                                Spacing = 1,
                                Children =
                                {
                            new TextBlock
                            {
                                Text =
                                    detected.Name
                            },

                            new TextBlock
                            {
                                Text =
                                    detected.ExecutablePath,

                                FontSize = 11,

                                Opacity = 0.55,

                                TextWrapping =
                                    TextWrapping.Wrap
                            }
                                }
                            }
                    };

                    checkBoxToApplication.Add(
                        checkBox,
                        detected);

                panel.Children.Add(checkBox);
            }

            ScrollViewer scrollViewer =
                new ScrollViewer
                {
                    Content = panel,

                    MaxHeight = 450,

                    VerticalScrollBarVisibility =
                        ScrollBarVisibility.Auto,

                    HorizontalScrollBarVisibility =
                        ScrollBarVisibility.Disabled
                };

            ContentDialog dialog =
                new ContentDialog
                {
                    Title =
                        "Scan Running Applications",

                    Content =
                        scrollViewer,

                    PrimaryButtonText =
                        "Add Selected",

                    CloseButtonText =
                        "Cancel",

                    XamlRoot =
                        this.Content.XamlRoot
                };

            ContentDialogResult result =
                await dialog.ShowAsync();

            if (result !=
                ContentDialogResult.Primary)
            {
                return;
            }

            foreach (
                KeyValuePair<CheckBox, DetectedApplication> pair
                in checkBoxToApplication)
            {
                if (pair.Key.IsChecked != true)
                {
                    continue;
                }

                await AddExecutableToGroupAsync(
                    group,
                    pair.Value.Name,
                    pair.Value.ExecutablePath);
            }

            await SaveAppGroupsAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"Scanning running applications failed: {ex}");

                try
                {
                    await ShowSimpleMessageAsync(
                        "Could not scan running applications.\n\n" + ex.Message);
                }
                catch
                {
                }
            }
        }

        // ============================================================
        // SAVE GROUPS
        // ============================================================

        private async System.Threading.Tasks.Task
            SaveAppGroupsAsync()
        {
            try
            {
                groupManager.Save();

                await System.Threading.Tasks.Task.CompletedTask;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"Failed to save app groups: {ex}");
            }
        }


        private async void SyncNowButton_Click(
            object sender,
            RoutedEventArgs e)
        {
            SyncResult result =
                await syncManager.SyncNowAsync(
                    tasks,
                    groupManager,
                    scheduleManager);

            if (result.Success)
            {
                // Merged items may have changed anything - refresh
                // the whole UI state.
                RefreshTaskList();

            UpdateFocusModeLock();

            UpdateScheduledBlockingState();

            UpdateWebsiteBlockingState();

            EnforceBlocking();
        }

            await ShowSimpleMessageAsync(
                result.Success
                    ? $"Sync complete.\n\n{result.Message}"
                    : $"Sync failed.\n\n{result.Message}");
        }


        // ============================================================
        // LOAD GROUPS
        // ============================================================

        private async System.Threading.Tasks.Task
            LoadAppGroupsAsync()
        {
            try
            {
                foreach (
                    AppGroup group
                    in groupManager.Groups)
                {
                    foreach (
                        BlockedApp app
                        in group.Apps)
                    {
                        bool exists =
                            availableApps.Any(
                                existing =>
                                    PathsEqual(
                                        existing.ExecutablePath,
                                        app.ExecutablePath));

                        if (!exists)
                        {
                            availableApps.Add(
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

                await System.Threading.Tasks.Task.CompletedTask;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"Failed to initialize app groups: {ex}");
            }
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

        // ============================================================
        // SAVE TASKS
        // ============================================================

        private async System.Threading.Tasks.Task SaveTasksAsync()
        {
            try
            {
                Directory.CreateDirectory(saveDirectory);

                JsonSerializerOptions options =
                    new JsonSerializerOptions
                    {
                        WriteIndented = true
                    };

                string json =
                    JsonSerializer.Serialize(
                        tasks,
                        options);

                string tempFilePath =
                    saveFilePath + ".tmp";

                // Write the new save first.
                await File.WriteAllTextAsync(
                    tempFilePath,
                    json);

                // Only replace the real save after the write succeeds.
                File.Move(
                    tempFilePath,
                    saveFilePath,
                    true);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"Failed to save tasks: {ex}");
            }
        }

        // ============================================================
        // LOAD TASKS
        // ============================================================

        private async System.Threading.Tasks.Task LoadTasksAsync()
        {
            try
            {
                if (!File.Exists(saveFilePath))
                {
                    System.Diagnostics.Debug.WriteLine(
                        "No task save file exists yet.");

                    return;
                }

                string json =
                    await File.ReadAllTextAsync(
                        saveFilePath);

                if (string.IsNullOrWhiteSpace(json))
                {
                    System.Diagnostics.Debug.WriteLine(
                        "Task save file is empty.");

                    return;
                }

                List<TodoTask>? loadedTasks =
                    JsonSerializer.Deserialize<List<TodoTask>>(
                        json,
                        new JsonSerializerOptions
                        {
                            PropertyNameCaseInsensitive = true
                        });

                if (loadedTasks == null)
                {
                    System.Diagnostics.Debug.WriteLine(
                        "Task save file could not be deserialized.");

                    return;
                }

                // Purge deletion tombstones older than 30 days so
                // tasks.json does not grow forever.
                loadedTasks.RemoveAll(
                    task =>
                        task.IsDeleted &&
                        task.LastModified <
                            DateTime.UtcNow.AddDays(-30));

                // Only replace the current list AFTER successfully
                // loading the save file.
                tasks.Clear();

                foreach (TodoTask task in loadedTasks)
                {
                    // Give older tasks an ID if they don't have one.
                    if (string.IsNullOrWhiteSpace(task.Id))
                    {
                        task.Id = Guid.NewGuid().ToString();
                    }

                    // Protect against older save files that don't have
                    // these properties.
                    task.BlockedGroups ??=
                        new List<string>();

                    task.BlockedApps ??=
                        new List<BlockedApp>();

                    // Give older blocked apps an ID if they don't have one.
                    foreach (BlockedApp app in task.BlockedApps)
                    {
                        if (string.IsNullOrWhiteSpace(app.Id))
                        {
                            app.Id = Guid.NewGuid().ToString();
                        }
                    }

                    // Add individually saved apps to the available
                    // application list.
                    foreach (BlockedApp app in task.BlockedApps)
                    {
                        if (string.IsNullOrWhiteSpace(
                            app.ExecutablePath))
                        {
                            continue;
                        }

                        bool exists =
                            availableApps.Any(
                                existing =>
                                    PathsEqual(
                                        existing.ExecutablePath,
                                        app.ExecutablePath));

                        if (!exists)
                        {
                            availableApps.Add(
                                new BlockedApp
                                {
                                    Name = app.Name,
                                    ExecutablePath =
                                        app.ExecutablePath
                                });
                        }
                    }

                    tasks.Add(task);
                }

                System.Diagnostics.Debug.WriteLine(
                    $"Successfully loaded {tasks.Count} task(s).");

                foreach (TodoTask task in tasks)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"Loaded task: {task.Title} | " +
                        $"Completed: {task.IsCompleted}");
                }

                // IMPORTANT:
                //
                // Do NOT call SaveTasksAsync() here.
                //
                // If the save file is damaged, we don't want to
                // accidentally replace it with an empty task list.
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"FAILED TO LOAD TASKS: {ex}");

                // IMPORTANT:
                //
                // Do not clear the existing list here.
                // Do not save here.
                //
                // Most importantly, don't turn a loading problem
                // into permanent data loss.
            }
        }

        // ============================================================
        // TRAY ICON
        // ============================================================

        private void InitializeTrayIcon()
        {
            try
            {
                trayIconManager =
                    new TrayIconManager
                    {
                        OpenRequested = () =>
                            DispatcherQueue.TryEnqueue(
                                () => ShowMainWindow()),

                        ExitRequested = () =>
                            DispatcherQueue.TryEnqueue(
                                async () =>
                                    await ExitFromTrayAsync())
                    };

                trayIconManager.Show("Lochlan Productivity");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"Tray icon unavailable: {ex}");
            }
        }

        public void ShowFromHiddenState()
        {
            ShowMainWindow();
        }

        private void ShowMainWindow()
        {
            try
            {
                this.AppWindow.Show();
            }
            catch
            {
            }

            this.Activate();
        }

        private async System.Threading.Tasks.Task
            ExitFromTrayAsync()
        {
            // Bring the window back so the confirmation dialog has
            // a XamlRoot and the user sees what is happening.
            ShowMainWindow();

            if (HasIncompleteTasks)
            {
                await ShowSimpleMessageAsync(
                    "Cannot quit while tasks are incomplete.\n\n" +
                    "Complete all of your tasks first.");

                return;
            }

            trayIconManager?.Dispose();

            trayIconManager = null;

            App.CurrentApp?.ExitApplication();

            Application.Current.Exit();
        }

        // ============================================================
        // START WITH WINDOWS
        // ============================================================

        private async System.Threading.Tasks.Task
            UpdateStartupToggleButtonAsync()
        {
            try
            {
                StartupState state =
                    await startupManager.GetStateAsync();

                StartupToggleButton.Content =
                    state switch
                    {
                        StartupState.Enabled =>
                            "Starts with Windows ✓",

                        StartupState.DisabledByUser =>
                            "Startup off (Task Manager)",

                        StartupState.DisabledByPolicy =>
                            "Startup blocked by policy",

                        _ => "Start with Windows ✗"
                    };
            }
            catch
            {
                StartupToggleButton.Content =
                    "Start with Windows";
            }
        }

        private async void StartupToggle_Click(
            object sender,
            RoutedEventArgs e)
        {
            if (HasIncompleteTasks)
            {
                await ShowSimpleMessageAsync(
                    "Startup settings are locked while tasks are incomplete.\n\n" +
                    "Complete all tasks before changing settings.");

                return;
            }

            bool currentlyEnabled =
                (await startupManager.GetStateAsync()) ==
                    StartupState.Enabled;

            bool success =
                await startupManager.SetEnabledAsync(
                    !currentlyEnabled);

            await UpdateStartupToggleButtonAsync();

            if (!success)
            {
                await ShowSimpleMessageAsync(
                    "Could not change the Windows startup setting.");
            }
        }

        // ============================================================
        // DIALOG HELPERS
        // ============================================================

        // WinUI allows only one open ContentDialog per XamlRoot, and
        // Hide() is asynchronous under the hood - showing another
        // dialog immediately afterwards throws and crashes the app.
        // Awaiting the Closed event removes that race.
        private static System.Threading.Tasks.Task
            WaitDialogClosedAsync(ContentDialog dialog)
        {
            TaskCompletionSource completion =
                new(
                    TaskCreationOptions.RunContinuationsAsynchronously);

            void OnClosed(
                ContentDialog sender,
                ContentDialogClosedEventArgs args)
            {
                completion.TrySetResult();
            }

            dialog.Closed += OnClosed;

            return completion.Task;
        }

        private static async System.Threading.Tasks.Task
            HideDialogAndWaitClosedAsync(ContentDialog dialog)
        {
            System.Threading.Tasks.Task closed =
                WaitDialogClosedAsync(dialog);

            dialog.Hide();

            await closed;
        }

        // ============================================================
        // SIMPLE MESSAGE
        // ============================================================

        private async System.Threading.Tasks.Task
            ShowSimpleMessageAsync(
                string message)
        {
            ContentDialog dialog =
                new ContentDialog
                {
                    Title =
                        "Lochlan Productivity",

                    Content =
                        new TextBlock
                        {
                            Text =
                                message,

                            TextWrapping =
                                TextWrapping.Wrap
                        },

                    CloseButtonText =
                        "OK",

                    XamlRoot =
                        this.Content.XamlRoot
                };

            await dialog.ShowAsync();
        }

        // ============================================================
        // CONFIRMATION
        // ============================================================

        private async System.Threading.Tasks.Task<bool>
            ShowConfirmationAsync(
                string title,
                string message)
        {
            ContentDialog dialog =
                new ContentDialog
                {
                    Title =
                        title,

                    Content =
                        new TextBlock
                        {
                            Text =
                                message,

                            TextWrapping =
                                TextWrapping.Wrap
                        },

                    PrimaryButtonText =
                        "Yes",

                    CloseButtonText =
                        "No",

                    DefaultButton =
                        ContentDialogButton.Close,

                    XamlRoot =
                        this.Content.XamlRoot
                };

            ContentDialogResult result =
                await dialog.ShowAsync();

            return result ==
                ContentDialogResult.Primary;
        }
    }

    // ================================================================
    // DATA MODELS
    // ================================================================


    public class TodoTask
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();

        public DateTime LastModified { get; set; } = DateTime.UtcNow;
        public string Title { get; set; } = "";

        public bool IsCompleted { get; set; } = false;

        // Tombstone for sync: removed tasks stay in the save file
        // so deletions propagate between computers, but they are
        // invisible and never enforced.
        public bool IsDeleted { get; set; } = false;

        public List<string> BlockedGroups { get; set; } = new();

        public List<BlockedApp> BlockedApps { get; set; } = new();

        // ============================================================
        // SCHEDULING
        // ============================================================

        public bool IsRecurring { get; set; } = false;

        public TaskPriority Priority { get; set; } =
            TaskPriority.Normal;

        // Optional time-of-day on top of DueDate.
        // Null means "any time that day" (end of day).
        public TimeSpan? DueTimeOfDay { get; set; }

        public RecurrenceType Recurrence { get; set; } =
            RecurrenceType.None;

        // Used for "every N days"
        public int RecurrenceInterval { get; set; } = 1;

        // Used for specific days of the week
        public List<DayOfWeek> RecurrenceDays { get; set; } = new();

        // The date this task is currently due.
        public DateTime DueDate { get; set; } =
            DateTime.Today;

        // Most recent date the task was completed.
        public DateTime? LastCompletedDate { get; set; }
    }

    public enum RecurrenceType
    {
        None,
        Daily,
        EveryNDays,
        WeeklyDays
    }

    public enum TaskPriority
    {
        Low = 0,
        Normal = 1,
        High = 2
    }

    public class BlockedApp
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();

        public string Name { get; set; } = "";

        public string ExecutablePath { get; set; } = "";
    }
}