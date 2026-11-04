using System;
using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;

namespace VoidModUpdaterTemplate
{
    public sealed class UpdaterStrings
    {
        // All this is just a bunch of text slop joycat
        public  string WindowTitle = "{0} Updater";
        public  string DependencyWindowTitle = "{0} Dependencies";

        public  string Checking = "Checking for updates...";
        public  string UpdateFound = "Update available: {0}";
        public  string UpToDate = "Up to date ({0}).";
        public  string CheckFailed = "Couldn't reach the update server.";
        public  string NoManifestUrl = "No manifest URL configured.";
        public  string Changelog = "Changelog:";
        public  string DownloadButton = "Download & Queue Update";
        public  string Downloading = "[{0}/{1}] Downloading: {2}";
        public  string UpToDateFile = "[{0}/{1}] Up to date: {2}";
        public  string UpdateDone = "All files updated. No restart needed.";
        public  string UpdateDoneRestart = "Files updated. <color=yellow>Restart the game to apply code changes.</color>";
        public  string UpdateFailed = "Update failed.";
        public  string ApplyScriptFailed = "Couldn't schedule the restart-time update. New files are waiting as .tmp next to the old ones.";

        public  string ConsentHeader = "{0} file(s) differ from the remote.";
        public  string ConsentAllow = "Allow Replace";
        public  string ConsentDeny = "Don't modify my files";
        public  string ConsentApplied = "Approved files replaced.";
        public  string ValidateButton = "Validate Mod Files";
        public  string ValidationHeader = "File Validation";
        public  string RepairButton = "Repair Missing Files";
        public  string RepairModifiedButton = "Restore Modified Files (overwrites)";
        public  string Repairing = "[{0}/{1}] Repairing {2}...";
        public  string RepairDone = "Repair completed. All files restored.";
        public  string RepairDoneRestart = "Repair completed. Restart to apply code changes.";
        public  string RepairFailed = "Repair failed.";
        public  string CheckDepsButton = "Check Dependencies";
        public  string RefreshButton = "Refresh";
        public  string CloseButton = "Close";
        public  string NoDependencies = "No dependencies listed.";
        public  string DepsHeader = "Dependency Checks";
        public  string DepMissing = "x MISSING (required)";
        public  string DepOld = "! Found, but older than required";
        public  string DepOptionalMissing = "o Not installed (optional)";
        public  string DepFound = "✓ Found";
        public  string DepNeedHave = " need: {0}  have: {1}";
        public  string DepsMissingWarning = "<color=red>Required dependencies are missing. Some features were disabled.</color>";
        public  string DepsOutdatedWarning = "<color=yellow>Some dependencies are outdated. Consider updating them.</color>";
        public  string ValidOk = "✓";
        public  string ValidMissing = "✗ MISSING";
        public  string ValidOptionalMissing = "optional, not installed";
        public  string ValidModified = "! MODIFIED";
        public  string ValidExtra = "? EXTRA";
    }

    public sealed class UpdaterConfig
    {
        public string ModName; // shown in titles, used in file names and log source
        public string ManifestUrl; // raw URL to manifest.json, preferably github joycat
        public string CurrentVersion;
        
         // Basically all ur updates must be on github <3 or u can add your own domain to this array if u want.
        public string[] AllowedHosts =
        [
            "raw.githubusercontent.com",
            "github.com",
            "objects.githubusercontent.com",
            "release-assets.githubusercontent.com"
        ];
        public int ManifestTimeoutMs = 8000;
        public int DownloadTimeoutMs = 60000;
        public string UserAgent; // default: "<ModName>-Updater/<version>"
        public bool ForceTls12 = true;

        // behavior
        public  bool CheckForUpdatesOnStart = true;
        public  bool CheckDependenciesOnStart = true;
        public  bool ShowWindowWhenUpToDate = false;
        public  bool ShowDependencyWindowOnProblem = true;
        public  bool OfferRepairModified = false;

        // Flag files on disk that aren't in the manifest. Auto-disabled when ModRoot is the shared plugins folder.
        public  bool ScanForExtraFiles = true;
        public Func<string, bool> IgnoreExtraFile;

        // File extensions, e.g (".ogg", ".wav") that always require the user's permission to overwrite, in addition to per-file "askBeforeReplace"
        public string[] AskBeforeReplaceExtensions = [];

        // paths
        public string ModRoot;   // defaults to folder containing the host plugin's DLL
        public string GameRoot;  // defaults to parent of the BepInEx folder (used for dependency pluginPath in plugin validation if that's enabled :3)

        // code-defined dependencies (used when the manifest has none / is unreachable)
        public  List<ModDependency> Dependencies = [];

        // in-game updater UI
        public int WindowId; // taken from ModName so two mods don't collide
        public Rect UpdateWindowRect = new Rect(20, 20, 420, 330);
        public Rect DependencyWindowRect = new Rect(460, 20, 380, 300);
        public UpdaterStrings Strings = new UpdaterStrings();

        // hooks or events ig <3 (always invoked on the Unity main thread)
        public ManualLogSource Logger;
        public Action<DependencyOutcome, List<DependencyResult>> OnDependenciesChecked;
        public Action<string> OnUpdateFound; // remote version
        public Action<bool> OnUpdateInstalled; // true = restart required

        internal void Prepare(string hostVersion)
        {
            if (string.IsNullOrWhiteSpace(ModName)) throw new ArgumentException("UpdaterConfig.ModName is required.");
            if (string.IsNullOrEmpty(CurrentVersion)) CurrentVersion = hostVersion;
            if (string.IsNullOrEmpty(UserAgent)) UserAgent = ModName + "-Updater/" + CurrentVersion;
            Logger ??= BepInEx.Logging.Logger.CreateLogSource(ModName + ".Updater");
            Strings ??= new UpdaterStrings();
            AllowedHosts ??= [];
            AskBeforeReplaceExtensions ??= [];
            if (WindowId == 0)
            {
                var h = 17;
                unchecked { foreach (var c in ModName) h = h * 31 + c; }
                WindowId = 10000 + (Math.Abs(h % 20000)) * 2; // uses WindowId and WindowId + 1
            }
        }
    }
}