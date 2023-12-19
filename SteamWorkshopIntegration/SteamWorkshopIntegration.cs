using Kingmaker.Utility.NewtonsoftJson;
using Kingmaker.Utility.UnityExtensions;
using Steamworks;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Kingmaker.Modding {
    public class SteamWorkshopIntegration {
        public static bool Started { get; private set; }
        // This file contains information about the latest installed version
        public const string ModManagingInfoFileName = "WorkshopManaged.json";
        private static string SettingsFilePath => Path.Combine(ApplicationPaths.persistentDataPath, OwlcatModificationsManager.SettingsFileName);
        private static string DefaultOwlcatTemplateDirectory => Path.Combine(ApplicationPaths.persistentDataPath, "Modifications");
        private static string DefaultUMMDirectory => Path.Combine(ApplicationPaths.persistentDataPath, "UnityModManager");
        private static string UnityModManagerFilesZipPath => Path.Combine(ApplicationPaths.streamingAssetsPath, "OwlcatUnityModManager.zip");
        private static string[] UnityModManagerFileNames = new[] { "dnlib.dll", "Ionic.Zip.dll", "UnityModManager.dll", "UnityModManager.pdb" };
        private OwlcatModificationsManager.SettingsData m_Settings; 

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
                PFLog.Mods.Exception(ex, null);
                return;
            }
            Start();
        }
        public void Start() {
            if (Started) {
                return;
            }
            Started = true;
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
            var unzipDir = new DirectoryInfo(Path.Combine(Path.GetTempPath(), "UMMAssets"));
            unzipDir.Create();
            ZipFile.ExtractToDirectory(UnityModManagerFilesZipPath, unzipDir.FullName);
            foreach (var file in UnityModManagerFileNames) {
                var target = new FileInfo(Path.Combine(DefaultUMMDirectory, file));
                if (target.Exists) {
                    target.Delete();
                }
                var source = new FileInfo(Path.Combine(unzipDir.FullName, file));
                source.MoveTo(target.FullName);
            }
            unzipDir.Delete(true);
        }
        private void EnsureTemplateDirectoryAndSettingsFile() {
            var owlcatTemplateDirectory = new DirectoryInfo(DefaultOwlcatTemplateDirectory);
            if (!owlcatTemplateDirectory.Exists) owlcatTemplateDirectory.Create();
            if (File.Exists(SettingsFilePath)) {
                try {
                    m_Settings = NewtonsoftJsonHelper.DeserializeFromFile<OwlcatModificationsManager.SettingsData>(SettingsFilePath);
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
            m_Settings = new();
            NewtonsoftJsonHelper.SerializeToFile(SettingsFilePath, m_Settings, true);
        }
    }
}
