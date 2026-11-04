using System;
using BepInEx;

namespace VoidModUpdaterTemplate
{

    [BepInPlugin(Guid, "VoidUpdater", Version)]
    public class UpdaterKitPlugin : BaseUnityPlugin
    {
        public const string Guid = "voiddeveloper.modupdaterkit";
        public const string Version = "1.0.0";
        public const string MinVersion = "1.0.0";
    }
}