using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading.Tasks;
using UnityEngine;
using HarmonyLib;
using MelonLoader;
using ABI_RC.Core.IO;
using ABI_RC.Core.InteractionSystem;
using ABI.CCK.Scripts;

using CacheManager;
using CacheManager.Tasks;

[assembly: MelonInfo(typeof(CacheManagerMod), "CacheManager", "1.0.0", "DragonPlayer", "https://github.com/DragonPlayerX/CacheManager")]
[assembly: MelonGame("Alpha Blend Interactive", "ChilloutVR")]

namespace CacheManager
{
    public class CacheManagerMod : MelonMod
    {
        public static readonly string Version = "1.0.0";

        public static readonly string[] CacheFolders = new string[] { "Avatars", "Worlds", "Spawnables" };

        public static CacheManagerMod Instance { get; private set; }
        public static MelonLogger.Instance Logger => Instance.LoggerInstance;

        public static MelonPreferences_Category Category;
        public static MelonPreferences_Entry<string> CacheDirectory;
        public static MelonPreferences_Entry<int> MaxSizeGB;
        public static MelonPreferences_Entry<bool> DebugLog;
        public static bool PreferencesSaved;

        private static MethodInfo getUnityDataPathMethod;
        private static MethodInfo getCachePathMethod;
        private static MethodInfo checkAvailabilityMethod;
        private static FieldInfo downloadIsActiveField;

        private static Stopwatch stopwatch = new Stopwatch();

        public override void OnApplicationStart()
        {
            Instance = this;
            Logger.Msg($"Initializing CacheManager {Version}...");

            Category = MelonPreferences.CreateCategory("CacheManager", "Cache Manager");
            CacheDirectory = Category.CreateEntry("CacheDirectory", Application.dataPath, "Cache Directory");
            MaxSizeGB = Category.CreateEntry("MaxSizeGB", 20, "Max Size (GB)");
            DebugLog = Category.CreateEntry("DebugLog", false, "Debug Log");

            getUnityDataPathMethod = typeof(Application).GetMethod("get_dataPath", BindingFlags.Public | BindingFlags.Static);
            getCachePathMethod = typeof(CacheManagerMod).GetMethod(nameof(GetCachePath), BindingFlags.NonPublic | BindingFlags.Static);
            checkAvailabilityMethod = typeof(CVRDownloadManager).GetMethod("CheckAvailability", BindingFlags.NonPublic | BindingFlags.Instance);
            downloadIsActiveField = typeof(CVRDownloadManager).GetField("_downloadIsActive", BindingFlags.NonPublic | BindingFlags.Instance);

            HarmonyInstance.Patch(typeof(DownloadManagerHelperFunctions).GetMethod(nameof(DownloadManagerHelperFunctions.GetAppDatapath)),
                new HarmonyMethod(typeof(CacheManagerMod).GetMethod(nameof(CachePathPatch), BindingFlags.NonPublic | BindingFlags.Static)));
            Logger.Msg("Patched GetAppDatapath method.");

            HarmonyInstance.Patch(typeof(CVRPortalManager).GetMethod("WriteData", BindingFlags.NonPublic | BindingFlags.Instance),
                new HarmonyMethod(typeof(CacheManagerMod).GetMethod(nameof(WorldImagePatch), BindingFlags.NonPublic | BindingFlags.Static)));
            Logger.Msg("Patched WriteData method.");

            HarmonyInstance.Patch(typeof(CVRAdvancedAvatarSettings).GetMethod("LoadAvatarProfiles", BindingFlags.Public | BindingFlags.Instance),
                transpiler: new HarmonyMethod(typeof(CacheManagerMod).GetMethod(nameof(ApplicationDataPathTranspiler), BindingFlags.NonPublic | BindingFlags.Static)));
            Logger.Msg("Executed transpiler for LoadAvatarProfiles.");

            HarmonyInstance.Patch(typeof(CVRAdvancedAvatarSettings).GetMethod("WriteCurrentSettingsToFile", BindingFlags.NonPublic | BindingFlags.Instance),
                transpiler: new HarmonyMethod(typeof(CacheManagerMod).GetMethod(nameof(ApplicationDataPathTranspiler), BindingFlags.NonPublic | BindingFlags.Static)));
            Logger.Msg("Executed transpiler for WriteCurrentSettingsToFile.");

            HarmonyInstance.Patch(typeof(CVRDownloadManager).GetNestedTypes(BindingFlags.NonPublic).First(t => t.Name.Contains("DownloadFile")).GetMethod("MoveNext", BindingFlags.NonPublic | BindingFlags.Instance),
                transpiler: new HarmonyMethod(typeof(CacheManagerMod).GetMethod(nameof(DownloadFileTranspiler), BindingFlags.NonPublic | BindingFlags.Static)));
            Logger.Msg("Executed transpiler for DownloadFile.");

            Logger.Msg($"Running version {Version} of CacheManager.");
        }

        public override void OnUpdate() => TaskProvider.MainThreadQueue.Dequeue();

        public override void OnSceneWasLoaded(int buildIndex, string sceneName)
        {
            if (buildIndex != -1 || PreferencesSaved)
                return;

            PreferencesSaved = true;
            MelonCoroutines.Start(SavePreferencesLater());
        }

        private static IEnumerator SavePreferencesLater()
        {
            yield return null;
            Category.SaveToFile(false);
            Logger.Msg("Saved preferences.");
        }

        private static bool CachePathPatch(ref string __result)
        {
            __result = GetCachePath();
            return false;
        }

        private static void WorldImagePatch(CVRPortalManager __instance)
        {
            __instance.LocalPanoPath = $"{GetCachePath()}/Worlds/{__instance.Portal.WorldId}_pano_{__instance.PanoResolution}.png";
        }

        private static IEnumerable<CodeInstruction> ApplicationDataPathTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            foreach (CodeInstruction instruction in instructions)
            {
                if (instruction.Calls(getUnityDataPathMethod))
                    yield return new CodeInstruction(OpCodes.Call, getCachePathMethod);
                else
                    yield return instruction;
            }
        }

        private static IEnumerable<CodeInstruction> DownloadFileTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            CodeInstruction lastInstruction = null;
            foreach (CodeInstruction instruction in instructions)
            {
                if (instruction.StoresField(downloadIsActiveField) && (lastInstruction?.opcode.Equals(OpCodes.Ldc_I4_0) ?? false))
                    yield return new CodeInstruction(OpCodes.Call, typeof(CacheManagerMod).GetMethod(nameof(CheckCache), BindingFlags.NonPublic | BindingFlags.Static));
                else
                    yield return instruction;

                lastInstruction = instruction;
            }
        }

        private static void CheckCache()
        {
            Task.Run(() =>
            {
                if (MaxSizeGB.Value <= 0)
                    return;

                stopwatch.Restart();

                IEnumerable<FileInfo> files = new DirectoryInfo(GetCachePath()).EnumerateFiles("*", SearchOption.AllDirectories).Where(f => CacheFolders.Any(f.DirectoryName.EndsWith)).OrderBy(f => f.LastWriteTime);

                long totalSize = files.Sum(f => f.Length);
                long overflow = totalSize - (MaxSizeGB.Value * 1024L * 1024L * 1024L);

                if (overflow > 0)
                {
                    long removed = 0;

                    foreach (FileInfo file in files)
                    {
                        overflow -= file.Length;
                        removed += file.Length;

                        File.Delete(file.FullName);

                        if (DebugLog.Value)
                            Logger.Msg($"Deleted {file.FullName}");

                        if (overflow <= 0)
                            break;
                    }

                    if (DebugLog.Value)
                        Logger.Msg($"Removed {removed / 1024 / 1024}mb in {stopwatch.ElapsedMilliseconds}ms from cache (New size is {(totalSize - removed) / 1024 / 1024}mb)");
                }

                stopwatch.Stop();
            }).NoAwait(async () =>
            {
                await TaskProvider.YieldToMainThread();

                downloadIsActiveField.SetValue(CVRDownloadManager.Instance, false);
                checkAvailabilityMethod.Invoke(CVRDownloadManager.Instance, new object[0]);
            });
        }

        private static string GetCachePath() => CacheDirectory.Value.TrimEnd(new char[] { '/', '\\' });
    }
}
