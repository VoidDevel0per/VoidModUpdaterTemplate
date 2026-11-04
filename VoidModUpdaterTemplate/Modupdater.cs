using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using BepInEx;
using BepInEx.Bootstrap;
using Newtonsoft.Json;
using UnityEngine;

namespace VoidModUpdaterTemplate
{
    public sealed class ModUpdater : MonoBehaviour
    {
        private static readonly object Sync = new object();

        private UpdaterConfig _cfg;
        private UpdaterStrings S => _cfg.Strings;
        private string? _modRoot;
        private string? _gameRoot;
        private ModManifest _manifest;
        private string _status;
        private string _error;
        private bool _isChecking, _isDownloading;
        private bool _updateDetected, _downloadCompleted;
        private bool _showUI, _showConsentUI, _showDepUI;
        private bool _validationDone;
        private List<FileValidationResult> _validationResults;
        private List<DependencyResult> _dependencyResults;
        private readonly List<PendingReplacement> _pendingConsent = [];
        private readonly ConcurrentQueue<Action> _mainQueue = new ConcurrentQueue<Action>();
        private Rect _popupRect, _depRect;
        private Vector2 _scroll, _validationScroll, _depScroll;

        private sealed class PendingReplacement
        {
            public string TempPath, DestPath;
        }

        public static ModUpdater Attach(BaseUnityPlugin host, UpdaterConfig config)
        {
            if (host == null) throw new ArgumentNullException(nameof(host));
            if (config == null) throw new ArgumentNullException(nameof(config));

            config.Prepare(host.Info.Metadata.Version.ToString());
            var modUpdater = host.gameObject.AddComponent<ModUpdater>();
            modUpdater.Init(config, host.GetType().Assembly);
            return modUpdater;
        }
        public bool UpdateFound { get { lock (Sync) return _updateDetected; } }
        public bool IsBusy { get { lock (Sync) return _isChecking || _isDownloading; } }
        public string Status { get { lock (Sync) return _status; } }

        public void ShowWindow() { lock (Sync) _showUI = true; }
        public void HideWindow() { lock (Sync) _showUI = false; }
        public void ToggleWindow() { lock (Sync) _showUI = !_showUI; }
        public void ForceCheckNow() => BeginCheck(startup: false);

        private void Init(UpdaterConfig cfg, Assembly hostAssembly)
        {
            _cfg = cfg;
            _modRoot = cfg.ModRoot ?? Path.GetDirectoryName(hostAssembly.Location);
            _gameRoot = cfg.GameRoot ?? Path.GetDirectoryName(Paths.BepInExRootPath);
            _popupRect = cfg.UpdateWindowRect;
            _depRect = cfg.DependencyWindowRect;

            if (cfg.ForceTls12)
            {
                try { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; }
                catch (Exception e) { cfg.Logger.LogWarning("Couldn't enable TLS 1.2: " + e.Message); }
            }
        }

        private void Start()
        {
            if (_cfg == null) return;
            if (_cfg.CheckForUpdatesOnStart || _cfg.CheckDependenciesOnStart)
                BeginCheck(startup: true);
        }

        private void Update()
        {
            while (_mainQueue.TryDequeue(out var action))
            {
                try { action(); }
                catch (Exception e) { _cfg.Logger.LogError("Updater callback failed: " + e); }
            }
        }

        private void RunOnMain(Action a) => _mainQueue.Enqueue(a);

        private void BeginCheck(bool startup)
        {
            lock (Sync)
            {
                if (_isChecking || _isDownloading) return;
                _isChecking = true;
                _error = null;
                _status = S.Checking;
            }
            ThreadPool.QueueUserWorkItem(_ => CheckWorker(startup));
        }

        private void CheckWorker(bool startup)
        {
            try
            {
                if (string.IsNullOrEmpty(_cfg.ManifestUrl))
                {
                    lock (Sync) { _isChecking = false; _status = S.NoManifestUrl; }
                }
                else
                {
                    _cfg.Logger.LogInfo("Fetching manifest: " + _cfg.ManifestUrl);
                    var manifest = JsonConvert.DeserializeObject<ModManifest>(DownloadString(_cfg.ManifestUrl));
                    if (manifest == null) throw new Exception("Manifest was empty or invalid.");

                    var newer = IsNewer(manifest.Version, _cfg.CurrentVersion);
                    lock (Sync)
                    {
                        _manifest = manifest;
                        _updateDetected = newer;
                        _isChecking = false;
                        _status = newer
                            ? string.Format(S.UpdateFound, manifest.Version)
                            : string.Format(S.UpToDate, _cfg.CurrentVersion);
                        if (!startup || _cfg.CheckForUpdatesOnStart && (newer || _cfg.ShowWindowWhenUpToDate)) _showUI = true;
                    }

                    _cfg.Logger.LogInfo($"Latest={manifest.Version} current={_cfg.CurrentVersion} update={newer}");
                    if (newer && _cfg.OnUpdateFound != null)
                    {
                        var version = manifest.Version;
                        RunOnMain(() => _cfg.OnUpdateFound(version));
                    }
                }
            }
            catch (Exception e)
            {
                lock (Sync) { _isChecking = false; _error = e.Message; _status = S.CheckFailed; }
                _cfg.Logger.LogError("Update check failed: " + e);
            }

            // Dependencies are checked after the manifest attempt (success or not) so we never block the main thread.
            if (startup && _cfg.CheckDependenciesOnStart)
                RunOnMain(() => RunDependencyCheck());
        }

        // Dependency checks if applicable

        public DependencyOutcome RunDependencyCheck()
        {
            List<DependencyResult> results;
            DependencyOutcome outcome;
            try
            {
                results = CheckDependencies();
                if (results.Exists(dependencyResult => dependencyResult.Status == DependencyStatus.Missing)) outcome = DependencyOutcome.MissingRequired;
                else if (results.Exists(r => r.Status == DependencyStatus.VersionTooOld)) outcome = DependencyOutcome.OutdatedOnly;
                else outcome = DependencyOutcome.AllPresent;
            }
            catch (Exception ex)
            {
                _cfg.Logger.LogError("Dependency check failed: " + ex);
                results = [];
                outcome = DependencyOutcome.CheckFailed;
            }

            lock (Sync)
            {
                _dependencyResults = results;
                if (outcome != DependencyOutcome.AllPresent && _cfg.ShowDependencyWindowOnProblem) _showDepUI = true;
            }

            foreach (var dependency in results.Where(dependencyResult => dependencyResult.Status == DependencyStatus.Missing))
                _cfg.Logger.LogError($"Missing: {dependency.Name} (requires {dependency.RequiredVersion})");
            foreach (var d in results.Where(r => r.Status == DependencyStatus.VersionTooOld))
                _cfg.Logger.LogWarning($"Outdated: {d.Name} (have {d.InstalledVersion}, need {d.RequiredVersion})");

            try { _cfg.OnDependenciesChecked?.Invoke(outcome, results); }
            catch (Exception e) { _cfg.Logger.LogError("OnDependenciesChecked threw: " + e); }
            return outcome;
        }

        public List<DependencyResult> CheckDependencies()
        {
            ModManifest manifest;
            lock (Sync) manifest = _manifest;

            var deps = manifest?.Dependencies is { Count: > 0 }
                ? manifest.Dependencies
                : _cfg.Dependencies;

            var results = new List<DependencyResult>();
            if (deps == null) return results;

            foreach (var dep in deps)
            {
                if (string.Equals(dep.Name, "BepInEx", StringComparison.OrdinalIgnoreCase)) continue;

                var info = Chainloader.PluginInfos.Values.FirstOrDefault(p =>
                    (!string.IsNullOrEmpty(dep.PluginGuid) && p.Metadata.GUID == dep.PluginGuid)
                    || string.Equals(p.Metadata.Name, dep.Name, StringComparison.OrdinalIgnoreCase));

                if (info != null)
                {
                    var installed = info.Metadata.Version.ToString();
                    var ok = string.IsNullOrEmpty(dep.MinVersion)
                             || ParseVersion(installed).CompareTo(ParseVersion(dep.MinVersion)) >= 0;
                    results.Add(new DependencyResult
                    {
                        Name = dep.Name,
                        Status = ok ? DependencyStatus.Found : DependencyStatus.VersionTooOld,
                        InstalledVersion = installed,
                        RequiredVersion = dep.MinVersion
                    });
                    continue;
                }

                if (!string.IsNullOrEmpty(dep.PluginPath))
                {
                    var full = Path.Combine(_gameRoot, dep.PluginPath);
                    var exists = File.Exists(full) || Directory.Exists(full);
                    results.Add(new DependencyResult
                    {
                        Name = dep.Name,
                        Status = exists ? DependencyStatus.Found
                            : dep.Required ? DependencyStatus.Missing : DependencyStatus.OptionalMissing,
                        InstalledVersion = exists ? "unknown" : "not found",
                        RequiredVersion = dep.MinVersion
                    });
                    continue;
                }

                results.Add(new DependencyResult
                {
                    Name = dep.Name,
                    Status = dep.Required ? DependencyStatus.Missing : DependencyStatus.OptionalMissing,
                    InstalledVersion = "not found",
                    RequiredVersion = dep.MinVersion
                });
            }

            return results;
        }

        // file validation stuffs

        public List<FileValidationResult> ValidateFiles()
        {
            var results = new List<FileValidationResult>();
            ModManifest manifest;
            lock (Sync) manifest = _manifest;
            if (manifest?.Files == null) return results;

            var known = new HashSet<string>();
            foreach (var entry in manifest.Files)
            {
                string dest;
                try { dest = ResolveSafePath(_modRoot, entry.Path); }
                catch (Exception ex)
                {
                    _cfg.Logger.LogWarning($"Skipping unsafe manifest path '{entry.Path}': {ex.Message}");
                    continue;
                }

                known.Add(NormalizeRel(entry.Path));

                ValidationStatus status;
                if (!File.Exists(dest)) status = entry.Required ? ValidationStatus.Missing : ValidationStatus.OptionalMissing;
                else if (ComputeSha256(dest) != NormalizeHash(entry.Sha256)) status = ValidationStatus.Modified;
                else status = ValidationStatus.Valid;

                results.Add(new FileValidationResult { Path = entry.Path, Status = status });
            }

            // Extra-files search, is skipped when the mod sits loose in BepInEx/plugins folder,
            var rootFull = TrimRoot(_modRoot);
            var sharedPluginsDir = !string.IsNullOrEmpty(Paths.PluginPath) && string.Equals(rootFull, TrimRoot(Paths.PluginPath), StringComparison.OrdinalIgnoreCase);

            if (_cfg.ScanForExtraFiles && !sharedPluginsDir && Directory.Exists(_modRoot))
            {
                foreach (var file in Directory.GetFiles(_modRoot, "*.*", SearchOption.AllDirectories))
                {
                    if (file.EndsWith(".bak", StringComparison.OrdinalIgnoreCase) ||
                        file.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) continue;

                    var full = Path.GetFullPath(file);
                    if (!full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase)) continue;
                    var rel = NormalizeRel(full[rootFull.Length..]);

                    if (known.Contains(rel)) continue;
                    if (_cfg.IgnoreExtraFile != null && _cfg.IgnoreExtraFile(rel)) continue;
                    results.Add(new FileValidationResult { Path = rel, Status = ValidationStatus.Extra });
                }
            }

            return results;
        }

        // Download & repair stuff

        private void DownloadAndApplyWorker()
        {
            ModManifest manifest;
            lock (Sync)
            {
                if (_isDownloading || _manifest == null) return;
                _isDownloading = true;
                _error = null;
                _downloadCompleted = false;
                manifest = _manifest;
            }

            var dlls = new List<(string src, string dst)>();
            try
            {
                var total = manifest.Files.Count;
                var done = 0;

                foreach (var entry in manifest.Files)
                {
                    done++;
                    var dest = ResolveSafePath(_modRoot, entry.Path);
                    var exists = File.Exists(dest);

                    if (exists && ComputeSha256(dest) == NormalizeHash(entry.Sha256))
                    {
                        lock (Sync) _status = string.Format(S.UpToDateFile, done, total, entry.Path);
                        continue;
                    }
                    if (!exists && !entry.Required) continue;

                    lock (Sync) _status = string.Format(S.Downloading, done, total, entry.Path);
                    var temp = DownloadVerified(entry, dest);

                    if (IsDll(entry.Path))
                    {
                        dlls.Add((temp, dest));
                    }
                    else if (NeedsConsent(entry))
                    {
                        lock (Sync)
                        {
                            _pendingConsent.Add(new PendingReplacement { TempPath = temp, DestPath = dest });
                            _showConsentUI = true;
                        }
                        _cfg.Logger.LogInfo("Waiting for user approval: " + entry.Path);
                    }
                    else
                    {
                        ReplaceFile(temp, dest, backup: true);
                        _cfg.Logger.LogInfo("Updated: " + entry.Path);
                    }
                }

                var scheduled = dlls.Count == 0 || TryLaunchApplyScript(dlls);

                lock (Sync)
                {
                    _isDownloading = false;
                    _downloadCompleted = true;
                    if (!scheduled) { _error = S.ApplyScriptFailed; _status = S.UpdateFailed; }
                    else _status = dlls.Count > 0 ? S.UpdateDoneRestart : S.UpdateDone;
                }

                var restart = dlls.Count > 0;
                if (_cfg.OnUpdateInstalled != null) RunOnMain(() => _cfg.OnUpdateInstalled(restart));
            }
            catch (Exception ex)
            {
                lock (Sync) { _isDownloading = false; _error = ex.Message; _status = S.UpdateFailed; }
                _cfg.Logger.LogError("Update failed: " + ex);
            }
        }

        private void RepairWorker(List<FileValidationResult> validation, bool includeModified)
        {
            ModManifest manifest;
            lock (Sync)
            {
                if (_isDownloading || _manifest == null) return;
                _isDownloading = true;
                _error = null;
                manifest = _manifest;
            }

            var dlls = new List<(string src, string dst)>();
            try
            {
                var targets = validation.FindAll(validationResult =>
                    validationResult.Status == ValidationStatus.Missing ||
                    (includeModified && validationResult.Status == ValidationStatus.Modified));
                var done = 0;
                foreach (var broken in targets)
                {
                    done++;
                    var entry = manifest.Files.Find(fileEntry =>
                        string.Equals(fileEntry.Path, broken.Path, StringComparison.OrdinalIgnoreCase));
                    if (entry == null) continue;

                    lock (Sync) _status = string.Format(S.Repairing, done, targets.Count, entry.Path);
                    var dest = ResolveSafePath(_modRoot, entry.Path);
                    var temp = DownloadVerified(entry, dest);

                    if (IsDll(entry.Path)) dlls.Add((temp, dest));
                    else ReplaceFile(temp, dest, backup: true);
                }

                var scheduled = dlls.Count == 0 || TryLaunchApplyScript(dlls);
                var fresh = ValidateFiles();

                lock (Sync)
                {
                    _validationResults = fresh;
                    _isDownloading = false;
                    if (!scheduled) { _error = S.ApplyScriptFailed; _status = S.RepairFailed; }
                    else _status = dlls.Count > 0 ? S.RepairDoneRestart : S.RepairDone;
                }
            }
            catch (Exception ex)
            {
                lock (Sync) { _isDownloading = false; _error = ex.Message; _status = S.RepairFailed; }
                _cfg.Logger.LogError("Repair failed: " + ex);
            }
        }

        private void ApplyApprovedReplacement()
        {
            List<PendingReplacement> toApply;
            lock (Sync)
            {
                toApply = new List<PendingReplacement>(_pendingConsent);
                _pendingConsent.Clear();
            }

            foreach (var replacement in toApply)
            {
                try { ReplaceFile(replacement.TempPath, replacement.DestPath, backup: true); }
                catch (Exception ex)
                {
                    _cfg.Logger.LogError($"Failed to replace {replacement.DestPath}: {ex}");
                    lock (Sync) _error = ex.Message;
                }
            }
            lock (Sync) _status = S.ConsentApplied;
        }

        /// Downloads the file to dest + ".tmp" and verifies the SHA-256 before returning the temp path
        private string DownloadVerified(ModFileEntry entry, string dest)
        {
            var temp = dest + ".tmp";
            Directory.CreateDirectory(Path.GetDirectoryName(dest) ?? throw new InvalidOperationException());
            DownloadFile(entry.Url, temp);

            var hash = ComputeSha256(temp);
            if (hash != NormalizeHash(entry.Sha256))
            {
                File.Delete(temp);
                throw new Exception($"Hash mismatch for {entry.Path} (expected {NormalizeHash(entry.Sha256)}, got {hash}). " +
                                    "The manifest's sha256 may be out of date.");
            }
            return temp;
        }

        private bool NeedsConsent(ModFileEntry entry)
        {
            if (entry.AskBeforeReplace) return true;
            var ext = Path.GetExtension(entry.Path);
            return _cfg.AskBeforeReplaceExtensions.Any(exts => string.Equals(exts, ext, StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsDll(string path) => path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);

        // A loaded DLL can't be overwritten, so a tiny script waits for this process to exit, then swaps files.
        private bool TryLaunchApplyScript(List<(string src, string dst)> files)
        {
            try
            {
                var pid = Process.GetCurrentProcess().Id;
                var safeName = new string(_cfg.ModName.Where(char.IsLetterOrDigit).ToArray());
                var windows = Environment.OSVersion.Platform == PlatformID.Win32NT;
                var scriptPath = Path.Combine(Path.GetTempPath(), $"{safeName}_apply_{pid}{(windows ? ".ps1" : ".sh")}");
                var stringBuilder = new StringBuilder();

                if (windows)
                {
                    stringBuilder.AppendLine($"Wait-Process -Id {pid} -ErrorAction SilentlyContinue");
                    stringBuilder.AppendLine("Start-Sleep -Milliseconds 700");
                    foreach (var (src, dst) in files)
                    {
                        stringBuilder.AppendLine($"if (Test-Path {PsQuote(dst)}) {{ Copy-Item {PsQuote(dst)} {PsQuote(dst + ".bak")} -Force }}");
                        stringBuilder.AppendLine($"Copy-Item {PsQuote(src)} {PsQuote(dst)} -Force");
                        stringBuilder.AppendLine($"Remove-Item {PsQuote(src)} -Force");
                    }
                    stringBuilder.AppendLine($"Remove-Item {PsQuote(scriptPath)} -Force");
                    File.WriteAllText(scriptPath, stringBuilder.ToString(), Encoding.UTF8);

                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "powershell.exe",
                        Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\"",
                        CreateNoWindow = true,
                        UseShellExecute = false
                    });
                }
                else
                {
                    stringBuilder.AppendLine("#!/bin/sh");
                    stringBuilder.AppendLine($"while kill -0 {pid} 2>/dev/null; do sleep 0.5; done");
                    stringBuilder.AppendLine("sleep 1");
                    foreach (var (src, dst) in files)
                    {
                        stringBuilder.AppendLine($"[ -f {ShQuote(dst)} ] && cp -f {ShQuote(dst)} {ShQuote(dst + ".bak")}");
                        stringBuilder.AppendLine($"cp -f {ShQuote(src)} {ShQuote(dst)}");
                        stringBuilder.AppendLine($"rm -f {ShQuote(src)}");
                    }
                    stringBuilder.AppendLine("rm -f \"$0\"");
                    File.WriteAllText(scriptPath, stringBuilder.ToString(), new UTF8Encoding(false));

                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "/bin/sh",
                        Arguments = $"\"{scriptPath}\"",
                        CreateNoWindow = true,
                        UseShellExecute = false
                    });
                }

                _cfg.Logger.LogInfo("Restart-time update scheduled: " + scriptPath);
                return true;
            }
            catch (Exception ex)
            {
                _cfg.Logger.LogError("Couldn't schedule restart-time update: " + ex);
                return false;
            }
        }

        private static string PsQuote(string str) => "'" + str.Replace("'", "''") + "'";
        private static string ShQuote(string str) => "'" + str.Replace("'", "'\\''") + "'";
        private static void ReplaceFile(string temp, string dest, bool backup)
        {
            if (File.Exists(dest))
            {
                if (backup) File.Copy(dest, dest + ".bak", true);
                File.Delete(dest);
            }
            File.Move(temp, dest);
        }

        private static string TrimRoot(string root) =>
            Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        private static string ResolveSafePath(string root, string relative)
        {
            if (string.IsNullOrWhiteSpace(relative))
                throw new InvalidOperationException("Manifest entry has an empty path.");

            var rootFull = TrimRoot(root);
            var full = Path.GetFullPath(Path.Combine(rootFull, relative));
            return !full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase) ? throw new InvalidOperationException("Manifest path escapes the mod folder: " + relative) : full;
        }

        private static string NormalizeRel(string wawa) =>
            wawa.Replace('\\', '/').TrimStart('/').ToLowerInvariant();

        private static string NormalizeHash(string eel) =>
            (eel ?? "").Trim().Replace(" ", "").Replace("-", "").ToLowerInvariant();

        private static string ComputeSha256(string path)
        {
            using var sha = SHA256.Create();
            using var file = File.OpenRead(path);
            var bytes = sha.ComputeHash(file);
            var stringBuilderb = new StringBuilder(bytes.Length * 2);
            foreach (var byt in bytes) stringBuilderb.Append(byt.ToString("x2"));
            return stringBuilderb.ToString();
        }

        private void EnsureTrustedUrl(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                uri.Scheme != Uri.UriSchemeHttps ||
                !_cfg.AllowedHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException("Refusing to download from untrusted URL: " + url);
        }

        private string DownloadString(string url)
        {
            EnsureTrustedUrl(url);
            using var client = MakeClient(_cfg.ManifestTimeoutMs);
            return client.DownloadString(url);
        }

        private void DownloadFile(string url, string dest)
        {
            EnsureTrustedUrl(url);
            using var client = MakeClient(_cfg.DownloadTimeoutMs);
            client.DownloadFile(url, dest);
        }

        private WebClient MakeClient(int timeoutMs)
        {
            var timeout = new TimeoutWebClient(timeoutMs);
            timeout.Headers[HttpRequestHeader.UserAgent] = _cfg.UserAgent;
            timeout.Headers[HttpRequestHeader.CacheControl] = "no-cache";
            return timeout;
        }
        private sealed class TimeoutWebClient(int timeout) : WebClient
        {
            protected override WebRequest GetWebRequest(Uri address)
            {
                var r = base.GetWebRequest(address);
                if (r != null)
                {
                    r.Timeout = timeout;
                    if (r is HttpWebRequest h) h.ReadWriteTimeout = timeout;
                }
                return r;
            }
        }

        private static bool IsNewer(string remote, string current) =>
            ParseVersion(remote).CompareTo(ParseVersion(current)) > 0;

        private static Version ParseVersion(string v)
        {
            v = (v ?? "").Trim().TrimStart('v', 'V');
            var cut = v.IndexOfAny(['-', '+']);
            if (cut >= 0) v = v.Substring(0, cut);
            if (!Version.TryParse(v, out var r)) return new Version(0, 0, 0, 0);
            return new Version(r.Major, r.Minor, Math.Max(r.Build, 0), Math.Max(r.Revision, 0));
        }
        private void OnGUI()
        {
            if (_cfg == null) return;
            bool show, showDep;
            lock (Sync) { show = _showUI; showDep = _showDepUI; }

            if (showDep)
                _depRect = GUI.Window(_cfg.WindowId + 1, _depRect, DrawDependencyPopup,
                    string.Format(S.DependencyWindowTitle, _cfg.ModName));
            if (show)
                _popupRect = GUI.Window(_cfg.WindowId, _popupRect, DrawPopup,
                    string.Format(S.WindowTitle, _cfg.ModName));
        }

        private void DrawPopup(int id)
        {
            string status, error;
            bool updateAvailable, busy, downloadComplete, showConsent, valDone;
            int pendingCount;
            List<FileValidationResult> validation;
            List<string> changelog;

            lock (Sync)
            {
                status = _status;
                error = _error;
                updateAvailable = _updateDetected;
                downloadComplete = _downloadCompleted;
                busy = _isChecking || _isDownloading;
                showConsent = _showConsentUI;
                pendingCount = _pendingConsent.Count;
                valDone = _validationDone;
                validation = _validationResults;
                changelog = _manifest?.Changelog ?? [];
            }
            GUILayout.Label(status ?? "");
            if (showConsent)
            {
                GUILayout.Label(string.Format(S.ConsentHeader, pendingCount));
                if (GUILayout.Button(S.ConsentAllow))
                {
                    lock (Sync) _showConsentUI = false;
                    ThreadPool.QueueUserWorkItem(_ => ApplyApprovedReplacement());
                }
                if (GUILayout.Button(S.ConsentDeny))
                {
                    List<PendingReplacement> discard;
                    lock (Sync)
                    {
                        discard = new List<PendingReplacement>(_pendingConsent);
                        _pendingConsent.Clear();
                        _showConsentUI = false;
                    }
                    foreach (var p in discard)
                    {
                        try { if (File.Exists(p.TempPath)) File.Delete(p.TempPath); }
                        catch (Exception ex) { _cfg.Logger.LogWarning($"Couldn't delete {p.TempPath}: {ex.Message}"); }
                    }
                }
            }
            if (!string.IsNullOrEmpty(error))
                GUILayout.Label("<color=red>" + error + "</color>");
            if (updateAvailable && !busy)
            {
                GUILayout.Label(S.Changelog);
                _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.Height(120));
                foreach (var line in changelog) GUILayout.Label("• " + line);
                GUILayout.EndScrollView();

                if (!downloadComplete && GUILayout.Button(S.DownloadButton))
                    ThreadPool.QueueUserWorkItem(_ => DownloadAndApplyWorker());
            }
            GUILayout.Space(5);
            if (GUILayout.Button(S.ValidateButton))
            {
                var fresh = ValidateFiles();
                lock (Sync) { _validationResults = fresh; _validationDone = true; }
            }
            if (valDone && validation != null)
            {
                GUILayout.Label(S.ValidationHeader);
                _validationScroll = GUILayout.BeginScrollView(_validationScroll, GUILayout.Height(150));
                foreach (var r in validation)
                {
                    string icon, color;
                    switch (r.Status)
                    {
                        case ValidationStatus.Valid: icon = S.ValidOk; color = "green"; break;
                        case ValidationStatus.Missing: icon = S.ValidMissing; color = "red"; break;
                        case ValidationStatus.OptionalMissing: icon = S.ValidOptionalMissing; color = "grey"; break;
                        case ValidationStatus.Modified: icon = S.ValidModified; color = "yellow"; break;
                        case ValidationStatus.Extra: icon = S.ValidExtra; color = "grey"; break;
                        default: icon = "?"; color = "white"; break;
                    }
                    GUILayout.Label($"<color={color}>{icon}</color> {r.Path}");
                }
                GUILayout.EndScrollView();
                if (!busy)
                {
                    if (validation.Exists(r => r.Status == ValidationStatus.Missing) && GUILayout.Button(S.RepairButton))
                        ThreadPool.QueueUserWorkItem(_ => RepairWorker(validation, false));

                    if (_cfg.OfferRepairModified && validation.Exists(r => r.Status == ValidationStatus.Modified)
                        && GUILayout.Button(S.RepairModifiedButton))
                        ThreadPool.QueueUserWorkItem(_ => RepairWorker(validation, true));
                }
            }
            if (GUILayout.Button(S.CheckDepsButton))
            {
                RunDependencyCheck();
                lock (Sync) _showDepUI = true;
            }

            if (GUILayout.Button(S.CloseButton)) HideWindow();
            GUI.DragWindow();
        }
        private void DrawDependencyPopup(int id)
        {
            List<DependencyResult> results;
            lock (Sync) results = _dependencyResults;
            var anyMissing = false;
            var anyOld = false;
            if (results == null || results.Count == 0)
            {
                GUILayout.Label(S.NoDependencies);
            }
            else
            {
                anyMissing = results.Exists(r => r.Status == DependencyStatus.Missing);
                anyOld = results.Exists(r => r.Status == DependencyStatus.VersionTooOld);

                GUILayout.Label(S.DepsHeader);
                _depScroll = GUILayout.BeginScrollView(_depScroll, GUILayout.Height(200));
                foreach (var r in results)
                {
                    string icon, color;
                    switch (r.Status)
                    {
                        case DependencyStatus.Missing: icon = S.DepMissing; color = "red"; break;
                        case DependencyStatus.VersionTooOld: icon = S.DepOld; color = "yellow"; break;
                        case DependencyStatus.OptionalMissing: icon = S.DepOptionalMissing; color = "grey"; break;
                        case DependencyStatus.Found: icon = S.DepFound; color = "green"; break;
                        default: icon = "?"; color = "white"; break;
                    }
                    GUILayout.Label($"<color={color}>{icon}</color> {r.Name}\n" +
                                    string.Format(S.DepNeedHave, r.RequiredVersion, r.InstalledVersion));
                    GUILayout.Space(2);
                }
                GUILayout.EndScrollView();
            }
            if (GUILayout.Button(S.CloseButton)) lock (Sync) _showDepUI = false;
            if (GUILayout.Button(S.RefreshButton)) RunDependencyCheck();

            if (anyMissing) GUILayout.Label(S.DepsMissingWarning);
            if (anyOld) GUILayout.Label(S.DepsOutdatedWarning);
            GUILayout.Space(5);
            GUI.DragWindow();
        }
    }
}