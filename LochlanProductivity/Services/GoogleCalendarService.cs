using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Calendar.v3;
using Google.Apis.Calendar.v3.Data;
using Google.Apis.Services;
using Google.Apis.Util.Store;

namespace LochlanProductivity.Services
{
    public class CalendarBusyEvent
    {
        public string Summary { get; set; } = "";
        public DateTime Start { get; set; }
        public DateTime End { get; set; }
        public string Status { get; set; } = "";
    }

    public class GoogleCalendarService
    {
        private readonly string credentialsPath =
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LochlanProductivity",
                "google-credentials.json");

        private readonly string tokenDirectory =
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LochlanProductivity",
                "google-token");

        private CalendarService? calendarService;

        private List<CalendarBusyEvent> cachedEvents = new();

        private DateTime lastFetchUtc = DateTime.MinValue;

        public bool IsConfigured => File.Exists(credentialsPath);

        public bool IsConnected => calendarService != null;

        public bool IsInBusyWindow { get; private set; }

        public CalendarBusyEvent? CurrentEvent { get; private set; }

        public DateTime? NextFetchUtc { get; private set; }

        public string LastError { get; private set; } = "";

        public IReadOnlyList<CalendarBusyEvent> CachedEvents => cachedEvents;

        public async Task<bool> TryConnectAsync(CancellationToken ct = default)
        {
            LastError = "";

            if (!IsConfigured)
            {
                LastError = "Missing google-credentials.json. Place it at %LocalAppData%/LochlanProductivity/google-credentials.json (from Google Cloud Console → OAuth Client ID).";
                return false;
            }

            try
            {
                using FileStream stream = new FileStream(credentialsPath, FileMode.Open, FileAccess.Read);

                string[] scopes = { CalendarService.Scope.CalendarReadonly };

                GoogleCredential? cred = null;

                // Use FileDataStore so refresh token is persisted per user.
                var secrets = await GoogleClientSecrets.FromStreamAsync(stream, ct);

                var credential = await GoogleWebAuthorizationBroker.AuthorizeAsync(
                    secrets.Secrets,
                    scopes,
                    "user",
                    ct,
                    new FileDataStore(tokenDirectory, true));

                calendarService = new CalendarService(new BaseClientService.Initializer
                {
                    HttpClientInitializer = credential,
                    ApplicationName = "LochlanProductivity"
                });

                HostsFileBlocker.Log("calendar: connected");
                return true;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                HostsFileBlocker.Log($"calendar connect failed: {ex.Message}");
                return false;
            }
        }

        public async Task RefreshAsync(CancellationToken ct = default)
        {
            if (calendarService == null)
            {
                bool ok = await TryConnectAsync(ct);
                if (!ok) return;
            }

            try
            {
                var now = DateTime.Now;
                var until = now.Date.AddDays(1).AddHours(6); // today + tomorrow morning

                var request = calendarService!.Events.List("primary");
                request.TimeMinDateTimeOffset = new DateTimeOffset(now.AddMinutes(-15));
                request.TimeMaxDateTimeOffset = new DateTimeOffset(until);
                request.SingleEvents = true;
                request.OrderBy = EventsResource.ListRequest.OrderByEnum.StartTime;
                request.ShowDeleted = false;
                request.MaxResults = 20;

                Events events = await request.ExecuteAsync(ct);

                var busy = new List<CalendarBusyEvent>();

                if (events.Items != null)
                {
                    foreach (var ev in events.Items)
                    {
                        // Every busy event counts per user request.
                        // Skip transparent (free) events.
                        if (ev.Transparency == "transparent")
                            continue;

                        if (ev.Status == "cancelled")
                            continue;

                        DateTime start;
                        DateTime end;

                        if (ev.Start.DateTimeDateTimeOffset.HasValue)
                            start = ev.Start.DateTimeDateTimeOffset.Value.LocalDateTime;
                        else if (!string.IsNullOrEmpty(ev.Start.Date))
                            continue; // all-day events don't trigger class block
                        else
                            continue;

                        if (ev.End.DateTimeDateTimeOffset.HasValue)
                            end = ev.End.DateTimeDateTimeOffset.Value.LocalDateTime;
                        else
                            continue;

                        busy.Add(new CalendarBusyEvent
                        {
                            Summary = ev.Summary ?? "(Busy)",
                            Start = start,
                            End = end,
                            Status = ev.Status ?? ""
                        });
                    }
                }

                cachedEvents = busy.OrderBy(e => e.Start).ToList();
                lastFetchUtc = DateTime.UtcNow;
                NextFetchUtc = lastFetchUtc.AddMinutes(5);

                UpdateCurrentWindow();

                HostsFileBlocker.Log($"calendar: fetched {busy.Count} busy events");
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                HostsFileBlocker.Log($"calendar refresh failed: {ex.Message}");
            }
        }

        public void UpdateCurrentWindow()
        {
            var now = DateTime.Now;
            var cur = cachedEvents.FirstOrDefault(e => now >= e.Start && now < e.End);
            CurrentEvent = cur;
            IsInBusyWindow = cur != null;
        }

        public void Disconnect()
        {
            try
            {
                calendarService?.Dispose();
            }
            catch { }
            calendarService = null;
            IsInBusyWindow = false;
            CurrentEvent = null;
            cachedEvents.Clear();
        }
    }
}
