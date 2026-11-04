# VoidModUpdater

An **automatic updater, file validator and dependency checker** for Casualties Unknown/Scav Prototype BepInEx mods (Unity, BepInEx 5).
You point it at a `manifest.json` hosted on GitHub (or any allowed host) and it handles version checks, hash-verified downloads, DLL swapping, repair of missing files, and dependency warnings, with a small in-game IMGUI window.

Everything mod-specific (what to do when a dependency is missing, what to do when an update is found) lives in your own mod through callbacks. The updater itself knows nothing about any particular mod.

---

## Contents

1. [How it works](#1-how-it-works)
2. [Installing the updater](#2-installing-the-updater)
3. [Quick start](#3-quick-start)
4. [The manifest](#4-the-manifest)
5. [Configuration reference](#5-configuration-reference)
6. [Hooks (callbacks)](#6-hooks-callbacks)
7. [Dependencies](#7-dependencies)
8. [File validation and repair](#8-file-validation-and-repair)
9. [Update flow in detail](#9-update-flow-in-detail)
10. [Public API](#10-public-api)
11. [Customizing the UI text](#11-customizing-the-ui-text)
12. [Release workflow](#12-release-workflow)
13. [Security model](#13-security-model)
14. [Platform notes](#14-platform-notes)
15. [Troubleshooting](#15-troubleshooting)
16. [New-mod checklist](#17-new-mod-checklist)

---

## 1. How it works



When the player clicks **Download & Queue Update**, each file in the manifest is compared by SHA-256. Changed files are downloaded to `*.tmp`, hash-verified, and then:

| File kind | What happens |
|---|---|
| Normal file | Replaced immediately, old one kept as `.bak` |
| Needs consent (`askBeforeReplace` or listed extension) | Held until the player clicks **Allow Replace** |
| `.dll` | Swapped by a powershell script **after the game exits** (a loaded DLL can't be overwritten while the game is running) |

All network work happens on thread-pool threads. Your callbacks always run on the **Unity main thread**.

---

## 2. Installing the updater

The updater ships as a shared BepInEx plugin, `VoidUpdaterTemplate.dll`, that all your mods depend on.

**Players:** put `VoidUpdaterTemplate.dll` in `BepInEx/plugins` once. Every mod that uses the updater shares it.

**Mod authors:**

1. Reference `VoidUpdaterTemplate.dll` from your mod project and set **Copy Local = false** (`<Private>false</Private>`), so your mod doesn't ship its own copy.

Your mod should also account for the dependency being missing in the chance that it is!

---

## 3. Quick start

Minimum setup is a name and a manifest URL. The current version is read from your `[BepInPlugin]` attribute, so there is nothing to keep in sync.

```csharp
using BepInEx;
using VoidUpdaterTemplate;

[BepInDependency(UpdaterupdaterPlugin.Guid, UpdaterupdaterPlugin.MinVersion)]
[BepInPlugin("yourname.yourmod", "YourMod", "1.0.0")]
public class Plugin : BaseUnityPlugin
{
    internal static ModUpdater Updater;

    private void Awake()
    {
        Updater = ModUpdater.Attach(this, new UpdaterConfig
        {
            ModName     = "YourMod",
            ManifestUrl = "https://raw.githubusercontent.com/You/YourMod/main/manifest.json" // Your url should always directly lead to the file!
        });
    }
}
```

Optional: open the window with a hotkey.

```csharp
private void Update()
{
    if (UnityEngine.Input.GetKeyDown(UnityEngine.KeyCode.F8))
        Updater.ToggleWindow();
}
```

> **Increment your `[BepInPlugin]` version on every release.** The updater compares the manifest version against it. If they're equal, players see "Up to date".

---

## 4. The manifest

`manifest.json` lives in your repo (or any allowed host). You can generate it with `make_manifest.py` (see [Release workflow](#12-release-workflow)).

```json
{
  "version": "1.2.2",
  "changelog": [
    "Fixed the crash on load",
    "New audio for the intro"
  ],
  "files": [
    {
      "path": "VoidMod.dll",
      "url": "https://raw.githubusercontent.com/You/YourMod/main/release/VoidMod.dll",
      "sha256": "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08"
    },
    {
      "path": "assets/intro.ogg",
      "url": "https://raw.githubusercontent.com/You/YourMod/main/release/assets/intro.ogg",
      "sha256": "…",
      "askBeforeReplace": true
    },
    {
      "path": "readme.txt",
      "url": "https://raw.githubusercontent.com/You/YourMod/main/release/readme.txt",
      "sha256": "…",
      "required": false
    }
  ],
  "dependencies": [
    { "name": "CUCoreLib",    "minVersion": "1.0.0", "pluginGUID": "com.example.cucorelib", "required": true  },
    { "name": "CasualtiesMP", "minVersion": "1.0.0", "required": false }
  ]
}
```

### Top-level fields

| Field | Type | Notes |
|---|---|---|
| `version` | string | Compared against the plugin's current version. A leading `v` and anything after `-` or `+` is ignored (`v1.2.3-beta` → `1.2.3`). Unparsable versions count as `0.0.0.0`, which is never "newer". |
| `changelog` | string[] | Shown in the update window. |
| `files` | object[] | Everything the mod should have on disk. See below. |
| `dependencies` | object[] | Optional. If not empty it **replaces** the dependencies set in code. |

### File entries

| Field | Type | Default | Notes |
|---|---|---|---|
| `path` | string | required | Relative to the **mod root** (the folder containing your plugin DLL). Forward or back slashes both work. Paths that escape the mod folder (`..`) are rejected. |
| `url` | string | required | Must be `https` and on an allowed host. |
| `sha256` | string | required | Case-insensitive. Spaces and dashes are ignored. |
| `required` | bool | `true` | `false` means: missing is not an error, shows as "optional" in validation, and an update won't download it if it isn't already there. |
| `askBeforeReplace` | bool | `false` | The player must approve overwriting this file. |

### Dependency entries

| Field | Type | Notes |
|---|---|---|
| `name` | string | Used for display **and** as a fallback match against the plugin's `[BepInPlugin]` name. An entry named `BepInEx` is skipped. |
| `minVersion` | string | Optional. If omitted, any installed version passes. |
| `pluginGUID` | string | Optional but preferred. Matched exactly against the plugin's GUID. |
| `pluginPath` | string | Optional. A file or folder relative to the **game root**, used if the plugin isn't loaded (e.g. a non-BepInEx dependency). |
| `required` | bool | Only affects the *not found* case: `true` → Missing, `false` → Optional-missing. |

---

## 5. Configuration reference

Everything below is a public field on `UpdaterConfig`. Set what you need; the rest has sensible defaults.

### Identity

| Field | Default | Description |
|---|---|---|
| `ModName` | **required** | Used in window titles, log source name (`<ModName>.Updater`), user agent and temp file names. |
| `ManifestUrl` | none | Raw URL to `manifest.json`. If empty, no remote check is done and the status says so. You can still use code-defined dependencies. |
| `CurrentVersion` | host plugin's `[BepInPlugin]` version | Override only if you want to compare against something else. |

### Networking

| Field | Default | Description |
|---|---|---|
| `AllowedHosts` | the four GitHub hosts: `raw.githubusercontent.com`, `github.com`, `objects.githubusercontent.com`, `release-assets.githubusercontent.com` | Every manifest and file URL must be `https` and on this list. Add your own host here; remove entries to lock down. |
| `ManifestTimeoutMs` | `8000` | Timeout for fetching the manifest. |
| `DownloadTimeoutMs` | `60000` | Timeout for each file download. |
| `UserAgent` | `<ModName>-Updater/<version>` | Sent with every request. |
| `ForceTls12` | `true` | Adds TLS 1.2 to the process's allowed protocols. Old Mono often defaults to TLS 1.0, which GitHub rejects. This is a process-wide setting it only adds a protocol. |

### Behavior

| Field | Default | Description |
|---|---|---|
| `CheckForUpdatesOnStart` | `true` | Open the update window automatically when a newer version is found. |
| `CheckDependenciesOnStart` | `true` | Run the dependency check once the manifest attempt finishes. |
| `ShowWindowWhenUpToDate` | `false` | Also open the window at startup when there's nothing to update. |
| `ShowDependencyWindowOnProblem` | `true` | Open the dependency window when the outcome isn't `AllPresent`. |
| `OfferRepairModified` | `false` | Adds a **Restore Modified Files (overwrites)** button. Off by default so player edits aren't clobbered. |
| `ScanForExtraFiles` | `true` | Flag files in the mod folder that aren't in the manifest. Automatically skipped when the mod root *is* `BepInEx/plugins` itself (otherwise every other mod's files would be listed </3). |
| `IgnoreExtraFile` | none | `Func<string,bool>`. Receives a **lowercase, forward-slash relative path** return `true` to hide it from the Extra list. |
| `AskBeforeReplaceExtensions` | none | e.g. `new[] { ".ogg" }`. Files with these extensions always need consent, in addition to per-file `askBeforeReplace`. |

> If `CheckForUpdatesOnStart` is `false` but `CheckDependenciesOnStart` is `true`, the manifest is still fetched (for its dependency list). It just won't pop the window open. `UpdateFound` and `OnUpdateFound` still reflect a newer version.

### Paths

| Field | Default | Description |
|---|---|---|
| `ModRoot` | folder containing the host plugin DLL | Base folder for manifest `path` values. |
| `GameRoot` | parent of the `BepInEx` folder | Base folder for dependency `pluginPath` values. |

### Code-defined dependencies

```csharp
Dependencies =
{
    new ModDependency { Name = "CUCoreLib", MinVersion = "1.0.0", Required = true },
    new ModDependency { Name = "CasualtiesMP", Required = false }
}
```

Used when the manifest has no `dependencies` (or the player is offline). Same fields as the JSON. Also note that the Name field MUST be the Plugin's GUID.

### UI

| Field | Default | Description |
|---|---|---|
| `WindowId` | derived from `ModName` | IMGUI window IDs. The updater uses `WindowId` and `WindowId + 1`. Leave `0` and two mods won't collide. |
| `UpdateWindowRect` | `(20, 20, 420, 330)` | Starting position/size of the updater window. |
| `DependencyWindowRect` | `(460, 20, 380, 300)` | Starting position/size of the dependency window. |
| `Strings` | built-in English from yours truly:fircharm: | See [Customizing the UI text](#11-customizing-the-ui-text). |

### Logging

| Field | Default | Description |
|---|---|---|
| `Logger` | new log source `<ModName>.Updater` | Pass your plugin's `Logger` to merge output into one source. |

---

## 6. Hooks/events (callbacks)

All hooks are ran on **Unity's main thread**, so touching Unity objects or Harmony patches inside them is safe. An exception thrown in a hook is caught and logged it shouldn't break the updater.

| Hook | Signature | Fires when |
|---|---|---|
| `OnDependenciesChecked` | `Action<DependencyOutcome, List<DependencyResult>>` | After **every** dependency check: at startup, on the **Check Dependencies** button, and on **Refresh** and `RunDependencyCheck()` |
| `OnUpdateFound` | `Action<string>` (remote version) | A manifest check finds a version newer than the current one. |
| `OnUpdateInstalled` | `Action<bool>` (`true` = restart needed) | The download/apply pass finished without error. `true` if any DLL was queued for restart-time swap. |

### `DependencyOutcome`

| Value | Meaning |
|---|---|
| `AllPresent` | Every required dependency is installed and new enough. (Optional missing ones don't count.) |
| `OutdatedOnly` | Nothing required is missing, but at least one found dependency is older than `minVersion`. |
| `MissingRequired` | At least one `required` dependency wasn't found. |
| `CheckFailed` | The check itself threw. Treat as "unknown, be safe". |

### Typical handler

```csharp
OnDependenciesChecked = (outcome, results) =>
{
    switch (outcome)
    {
        case DependencyOutcome.MissingRequired:
        case DependencyOutcome.CheckFailed:
            Harmony.UnpatchID(); // I'd do this if you were using CUCoreLib or you used methods from another library.
            break;

        case DependencyOutcome.OutdatedOnly:
            // run, but maybe disable features that need the newer API
            foreach (var res in results)
                if (res.Name == "SomeLib" && res.Status == DependencyStatus.VersionTooOld)
                    Plugin.NukeFeatures(); // Or whatever method you define for to handle it x3
            Plugin.LoadAssets();
            break;

        case DependencyOutcome.AllPresent:
            Plugin.LoadAssets();
            break;
    }
}
```

---

## 7. Dependencies

**Source of truth.** If the manifest's `dependencies` array has at least one entry it's used; otherwise `UpdaterConfig.Dependencies` is used. That means an offline player still gets a dependency check from the code-defined list.

**Matching, per dependency (first hit wins):**

1. A loaded BepInEx plugin whose GUID equals `pluginGUID` (if given) **or** whose `[BepInPlugin]` name equals `name` (case-insensitive).
   → `Found` if installed version ≥ `minVersion` (or no `minVersion`), else `VersionTooOld`.
2. If `pluginPath` is given and not found as a plugin: check that the file/folder exists under the game root.
   → `Found`, or `Missing` / `OptionalMissing` depending on `required`.
3. Otherwise → `Missing` or `OptionalMissing` depending on `required`.

**Timing.** The check runs on the main thread (BepInEx's plugin list must be read there) after the startup manifest attempt, so all plugins have finished loading.

---

## 8. File validation and repair

Click **Validate Mod Files** in the window (or call `ValidateFiles()`). It needs the manifest to be loaded; with no manifest it returns an empty list.

| Status | Meaning |
|---|---|
| `Valid` | On disk and hash matches. |
| `Missing` | Required file not on disk. |
| `OptionalMissing` | `required: false` and not on disk. Not an error. |
| `Modified` | On disk but the hash differs from the manifest. |
| `Extra` | On disk but not in the manifest (only if `ScanForExtraFiles` applies). `.bak` and `.tmp` files are always ignored. |

**Repair Missing Files** re-downloads each `Missing` file with the same hash check. With `OfferRepairModified = true` a second button also restores `Modified` files (overwriting them, with a `.bak` kept). Repaired `.dll` files go through the restart-time swap like an update.

---

## 9. Update flow in detail

For each manifest file, in order:

1. If the file exists and its hash matches → skip ("Up to date").
2. If it doesn't exist and `required` is `false` → skip.
3. Download to `<dest>.tmp`, then verify SHA-256.
   **On mismatch the temp file is deleted and the whole update stops with an error.**
4. Route by kind:
   - `.dll` → queued for the restart-time script.
   - needs consent → held in a pending list, window shows **Allow Replace / Don't modify my files**.
   - everything else → copied over the old file (`.bak` kept), temp removed.

After the loop, any queued DLLs are scheduled for swapping and `OnUpdateInstalled(restartNeeded)` fires.

**Things to know**

- The update is **not atomic**. If file 5 of 8 fails its hash check, files 1–4 are already replaced. The `.bak` files are your safety net, and **Validate + Repair** will fix a half-applied state.
- If the player closes the game with consent files still pending, the `.tmp` files stay on disk. The next update re-downloads them.
- **Deny** deletes the pending temp files and leaves the player's originals alone.
- The status line reports progress as `[done/total]`.

### Restart-time DLL swap

A script is written to the OS temp folder, waits for the game process to exit, backs up the old DLL as `.bak`, copies the new one in, deletes the temp file and then deletes itself.

| OS | Mechanism |
|---|---|
| Windows | `powershell.exe -NoProfile -ExecutionPolicy Bypass -File <script>.ps1` |
| Linux / macOS | `/bin/sh <script>.sh` (waits with `kill -0`)|

Yes you're welcome linux users <3


---

## 10 Public Updater Methods/Fields

`ModUpdater.Attach(...)` returns the component; keep a reference if you want any of these.

| Member | Description |
|---|---|
| `static ModUpdater Attach(BaseUnityPlugin host, UpdaterConfig config)` | Create and start the updater. Call once from `Awake()`. |
| `bool UpdateFound` | A newer version was detected. Thread-safe. |
| `bool IsBusy` | A check or download is in progress. |
| `string Status` | Current status line. |
| `void ShowWindow()` / `HideWindow()` / `ToggleWindow()` | Control the updater window (e.g. from a hotkey or your own menu). |
| `void ForceCheckNow()` | Re-download the manifest and compare versions. Always opens the window. |
| `DependencyOutcome RunDependencyCheck()` | Run the dependency check now, update the dependency window, fire `OnDependenciesChecked`. **Main thread only.** |
| `List<DependencyResult> CheckDependencies()` | Same check without side effects or callbacks. **Main thread only.** |
| `List<FileValidationResult> ValidateFiles()` | Hash every manifest file on the calling thread. Large mods may hitch for a moment. |

---

## 11. Customizing the UI text

Every string is a field on `UpdaterStrings`. Replace any of them (translation, house tone, etc.). Rich-text tags like `<color=yellow>…</color>` work because the windows use IMGUI labels.

```csharp
Strings = new UpdaterStrings
{
    WindowTitle   = "{0} Updater :3",
    DownloadButton = "New update dropped :3c",
    UpdateDoneRestart = "Done! <color=yellow>Restart the game</color> to finish."
}
```

Strings with `string.Format` placeholders:

| Field | Placeholders |
|---|---|
| `WindowTitle`, `DependencyWindowTitle` | `{0}` mod name |
| `UpdateFound` | `{0}` remote version |
| `UpToDate` | `{0}` current version |
| `Downloading`, `UpToDateFile`, `Repairing` | `{0}` done, `{1}` total, `{2}` file path |
| `ConsentHeader` | `{0}` number of files |
| `DepNeedHave` | `{0}` required version, `{1}` installed version |

Other fields (button labels, validation icons, warnings) are plain text. See `UpdaterConfig.cs` for the full list.

---

## 12. Release workflow

`make_manifest.py` builds the manifest and computes every hash so they can't drift.

1. Build your mod and assemble a release folder that mirrors the mod folder:
   ```
   release/
     VoidMod.dll
     assets/intro.ogg
     readme.txt
   ```
2. Increment `[BepInPlugin]` version in code.
3. Run:
   ```bash
   python make_manifest.py --root ./release --version 1.2.2 \
       --base-url https://raw.githubusercontent.com/You/YourMod/main/release \
       --ask-before-replace .ogg \
       --optional "*.txt" \
       --changelog "Fixed the crash on load" "New intro audio" \
       --out manifest.json
   ```
4. Commit and push the release folder **and** `manifest.json` together.

| Flag | Purpose |
|---|---|
| `--root` | Folder mirroring the mod folder. |
| `--base-url` | URL that `--root` is served from. |
| `--version` | Version written to the manifest. |
| `--out` | Output path (default `manifest.json`). |
| `--changelog` | Replace changelog lines. If omitted, an existing manifest's changelog is kept. |
| `--ask-before-replace EXT…` | Mark files with these extensions `askBeforeReplace`. |
| `--optional GLOB…` | Mark matching files `required: false`. |
| `--exclude GLOB…` | Skip files (default `*.bak *.tmp *.pdb`). |

Existing `dependencies` in the output file are preserved, so edit them in the JSON once and forget about them.

> Publish the **files before the manifest** (or in the same commit). A manifest that points at files that aren't live yet causes a failed download.

---

## 13. Security model

- **HTTPS only**, and only to hosts in `AllowedHosts`. Anything else is refused before a request is made.
- **Hashes verified**: every downloaded file is checked against the manifest's SHA-256 before it's installed. A mismatch aborts.
- **Path safe**: manifest paths that resolve outside the mod folder are rejected.
- **Consent for sensitive files**: use `askBeforeReplace` for anything players commonly customize.
- **Backups**: replaced files keep a `.bak` copy.

What this does **not** protect against: anyone who can push to the repo hosting the manifest controls both the files and their hashes. Treat write access to that repo as the trust boundary: use branch protection, 2FA, and a dedicated release branch if you can.

---

## 14. Platform notes

- **Windows:** fully supported.
- **Linux/macOS:** supported via the `/bin/sh` swap script.
- **Proton/Wine:** the game thinks it's Windows and will try `powershell.exe`, which often doesn't exist in a Wine prefix. Non-DLL updates still work. DLL updates will fail to schedule; the player will see the error and the new DLL waits as `.tmp` for a manual rename. Consider shipping a "manual update" note.
- **Raw GitHub caching:** `raw.githubusercontent.com` can serve cached content for a few minutes after a push. A just-published release may not be visible right away.

---

## 15. Troubleshooting

| Symptom | Likely cause / fix |
|---|---|
| Mod doesn't load, log shows missing dependency | `VoidUpdaterTemplate.dll` isn't in `BepInEx/plugins`. |
| **Hash mismatch** error | The manifest hash doesn't match the hosted file. Re-run `make_manifest.py` after your final build. Also check that git isn't rewriting line endings on text files (`.gitattributes`: `* -text` for release assets) and that large files aren't behind Git LFS pointers. |
| "Refusing to download from untrusted URL" | The URL isn't `https`, or its host isn't in `AllowedHosts`. |
| Window never appears | Version in the manifest isn't higher than your `[BepInPlugin]` version, or `CheckForUpdatesOnStart` is `false`. Call `ToggleWindow()` from a hotkey to open it manually. |
| "Couldn't reach the update server" | Offline, timeout (`ManifestTimeoutMs`), or TLS problems. Keep `ForceTls12` on. See the log for the exact exception. |
| Dependency shows Missing but it's installed | The plugin's name or GUID doesn't match. Match by `pluginGUID` (exact) or make `name` equal the plugin's `[BepInPlugin]` name. |
| Everything shows as "Extra" | The mod DLL sits in a shared folder with other files. The updater skips scanning when `ModRoot` is `BepInEx/plugins`; if you use another shared folder, set `ScanForExtraFiles = false` or use `IgnoreExtraFile`. |
| Two mods' windows fight over the same IDs | Give each mod a distinct `ModName` (or set `WindowId` manually). |
| New DLL not applied after restart | Script couldn't launch (see Proton note), or antivirus blocked it. Look for `<name>.dll.tmp` and the error in the BepInEx log. |
| Update applied halfway | A later file failed. Click **Validate Mod Files**, then **Repair Missing Files** (or **Restore Modified**, if enabled). |

---


## 17. New-mod checklist

- [ ] Reference `VoidUpdaterTemplate.dll` (Copy Local = false)
- [ ] Call `ModUpdater.Attach(this, new UpdaterConfig { ModName, ManifestUrl })` in `Awake()`
- [ ] Decide which files need `askBeforeReplace` (audio, configs, anything players tweak)
- [ ] Decide which files are `required: false` (readmes, optional assets)
- [ ] List dependencies in the manifest (and in code as an offline fallback)
- [ ] Handle `OnDependenciesChecked`: disable patches or features on `MissingRequired` / `CheckFailed`
- [ ] Optionally bind a hotkey or menu button to `ToggleWindow()`
- [ ] Generate `manifest.json` with `make_manifest.py` for each release
- [ ] Bump the `[BepInPlugin]` version before releasing
- [ ] Test: install an old version, publish a new one, confirm the window, download, restart and swap all work
