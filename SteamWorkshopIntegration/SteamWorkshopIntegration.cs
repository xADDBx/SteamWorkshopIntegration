using Kingmaker.EntitySystem.Interfaces;
using Kingmaker.Utility.NewtonsoftJson;
using Kingmaker.Utility.UnityExtensions;
using Newtonsoft.Json;
using Steamworks;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using static UnityEngine.PlayerLoop.PreUpdate;

namespace Kingmaker.Modding {
    public class SteamWorkshopIntegration {
        public static bool Started { get; private set; }
        private static string SettingsFilePath => Path.Combine(ApplicationPaths.persistentDataPath, OwlcatModificationsManager.SettingsFileName);
        internal static string DefaultOwlcatTemplateDirectory => Path.Combine(ApplicationPaths.persistentDataPath, "Modifications");
        internal static string DefaultUMMDirectory => Path.Combine(ApplicationPaths.persistentDataPath, "UnityModManager");
        private static string UnityModManagerFilesZipPath => Path.Combine(ApplicationPaths.streamingAssetsPath, "OwlcatUnityModManager.zip");
        private static string[] UnityModManagerFileNames = new[] { "dnlib.dll", "Ionic.Zip.dll", "UnityModManager.dll", "UnityModManager.pdb" };
        internal WriteableSettingsData m_Settings;
        public Dictionary<PublishedFileId_t, SteamWorkshopModification> SubscribedModifications = new();

        private static SteamWorkshopIntegration s_Instance;
        public static SteamWorkshopIntegration Instance {
            get {
                if (s_Instance == null) {
                    s_Instance = new();
                }
                return s_Instance;
            }
        }
        private SteamWorkshopIntegration() {
            if (!SteamAPI.Init()) {
                // After everything works this could possibly removed because when this returns false the issue is pretty much unrelated (game not launched via Steam; Steam installation problematic etc.)
                PFLog.Mods.Log("Could not initialize SteamAPI.");
                return;
            }
            if (!SteamUser.BLoggedOn()) {
                // SteamServersConnected_t callback could be added to handle the case that user reconnects during a game session.
                PFLog.Mods.Log("Could not connect to Steam Servers. User is either not logged in or Servers are down.");
                return;
            }
            PFLog.Mods.Log("Starting Steam Workshop Integration.");
            try {
                EnsureUnityModManagerFiles();
                EnsureTemplateDirectoryAndSettingsFile();
            } catch (Exception ex) {
                PFLog.Mods.Exception(ex);
                return;
            }
        }
        public void Start() {
            if (Started) {
                return;
            }
            Started = true;
            try {
                GetSubscribedModifications();
                GetInstalledModifications();
                InstallOrUpdateSubscribedModifications();
                File.WriteAllText(SettingsFilePath, JsonUtility.ToJson(m_Settings, true));
            } catch (Exception ex) {
                PFLog.Mods.Exception(ex);
                return;
            }
        }
        private void InstallOrUpdateSubscribedModifications() {
            foreach (var mod in SubscribedModifications.Values) {
                if (!mod.IsDownloading) {
                    if (mod.WorkshopSettings == null) {
                        mod.Install();
                    } else if (mod.LocalTimestamp < mod.SteamTimestamp) {
                        mod.Install(true);
                    }
                }
            }
        }
        private void GetInstalledModifications() {
            foreach (var directory in Directory.GetDirectories(DefaultUMMDirectory)) {
                var workshopManagingFile = new FileInfo(Path.Combine(directory, SteamWorkshopModification.WorkshopSettingsData.ModManagingInfoFileName));
                if (workshopManagingFile.Exists) {
                    try {
                        var workshopSettings = JsonUtility.FromJson<SteamWorkshopModification.WorkshopSettingsData>(File.ReadAllText(workshopManagingFile.FullName));
                        if (workshopSettings == null) throw new Exception();
                        if (SubscribedModifications.TryGetValue(workshopSettings.SteamFileId, out var mod)) {
                            mod.WorkshopSettings = workshopSettings;
                        } else {
                            UninstallModification(directory, true, workshopSettings.UniqueName);
                        }
                    } catch (Exception) {
                        UninstallModification(directory, true);
                    }
                }
            }
            foreach (var directory in Directory.GetDirectories(DefaultOwlcatTemplateDirectory)) {
                var workshopManagingFile = new FileInfo(Path.Combine(directory, SteamWorkshopModification.WorkshopSettingsData.ModManagingInfoFileName));
                if (workshopManagingFile.Exists) {
                    try {
                        var workshopSettings = JsonUtility.FromJson<SteamWorkshopModification.WorkshopSettingsData>(File.ReadAllText(workshopManagingFile.FullName));
                        if (workshopSettings == null) throw new Exception();
                        if (SubscribedModifications.TryGetValue(workshopSettings.SteamFileId, out var mod)) {
                            mod.WorkshopSettings = workshopSettings;
                        } else {
                            UninstallModification(directory, false, workshopSettings.UniqueName);
                        }
                    } catch (Exception) {
                        UninstallModification(directory, false);
                    }
                }
            }
        }
        private void UninstallModification(string ModificationDirectory, bool isUmm, string UniqueName = null) {
            PFLog.Mods.Log($"Deleting unsubscribed modification: {UniqueName ?? "Unknown Modification"} at {ModificationDirectory}");
            try {
                Directory.Delete(ModificationDirectory, true);
                if (!isUmm && !UniqueName.IsNullOrEmpty()) {
                    var newList = m_Settings.EnabledModifications.ToList();
                    newList.Remove(UniqueName);
                    m_Settings.EnabledModifications = newList.ToArray();
                }
            } catch (Exception ex) {
                PFLog.Mods.Exception(ex);
            }
        }
        private void GetSubscribedModifications() {
            uint numSubscribedMods = SteamUGC.GetNumSubscribedItems();
            PublishedFileId_t[] subscribedItems = new PublishedFileId_t[numSubscribedMods];
            SteamUGC.GetSubscribedItems(subscribedItems, numSubscribedMods);
            foreach (var mod in subscribedItems) {
                SubscribedModifications[mod] = new SteamWorkshopModification(mod);
            }
        }
        private void EnsureUnityModManagerFiles() {
            var ummDirectory = new DirectoryInfo(DefaultUMMDirectory);
            if (ummDirectory.Exists) {
                var files = new HashSet<string>(ummDirectory.GetFiles().Select(f => f.Name));
                if (!UnityModManagerFileNames.All(files.Contains)) {
                    ReinstallUnityModManager();
                }
            } else {
                ummDirectory.Create();
                ReinstallUnityModManager();
            }
        }
        private void ReinstallUnityModManager() {
            PFLog.Mods.Log($"Copying UnityModManager Files from {UnityModManagerFilesZipPath} to {DefaultUMMDirectory}.");
            var unzipDir = new DirectoryInfo(Path.Combine(Path.GetTempPath(), "RogueTraderUMMAssets"));
            try {
                if (unzipDir.Exists) {
                    unzipDir.Delete(true);
                }
                unzipDir.Create();
                ZipFile.ExtractToDirectory(UnityModManagerFilesZipPath, unzipDir.FullName);
                foreach (var file in UnityModManagerFileNames) {
                    var target = new FileInfo(Path.Combine(DefaultUMMDirectory, file));
                    if (target.Exists) {
                        target.Delete();
                    }
                    var source = new FileInfo(Path.Combine(unzipDir.FullName, "UnityModManager", file));
                    source.MoveTo(target.FullName);
                }
            } finally {
                if (unzipDir.Exists) {
                    unzipDir.Delete(true);
                }
            }
        }
        private void EnsureTemplateDirectoryAndSettingsFile() {
            var owlcatTemplateDirectory = new DirectoryInfo(DefaultOwlcatTemplateDirectory);
            if (!owlcatTemplateDirectory.Exists) owlcatTemplateDirectory.Create();
            if (File.Exists(SettingsFilePath)) {
                try {
                    m_Settings = JsonUtility.FromJson<WriteableSettingsData>(File.ReadAllText(SettingsFilePath));
                    if (m_Settings == null) throw new Exception();
                } catch (Exception) {
                    File.Delete(SettingsFilePath);
                    RecreateTemplateModSettingsFile();
                }
            } else {
                RecreateTemplateModSettingsFile();
            }
        }
        private void RecreateTemplateModSettingsFile() {
            PFLog.Mods.Log($"Creating default OwlcatModificationManagerSettings.json in {SettingsFilePath}");
            m_Settings = new();
            File.WriteAllText(SettingsFilePath, JsonUtility.ToJson(m_Settings, true));
        }
        public class WriteableSettingsData {
            [JsonProperty]
            public string[] SourceDirectories = new string[0];

            [JsonProperty]
            public string[] EnabledModifications = new string[0];
        }
    }
}
