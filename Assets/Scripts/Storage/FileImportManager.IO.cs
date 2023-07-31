using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using ArcCreate.Data;
using ArcCreate.Storage.Data;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace ArcCreate.Storage
{
    /// <summary>
    /// Partial class for handling file IO while importing.
    /// </summary>
    public partial class FileImportManager : MonoBehaviour
    {
        public async UniTask ImportArchive(string path, bool isImportingDefaultPackage = false)
        {
            using (FileStream fs = File.Open(path, FileMode.Open, FileAccess.Read))
            {
                using (ZipArchive zip = new ZipArchive(fs, ZipArchiveMode.Read))
                {
                    Debug.Log("Importing package from " + path);
                    await ImportLevelArchive(zip, isImportingDefaultPackage);
                }
            }
        }

        public async UniTask ImportFromUri(string uri)
        {
            string tempPath = Path.Combine(FileStatics.TempPath, "importing.arcpkg");
            try
            {
                using (UnityWebRequest req = new UnityWebRequest(uri))
                {
                    await req.SendWebRequest();

                    byte[] data = req.downloadHandler.data;
                    using (FileStream fs = File.OpenWrite(tempPath))
                    {
                        fs.Write(data, 0, data.Length);
                    }
                }

                await ImportArchive(tempPath);
            }
            catch (Exception e)
            {
                Debug.LogError(e);
            }
            finally
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
        }

        public async UniTask ImportLevelArchive(ZipArchive archive, bool isImportingDefaultPackage = false)
        {
            ClearError();
            ClearSummary();

            if (!isImportingDefaultPackage)
            {
                ShowLoading(I18n.S("Storage.Loading.Archive"));
                await UniTask.NextFrame();
            }

            foreach (var entry in archive.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name))
                {
                    continue;
                }

                string path = Path.Combine(FileStatics.TempImportPath, entry.FullName.Replace("\\", "/"));
                if (!Directory.Exists(Path.GetDirectoryName(path)))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                }

                using (FileStream fs = File.OpenWrite(path))
                {
                    using (Stream zs = entry.Open())
                    {
                        zs.CopyTo(fs);
                    }
                }
            }

            try
            {
                await ImportDirectory(new DirectoryInfo(FileStatics.TempImportPath), isImportingDefaultPackage);
            }
            catch (Exception e)
            {
                if (Directory.Exists(FileStatics.TempImportPath))
                {
                    Directory.Delete(FileStatics.TempImportPath, true);
                }

                DisplayError("Archive file", e);
                print(e.Message + " " + e.StackTrace);
            }
        }

        private async UniTask ImportDirectory(DirectoryInfo dir, bool isImportingDefaultPackage)
        {
            var pendingConflicts = new List<IStorageUnit>();
            var identifierToAssetMap = new Dictionary<string, (IStorageUnit asset, DirectoryInfo directory)>();
            var toStore = new List<(IStorageUnit asset, DirectoryInfo directory)>();
            var toDelete = new List<IStorageUnit>();

            if (!isImportingDefaultPackage)
            {
                ShowLoading(I18n.S("Storage.Loading.ValidatePackage"));
                await UniTask.NextFrame();
            }

            List<(IStorageUnit asset, DirectoryInfo directory)> importingData = ReadItemsFromDirectory(dir);
            foreach (var (asset, directory) in importingData)
            {
                asset.IsDefaultAsset = isImportingDefaultPackage;
            }

            // Detect duplicate identifier within the same importing package file
            HashSet<string> ids = new HashSet<string>();
            foreach (var (asset, directory) in importingData)
            {
                string id = asset.Identifier;
                if (ids.Contains(id))
                {
                    throw new FileLoadException($"Invalid package: Duplicate identifier deteced within the same package: {id}");
                }

                ids.Add(id);
            }

            // Detect duplicate identifier with existing package (i.e import conflict)
            if (!isImportingDefaultPackage)
            {
                ShowLoading(I18n.S("Storage.Loading.CheckConflict"));
                await UniTask.NextFrame();
            }

            foreach (var (asset, directory) in importingData)
            {
                identifierToAssetMap.Add(asset.Identifier, (asset, directory));
                IStorageUnit conflict = asset.GetConflictingIdentifier();
                if (conflict != null)
                {
                    pendingConflicts.Add(conflict);
                }
                else
                {
                    toStore.Add((asset, directory));
                }
            }

            // Await confirmation
            bool hasConflict = pendingConflicts.Count >= 0;
            if (hasConflict)
            {
                foreach (var conflict in pendingConflicts)
                {
                    var (asset, directory) = identifierToAssetMap[conflict.Identifier];
                    bool shouldReplace =
                        asset.Version >= conflict.Version
                        || await PromptConflictToUser(asset, conflict);
                    if (shouldReplace)
                    {
                        toDelete.Add(conflict);
                        toStore.Add((asset, directory));
                        asset.IsDefaultAsset = conflict.IsDefaultAsset;
                    }
                    else
                    {
                        toStore.Remove((asset, directory));
                    }
                }
            }

            // There should be no issues importing now
            foreach (IStorageUnit item in toDelete)
            {
                if (!isImportingDefaultPackage)
                {
                    ShowLoading(I18n.S("Storage.Loading.DeleteAsset", item.Identifier));
                    await UniTask.NextFrame();
                }

                item.Delete();
            }

            foreach (var (asset, directory) in toStore)
            {
                if (!isImportingDefaultPackage)
                {
                    ShowLoading(I18n.S("Storage.Loading.StoreAsset", asset.Identifier));
                    await UniTask.NextFrame();
                }

                asset.Insert();
                MoveDirectory(directory, new DirectoryInfo(asset.GetParentDirectory()));
            }

            if (!isImportingDefaultPackage)
            {
                ShowLoading(I18n.S("Storage.Loading.Finalizing"));
                await UniTask.NextFrame();
            }

            if (Directory.Exists(FileStatics.TempPath))
            {
                Directory.Delete(FileStatics.TempPath, true);
            }

            storageData.NotifyStorageChange();

            if (!isImportingDefaultPackage)
            {
                ShowSummary(toStore.Select(((IStorageUnit asset, DirectoryInfo dir) tuple) => tuple.asset).ToList());
            }

            HideLoading();
        }

        private void MoveDirectory(DirectoryInfo from, DirectoryInfo to)
        {
            Stack<DirectoryInfo> stack = new Stack<DirectoryInfo>();
            stack.Push(from);
            while (stack.Count > 0)
            {
                DirectoryInfo dir = stack.Pop();
                foreach (var subdir in dir.EnumerateDirectories())
                {
                    stack.Push(subdir);
                }

                foreach (var file in dir.EnumerateFiles())
                {
                    string relativeToRoot = file.FullName.Substring(from.FullName.Length).TrimStart('/', '\\');
                    string target = Path.Combine(to.FullName, relativeToRoot);
                    if (!Directory.Exists(Path.GetDirectoryName(target)))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(target));
                    }

                    file.CopyTo(target);
                }
            }

            from.Delete(true);
        }

        /// <summary>
        /// Reads the directory and return all items along with their file references.
        /// </summary>
        /// <param name="parentDirectory">The directory to read from.</param>
        /// <returns>List of all importing assets and directory of the asset.</returnx>
        private List<(IStorageUnit asset, DirectoryInfo directory)> ReadItemsFromDirectory(DirectoryInfo parentDirectory)
        {
            // Read import info
            FileInfo importYaml = parentDirectory.EnumerateFiles().First(file => file.Name == ImportInformation.FileName);
            string yaml = File.ReadAllText(importYaml.FullName);
            var deserializer = new DeserializerBuilder()
                .WithNamingConvention(new CamelCaseNamingConvention())
                .IgnoreUnmatchedProperties()
                .Build();
            List<ImportInformation> imports = deserializer.Deserialize<List<ImportInformation>>(yaml);

            List<(IStorageUnit, DirectoryInfo)> importingData = new List<(IStorageUnit, DirectoryInfo)>();
            foreach (ImportInformation import in imports)
            {
                IStorageUnit storage = null;
                string settingsContent = File.ReadAllText(Path.Combine(parentDirectory.FullName, import.Directory, import.SettingsFile));

                try
                {
                    switch (import.Type)
                    {
                        case ImportInformation.LevelType:
                            storage = new LevelStorage()
                            {
                                Settings = deserializer.Deserialize<ProjectSettings>(settingsContent),
                            };
                            break;
                        case ImportInformation.PackType:
                            var packInfo = deserializer.Deserialize<PackStorage.PackImportInformation>(settingsContent);
                            storage = new PackStorage()
                            {
                                PackName = packInfo.PackName,
                                ImagePath = packInfo.ImagePath,
                                LevelIdentifiers = packInfo.LevelIdentifiers,
                            };
                            break;
                    }

                    if (!storage.ValidateSelf(out string reason))
                    {
                        throw new Exception($"Invalid package, Importing was skipped.\n{reason}");
                    }
                }
                catch (Exception e)
                {
                    DisplayError($"Asset ({import.Type}, {import.Identifier})", e);
                    continue;
                }

                if (storage != null)
                {
                    storage.Identifier = import.Identifier;
                    storage.Version = import.Version;
                    storage.AddedDate = DateTime.UtcNow;
                    DirectoryInfo root = new DirectoryInfo(Path.Combine(parentDirectory.FullName, import.Directory));
                    importingData.Add((storage, root));
                }
            }

            return importingData;
        }
    }
}