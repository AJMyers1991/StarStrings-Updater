using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace StarStringsUpdater
{
    // ────────────────────────────────────────────────────────
    //  CONFIG
    // ────────────────────────────────────────────────────────
    internal class ConfigData
    {
        public List<string> Paths = new List<string>();
        public bool AutoUpdate = false;
        public string Schedule = "daily";         // "daily" | "logon"
        public string LastGameBuild = "";
        public string LastRemoteETag = "";
        public string LastGlobalIniUrl = "";
        public string LastDefaultBranch = "master";
        public string LastChecked = "";
        public string LastUpdated = "";
    }

    internal static class ConfigStore
    {
        private static readonly string Dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "StarStringsUpdater");

        private static readonly string FilePath = Path.Combine(Dir, "config.ini");

        public static ConfigData Load()
        {
            var cfg = new ConfigData();
            if (!File.Exists(FilePath))
                return cfg;

            foreach (var line in File.ReadAllLines(FilePath, Encoding.UTF8))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("#") || trimmed.Length == 0)
                    continue;

                var eq = trimmed.IndexOf('=');
                if (eq < 0)
                    continue;

                var key = trimmed.Substring(0, eq).Trim();
                var val = trimmed.Substring(eq + 1).Trim();

                switch (key.ToLowerInvariant())
                {
                    case "paths":      cfg.Paths.Add(val); break;
                    case "autoupdate": cfg.AutoUpdate = val.Equals("true", StringComparison.OrdinalIgnoreCase); break;
                    case "schedule":   cfg.Schedule = val; break;
                    case "lastgamebuild":  cfg.LastGameBuild = val; break;
                    case "lastremoteetag": cfg.LastRemoteETag = val; break;
                    case "lastglobaliniurl": cfg.LastGlobalIniUrl = val; break;
                    case "lastdefaultbranch": cfg.LastDefaultBranch = val; break;
                    case "lastchecked": cfg.LastChecked = val; break;
                    case "lastupdated": cfg.LastUpdated = val; break;
                }
            }
            return cfg;
        }

        public static void Save(ConfigData cfg)
        {
            if (!Directory.Exists(Dir))
                Directory.CreateDirectory(Dir);

            var sb = new StringBuilder();
            sb.AppendLine("# StarStrings Updater configuration");
            sb.AppendLine("# Generated " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine();

            foreach (var p in cfg.Paths)
                sb.AppendLine("paths=" + p);

            sb.AppendLine("autoUpdate=" + (cfg.AutoUpdate ? "true" : "false"));
            sb.AppendLine("schedule=" + cfg.Schedule);
            sb.AppendLine("lastGameBuild=" + cfg.LastGameBuild);
            sb.AppendLine("lastRemoteETag=" + cfg.LastRemoteETag);
            sb.AppendLine("lastGlobalIniUrl=" + cfg.LastGlobalIniUrl);
            sb.AppendLine("lastDefaultBranch=" + cfg.LastDefaultBranch);
            sb.AppendLine("lastChecked=" + cfg.LastChecked);
            sb.AppendLine("lastUpdated=" + cfg.LastUpdated);

            File.WriteAllText(FilePath, sb.ToString(), Encoding.UTF8);
        }

        public static bool Exists()
        {
            return File.Exists(FilePath);
        }

        public static string LogFile()
        {
            return Path.Combine(Dir, "update.log");
        }

        public static void WriteLog(string message)
        {
            if (!Directory.Exists(Dir))
                Directory.CreateDirectory(Dir);

            var logPath = LogFile();
            var entry = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}";
            File.AppendAllText(logPath, entry + Environment.NewLine, Encoding.UTF8);
        }

        public static string StateDir() => Dir;
    }

    // ────────────────────────────────────────────────────────
    //  UPDATE ENGINE
    // ────────────────────────────────────────────────────────
    internal class UpdateResult
    {
        public bool Success;
        public string Message;
        public string NewGameBuild;
        public string NewRemoteETag;
        public List<string> Details = new List<string>();
    }

    internal class UpdateEngine
    {
        private const string RepoOwner = "MrKraken";
        private const string RepoName = "StarStrings";
        private const string UserAgent = "StarStringsUpdater/2.0";

        private readonly ConfigData _cfg;
        private readonly StringBuilder _log;
        private readonly bool _silent;

        public UpdateEngine(ConfigData cfg, bool silent)
        {
            _cfg = cfg;
            _silent = silent;
            _log = new StringBuilder();
        }

        public string Log => _log.ToString();

        // ── GitHub API helpers ──────────────────────────────

        private string ApiGet(string url)
        {
            using (var wc = new WebClient())
            {
                wc.Headers[HttpRequestHeader.UserAgent] = UserAgent;
                return wc.DownloadString(url);
            }
        }

        private string ApiHead(string url, out string etag, out string lastModified)
        {
            etag = "";
            lastModified = "";
            try
            {
                var req = WebRequest.Create(url) as HttpWebRequest;
                req.Method = "HEAD";
                req.UserAgent = UserAgent;
                req.Timeout = 10000;
                using (var resp = req.GetResponse() as HttpWebResponse)
                {
                    etag = resp.Headers["ETag"] ?? "";
                    lastModified = resp.Headers["Last-Modified"] ?? "";
                }
                return "OK";
            }
            catch (WebException ex)
            {
                return ex.Message;
            }
        }

        // ── File discovery ──────────────────────────────────

        /// <summary>
        /// Resolve the raw URL for global.ini using a three-layer strategy:
        /// 1. Cached URL from config (if still valid)
        /// 2. GitHub Tree API discovery
        /// 3. Hardcoded fallback
        /// Returns the URL and sets cfg.LastGlobalIniUrl / cfg.LastDefaultBranch on success.
        /// </summary>
        private string ResolveGlobalIniUrl()
        {
            // Layer 1 — try cached URL
            if (!string.IsNullOrWhiteSpace(_cfg.LastGlobalIniUrl))
            {
                LogLine($"[DISCOVERY] Trying cached URL: {_cfg.LastGlobalIniUrl}");
                if (UrlRespondsOk(_cfg.LastGlobalIniUrl))
                {
                    LogLine("[DISCOVERY] Cached URL is reachable.");
                    return _cfg.LastGlobalIniUrl;
                }
                LogLine("[DISCOVERY] Cached URL returned non-200 — will re-discover.");
            }

            // Layer 2 — GitHub API discovery
            try
            {
                LogLine("[DISCOVERY] Fetching default branch from GitHub API...");
                var repoJson = ApiGet($"https://api.github.com/repos/{RepoOwner}/{RepoName}");
                var defaultBranch = "master";
                var branchMatch = Regex.Match(repoJson, "\"default_branch\"\\s*:\\s*\"([^\"]+)\"");
                if (branchMatch.Success)
                    defaultBranch = branchMatch.Groups[1].Value;

                LogLine($"[DISCOVERY] Default branch: {defaultBranch}");

                LogLine("[DISCOVERY] Fetching repository file tree...");
                var treeJson = ApiGet(
                    $"https://api.github.com/repos/{RepoOwner}/{RepoName}/git/trees/{defaultBranch}?recursive=1");

                // Find all files named global.ini
                var pathMatches = Regex.Matches(treeJson, "\"path\"\\s*:\\s*\"([^\"]+)\"");
                var candidates = new List<string>();
                for (int i = 0; i < pathMatches.Count; i++)
                {
                    var p = pathMatches[i].Groups[1].Value;
                    if (p.EndsWith("global.ini", StringComparison.OrdinalIgnoreCase))
                        candidates.Add(p);
                }

                if (candidates.Count == 0)
                {
                    LogLine("[DISCOVERY] No global.ini found in repository tree.");
                }
                else
                {
                    // Scoring: prefer paths with For_Players + Localization/english
                    string best = null;
                    int bestScore = -1;
                    foreach (var c in candidates)
                    {
                        int score = 0;
                        if (c.Contains("For_Players")) score += 10;
                        if (c.Contains("Localization")) score += 5;
                        if (c.Contains("english")) score += 5;
                        if (c.Contains("Data")) score += 2;
                        if (score > bestScore)
                        {
                            bestScore = score;
                            best = c;
                        }
                    }

                    if (best != null)
                    {
                        var url = $"https://raw.githubusercontent.com/{RepoOwner}/{RepoName}/{defaultBranch}/{best}";
                        LogLine($"[DISCOVERY] Best match: {best}");
                        LogLine($"[DISCOVERY] Resolved URL: {url}");

                        _cfg.LastGlobalIniUrl = url;
                        _cfg.LastDefaultBranch = defaultBranch;
                        return url;
                    }
                }
            }
            catch (Exception ex)
            {
                LogLine($"[DISCOVERY] GitHub API failed: {ex.Message}");
            }

            // Layer 3 — hardcoded fallback
            var fallback = $"https://raw.githubusercontent.com/{RepoOwner}/{RepoName}/refs/heads/master/src/For_Players/Data/Localization/english/global.ini";
            LogLine($"[DISCOVERY] Falling back to hardcoded URL: {fallback}");
            _cfg.LastGlobalIniUrl = fallback;
            _cfg.LastDefaultBranch = "master";
            return fallback;
        }

        private bool UrlRespondsOk(string url)
        {
            try
            {
                var req = WebRequest.Create(url) as HttpWebRequest;
                req.Method = "HEAD";
                req.UserAgent = UserAgent;
                req.Timeout = 8000;
                using (var resp = req.GetResponse() as HttpWebResponse)
                {
                    return (int)resp.StatusCode >= 200 && (int)resp.StatusCode < 300;
                }
            }
            catch
            {
                return false;
            }
        }

        // ── Update check ────────────────────────────────────

        /// <summary>
        /// Check if an update is needed. Returns true if either game build or remote file changed.
        /// </summary>
        public bool CheckNeedsUpdate(out string reason)
        {
            reason = "";

            // ── 1. Game build check ──────────────────────
            foreach (var installPath in _cfg.Paths)
            {
                var manifestPath = Path.Combine(installPath, "build_manifest.id");
                if (!File.Exists(manifestPath))
                    continue;

                try
                {
                    var manifestJson = File.ReadAllText(manifestPath, Encoding.UTF8);
                    var verMatch = Regex.Match(manifestJson, "\"Version\"\\s*:\\s*\"?([^\",}\\s]+)\"?");
                    var chgMatch = Regex.Match(manifestJson, "\"RequestedP4ChangeNum\"\\s*:\\s*\"?(\\d+)\"?");

                    var version = verMatch.Success ? verMatch.Groups[1].Value.Trim() : "";
                    var changeNum = chgMatch.Success ? chgMatch.Groups[1].Value.Trim() : "";

                    if (string.IsNullOrWhiteSpace(version) && string.IsNullOrWhiteSpace(changeNum))
                        continue;

                    var currentBuild = version + "|" + changeNum;
                    if (currentBuild != _cfg.LastGameBuild)
                    {
                        reason = $"Game build changed: {_cfg.LastGameBuild} → {currentBuild}";
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    LogLine($"[CHECK] Could not read build manifest at {installPath}: {ex.Message}");
                }
            }

            // ── 2. Remote file check ─────────────────────
            try
            {
                var url = ResolveGlobalIniUrl();
                string etag, lastModified;
                ApiHead(url, out etag, out lastModified);

                var remoteFingerprint = string.IsNullOrWhiteSpace(etag) ? (lastModified ?? "") : etag;
                if (!string.IsNullOrWhiteSpace(remoteFingerprint) &&
                    remoteFingerprint != _cfg.LastRemoteETag)
                {
                    reason = $"Remote file changed (ETag: {_cfg.LastRemoteETag} → {remoteFingerprint})";
                    return true;
                }
            }
            catch (Exception ex)
            {
                LogLine($"[CHECK] Remote check failed: {ex.Message}");
            }

            return false;
        }

        // ── Full update ─────────────────────────────────────

        public UpdateResult RunUpdate(Action<int, int, string> progressCallback = null)
        {
            var result = new UpdateResult();
            var now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

            // Discover URL
            string url;
            try
            {
                url = ResolveGlobalIniUrl();
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Message = "Could not determine download URL: " + ex.Message;
                LogLine($"[ERROR] {result.Message}");
                return result;
            }

            // Download the file once (same file for all installs)
            byte[] globalIniBytes;
            try
            {
                LogLine($"[DOWNLOAD] Fetching {url}");
                Report(progressCallback, 0, _cfg.Paths.Count, "Downloading StarStrings global.ini...");
                using (var wc = new WebClient())
                {
                    wc.Headers[HttpRequestHeader.UserAgent] = UserAgent;
                    globalIniBytes = wc.DownloadData(url);
                }

                if (globalIniBytes == null || globalIniBytes.Length == 0)
                {
                    result.Success = false;
                    result.Message = "Downloaded file is empty.";
                    LogLine($"[ERROR] {result.Message}");
                    return result;
                }

                // Sanity check — reject HTML error pages
                if (globalIniBytes.Length > 0 && globalIniBytes[0] == (byte)'<')
                {
                    result.Success = false;
                    result.Message = "Downloaded file appears to be an HTML error page, not a valid global.ini.";
                    LogLine($"[ERROR] {result.Message}");
                    return result;
                }

                LogLine($"[DOWNLOAD] Received {globalIniBytes.Length} bytes.");
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Message = "Download failed: " + ex.Message;
                LogLine($"[ERROR] {result.Message}");
                return result;
            }

            // Apply to each install path
            int idx = 0;
            int total = _cfg.Paths.Count;
            bool anySuccess = false;

            foreach (var installPath in _cfg.Paths)
            {
                idx++;
                var pathLabel = installPath;
                Report(progressCallback, idx, total, $"Updating: {pathLabel}");

                try
                {
                    // Create directory
                    var locPath = Path.Combine(installPath, "Data", "Localization", "english");
                    if (!Directory.Exists(locPath))
                    {
                        Directory.CreateDirectory(locPath);
                        LogLine($"[INSTALL] Created directory: {locPath}");
                    }

                    // Write global.ini
                    var iniPath = Path.Combine(locPath, "global.ini");
                    File.WriteAllBytes(iniPath, globalIniBytes);
                    result.Details.Add($"  ✓ {pathLabel} — global.ini updated");
                    LogLine($"[INSTALL] Wrote {iniPath} ({globalIniBytes.Length} bytes)");

                    // Handle user.cfg
                    var cfgPath = Path.Combine(installPath, "user.cfg");
                    if (!File.Exists(cfgPath))
                    {
                        File.WriteAllText(cfgPath, "g_language = english" + Environment.NewLine, Encoding.UTF8);
                        result.Details.Add($"     Created user.cfg with g_language = english");
                        LogLine($"[INSTALL] Created user.cfg at {cfgPath}");
                    }
                    else
                    {
                        var existingContent = File.ReadAllText(cfgPath);
                        if (!Regex.IsMatch(existingContent, @"^\s*g_language\s*=", RegexOptions.Multiline))
                        {
                            // Append without altering existing content
                            var sb2 = new StringBuilder(existingContent);
                            if (!existingContent.EndsWith(Environment.NewLine))
                                sb2.Append(Environment.NewLine);
                            sb2.AppendLine("g_language = english");
                            File.WriteAllText(cfgPath, sb2.ToString(), Encoding.UTF8);
                            result.Details.Add($"     Appended g_language = english to user.cfg");
                            LogLine($"[INSTALL] Appended g_language = english to {cfgPath}");
                        }
                        else
                        {
                            result.Details.Add($"     user.cfg already has g_language setting — skipped");
                            LogLine($"[INSTALL] user.cfg already has g_language at {cfgPath}");
                        }
                    }

                    anySuccess = true;
                }
                catch (UnauthorizedAccessException)
                {
                    result.Details.Add($"  ✗ {pathLabel} — Access denied. Run as Administrator.");
                    LogLine($"[ERROR] Access denied: {pathLabel}");
                }
                catch (Exception ex)
                {
                    result.Details.Add($"  ✗ {pathLabel} — {ex.Message}");
                    LogLine($"[ERROR] {pathLabel}: {ex.Message}");
                }
            }

            // Save updated fingerprints
            if (anySuccess)
            {
                // Update game build fingerprint
                foreach (var installPath in _cfg.Paths)
                {
                    var manifestPath = Path.Combine(installPath, "build_manifest.id");
                    if (File.Exists(manifestPath))
                    {
                        try
                        {
                            var manifestJson = File.ReadAllText(manifestPath, Encoding.UTF8);
                            var verMatch = Regex.Match(manifestJson, "\"Version\"\\s*:\\s*\"?([^\",}\\s]+)\"?");
                            var chgMatch = Regex.Match(manifestJson, "\"RequestedP4ChangeNum\"\\s*:\\s*\"?(\\d+)\"?");
                            var version = verMatch.Success ? verMatch.Groups[1].Value.Trim() : "";
                            var changeNum = chgMatch.Success ? chgMatch.Groups[1].Value.Trim() : "";
                            if (!string.IsNullOrWhiteSpace(version) || !string.IsNullOrWhiteSpace(changeNum))
                            {
                                _cfg.LastGameBuild = version + "|" + changeNum;
                                break;
                            }
                        }
                        catch { }
                    }
                }

                // Update remote ETag
                try
                {
                    string etag, lastModified;
                    ApiHead(url, out etag, out lastModified);
                    _cfg.LastRemoteETag = string.IsNullOrWhiteSpace(etag) ? (lastModified ?? "") : etag;
                }
                catch { }

                _cfg.LastUpdated = now;
                ConfigStore.Save(_cfg);
            }

            _cfg.LastChecked = now;

            result.Success = anySuccess;
            result.Message = anySuccess
                ? $"Successfully updated {idx} installation(s)."
                : "No installations were updated successfully.";

            result.NewGameBuild = _cfg.LastGameBuild;
            result.NewRemoteETag = _cfg.LastRemoteETag;

            LogLine($"[RESULT] {result.Message}");
            return result;
        }

        private void Report(Action<int, int, string> callback, int current, int total, string status)
        {
            callback?.Invoke(current, total, status);
        }

        private void LogLine(string msg)
        {
            _log.AppendLine(msg);
            ConfigStore.WriteLog(msg);
        }
    }

    // ────────────────────────────────────────────────────────
    //  SCHEDULED TASK MANAGER
    // ────────────────────────────────────────────────────────
    internal static class ScheduledTaskManager
    {
        private const string TaskName = "StarStrings Updater";
        private const string TaskDescription = "Automatically updates StarStrings localization files when a new Star Citizen build is detected or the remote file changes.";

        public static bool TaskExists()
        {
            return RunPowerShellSilent(
                $"$t = Get-ScheduledTask -TaskName '{TaskName}' -ErrorAction SilentlyContinue; if ($t) {{ Write-Output 'EXISTS' }}")
                .Trim() == "EXISTS";
        }

        public static bool CreateTask(string exePath, string schedule)
        {
            try
            {
                string triggerScript;
                if (schedule == "logon")
                {
                    triggerScript = "$trigger = New-ScheduledTaskTrigger -AtLogOn";
                }
                else
                {
                    // Parse schedule like "09:00"
                    var time = schedule;
                    if (!time.Contains(":")) time = "09:00";
                    triggerScript = $"$trigger = New-ScheduledTaskTrigger -Daily -At {time}";
                }

                var psCommand = $@"
$action = New-ScheduledTaskAction -Execute '{exePath.Replace("'", "''")}' -Argument '--silent'
{triggerScript}
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable -RunOnlyIfNetworkAvailable -ExecutionTimeLimit (New-TimeSpan -Hours 1)
$principal = New-ScheduledTaskPrincipal -UserID ""$env:USERDOMAIN\$env:USERNAME"" -RunLevel Highest -LogonType Interactive
Register-ScheduledTask -TaskName '{TaskName}' -Description '{TaskDescription}' -Action $action -Trigger $trigger -Settings $settings -Principal $principal -Force
Write-Output 'OK'
";

                var output = RunPowerShell(psCommand);
                ConfigStore.WriteLog($"[SCHEDULER] Create task result: {output.Trim()}");
                return output.Contains("OK");
            }
            catch (Exception ex)
            {
                ConfigStore.WriteLog($"[SCHEDULER] Create task failed: {ex.Message}");
                return false;
            }
        }

        public static bool RemoveTask()
        {
            try
            {
                var output = RunPowerShellSilent(
                    $"Unregister-ScheduledTask -TaskName '{TaskName}' -Confirm:$false -ErrorAction Stop; Write-Output 'REMOVED'");
                ConfigStore.WriteLog($"[SCHEDULER] Remove task result: {output.Trim()}");
                return output.Contains("REMOVED");
            }
            catch (Exception ex)
            {
                ConfigStore.WriteLog($"[SCHEDULER] Remove task failed: {ex.Message}");
                return false;
            }
        }

        public static string GetTaskSchedule()
        {
            try
            {
                var psCommand = $@"
$t = Get-ScheduledTask -TaskName '{TaskName}' -ErrorAction SilentlyContinue
if (-not $t) {{ Write-Output 'NONE' }}
else {{
    $triggers = $t.Triggers
    if ($triggers.Count -gt 0) {{
        $trig = $triggers[0]
        if ($trig.CimClass.CimClassName -eq 'MSFT_TaskDailyTrigger') {{
            $dt = [datetime]$trig.StartBoundary
            Write-Output ( 'DAILY|' + $dt.ToString('HH:mm') )
        }} elseif ($trig.CimClass.CimClassName -eq 'MSFT_TaskLogonTrigger') {{
            Write-Output 'LOGON'
        }} else {{
            Write-Output 'UNKNOWN'
        }}
    }} else {{
        Write-Output 'NOTRIGGERS'
    }}
}}
";
                return RunPowerShellSilent(psCommand).Trim();
            }
            catch
            {
                return "NONE";
            }
        }

        private static string RunPowerShell(string command)
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{command}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using (var p = Process.Start(psi))
            {
                var stdout = p.StandardOutput.ReadToEnd();
                p.WaitForExit(30000);
                if (!p.HasExited)
                {
                    p.Kill();
                    return "TIMEOUT";
                }
                return stdout;
            }
        }

        private static string RunPowerShellSilent(string command)
        {
            return RunPowerShell(command);
        }
    }

    // ────────────────────────────────────────────────────────
    //  PATH ROW (helper for SetupForm)
    // ────────────────────────────────────────────────────────
    internal class PathRow
    {
        public TextBox TextBox;
        public Button BrowseBtn;
        public Button RemoveBtn;
        public int Index;
    }

    // ────────────────────────────────────────────────────────
    //  SETUP FORM
    // ────────────────────────────────────────────────────────
    internal class SetupForm : Form
    {
        private readonly ConfigData _cfg;
        private readonly List<PathRow> _rows = new List<PathRow>();
        private Panel _pathPanel;
        private Button _addBtn;
        private CheckBox _autoUpdateChk;
        private ComboBox _scheduleCmb;
        private Label _scheduleLbl;
        private Button _saveBtn;
        private Button _cancelBtn;

        private const int RowHeight = 30;
        private const int RowPadding = 6;
        private const int TextBoxWidth = 340;
        private const int BrowseWidth = 80;
        private const int RemoveWidth = 30;

        public SetupForm(ConfigData cfg)
        {
            _cfg = cfg;
            InitializeComponent();

            // Pre-populate existing paths
            if (_cfg.Paths.Count > 0)
            {
                foreach (var p in _cfg.Paths)
                    AddRow(p);
            }
            else
            {
                AddRow(""); // default one empty row
            }

            // Restore automation settings
            _autoUpdateChk.Checked = _cfg.AutoUpdate;
            _scheduleCmb.Enabled = _cfg.AutoUpdate;
            _scheduleLbl.Enabled = _cfg.AutoUpdate;
            if (!string.IsNullOrWhiteSpace(_cfg.Schedule) && _cfg.Schedule != "logon")
                _scheduleCmb.SelectedItem = _cfg.Schedule;
            else if (_cfg.Schedule == "logon" && _scheduleCmb.Items.Count > 3)
                _scheduleCmb.SelectedIndex = 3;

            LayoutRows();
            UpdateRemoveButtons();
        }

        private void InitializeComponent()
        {
            this.Text = "StarStrings Updater — Setup";
            this.StartPosition = FormStartPosition.CenterScreen;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.ClientSize = new Size(550, 460);
            this.Font = new Font("Segoe UI", 9f);

            var descLabel = new Label
            {
                Text = "Select your Star Citizen installation folder(s).\nClick Browse to locate StarCitizen_Launcher.exe in each game folder.",
                Location = new Point(14, 10),
                Size = new Size(520, 36),
                AutoSize = false
            };

            var pathGroup = new GroupBox
            {
                Text = "Installation Paths",
                Location = new Point(14, 55),
                Size = new Size(520, 240)
            };

            _pathPanel = new Panel
            {
                Location = new Point(10, 22),
                Size = new Size(500, 178),
                AutoScroll = true,
                BorderStyle = BorderStyle.None
            };

            _addBtn = new Button
            {
                Text = "+  Add Installation",
                Location = new Point(14, 200),
                Size = new Size(520, 28),
                FlatStyle = FlatStyle.System
            };
            _addBtn.Click += (s, e) =>
            {
                AddRow("");
                LayoutRows();
                UpdateRemoveButtons();
            };

            pathGroup.Controls.Add(_pathPanel);
            pathGroup.Controls.Add(_addBtn);

            // Automation group
            var autoGroup = new GroupBox
            {
                Text = "Automation",
                Location = new Point(14, 310),
                Size = new Size(520, 70)
            };

            _autoUpdateChk = new CheckBox
            {
                Text = "Enable automatic updates via Windows Task Scheduler",
                Location = new Point(10, 20),
                Size = new Size(500, 22),
                AutoSize = false
            };
            _autoUpdateChk.CheckedChanged += (s, e) =>
            {
                _scheduleCmb.Enabled = _autoUpdateChk.Checked;
                _scheduleLbl.Enabled = _autoUpdateChk.Checked;
            };

            _scheduleLbl = new Label
            {
                Text = "Schedule:",
                Location = new Point(30, 48),
                Size = new Size(60, 20),
                Enabled = false,
                TextAlign = ContentAlignment.MiddleLeft
            };

            _scheduleCmb = new ComboBox
            {
                Location = new Point(92, 45),
                Size = new Size(160, 22),
                DropDownStyle = ComboBoxStyle.DropDownList,
                Enabled = false
            };
            _scheduleCmb.Items.AddRange(new object[] { "09:00", "12:00", "18:00", "At user logon" });
            _scheduleCmb.SelectedIndex = 0;

            autoGroup.Controls.Add(_autoUpdateChk);
            autoGroup.Controls.Add(_scheduleLbl);
            autoGroup.Controls.Add(_scheduleCmb);

            // Buttons
            _saveBtn = new Button
            {
                Text = "Save && Update Now",
                Location = new Point(280, 400),
                Size = new Size(130, 28),
                FlatStyle = FlatStyle.System
            };
            _saveBtn.Click += SaveBtn_Click;

            _cancelBtn = new Button
            {
                Text = "Cancel",
                Location = new Point(418, 400),
                Size = new Size(80, 28),
                FlatStyle = FlatStyle.System,
                DialogResult = DialogResult.Cancel
            };

            this.Controls.Add(descLabel);
            this.Controls.Add(pathGroup);
            this.Controls.Add(autoGroup);
            this.Controls.Add(_saveBtn);
            this.Controls.Add(_cancelBtn);
            this.AcceptButton = _saveBtn;
            this.CancelButton = _cancelBtn;
        }

        private void AddRow(string path)
        {
            var row = new PathRow
            {
                Index = _rows.Count,
                TextBox = new TextBox
                {
                    Text = path,
                    ReadOnly = true,
                    BackColor = SystemColors.Window,
                    Width = TextBoxWidth,
                    Height = 22
                },
                BrowseBtn = new Button
                {
                    Text = "Browse...",
                    Width = BrowseWidth,
                    Height = 22,
                    FlatStyle = FlatStyle.System
                },
                RemoveBtn = new Button
                {
                    Text = "\u2715",
                    Width = RemoveWidth,
                    Height = 22,
                    FlatStyle = FlatStyle.System,
                    ForeColor = Color.DarkRed
                }
            };

            row.BrowseBtn.Tag = row;
            row.BrowseBtn.Click += BrowseBtn_Click;
            row.RemoveBtn.Tag = row;
            row.RemoveBtn.Click += RemoveBtn_Click;

            _rows.Add(row);
            _pathPanel.Controls.Add(row.TextBox);
            _pathPanel.Controls.Add(row.BrowseBtn);
            _pathPanel.Controls.Add(row.RemoveBtn);
        }

        private void RemoveRow(int index)
        {
            if (index < 0 || index >= _rows.Count) return;

            var row = _rows[index];
            _pathPanel.Controls.Remove(row.TextBox);
            _pathPanel.Controls.Remove(row.BrowseBtn);
            _pathPanel.Controls.Remove(row.RemoveBtn);
            _rows.RemoveAt(index);

            // Re-index remaining rows
            for (int i = 0; i < _rows.Count; i++)
            {
                _rows[i].Index = i;
                _rows[i].BrowseBtn.Tag = _rows[i];
                _rows[i].RemoveBtn.Tag = _rows[i];
            }

            LayoutRows();
            UpdateRemoveButtons();
        }

        private void BrowseBtn_Click(object sender, EventArgs e)
        {
            var row = ((Button)sender).Tag as PathRow;
            if (row != null)
                BrowsePath(row.Index);
        }

        private void RemoveBtn_Click(object sender, EventArgs e)
        {
            var row = ((Button)sender).Tag as PathRow;
            if (row == null) return;

            if (_rows.Count <= 1)
            {
                MessageBox.Show(this, "You must have at least one installation path.",
                    "StarStrings Updater", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            RemoveRow(row.Index);
        }

        private void BrowsePath(int index)
        {
            if (index < 0 || index >= _rows.Count) return;

            using (var dlg = new OpenFileDialog())
            {
                dlg.Title = "Select StarCitizen_Launcher.exe";
                dlg.Filter = "Star Citizen Launcher|StarCitizen_Launcher.exe|All Files|*.*";
                dlg.FilterIndex = 0;
                dlg.RestoreDirectory = true;

                // If textbox already has a path, start browsing there
                var existingText = _rows[index].TextBox.Text.Trim();
                if (!string.IsNullOrWhiteSpace(existingText) && Directory.Exists(existingText))
                {
                    dlg.InitialDirectory = existingText;
                }

                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    var dir = Path.GetDirectoryName(dlg.FileName);
                    _rows[index].TextBox.Text = dir;
                }
            }
        }

        private void LayoutRows()
        {
            for (int i = 0; i < _rows.Count; i++)
            {
                var y = i * (RowHeight + RowPadding) + 4;
                _rows[i].TextBox.Location = new Point(2, y);
                _rows[i].BrowseBtn.Location = new Point(TextBoxWidth + 8, y);
                _rows[i].RemoveBtn.Location = new Point(TextBoxWidth + BrowseWidth + 14, y);
            }
        }

        private void UpdateRemoveButtons()
        {
            // Hide remove button when only one row
            foreach (var row in _rows)
                row.RemoveBtn.Visible = _rows.Count > 1;
        }

        private void SaveBtn_Click(object sender, EventArgs e)
        {
            // Collect and validate paths
            var paths = new List<string>();
            foreach (var row in _rows)
            {
                var path = row.TextBox.Text.Trim();
                if (string.IsNullOrWhiteSpace(path))
                {
                    MessageBox.Show(this, "All installation paths must be filled in.\nUse the ✕ button to remove unused rows, or Browse to select a folder.",
                        "Validation Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                if (!Directory.Exists(path))
                {
                    var result = MessageBox.Show(this,
                        $"The folder does not exist:\n\n{path}\n\nDo you want to save it anyway? You can fix it later in Settings.",
                        "Folder Not Found", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                    if (result == DialogResult.No)
                        return;
                }

                // Normalize
                path = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                paths.Add(path);
            }

            if (paths.Count == 0)
            {
                MessageBox.Show(this, "Please add at least one installation path.",
                    "No Paths", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // Save config
            _cfg.Paths = paths;
            _cfg.AutoUpdate = _autoUpdateChk.Checked;
            if (_autoUpdateChk.Checked)
            {
                var sel = _scheduleCmb.SelectedItem?.ToString() ?? "09:00";
                _cfg.Schedule = sel == "At user logon" ? "logon" : sel;
            }
            else
            {
                _cfg.Schedule = "";
            }
            ConfigStore.Save(_cfg);

            this.DialogResult = DialogResult.OK;
            this.Close();
        }
    }

    // ────────────────────────────────────────────────────────
    //  UPDATE PROGRESS FORM
    // ────────────────────────────────────────────────────────
    internal class UpdateProgressForm : Form
    {
        private readonly ConfigData _cfg;
        private readonly bool _silent;
        private ProgressBar _progressBar;
        private Label _statusLabel;
        private Label _detailLabel;
        private Button _closeBtn;
        private TextBox _logBox;

        public bool UpdateSucceeded { get; private set; }

        public UpdateProgressForm(ConfigData cfg, bool silent = false)
        {
            _cfg = cfg;
            _silent = silent;
            InitializeComponent();
            this.Shown += (s, e) => BeginInvoke(new Action(RunUpdateInBackground));
        }

        private void InitializeComponent()
        {
            this.Text = "StarStrings Updater — Updating...";
            this.StartPosition = FormStartPosition.CenterScreen;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.ClientSize = new Size(480, 260);
            this.Font = new Font("Segoe UI", 9f);

            _progressBar = new ProgressBar
            {
                Style = ProgressBarStyle.Marquee,
                Location = new Point(14, 14),
                Size = new Size(450, 22),
                MarqueeAnimationSpeed = 30
            };

            _statusLabel = new Label
            {
                Text = "Preparing...",
                Location = new Point(14, 44),
                Size = new Size(450, 20),
                AutoSize = false
            };

            _detailLabel = new Label
            {
                Text = "",
                Location = new Point(14, 66),
                Size = new Size(450, 16),
                AutoSize = false,
                ForeColor = Color.Gray
            };

            _logBox = new TextBox
            {
                Location = new Point(14, 90),
                Size = new Size(450, 120),
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                BackColor = SystemColors.ControlLightLight,
                Font = new Font("Consolas", 8.25f)
            };

            _closeBtn = new Button
            {
                Text = "Close",
                Location = new Point(380, 220),
                Size = new Size(84, 28),
                FlatStyle = FlatStyle.System,
                Enabled = false,
                DialogResult = DialogResult.OK
            };
            _closeBtn.Click += (s, e) => this.Close();

            this.Controls.Add(_progressBar);
            this.Controls.Add(_statusLabel);
            this.Controls.Add(_detailLabel);
            this.Controls.Add(_logBox);
            this.Controls.Add(_closeBtn);
        }

        private void RunUpdateInBackground()
        {
            // Run the synchronous update on a background thread so the UI stays responsive.
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                var engine = new UpdateEngine(_cfg, _silent);
                var result = engine.RunUpdate((current, total, status) =>
                {
                    this.BeginInvoke(new Action(() =>
                    {
                        if (_progressBar.Style == ProgressBarStyle.Marquee)
                            _progressBar.Style = ProgressBarStyle.Blocks;

                        _progressBar.Maximum = total;
                        _progressBar.Value = Math.Min(current, total);
                        _statusLabel.Text = status;
                    }));
                });

                this.BeginInvoke(new Action(() =>
                {
                    _progressBar.Style = ProgressBarStyle.Blocks;
                    _progressBar.Value = _progressBar.Maximum;
                    _statusLabel.Text = result.Message;
                    _logBox.Text = engine.Log;

                    if (result.Details.Count > 0)
                        _logBox.AppendText(Environment.NewLine + string.Join(Environment.NewLine, result.Details));

                    _closeBtn.Enabled = true;
                    this.Text = result.Success
                        ? "StarStrings Updater — Complete"
                        : "StarStrings Updater — Errors Occurred";

                    UpdateSucceeded = result.Success;
                }));
            });
        }
    }

    // ────────────────────────────────────────────────────────
    //  MAIN FORM (Dashboard)
    // ────────────────────────────────────────────────────────
    internal class MainForm : Form
    {
        private readonly ConfigData _cfg;

        private Label _lastCheckedLbl;
        private Label _lastUpdatedLbl;
        private Label _installCountLbl;
        private Label _statusLbl;

        private Button _updateNowBtn;
        private Button _configureBtn;

        private CheckBox _autoUpdateChk;
        private Label _scheduleDisplayLbl;
        private Button _removeTaskBtn;

        private GroupBox _statusGroup;
        private GroupBox _autoGroup;

        public MainForm(ConfigData cfg)
        {
            _cfg = cfg;
            InitializeComponent();
            RefreshStatus();
        }

        private void InitializeComponent()
        {
            this.Text = "StarStrings Updater";
            this.StartPosition = FormStartPosition.CenterScreen;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.ClientSize = new Size(460, 370);
            this.Font = new Font("Segoe UI", 9f);

            // Status group
            _statusGroup = new GroupBox
            {
                Text = "Status",
                Location = new Point(14, 10),
                Size = new Size(430, 140)
            };

            var y = 22;
            _lastCheckedLbl = CreateInfoLabel("Last checked:  —", new Point(14, y)); y += 22;
            _lastUpdatedLbl = CreateInfoLabel("Last updated:  —", new Point(14, y)); y += 22;
            _installCountLbl = CreateInfoLabel("Installations:  0", new Point(14, y)); y += 22;
            _statusLbl = new Label
            {
                Text = "Status:  Ready",
                Location = new Point(14, y),
                Size = new Size(400, 22),
                AutoSize = false,
                Font = new Font("Segoe UI", 9f, FontStyle.Bold)
            };

            _statusGroup.Controls.Add(_lastCheckedLbl);
            _statusGroup.Controls.Add(_lastUpdatedLbl);
            _statusGroup.Controls.Add(_installCountLbl);
            _statusGroup.Controls.Add(_statusLbl);

            // Buttons
            _updateNowBtn = new Button
            {
                Text = "Update Now",
                Location = new Point(14, 162),
                Size = new Size(120, 30),
                FlatStyle = FlatStyle.System
            };
            _updateNowBtn.Click += UpdateNowBtn_Click;

            _configureBtn = new Button
            {
                Text = "Configure Paths...",
                Location = new Point(144, 162),
                Size = new Size(120, 30),
                FlatStyle = FlatStyle.System
            };
            _configureBtn.Click += ConfigureBtn_Click;

            // Automation group
            _autoGroup = new GroupBox
            {
                Text = "Automation",
                Location = new Point(14, 205),
                Size = new Size(430, 110)
            };

            _autoUpdateChk = new CheckBox
            {
                Text = "Enable automatic updates via Task Scheduler",
                Location = new Point(10, 22),
                Size = new Size(400, 22),
                AutoSize = false
            };
            _autoUpdateChk.CheckedChanged += AutoUpdateChk_CheckedChanged;

            _scheduleDisplayLbl = new Label
            {
                Text = "Schedule:  —",
                Location = new Point(30, 50),
                Size = new Size(380, 20),
                AutoSize = false,
                ForeColor = Color.DimGray
            };

            _removeTaskBtn = new Button
            {
                Text = "Remove Scheduled Task",
                Location = new Point(30, 74),
                Size = new Size(160, 26),
                FlatStyle = FlatStyle.System,
                Visible = false
            };
            _removeTaskBtn.Click += RemoveTaskBtn_Click;

            _autoGroup.Controls.Add(_autoUpdateChk);
            _autoGroup.Controls.Add(_scheduleDisplayLbl);
            _autoGroup.Controls.Add(_removeTaskBtn);

            // Close button
            var closeBtn = new Button
            {
                Text = "Close",
                Location = new Point(360, 328),
                Size = new Size(84, 28),
                FlatStyle = FlatStyle.System
            };
            closeBtn.Click += (s, e) => this.Close();

            this.Controls.Add(_statusGroup);
            this.Controls.Add(_updateNowBtn);
            this.Controls.Add(_configureBtn);
            this.Controls.Add(_autoGroup);
            this.Controls.Add(closeBtn);

            this.CancelButton = closeBtn;
            this.Load += MainForm_Load;
        }

        private void MainForm_Load(object sender, EventArgs e)
        {
            RefreshAutomationState();
        }

        private static Label CreateInfoLabel(string text, Point location)
        {
            return new Label
            {
                Text = text,
                Location = location,
                Size = new Size(400, 22),
                AutoSize = false
            };
        }

        private void RefreshStatus()
        {
            _lastCheckedLbl.Text = $"Last checked:  {(_cfg.LastChecked.Length > 0 ? _cfg.LastChecked : "—")}";
            _lastUpdatedLbl.Text = $"Last updated:  {(_cfg.LastUpdated.Length > 0 ? _cfg.LastUpdated : "—")}";
            _installCountLbl.Text = $"Installations:  {_cfg.Paths.Count}";
            _statusLbl.Text = "Status:  Ready";
        }

        private void RefreshAutomationState()
        {
            var taskExists = ScheduledTaskManager.TaskExists();

            // Auto-create task if config says it should exist but it doesn't
            if (_cfg.AutoUpdate && !taskExists && !string.IsNullOrWhiteSpace(_cfg.Schedule))
            {
                var exePath = Application.ExecutablePath;
                if (ScheduledTaskManager.CreateTask(exePath, _cfg.Schedule))
                {
                    ConfigStore.WriteLog("[AUTO] Re-created scheduled task from saved config.");
                    taskExists = true;
                }
            }

            _autoUpdateChk.Checked = taskExists;

            if (taskExists)
            {
                var sched = ScheduledTaskManager.GetTaskSchedule();
                if (sched.StartsWith("DAILY|"))
                {
                    var time = sched.Substring(6);
                    _scheduleDisplayLbl.Text = $"Schedule:  Daily at {time}";
                }
                else if (sched == "LOGON")
                {
                    _scheduleDisplayLbl.Text = "Schedule:  At user logon";
                }
                else
                {
                    _scheduleDisplayLbl.Text = $"Schedule:  {sched}";
                }
                _scheduleDisplayLbl.Visible = true;
                _removeTaskBtn.Visible = true;
            }
            else
            {
                _scheduleDisplayLbl.Text = "Schedule:  —";
                _scheduleDisplayLbl.Visible = true;
                _removeTaskBtn.Visible = false;
            }
        }

        private void AutoUpdateChk_CheckedChanged(object sender, EventArgs e)
        {
            if (_autoUpdateChk.Checked)
            {
                // Show a mini-dialog to pick schedule, then create the task
                using (var dlg = new SchedulePickerForm())
                {
                    if (dlg.ShowDialog(this) == DialogResult.OK)
                    {
                        var schedule = dlg.SelectedSchedule;
                        var exePath = Application.ExecutablePath;
                        if (ScheduledTaskManager.CreateTask(exePath, schedule))
                        {
                            _cfg.AutoUpdate = true;
                            _cfg.Schedule = schedule;
                            ConfigStore.Save(_cfg);
                            RefreshAutomationState();
                            _statusLbl.Text = "Status:  Automatic updates enabled.";
                        }
                        else
                        {
                            _autoUpdateChk.Checked = false;
                            MessageBox.Show(this,
                                "Failed to create the scheduled task.\n\nMake sure you are running as Administrator and try again.",
                                "Task Creation Failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        }
                    }
                    else
                    {
                        _autoUpdateChk.Checked = false;
                    }
                }
            }
            else
            {
                if (ScheduledTaskManager.TaskExists())
                {
                    if (ScheduledTaskManager.RemoveTask())
                    {
                        _cfg.AutoUpdate = false;
                        _cfg.Schedule = "";
                        ConfigStore.Save(_cfg);
                        RefreshAutomationState();
                        _statusLbl.Text = "Status:  Automatic updates disabled.";
                    }
                    else
                    {
                        _autoUpdateChk.Checked = true;
                        MessageBox.Show(this, "Failed to remove the scheduled task.", "Error",
                            MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
            }
        }

        private void RemoveTaskBtn_Click(object sender, EventArgs e)
        {
            if (ScheduledTaskManager.TaskExists())
            {
                var confirm = MessageBox.Show(this,
                    "Remove the automatic update scheduled task?\n\nYou can re-enable it at any time.",
                    "Remove Scheduled Task", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (confirm == DialogResult.Yes)
                {
                    if (ScheduledTaskManager.RemoveTask())
                    {
                        _cfg.AutoUpdate = false;
                        _cfg.Schedule = "";
                        ConfigStore.Save(_cfg);
                        RefreshAutomationState();
                        _statusLbl.Text = "Status:  Scheduled task removed.";
                    }
                    else
                    {
                        MessageBox.Show(this, "Failed to remove the scheduled task.", "Error",
                            MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
            }
        }

        private void UpdateNowBtn_Click(object sender, EventArgs e)
        {
            using (var progress = new UpdateProgressForm(_cfg, false))
            {
                progress.ShowDialog(this);
                if (progress.UpdateSucceeded)
                {
                    // Reload config (it was updated by the engine)
                    var freshCfg = ConfigStore.Load();
                    _cfg.Paths = freshCfg.Paths;
                    _cfg.LastGameBuild = freshCfg.LastGameBuild;
                    _cfg.LastRemoteETag = freshCfg.LastRemoteETag;
                    _cfg.LastChecked = freshCfg.LastChecked;
                    _cfg.LastUpdated = freshCfg.LastUpdated;
                    _statusLbl.Text = "Status:  Up to date.";
                }
                else
                {
                    _statusLbl.Text = "Status:  Update completed with errors.";
                }
                RefreshStatus();
            }
        }

        private void ConfigureBtn_Click(object sender, EventArgs e)
        {
            using (var setup = new SetupForm(_cfg))
            {
                if (setup.ShowDialog(this) == DialogResult.OK)
                {
                    _statusLbl.Text = "Status:  Configuration saved.";
                    RefreshStatus();

                    // Update automation state if changed
                    RefreshAutomationState();
                }
            }
        }
    }

    // ────────────────────────────────────────────────────────
    //  SCHEDULE PICKER (mini dialog)
    // ────────────────────────────────────────────────────────
    internal class SchedulePickerForm : Form
    {
        public string SelectedSchedule { get; private set; } = "09:00";

        public SchedulePickerForm()
        {
            this.Text = "Schedule Automatic Updates";
            this.StartPosition = FormStartPosition.CenterParent;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.ClientSize = new Size(320, 160);
            this.Font = new Font("Segoe UI", 9f);

            var desc = new Label
            {
                Text = "Choose when to check for and apply updates:",
                Location = new Point(14, 12),
                Size = new Size(290, 32),
                AutoSize = false
            };

            var lbl = new Label
            {
                Text = "Schedule:",
                Location = new Point(14, 50),
                Size = new Size(60, 24),
                TextAlign = ContentAlignment.MiddleLeft
            };

            var cmb = new ComboBox
            {
                Location = new Point(80, 50),
                Size = new Size(180, 22),
                DropDownStyle = ComboBoxStyle.DropDownList
            };
            cmb.Items.AddRange(new object[] { "09:00", "12:00", "18:00", "At user logon" });
            cmb.SelectedIndex = 0;

            var infoLbl = new Label
            {
                Text = "If a scheduled time is missed, the task will run\nas soon as possible afterward.",
                Location = new Point(14, 82),
                Size = new Size(290, 30),
                AutoSize = false,
                ForeColor = Color.DimGray
            };

            var okBtn = new Button
            {
                Text = "OK",
                Location = new Point(140, 120),
                Size = new Size(80, 28),
                FlatStyle = FlatStyle.System,
                DialogResult = DialogResult.OK
            };
            okBtn.Click += (s, e) =>
            {
                var sel = cmb.SelectedItem?.ToString() ?? "09:00";
                SelectedSchedule = sel == "At user logon" ? "logon" : sel;
            };

            var cancelBtn = new Button
            {
                Text = "Cancel",
                Location = new Point(226, 120),
                Size = new Size(80, 28),
                FlatStyle = FlatStyle.System,
                DialogResult = DialogResult.Cancel
            };

            this.Controls.Add(desc);
            this.Controls.Add(lbl);
            this.Controls.Add(cmb);
            this.Controls.Add(infoLbl);
            this.Controls.Add(okBtn);
            this.Controls.Add(cancelBtn);
            this.AcceptButton = okBtn;
            this.CancelButton = cancelBtn;
        }
    }

    // ────────────────────────────────────────────────────────
    //  PROGRAM (Entry Point)
    // ────────────────────────────────────────────────────────
    internal static class Program
    {
        private const string MutexName = @"Local\StarStringsUpdater_SingleInstance";

        [STAThread]
        static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // Parse arguments
            bool silent = false;
            foreach (var arg in args)
            {
                if (arg.Equals("--silent", StringComparison.OrdinalIgnoreCase) ||
                    arg.Equals("-s", StringComparison.OrdinalIgnoreCase))
                    silent = true;
            }

            // ── Single instance guard ──────────────────────
            bool createdNew;
            using (var mutex = new Mutex(true, MutexName, out createdNew))
            {
                if (!createdNew)
                {
                    if (!silent)
                    {
                        MessageBox.Show("StarStrings Updater is already running.",
                            "StarStrings Updater", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    ConfigStore.WriteLog("[GUARD] Another instance is already running. Exiting.");
                    return;
                }

                // ── Elevation check ─────────────────────────
                if (!IsAdministrator())
                {
                    if (silent)
                    {
                        // Scheduled task should have been created with highest privileges.
                        // If we got here without admin, the task was misconfigured.
                        ConfigStore.WriteLog("[ELEVATION] Silent run without administrator privileges — aborting.");
                        return;
                    }

                    // Relaunch as admin
                    try
                    {
                        var psi = new ProcessStartInfo
                        {
                            FileName = Application.ExecutablePath,
                            Arguments = string.Join(" ", args),
                            Verb = "runas",
                            UseShellExecute = true
                        };
                        Process.Start(psi);
                    }
                    catch (System.ComponentModel.Win32Exception)
                    {
                        // User declined UAC
                        MessageBox.Show(
                            "Administrator privileges are required to update files in protected folders.\n\n" +
                            "Please right-click the program and select \"Run as administrator\".",
                            "StarStrings Updater — Elevation Required",
                            MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                    return;
                }

                // ── Silent mode ─────────────────────────────
                if (silent)
                {
                    RunSilent();
                    return;
                }

                // ── Interactive mode ────────────────────────
                RunInteractive();
            }
        }

        private static bool IsAdministrator()
        {
            using (var identity = WindowsIdentity.GetCurrent())
            {
                var principal = new WindowsPrincipal(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
        }

        private static void RunSilent()
        {
            ConfigStore.WriteLog("=== StarStrings Updater (silent) ===");

            var cfg = ConfigStore.Load();
            if (cfg.Paths.Count == 0)
            {
                ConfigStore.WriteLog("[SILENT] No installation paths configured. Exiting.");
                return;
            }

            var engine = new UpdateEngine(cfg, true);

            string reason;
            if (engine.CheckNeedsUpdate(out reason))
            {
                ConfigStore.WriteLog($"[SILENT] Update needed: {reason}");
                var result = engine.RunUpdate();
                ConfigStore.WriteLog($"[SILENT] Result: {result.Message}");
                ConfigStore.Save(cfg);
            }
            else
            {
                ConfigStore.WriteLog("[SILENT] No update needed.");
                cfg.LastChecked = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                ConfigStore.Save(cfg);
            }
        }

        private static void RunInteractive()
        {
            ConfigData cfg;

            if (!ConfigStore.Exists())
            {
                // First run — show setup wizard
                cfg = new ConfigData();
                using (var setup = new SetupForm(cfg))
                {
                    if (setup.ShowDialog() != DialogResult.OK)
                        return; // user cancelled
                }

                // Reload after setup saved
                cfg = ConfigStore.Load();

                // Run initial update
                using (var progress = new UpdateProgressForm(cfg, false))
                {
                    progress.ShowDialog();
                }

                // Reload after update
                cfg = ConfigStore.Load();
            }
            else
            {
                cfg = ConfigStore.Load();
            }

            // Show main dashboard
            using (var main = new MainForm(cfg))
            {
                Application.Run(main);
            }
        }
    }
}