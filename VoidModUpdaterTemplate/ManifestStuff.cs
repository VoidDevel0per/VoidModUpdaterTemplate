using System.Collections.Generic;
using Newtonsoft.Json;

namespace VoidModUpdaterTemplate
{
    // manifest.json structure

    public class ModFileEntry
    {
        [JsonProperty("path")] public string Path;
        [JsonProperty("url")] public string Url;
        [JsonProperty("sha256")] public string Sha256;

        // Missing optional files are not flagged and are not downloaded by update.
        [JsonProperty("required")] public bool Required = true;

        // Overwriting this file needs the player's permission (e.g. custom audio they may have added or swapped).
        [JsonProperty("askBeforeReplace")] public bool AskBeforeReplace;
    }

    public class ModDependency
    {
        [JsonProperty("name")] public string Name;
        [JsonProperty("minVersion")] public string MinVersion;
        [JsonProperty("pluginGUID")] public string PluginGuid;
        [JsonProperty("pluginPath")] public string PluginPath; // relative to the game root
        [JsonProperty("required")] public bool Required;
    }

    public class ModManifest
    {
        [JsonProperty("version")] public string Version;
        [JsonProperty("changelog")] public List<string> Changelog;
        [JsonProperty("files")] public List<ModFileEntry> Files;
        [JsonProperty("dependencies")] public List<ModDependency> Dependencies;
    }

    // Validation & Dependency result stuff
    public enum ValidationStatus { Valid, Missing, OptionalMissing, Modified, Extra }

    public enum DependencyStatus { Found, Missing, VersionTooOld, OptionalMissing }

    public enum DependencyOutcome
    {
        AllPresent, // everything required is here, and isn't too old by your manifests terms.
        OutdatedOnly, // nothing missing, but something is older than minVersion
        MissingRequired, // at least one required dependency is absent
        CheckFailed // the check itself failed
    }

    public struct DependencyResult
    {
        public string Name;
        public DependencyStatus Status;
        public string InstalledVersion;
        public string RequiredVersion;
    }

    public struct FileValidationResult
    {
        public string Path;
        public ValidationStatus Status;
    }
}