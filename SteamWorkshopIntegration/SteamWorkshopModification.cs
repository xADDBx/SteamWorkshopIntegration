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
using static Kingmaker.Modding.SteamWorkshopIntegration;

namespace Kingmaker.Modding {
    public class SteamWorkshopModification {
        public const string UMMInfoFileName = "Info.json";
        public PublishedFileId_t SteamFileId;
        public WorkshopSettingsData WorkshopSettings;
        public string SteamLocation;
        public string LocalLocation;
        public OwlcatModificationManifest Manifest = null;
        public bool IsUmm = false;
        public bool IsOwlcatTemplate = false;
        public bool IsDownloading = false;
        public bool NeedsDownload;
        public uint SteamTimestamp;
        public uint LocalTimestamp => WorkshopSettings?.LocalTimestamp ?? 0;

        public SteamWorkshopModification(PublishedFileId_t mod) {
            SteamFileId = mod;
            var state = (EItemState)SteamUGC.GetItemState(mod);
            var downloaded = state.HasFlag(EItemState.k_EItemStateInstalled);
            var hasUpdate = state.HasFlag(EItemState.k_EItemStateNeedsUpdate);
            IsDownloading = state.HasFlag(EItemState.k_EItemStateDownloading);
            NeedsDownload = (hasUpdate || !downloaded) && !IsDownloading;
            if (downloaded) {
                // Using a path buffer of size 4096 since those are the supported lengths on Mac and Linux. Windows by defaults supports 260 chars; Windows extended would support 32,767 but that might cause problems?
                SteamUGC.GetItemInstallInfo(mod, out var size, out var dir, 4096, out var timestamp);
                SteamLocation = dir;
                SteamTimestamp = timestamp;
            }
        }
        public void Install(bool isUpdate = false) {
            PFLog.Mods.Log($"{(isUpdate ? "Updating" : "Installing")} Steam Workshop with Id: " + SteamFileId);
            var unzipDir = new DirectoryInfo(Path.Combine(Path.GetTempPath(), "RogueTraderWorkshopMod" + SteamFileId.m_PublishedFileId.ToString()));
            try {
                if (unzipDir.Exists) {
                    unzipDir.Delete(true);
                }
                unzipDir.Create();
                // Files are either available as
                // 1. _legacy.bin archive; In which case SteamLocation points to a file
                // 2. .zip; In which case SteamLocation points to the directory containing the zip
                // 3. unzipped files; In which case SteamLocation points to the directory containing the files
                string pathToZip = null;
                if (SteamLocation.EndsWith(@"_legacy.bin")) {
                    pathToZip = SteamLocation;
                } else {
                    var zips = Directory.GetFiles(SteamLocation, "*.zip");
                    if (zips.Length > 0) {
                        // I'll assume there will only ever be one zip?
                        pathToZip = Path.Combine(SteamLocation, zips.First());
                    }
                }
                if (pathToZip != null) {
                    ZipFile.ExtractToDirectory(pathToZip, unzipDir.FullName);
                } else {
                    CopyDirectoryRecursively(new DirectoryInfo(SteamLocation), unzipDir.FullName);
                }
                // Workaround for the case that the mod is shipped as a nested zip archive. This tries to resolve at most 3 nested layers.
                // E.g. if the mod is shipped as 152365151351_legacy.bin which contains MyMod.zip (which might have another single zip for whatever reason?). MyMod.zip would be the first nested layer.
                // Some mod might, for whatever reason, want to ship zip files. To prevent accidentally unpacking them too this will only continue until there are other files besides the single zip
                int i = 0;
                while (unzipDir.GetFiles().Length == 1 && unzipDir.GetFiles("*.zip")?.Length == 1) {
                    i += 1;
                    if (i > 3) {
                        throw new Exception("Error while trying to Install Workshop Item. Exceeded 3 nested zip archives.");
                    }
                    var newUnzipDir = new DirectoryInfo(Path.Combine(Path.GetTempPath(), "RogueTraderWorkshopMod" + SteamFileId.m_PublishedFileId.ToString() + "_nested"));
                    if (newUnzipDir.Exists) {
                        newUnzipDir.Delete(true);
                    }
                    try {
                        newUnzipDir.Create();
                        var archive = unzipDir.GetFiles().First();
                        ZipFile.ExtractToDirectory(archive.FullName, newUnzipDir.FullName);
                        archive.Delete();
                        CopyDirectoryRecursively(newUnzipDir, unzipDir.FullName);
                    } catch (Exception ex) {
                        throw ex;
                    } finally {
                        if (newUnzipDir?.Exists ?? false) {
                            newUnzipDir.Delete(true);
                        }
                    }
                }
                var manifestPath = Path.Combine(unzipDir.FullName, OwlcatModification.ManifestFileName);
                if (File.Exists(manifestPath)) {
                    try {
                        Manifest = JsonUtility.FromJson<OwlcatModificationManifest>(File.ReadAllText(manifestPath));
                    } catch (Exception ex) {
                        PFLog.Mods.Exception(ex);
                    }
                }
                string targetDir = null;
                if (Manifest != null) {
                    PFLog.Mods.Log($"Steam Workshop Item recognized as {Manifest.DisplayName} with Id {Manifest.UniqueName}");
                    if (!Manifest.UniqueName.IsNullOrEmpty()) {
                        if (unzipDir.GetFiles()?.Where(file => file.Name == UMMInfoFileName)?.Any() ?? false) {
                            IsUmm = true;
                            targetDir = Path.Combine(DefaultUMMDirectory, Manifest.UniqueName);
                        } else if (unzipDir.GetFiles()?.Where(file => file.Name == OwlcatModification.SettingsFileName)?.Any() ?? false) {
                            IsOwlcatTemplate = true;
                            targetDir = Path.Combine(DefaultOwlcatTemplateDirectory, Manifest.UniqueName);
                            var newList = SteamWorkshopIntegration.Instance.m_Settings.EnabledModifications.ToList();
                            if (!newList.Contains(Manifest.UniqueName)) {
                                newList.Add(Manifest.UniqueName);
                                SteamWorkshopIntegration.Instance.m_Settings.EnabledModifications = newList.ToArray();
                            }
                        } else {
                            // This is a case that won't be handled. I've heard of people trying to upload other workshop content like custom portraits; which would end up here
                        }
                    } else {
                        PFLog.Mods.Error($"Steam Workshop Item {Manifest.DisplayName} with Steam FileId {SteamFileId} has null or empty UniqueName and can't be installed");
                    }
                } else {
                    // Theoretically UMM mods should be able to run without a manifest, but since shipping without it is actually pretty hard to realize this will be ignored
                }
                if ((IsUmm || IsOwlcatTemplate) && !targetDir.IsNullOrEmpty()) {
                    CopyDirectoryRecursively(unzipDir, targetDir);
                    if (!isUpdate) WorkshopSettings = new();
                    WorkshopSettings.LocalTimestamp = SteamTimestamp;
                    WorkshopSettings.UniqueName = Manifest.UniqueName;
                    WorkshopSettings.SteamFileId = SteamFileId;
                    File.WriteAllText(Path.Combine(targetDir, WorkshopSettingsData.ModManagingInfoFileName), JsonUtility.ToJson(WorkshopSettings, true));
                }
            } catch (Exception ex) {
                PFLog.Mods.Exception(ex);
            } finally {
                if (unzipDir?.Exists ?? false) {
                    unzipDir.Delete(true);
                }
            }
        }

        public static void CopyDirectoryRecursively(DirectoryInfo dir, string target) {
            var targetDir = new DirectoryInfo(target);
            if (!targetDir.Exists) {
                targetDir.Create();
            }
            foreach (var file in dir.GetFiles()) {
                string path = Path.Combine(target, file.Name);
                if (File.Exists(path)) File.Delete(path);
                file.CopyTo(path);
            }
            foreach (var directory in dir.GetDirectories()) {
                CopyDirectoryRecursively(directory, Path.Combine(target, directory.Name));
            }
        }
        public class WorkshopSettingsData {
            // This file contains information about the latest installed version
            [JsonProperty]
            public const string ModManagingInfoFileName = "WorkshopManaged.json";
            [JsonProperty]
            public uint LocalTimestamp;
            [JsonProperty]
            public PublishedFileId_t SteamFileId;
            [JsonProperty]
            public string UniqueName;
        }
    }
}
