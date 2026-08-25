using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace LochlanProductivity.Services
{
    // ============================================================
    // WEBSITE BLOCKING
    //
    // Blocks distracting websites system-wide while enforcement is
    // active by splicing a marked section into the Windows hosts
    // file (domains -> 0.0.0.0).
    //
    // Writing %SystemRoot%\System32\drivers\etc\hosts requires
    // admin rights. Strategy:
    //   1. Try the direct write (works when the app runs elevated).
    //   2. Otherwise stage the full new content and relaunch this
    //      exe elevated ("runas") with --lp-hostsfile <staged>;
    //      that instance performs the swap and exits (one UAC hit).
    //
    // Blocks are applied/removed only on enforcement-state
    // transitions, so elevation prompts stay rare.
    // ============================================================

    public class HostsFileBlocker
    {
        public static readonly string HostsPath =
            Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.System),
                "drivers",
                "etc",
                "hosts");

        public const string BeginMarker =
            "# >>> LochlanProductivity Block >>>";

        public const string EndMarker =
            "# <<< LochlanProductivity Block <<<";

        // ============================================================
        // ELEVATED HELPER TASK
        //
        // A scheduled task (registered once with a single UAC) that
        // copies the staged file over the hosts file. Because the
        // task's definition was approved by an administrator, the
        // app can start it later with NO elevation prompt - this is
        // what makes per-transition prompts disappear.
        // ============================================================

        private const string TaskName =
            "LochlanProductivityHosts";

        private static bool? taskRegistrationFailedThisSession;

        // NOTE: deliberately under the user profile, NOT
        // LocalAppData - MSIX virtualizes AppData writes into the
        // package sandbox, which would hide the staged file from
        // the elevated helper (it runs outside the sandbox).
        private readonly string stagingDirectory =
            Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.UserProfile),
                "LochlanProductivityData");

        // ============================================================
        // PUBLIC API
        // ============================================================

        public bool HasBlockSection()
        {
            try
            {
                return File.Exists(HostsPath) &&
                    File.ReadAllText(HostsPath)
                        .Contains(BeginMarker, StringComparison.Ordinal);
            }
            catch
            {
                return false;
            }
        }

        // True when the hosts file contains at least one actual
        // block entry inside our section (bare markers don't count -
        // they remain behind after an unblock).
        public bool HasActiveBlockEntries()
        {
            try
            {
                if (!File.Exists(HostsPath))
                    return false;

                string[] lines =
                    File.ReadAllLines(HostsPath);

                bool inside = false;

                foreach (string line in lines)
                {
                    string trimmed = line.Trim();

                    if (trimmed.Equals(BeginMarker, StringComparison.Ordinal))
                    {
                        inside = true;
                        continue;
                    }

                    if (trimmed.Equals(EndMarker, StringComparison.Ordinal))
                    {
                        inside = false;
                        continue;
                    }

                    if (inside && trimmed.Length > 0)
                    {
                        return true;
                    }
                }

                return false;
            }
            catch
            {
                return false;
            }
        }

        public bool Apply(IEnumerable<string> domains)
        {
            List<string> lines =
                new()
                {
                    BeginMarker
                };

            foreach (string domain in NormalizeDomains(domains))
            {
                lines.Add($"0.0.0.0 {domain}");
                lines.Add($"0.0.0.0 www.{domain}");
            }

            lines.Add(EndMarker);

            return WriteSection(lines);
        }

        public bool Remove()
        {
            return WriteSection(new List<string>());
        }

        // ============================================================
        // SECTION SPLICING
        // ============================================================

        private bool WriteSection(List<string> sectionLines)
        {
            try
            {
                string[] existing =
                    File.Exists(HostsPath)
                        ? File.ReadAllLines(HostsPath)
                        : Array.Empty<string>();

                List<string> output = new();

                bool insideSection = false;

                foreach (string line in existing)
                {
                    string trimmed = line.Trim();

                    if (trimmed.Equals(BeginMarker, StringComparison.Ordinal))
                    {
                        insideSection = true;
                        continue;
                    }

                    if (trimmed.Equals(EndMarker, StringComparison.Ordinal))
                    {
                        insideSection = false;
                        continue;
                    }

                    if (!insideSection)
                    {
                        output.Add(line);
                    }
                }

                while (output.Count > 0 &&
                       output[^1].Trim().Length == 0)
                {
                    output.RemoveAt(output.Count - 1);
                }

                if (sectionLines.Count > 0)
                {
                    if (output.Count > 0)
                    {
                        output.Add("");
                    }

                    output.AddRange(sectionLines);
                }
                else
                {
                    // Unblock: keep an EMPTY marked section so the staged
                    // file still passes the helper's sanity check and the
                    // hosts file stays recognizably ours.
                    if (output.Count > 0)
                    {
                        output.Add("");
                    }

                    output.Add(BeginMarker);
                    output.Add(EndMarker);
                }

                return SwapHosts(output);
            }
            catch (Exception ex)
            {
                Log($"WriteSection failed: {ex.Message}");

                return false;
            }
        }

        // ============================================================
        // SWAP (direct, then pre-approved task, then legacy fallback)
        // ============================================================

        private bool SwapHosts(List<string> lines)
        {
            Directory.CreateDirectory(stagingDirectory);

            string stagingPath =
                Path.Combine(stagingDirectory, "hosts-staging.txt");

            File.WriteAllLines(
                stagingPath,
                lines,
                new UTF8Encoding(false));

            Log($"staged {lines.Count} line(s); trying direct write");

            try
            {
                File.Copy(stagingPath, HostsPath, true);

                Log("direct write succeeded");

                return true;
            }
            catch (UnauthorizedAccessException)
            {
                Log("direct write denied");
            }
            catch (IOException ex)
            {
                Log($"direct write IO error: {ex.Message}");
            }

            // Preferred transport: the pre-approved scheduled task.
            if (EnsureHelperTaskRegistered() && RunHelperTask())
            {
                bool expectBlock =
                    lines.Contains(BeginMarker);

                if (WaitForSwap(expectBlock))
                {
                    Log("task swap verified");
                    return true;
                }

                Log("task ran but swap not confirmed; falling back");
            }

            RunElevatedSwap(stagingPath);

            // Fire-and-forget: assume the elevated helper wins.
            return true;
        }

        private void RunElevatedSwap(string stagingPath)
        {
            string? executablePath = Environment.ProcessPath;

            if (string.IsNullOrWhiteSpace(executablePath))
            {
                throw new InvalidOperationException(
                    "Could not determine the executable path for elevation.");
            }

            ProcessStartInfo startInfo =
                new ProcessStartInfo(executablePath)
                {
                    Verb = "runas",

                    UseShellExecute = true,

                    Arguments =
                        $"--lp-hostsfile \"{stagingPath}\""
                };

            try
            {
                Process.Start(startInfo);

                Log("elevated helper launched");
            }
            catch (Exception ex)
            {
                // Typical causes: UAC declined, or a packaged app
                // deployed without the allowElevation capability.
                Log($"elevation failed: {ex.Message}");

                throw;
            }
        }

        public static void Log(string message)
        {
            try
            {
                string directory =
                    Path.Combine(
                        Environment.GetFolderPath(
                            Environment.SpecialFolder.LocalApplicationData),
                        "LochlanProductivity");

                Directory.CreateDirectory(directory);

                File.AppendAllText(
                    Path.Combine(directory, "webblock.log"),
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
            }
            catch
            {
            }
        }

        // ============================================================
        // HELPER TASK MANAGEMENT
        // ============================================================

        private static string HelperScript()
        {
            // Fixed staging path: same constant the app writes to.
            string stagingDirectory =
                Path.Combine(
                    Environment.GetFolderPath(
                        Environment.SpecialFolder.UserProfile),
                    "LochlanProductivityData");

            return
                "$ErrorActionPreference='Stop'; " +
                "$dir='" + stagingDirectory + "'; " +
                "$src=Join-Path $dir 'hosts-staging.txt'; " +
                "try { " +
                "if (-not (Test-Path $src)) { throw 'no staging file' } " +
                "$c = Get-Content $src -Raw; " +
                "if (-not $c.Contains('LochlanProductivity Block')) { throw 'sanity' } " +
                "Copy-Item -LiteralPath $src -Destination \"$env:SystemRoot\\System32\\drivers\\etc\\hosts\" -Force; " +
                "'OK' | Out-File (Join-Path $dir 'swap-last-result.txt') -Encoding ascii " +
                "} catch { " +
                "$_.Exception.Message | Out-File (Join-Path $dir 'swap-last-result.txt') -Encoding ascii; " +
                "exit 1 }";
        }

        public static bool IsHelperTaskRegistered()
        {
            try
            {
                ProcessStartInfo query =
                    new ProcessStartInfo(
                        "schtasks",
                        $"/Query /TN \"{TaskName}\"")
                {
                    UseShellExecute = false,

                    CreateNoWindow = true
                };

                using Process? process =
                    Process.Start(query);

                process?.WaitForExit(5000);

                return process?.ExitCode == 0;
            }
            catch
            {
                return false;
            }
        }

        private static bool RunHelperTask()
        {
            try
            {
                ProcessStartInfo run =
                    new ProcessStartInfo(
                        "schtasks",
                        $"/Run /TN \"{TaskName}\"")
                {
                    UseShellExecute = false,

                    CreateNoWindow = true
                };

                using Process? process =
                    Process.Start(run);

                process?.WaitForExit(5000);

                bool started = process?.ExitCode == 0;

                Log(started
                    ? "helper task triggered"
                    : $"helper task trigger failed ({process?.ExitCode})");

                return started;
            }
            catch (Exception ex)
            {
                Log($"helper task trigger error: {ex.Message}");

                return false;
            }
        }

        private static bool EnsureHelperTaskRegistered()
        {
            if (IsHelperTaskRegistered())
                return true;

            if (taskRegistrationFailedThisSession == true)
                return false;

            try
            {
                string encodedScript =
                    Convert.ToBase64String(
                        Encoding.Unicode.GetBytes(
                            HelperScript()));

                string registerCommand =
                    "$ErrorActionPreference='Stop'; " +
                    "$action = New-ScheduledTaskAction " +
                    "-Execute 'powershell.exe' " +
                    $"-Argument '-NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand {encodedScript}'; " +
                    "$principal = New-ScheduledTaskPrincipal " +
                    "-UserId ([System.Security.Principal.WindowsIdentity]::GetCurrent().Name) " +
                    "-LogonType Interactive -RunLevel Highest; " +
                    "$settings = New-ScheduledTaskSettingsSet " +
                    "-AllowStartIfOnBatteries -DontStopIfGoingOnBatteries " +
                    "-ExecutionTimeLimit (New-TimeSpan -Minutes 5); " +
                    "Register-ScheduledTask " +
                    $"-TaskName '{TaskName}' " +
                    "-Action $action -Principal $principal " +
                    "-Settings $settings -Force | Out-Null; 'REGISTERED'";

                string encodedRegister =
                    Convert.ToBase64String(
                        Encoding.Unicode.GetBytes(registerCommand));

                using Process? process =
                    Process.Start(
                        new ProcessStartInfo("powershell.exe")
                        {
                            Arguments =
                                $"-NoProfile -EncodedCommand {encodedRegister}",

                            Verb = "runas",

                            UseShellExecute = true,

                            WindowStyle =
                                ProcessWindowStyle.Hidden
                        });

                process?.WaitForExit(60000);
            }
            catch (Exception ex)
            {
                Log($"task registration declined/failed: {ex.Message}");
            }

            bool registered = IsHelperTaskRegistered();

            if (!registered)
            {
                taskRegistrationFailedThisSession = true;
            }

            Log(registered
                ? "helper task registered (one UAC)"
                : "helper task NOT registered");

            return registered;
        }

        private static bool WaitForSwap(bool expectBlockSection)
        {
            // Kept short: this runs on the UI thread inside the
            // timer tick. The task normally completes in <300 ms.
            for (int attempt = 0; attempt < 15; attempt++)
            {
                try
                {
                    if (File.ReadAllText(HostsPath)
                            .Contains(BeginMarker, StringComparison.Ordinal)
                        == expectBlockSection)
                    {
                        return true;
                    }
                }
                catch
                {
                }

                Thread.Sleep(120);
            }

            return false;
        }

        // ============================================================
        // ELEVATED HELPER ENTRY POINT (--lp-hostsfile legacy fallback)
        //
        // App.OnLaunched routes --lp-hostsfile here before anything
        // else (before the single-instance mutex), so this runs even
        // while the main instance holds the mutex.
        // ============================================================

        public static void PerformStagedSwap(string stagingPath)
        {
            if (string.IsNullOrWhiteSpace(stagingPath) ||
                !File.Exists(stagingPath))
            {
                return;
            }

            // Sanity check: never clobber hosts with foreign data.
            string stagedContent =
                File.ReadAllText(stagingPath);

            if (!stagedContent.Contains(BeginMarker, StringComparison.Ordinal))
            {
                return;
            }

            File.Copy(stagingPath, HostsPath, true);

            Log("elevated swap complete");

            try
            {
                File.Delete(stagingPath);
            }
            catch
            {
            }
        }

        // ============================================================
        // DOMAIN NORMALIZATION
        // ============================================================

        public static string? NormalizeDomain(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return null;

            string domain = input.Trim().ToLowerInvariant();

            foreach (string prefix in new[] { "https://", "http://" })
            {
                if (domain.StartsWith(prefix, StringComparison.Ordinal))
                {
                    domain = domain[prefix.Length..];
                }
            }

            int slashIndex = domain.IndexOfAny(new[] { '/', '\\', '?' });

            if (slashIndex >= 0)
            {
                domain = domain[..slashIndex];
            }

            if (domain.StartsWith("www.", StringComparison.Ordinal))
            {
                domain = domain[4..];
            }

            domain = domain.Trim('.');

            if (domain.Length == 0)
                return null;

            foreach (char character in domain)
            {
                bool valid =
                    char.IsLetterOrDigit(character) ||
                    character == '.' ||
                    character == '-';

                if (!valid)
                    return null;
            }

            return domain;
        }

        private static List<string> NormalizeDomains(
            IEnumerable<string> domains)
        {
            HashSet<string> unique = new(StringComparer.OrdinalIgnoreCase);

            foreach (string domain in domains)
            {
                string? normalized = NormalizeDomain(domain);

                if (normalized != null)
                {
                    unique.Add(normalized);
                }
            }

            return unique.OrderBy(d => d, StringComparer.Ordinal).ToList();
        }
    }

    // ============================================================
    // BLOCKED SITES STORE (atomic JSON, DailyPrompt pattern)
    // ============================================================

    public class BlockedSitesManager
    {
        private readonly string saveFilePath =
            Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData),
                "LochlanProductivity",
                "blocked-sites.json");

        private readonly HostsFileBlocker hostsFileBlocker = new();

        public List<string> Domains { get; } = new();

        public BlockedSitesManager()
        {
            Load();
        }

        public bool Add(string domain)
        {
            string? normalized =
                HostsFileBlocker.NormalizeDomain(domain);

            if (normalized == null)
                return false;

            if (Domains.Any(
                existing =>
                    existing.Equals(
                        normalized,
                        StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            Domains.Add(normalized);

            Domains.Sort(StringComparer.Ordinal);

            Save();

            return true;
        }

        public bool Remove(string domain)
        {
            string? match =
                Domains.FirstOrDefault(
                    existing =>
                        existing.Equals(
                            domain,
                            StringComparison.OrdinalIgnoreCase));

            if (match == null)
                return false;

            Domains.Remove(match);

            Save();

            return true;
        }

        // Used by SyncManager: absorb the other computer's sites
        // (union semantics). Returns how many were new.
        public int Absorb(IEnumerable<string> domains)
        {
            int added = 0;

            foreach (string domain in domains)
            {
                string? normalized =
                    HostsFileBlocker.NormalizeDomain(domain);

                if (normalized == null)
                    continue;

                if (Domains.Any(
                    existing =>
                        existing.Equals(
                            normalized,
                            StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                Domains.Add(normalized);

                added++;
            }

            if (added > 0)
            {
                Domains.Sort(StringComparer.Ordinal);

                Save();
            }

            return added;
        }

        public bool ContainsAll(IEnumerable<string> domains) =>
            domains.All(d =>
                Domains.Contains(d, StringComparer.OrdinalIgnoreCase));

        private void Load()
        {
            try
            {
                if (!File.Exists(saveFilePath))
                    return;

                string json =
                    File.ReadAllText(saveFilePath);

                BlockedSitesData? loaded =
                    JsonSerializer.Deserialize<BlockedSitesData>(
                        json,
                        new JsonSerializerOptions
                        {
                            PropertyNameCaseInsensitive = true
                        });

                if (loaded?.Domains == null)
                    return;

                foreach (string domain in loaded.Domains)
                {
                    string? normalized =
                        HostsFileBlocker.NormalizeDomain(domain);

                    if (normalized != null &&
                        !Domains.Contains(
                            normalized,
                            StringComparer.OrdinalIgnoreCase))
                    {
                        Domains.Add(normalized);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"Failed to load blocked sites: {ex}");
            }
        }

        private void Save()
        {
            try
            {
                string? directory =
                    Path.GetDirectoryName(saveFilePath);

                if (directory == null)
                    return;

                Directory.CreateDirectory(directory);

                string json =
                    JsonSerializer.Serialize(
                        new BlockedSitesData
                        {
                            Domains = Domains.ToList()
                        },
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
                    $"Failed to save blocked sites: {ex}");
            }
        }
    }

    public class BlockedSitesData
    {
        public List<string> Domains { get; set; } = new();
    }
}
