﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.Contracts;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Policy;
using System.Text;
using System.Threading.Tasks;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Logging;
using HarmonyLib;
using LCModSync.Patches;
using Mono.Cecil;
using UnityEngine;
using UnityEngine.Windows;
using System.Web;
using Newtonsoft.Json;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.ComponentModel;

namespace LCModSync
{
    [BepInPlugin(modGUID, modName, modVersion)]
    public class ModSyncPlugin : BaseUnityPlugin
    {
        private const string modGUID = "Posiedon.ModSync";
        private const string modName = "Lethal Company ModSync";
        private const string modVersion = "0.0.2";

        private readonly Harmony harmony = new Harmony(modGUID);

        internal static ManualLogSource mls;

        //internal static List<PluginInfo> plugins;

        internal static List<string> modURLs;
        internal static List<string> modNames;

        public static ModSyncPlugin Instance;

        public string ScriptDirectory => Path.Combine(Paths.BepInExRootPath, "scripts");
        private GameObject scriptManager;
        static HttpClient client = new HttpClient();

        private static string zipPath;
        private static string outputPath;

        void Awake()
        {
            this.gameObject.hideFlags = UnityEngine.HideFlags.HideAndDontSave;
            if(Instance == null)
            {
                Instance = this;
            }

            mls = BepInEx.Logging.Logger.CreateLogSource(modGUID);
            mls.LogInfo($"Loaded {modName}, patching.");
            harmony.PatchAll(typeof(GameNetworkManagerPatch));
            harmony.PatchAll(typeof(LobbySlotPatch));
            System.IO.Directory.CreateDirectory(".\\BepInEx\\scripts");
            System.IO.Directory.CreateDirectory(".\\BepInEx\\downloads");

            modURLs = new List<string>();
            modNames = new List<string>();

            getPlugins();
            mls.LogInfo(String.Join(" ", modURLs));

            downloadMods("2018", "LC_API");

        }

        static void getPlugins()
        {
            mls.LogInfo("Checking for plugins");
            foreach (var plugin in Chainloader.PluginInfos)
            {
                try
                {
                    plugin.Value.Instance.BroadcastMessage("modURL", Instance, UnityEngine.SendMessageOptions.DontRequireReceiver);
                }
                catch (Exception e)
                {
                    // ignore mod if they haven't implemented the necessary method
                    mls.LogInfo($"Failed to gather details about {plugin.Value.Metadata.Name}. Skipping.");
                }
            }

        }

        static string getModURLFromRequest(string inputData)
        {
            string attribute = "download_url";

            if (!inputData.Contains(attribute))
            {
                return "";
            }
            int first = inputData.IndexOf(attribute) + attribute.Length;

            List<Char> termsList = new List<Char>();
            int foundBreaks = 0;

            for (int i = 0; i < 100; i++)
            {
                if (inputData[first + i].ToString() == "\"")
                {
                    foundBreaks++;
                }
                else
                {
                    if (foundBreaks == 2)
                    {
                        termsList.Add(inputData[first + i]);
                    }
                    if (foundBreaks >= 3)
                    {
                        break;
                    }
                }
            }

            string finalStr = "";

            foreach (char i in termsList)
            {
                finalStr += i.ToString();
            }

            return finalStr;
        }

        internal static void downloadMods(string modCreator = "", string modName = "")
        {
            // we can also test more logic here, a hacker may be able to mod their own thing to inject their own files,
            // but unless they distribute malicious copies of this mod, they can't bypass checks here
            // doesn't bother checking if mods exist yet


            //mls.LogInfo("We are trying to get the info, I swear");

            // dude I tried parsing this into json like 20 different ways
            // I give up, time to get the URL from the string ffs
            string requestBuilder = $"{modCreator}/{modName}/";
            string modData = getModInfoFromStore(requestBuilder);


            //mls.LogInfo(getModURLFromRequest(modData));

            string finalModURL = getModURLFromRequest(modData);

            Uri uri = new Uri(finalModURL);
            if (uri.Host != "www.thunderstore.io" ^ uri.Host != "https://thunderstore.io")
            {
                mls.LogInfo("Potentially bad link detected, ignoring download");
                return;
            }

            mls.LogInfo("It was a thunderstore link so time to download >:)");

            using (WebClient wc = new WebClient())
            {
                wc.DownloadFileCompleted += onDownloadComplete;
                wc.DownloadFileAsync(
                    new System.Uri(finalModURL),
                    // name of file, aka where it will go
                    ".\\Bepinex\\downloads\\" + $"{modName}.zip"
                );
            }

            // now that the mod was downloaded, we need to extract it since it is a zip
            zipPath = Path.GetFullPath(".\\Bepinex\\downloads\\" + $"{modName}.zip");
            outputPath = Path.GetFullPath(".\\Bepinex\\scripts\\");

            if (!outputPath.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal))
                outputPath += Path.DirectorySeparatorChar;               
        }

        private static void onDownloadComplete(object sender, AsyncCompletedEventArgs e)
        {
            extractCurrentMod();
        }

        private static void extractCurrentMod()
        {
            try
            {
                using (ZipArchive archive = ZipFile.OpenRead(zipPath))
                {

                    foreach (ZipArchiveEntry entry in archive.Entries)
                    {
                        if (entry.FullName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                        {
                            //File.Create(outputPath + entry.FullName);
                            mls.LogInfo(entry.FullName);
                            // Gets the full path to ensure that relative segments are removed.
                            //string destinationPath = Path.GetFullPath(Path.Combine(outputPath, entry.FullName));

                            // Ordinal match is safest, case-sensitive volumes can be mounted within volumes that are case-insensitive.
                            if (outputPath.StartsWith(outputPath, StringComparison.Ordinal))
                                entry.ExtractToFile(outputPath + entry.Name);
                            mls.LogInfo("trying to extract buckaroo");
                        }
                    }

                }
            }
            catch(Exception e)
            {
                mls.LogInfo($"Extraction failed: {e.Message}");
            }



            try
            {
                File.Delete(zipPath);
            }
            catch
            {
                mls.LogInfo("already gone");
            }
        }


        static internal void storeModInfo(string modURL, string modName)
        {
            // we can do more checking here for security in the future. E.g. if it's not a certain type of url ignore it
            try
            {
                if (modURLs.Contains(modURL))
                {
                    return;
                }
                else
                {
                    modNames.Add(modName);
                    modURLs.Add(modURL);
                }
            }
            catch
            {
                mls.LogInfo("getModInfo failed");
            }

        }

        public void getModURLandName(string url, string modname)
        {
            /* After sending a message to a mod to ask for their information, they should be calling this method
             * Upon calling this method, they should include a string for the URL of the mod, and a string for the name formatted as name.dll
             * nonstatic, can be called from external area
             */

            mls.LogInfo($"We have received {url}");
            storeModInfo(url, modname);
        }

        private IEnumerable<Type> GetTypesSafe(Assembly ass)
        {
            try
            {
                return ass.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                var sbMessage = new StringBuilder();
                sbMessage.AppendLine("\r\n-- LoaderExceptions --");
                foreach (var l in ex.LoaderExceptions)
                    sbMessage.AppendLine(l.ToString());
                sbMessage.AppendLine("\r\n-- StackTrace --");
                sbMessage.AppendLine(ex.StackTrace);
                Logger.LogError(sbMessage.ToString());
                return ex.Types.Where(x => x != null);
            }
        }

        private IEnumerator DelayAction(Action action)
        {
            yield return null;
            action();
        }

        private static IEnumerator DelayActionStatic(Action action)
        {
            yield return null;
            action();
        }

        internal void ReloadPlugins()
        {
            if (scriptManager != null)
            {

                foreach (var previouslyLoadedPlugin in scriptManager.GetComponents<BaseUnityPlugin>())
                {
                    var metadataGUID = previouslyLoadedPlugin.Info.Metadata.GUID;
                    if (Chainloader.PluginInfos.ContainsKey(metadataGUID))
                        Chainloader.PluginInfos.Remove(metadataGUID);
                }

                Destroy(scriptManager);
            }

            scriptManager = new GameObject($"ScriptEngine_{DateTime.Now.Ticks}");
            DontDestroyOnLoad(scriptManager);

            var files = Directory.GetFiles(ScriptDirectory, "*.dll", SearchOption.AllDirectories);
            if (files.Length > 0)
            {
                foreach (string path in Directory.GetFiles(ScriptDirectory, "*.dll", SearchOption.AllDirectories))
                    LoadDLL(path, scriptManager);

            }
            else
            {

            }
        }

        private void LoadDLL(string path, GameObject obj)
        {
            var defaultResolver = new DefaultAssemblyResolver();
            defaultResolver.AddSearchDirectory(ScriptDirectory);
            defaultResolver.AddSearchDirectory(Paths.ManagedPath);
            defaultResolver.AddSearchDirectory(Paths.BepInExAssemblyDirectory);


            using (var dll = AssemblyDefinition.ReadAssembly(path, new ReaderParameters { AssemblyResolver = defaultResolver }))
            {
                //dll.Name.Name = $"{dll.Name.Name}-{DateTime.Now.Ticks}";

                using (var ms = new MemoryStream())
                {
                    dll.Write(ms);
                    var ass = Assembly.Load(ms.ToArray());

                    foreach (Type type in GetTypesSafe(ass))
                    {
                        
                        try
                        {
                            
                            if (!typeof(BaseUnityPlugin).IsAssignableFrom(type)) continue;

                            var metadata = MetadataHelper.GetMetadata(type);
                            if (metadata == null) continue;

                            if (Chainloader.PluginInfos.TryGetValue(metadata.GUID, out var existingPluginInfo))
                                throw new InvalidOperationException($"A plugin with GUID {metadata.GUID} is already loaded! ({existingPluginInfo.Metadata.Name} v{existingPluginInfo.Metadata.Version})");

                            var typeDefinition = dll.MainModule.Types.First(x => x.FullName == type.FullName);
                            var pluginInfo = Chainloader.ToPluginInfo(typeDefinition);
                        

                            StartCoroutine(DelayAction(() =>
                            {
                                try
                                {
                                    // Need to add to PluginInfos first because BaseUnityPlugin constructor (called by AddComponent below)
                                    // looks in PluginInfos for an existing PluginInfo and uses it instead of creating a new one.
                                    Chainloader.PluginInfos[metadata.GUID] = pluginInfo;

                                    var instance = obj.AddComponent(type);

                                    // Fill in properties that are normally set by Chainloader
                                    var tv = Traverse.Create(pluginInfo);
                                    tv.Property<BaseUnityPlugin>(nameof(pluginInfo.Instance)).Value = (BaseUnityPlugin)instance;
                                    // Loading the assembly from memory causes Location to be lost
                                    tv.Property<string>(nameof(pluginInfo.Location)).Value = path;
                                }
                                catch (Exception e)
                                {
                                    Logger.LogError($"Failed to load plugin {metadata.GUID} because of exception: {e}");
                                    Chainloader.PluginInfos.Remove(metadata.GUID);
                                }
                            }));
                        }
                        catch (Exception e)
                        {
                            Logger.LogError($"Failed to load plugin {type.Name} because of exception: {e}");
                        }
                    }
                }
            }
        }

        static string getModInfoFromStore(string packagename)
        {
            //client.BaseAddress = new Uri("https://thunderstore.io/api/docs/?format=openapi");
            client.DefaultRequestHeaders.Accept.Clear();
            client.DefaultRequestHeaders.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/json"));

            string requestBuild = "https://thunderstore.io/api/experimental/package/" + packagename;

            try
            {
                var result = client.GetAsync(requestBuild).Result;
                var json = result.Content.ReadAsStringAsync().Result;
                return json;
            }
            catch
            {
                return "exception occurred";
            }
        }

    }
}
