﻿using System;
using System.Collections.Generic;
using System.Linq;
using Unity.XRTools.Utils;
using Unity.XRTools.Utils.GUI;
using Unity.XRTools.Utils.Internal;
using UnityEngine;
using UnityEngine.SceneManagement;

#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;
#endif

namespace Unity.XRTools.ModuleLoader
{
    /// <summary>
    /// Core class for Module Loader package
    /// This is the entry point for loading modules and a hub for all callbacks and methods to access/control modules
    /// </summary>
    [ScriptableSettingsPath(SettingsPath)]
    public class ModuleLoaderCore : ScriptableSettings<ModuleLoaderCore>
    {
        [Flags]
        internal enum OverrideModes
        {
            Editor =   1 << 0,
            PlayMode = 1 << 1,
            Player =   1 << 2
        }

        [Flags]
        internal enum OverridePlatforms
        {
            Windows = 1 << 0,
            Mac     = 1 << 1,
            Linux   = 1 << 2,
            WebGL   = 1 << 3,
            Android = 1 << 4,
            IOS     = 1 << 5,
            Lumin   = 1 << 6,
            WSA     = 1 << 7,
            Unknown = 1 << 31
        }

        [Serializable]
        internal class PlatformOverride
        {
#pragma warning disable 649
            [SerializeField]
            ModuleLoaderSettingsOverride m_Settings;

            [FlagsProperty]
            [SerializeField]
            OverridePlatforms m_Platforms;

            [FlagsProperty]
            [SerializeField]
            OverrideModes m_Modes;
#pragma warning restore 649

            public ModuleLoaderSettingsOverride settings { get { return m_Settings; } }
            public OverridePlatforms platforms { get { return m_Platforms; } }
            public OverrideModes modes { get { return m_Modes; } }
        }

        /// <summary>
        /// Root folder for all assets automatically generated by ModuleLoader
        /// </summary>
        public const string ModuleLoaderAssetsFolder = "Assets/ModuleLoader";

        /// <summary>
        /// Default folder for user settings assets
        /// </summary>
        public const string UserSettingsFolder = ModuleLoaderAssetsFolder + "/UserSettings";

        /// <summary>
        /// Default folder for project settings assets
        /// </summary>
        public const string SettingsPath = ModuleLoaderAssetsFolder + "/Settings";

        const string k_ModuleParentName = "___MODULES___";
        const string k_InactiveParentName = "___INACTIVE_PARENT___";

        GameObject m_ModuleParent;
        GameObject m_InactiveParent;

#pragma warning disable 649
        [SerializeField]
        List<string> m_ExcludedTypes = new List<string>();

        [SerializeField]
        ModuleLoaderSettingsOverride m_SettingsOverride;

        [SerializeField]
        PlatformOverride[] m_PlatformOverrides = new PlatformOverride[0];
#pragma warning restore 649

        readonly List<IModule> m_Modules = new List<IModule>();

        internal readonly List<IModule> moduleUnloads = new List<IModule>();
        internal readonly List<IModuleBehaviorCallbacks> behaviorCallbackModules = new List<IModuleBehaviorCallbacks>();
        internal readonly List<IModuleSceneCallbacks> sceneCallbackModules = new List<IModuleSceneCallbacks>();
        internal readonly List<IModuleBuildCallbacks> buildCallbackModules = new List<IModuleBuildCallbacks>();
        internal readonly List<IModuleAssetCallbacks> assetCallbackModules = new List<IModuleAssetCallbacks>();

#if UNITY_EDITOR
        /// <summary>
        /// This is set to true before calling OnSceneOpening on all modules and back to false before calling OnSceneOpened on all modules
        /// </summary>
        public static bool isSwitchingScenes { get; private set; }

        /// <summary>
        /// This is set to true by ModuleLoaderCore before calling OnPreprocessBuild on all modules, and back to false in the first Editor update after the build completes
        /// </summary>
        public static bool isBuilding { get; private set; }

        /// <summary>
        /// Set this to true to prevent ModuleLoaderCore from calling OnSceneOpening and OnSceneOpened on modules
        /// </summary>
        public static bool blockSceneCallbacks { private get; set; }
#endif

        /// <summary>
        /// True if Module Loader is in the process of unloading modules
        /// </summary>
        public static bool isUnloadingModules { get; private set; }

        /// <summary>
        /// List of all currently active/loaded modules
        /// </summary>
        public List<IModule> modules { get { return m_Modules; } }

        internal List<string> excludedTypes
        {
            get
            {
                if (currentOverride != null)
                    return currentOverride.ExcludedTypes;

                return m_ExcludedTypes;
            }
        }

        internal ModuleLoaderSettingsOverride currentOverride
        {
            get
            {
                if (m_SettingsOverride)
                    return m_SettingsOverride;

                var currentPlatform = GetCurrentOverridePlatform();
                foreach (var platformOverride in m_PlatformOverrides)
                {
                    if ((platformOverride.platforms & currentPlatform) == 0)
                        continue;

                    var modes = platformOverride.modes;
                    if (!CheckCurrentMode(modes))
                        continue;

                    return platformOverride.settings;
                }

                return null;
            }
        }

        /// <summary>
        /// Invoked when modules are loaded, after all calls to LoadModule
        /// </summary>
        public event Action ModulesLoaded;

        /// <summary>
        /// True if modules have been loaded
        /// </summary>
        public bool ModulesAreLoaded { get; private set; }

        // Local method use only -- created here to reduce garbage collection. Collections must be cleared before use
        static readonly List<Type> k_ModuleTypes = new List<Type>();

        /// <summary>
        /// Get the current platform for the purposes of provider selection
        /// Ths will return the value of Application.platform, except in the editor in playmode if the Override Platform
        /// in Playmode setting is enabled. Then it will use the selected override platform.
        /// </summary>
        /// <returns>The runtime platform which should be used in provider selection</returns>
        public static RuntimePlatform GetCurrentPlatform()
        {
#if UNITY_EDITOR
            var settings = ModuleLoaderDebugSettings.instance;
            if (Application.isPlaying && settings.overridePlatformInPlaymode)
                return settings.playmodePlatformOverride;

            return Application.platform;
#else
            return Application.platform;
#endif
        }

        internal static bool CheckCurrentMode(OverrideModes modes)
        {
#if UNITY_EDITOR
            if (Application.isPlaying && (modes & OverrideModes.PlayMode) != 0)
                return true;

            if (!Application.isPlaying && (modes & OverrideModes.Editor) != 0)
                return true;
#else
            if ((modes & OverrideModes.Player) != 0)
                return true;
#endif

            return false;
        }

        internal static OverridePlatforms GetCurrentOverridePlatform()
        {
#if UNITY_EDITOR
            var activeBuildTarget = EditorUserBuildSettings.activeBuildTarget;
            switch (activeBuildTarget)
            {
                case BuildTarget.StandaloneOSX:
                    return OverridePlatforms.Mac;
                case BuildTarget.StandaloneWindows:
                    return OverridePlatforms.Windows;
                case BuildTarget.iOS:
                    return OverridePlatforms.IOS;
                case BuildTarget.Android:
                    return OverridePlatforms.Android;
                case BuildTarget.StandaloneWindows64:
                    return OverridePlatforms.Windows;
                case BuildTarget.WebGL:
                    return OverridePlatforms.WebGL;
#if !UNITY_2019_2_OR_NEWER
                case BuildTarget.StandaloneLinux:
                case BuildTarget.StandaloneLinuxUniversal:
#endif
                case BuildTarget.StandaloneLinux64:
                    return OverridePlatforms.Linux;
                case BuildTarget.Lumin:
                    return OverridePlatforms.Lumin;
                case BuildTarget.WSAPlayer:
                    return OverridePlatforms.WSA;
                default:
                    return OverridePlatforms.Unknown;
            }
#else

            var platform = Application.platform;
            switch (platform)
            {
                case RuntimePlatform.OSXEditor:
                    return OverridePlatforms.Mac;
                case RuntimePlatform.OSXPlayer:
                    return OverridePlatforms.Mac;
                case RuntimePlatform.WindowsPlayer:
                    return OverridePlatforms.Windows;
                case RuntimePlatform.WindowsEditor:
                    return OverridePlatforms.Windows;
                case RuntimePlatform.IPhonePlayer:
                    return OverridePlatforms.IOS;
                case RuntimePlatform.Android:
                    return OverridePlatforms.Android;
                case RuntimePlatform.LinuxPlayer:
                    return OverridePlatforms.Linux;
                case RuntimePlatform.LinuxEditor:
                    return OverridePlatforms.Linux;
                case RuntimePlatform.WebGLPlayer:
                    return OverridePlatforms.WebGL;
                case RuntimePlatform.Lumin:
                    return OverridePlatforms.Lumin;
                case RuntimePlatform.WSAPlayerX64:
                case RuntimePlatform.WSAPlayerX86:
                case RuntimePlatform.WSAPlayerARM:
                    return OverridePlatforms.WSA;
                default:
                    return OverridePlatforms.Unknown;
            }
#endif
        }

        /// <summary>
        /// Function called when all scriptable settings are loaded and ready for use
        /// </summary>
        protected override void OnLoaded()
        {
            // On first import, due to creation of ScriptableSettings assets, OnLoaded is called twice in a row
            UnloadModules();

#if UNITY_EDITOR
            isSwitchingScenes = false;

            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            EditorSceneManager.sceneOpening += OnSceneOpening;
#if UNITY_2018_2_OR_NEWER
            EditorSceneManager.activeSceneChangedInEditMode += OnActiveSceneChanged;
#endif
            EditorSceneManager.newSceneCreated += OnNewSceneCreated;
            EditorSceneManager.sceneOpened += OnSceneOpened;
            EditorApplication.update += EditorUpdate;
#endif
            SceneManager.sceneLoaded += OnSceneLoaded;
            SceneManager.sceneUnloaded += OnSceneUnloaded;
            SceneManager.activeSceneChanged += OnActiveSceneChanged;

            if (!Application.isPlaying)
                LoadModules();
        }

        void OnDisable()
        {
#if UNITY_EDITOR
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            EditorSceneManager.sceneOpening -= OnSceneOpening;
#if UNITY_2018_2_OR_NEWER
            EditorSceneManager.activeSceneChangedInEditMode -= OnActiveSceneChanged;
#endif
            EditorSceneManager.newSceneCreated -= OnNewSceneCreated;
            EditorSceneManager.sceneOpened -= OnSceneOpened;
            EditorApplication.update -= EditorUpdate;
#endif
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneUnloaded -= OnSceneUnloaded;
            SceneManager.activeSceneChanged -= OnActiveSceneChanged;
            UnloadModules();
        }

        /// <summary>
        /// Unload and then load modules
        /// </summary>
        public void ReloadModules()
        {
            UnloadModules();
            LoadModules();
        }

#if UNITY_EDITOR
        static void EditorUpdate()
        {
            // Build errors and canceled builds will skip OnPostProcessBuild and may even not recompile scripts,
            // so we have to check the isBuilding state here.
            if (isBuilding)
            {
                isBuilding = false;
                if (s_Instance != null)
                    s_Instance.ReloadModules();
            }
        }

        void OnSceneOpened(Scene scene, OpenSceneMode mode)
        {
            isSwitchingScenes = false;

            if (blockSceneCallbacks)
                return;

            if (isBuilding)
                return;

            if (mode == OpenSceneMode.Single)
                ReloadModules();

            foreach (var module in sceneCallbackModules)
            {
                try
                {
                    module.OnSceneOpened(scene, mode);
                }
                catch (Exception e)
                {
                    Debug.LogException(e);
                }
            }
        }

        void OnSceneOpening(string path, OpenSceneMode mode)
        {
            isSwitchingScenes = true;

            if (blockSceneCallbacks)
                return;

            if (isBuilding)
                return;

            foreach (var module in sceneCallbackModules)
            {
                try
                {
                    module.OnSceneOpening(path, mode);
                }
                catch (Exception e)
                {
                    Debug.LogException(e);
                }
            }

            if (mode == OpenSceneMode.Single)
                UnloadModules();
        }

        void OnNewSceneCreated(Scene scene, NewSceneSetup setup, NewSceneMode mode)
        {
            if (isBuilding)
                return;

            ReloadModules();

            foreach (var module in sceneCallbackModules)
            {
                try
                {
                    module.OnNewSceneCreated(scene, setup, mode);
                }
                catch (Exception e)
                {
                    Debug.LogException(e);
                }
            }
        }

        static void OnPlayModeStateChanged(PlayModeStateChange playModeStateChange)
        {
            if (s_Instance == null)
                return;

            switch (playModeStateChange)
            {
                case PlayModeStateChange.ExitingEditMode:
                    s_Instance.UnloadModules();
                    break;
                case PlayModeStateChange.EnteredEditMode:
                    s_Instance.ReloadModules();
                    break;
            }
        }

        /// <summary>
        /// Called by Unity before creating a Player build
        /// </summary>
        public static void OnPreprocessBuild()
        {
            isBuilding = true;

            if (s_Instance == null)
                return;

            foreach (var module in s_Instance.buildCallbackModules)
            {
                try
                {
                    module.OnPreprocessBuild();
                }
                catch (Exception e)
                {
                    Debug.LogException(e);
                }
            }
        }

        /// <summary>
        /// Called by Unity during a Player build for each scene included in the build
        /// </summary>
        public static void OnProcessScene(Scene scene)
        {
            if (s_Instance == null)
                return;

            foreach (var module in s_Instance.buildCallbackModules)
            {
                try
                {
                    module.OnProcessScene(scene);
                }
                catch (Exception e)
                {
                    Debug.LogException(e);
                }
            }
        }

        /// <summary>
        /// Called by Unity after creating a Player build
        /// </summary>
        public static void OnPostprocessBuild()
        {
            isBuilding = false;

            if (s_Instance == null)
                return;

            s_Instance.ReloadModules();

            foreach (var module in s_Instance.buildCallbackModules)
            {
                try
                {
                    module.OnPostprocessBuild();
                }
                catch (Exception e)
                {
                    Debug.LogException(e);
                }
            }
        }

        public static void OnWillCreateAsset(string path)
        {
            if (s_Instance == null)
                return;

            foreach (var module in s_Instance.assetCallbackModules)
            {
                module.OnWillCreateAsset(path);
            }
        }

        /// <summary>
        /// Called by Unity before assets will be saved to disk
        /// </summary>
        /// <param name="paths">An array of paths where assets will be saved</param>
        /// <returns>Paths where assets will be saved, possibly with some removed by modules, indicating they should
        /// not be saved</returns>
        public static string[] OnWillSaveAssets(string[] paths)
        {
            if (s_Instance == null)
                return paths;

            return s_Instance.assetCallbackModules.Aggregate(paths, (current, module) =>
            {
                var newPaths = paths;
                try
                {
                    newPaths = module.OnWillSaveAssets(current);
                }
                catch (Exception e)
                {
                    Debug.LogException(e);
                }

                return newPaths;
            });
        }

        /// <summary>
        /// Called by unity before an asset will be deleted
        /// </summary>
        /// <param name="path">The path to the asset</param>
        /// <param name="options">Options used to delete the asset</param>
        /// <returns>Deletion result, which can be used to suppress deletion of the asset</returns>
        public static AssetDeleteResult OnWillDeleteAsset(string path, RemoveAssetOptions options)
        {
            if (s_Instance == null)
                return AssetDeleteResult.DidDelete;

            return s_Instance.assetCallbackModules.Aggregate(AssetDeleteResult.DidNotDelete, (current, module) =>
            {
                try
                {
                    current |= module.OnWillDeleteAsset(path, options);
                }
                catch (Exception e)
                {
                    Debug.LogException(e);
                }

                return current;
            });
        }
#endif

        /// <summary>
        /// Invoke OnBehaviorAwake callback on all behavior callback modules
        /// </summary>
        public void OnBehaviorAwake()
        {
            foreach (var module in behaviorCallbackModules)
            {
                try
                {
                    module.OnBehaviorAwake();
                }
                catch (Exception e)
                {
                    Debug.LogException(e);
                }
            }

            // We inject functionality again when the app is started in case there are any providers
            // that didn't exist before scene analysis
            var fiModule = GetModule<FunctionalityInjectionModule>();
            if (fiModule != null)
                InjectFunctionalityInModules(fiModule.activeIsland);
        }

        /// <summary>
        /// Invoke OnBehaviorEnable callback on all behavior callback modules
        /// </summary>
        public void OnBehaviorEnable()
        {
            foreach (var module in behaviorCallbackModules)
            {
                try
                {
                    module.OnBehaviorEnable();
                }
                catch (Exception e)
                {
                    Debug.LogException(e);
                }
            }
        }

        /// <summary>
        /// Invoke OnBehaviorStart callback on all behavior callback modules
        /// </summary>
        public void OnBehaviorStart()
        {
            foreach (var module in behaviorCallbackModules)
            {
                try
                {
                    module.OnBehaviorStart();
                }
                catch (Exception e)
                {
                    Debug.LogException(e);
                }
            }
        }

        /// <summary>
        /// Invoke OnBehaviorUpdate callback on all behavior callback modules
        /// </summary>
        public void OnBehaviorUpdate()
        {
            foreach (var module in behaviorCallbackModules)
            {
                try
                {
                    module.OnBehaviorUpdate();
                }
                catch (Exception e)
                {
                    Debug.LogException(e);
                }
            }
        }

        /// <summary>
        /// Invoke OnBehaviorDisable callback on all behavior callback modules
        /// </summary>
        public void OnBehaviorDisable()
        {
            foreach (var module in behaviorCallbackModules)
            {
                try
                {
                    module.OnBehaviorDisable();
                }
                catch (Exception e)
                {
                    Debug.LogException(e);
                }
            }
        }

        /// <summary>
        /// Invoke OnBehaviorDestroy callback on all behavior callback modules
        /// </summary>
        public void OnBehaviorDestroy()
        {
            foreach (var module in behaviorCallbackModules)
            {
                try
                {
                    module.OnBehaviorDestroy();
                }
                catch (Exception e)
                {
                    Debug.LogException(e);
                }
            }
        }

        /// <summary>
        /// Inject functionality on all modules using the provided FunctionalityIsland
        /// </summary>
        /// <param name="island">The functionality island to use for functionality injection</param>
        public void InjectFunctionalityInModules(FunctionalityIsland island)
        {
            foreach (var module in m_Modules)
            {
                island.InjectFunctionalitySingle(module);
            }
        }

        void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            foreach (var module in sceneCallbackModules)
            {
                try
                {
                    module.OnSceneLoaded(scene, mode);
                }
                catch (Exception e)
                {
                    Debug.LogException(e);
                }
            }
        }

        void OnSceneUnloaded(Scene scene)
        {
            foreach (var module in sceneCallbackModules)
            {
                try
                {
                    module.OnSceneUnloaded(scene);
                }
                catch (Exception e)
                {
                    Debug.LogException(e);
                }
            }
        }

        void OnActiveSceneChanged(Scene oldScene, Scene newScene)
        {
            foreach (var module in sceneCallbackModules)
            {
                try
                {
                    module.OnActiveSceneChanged(oldScene, newScene);
                }
                catch (Exception e)
                {
                    Debug.LogException(e);
                }
            }
        }

        /// <summary>
        /// Add all types which implement IModule to a list
        /// </summary>
        /// <param name="moduleTypes">The list to which modules will be added</param>
        public static void GetModuleTypes(List<Type> moduleTypes)
        {
            typeof(IModule).GetImplementationsOfInterface(moduleTypes);
        }

        /// <summary>
        /// Load all modules, except those whose types are excluded by Module Loader settings
        /// </summary>
        public void LoadModules()
        {
            k_ModuleTypes.Clear();
            GetModuleTypes(k_ModuleTypes);
            k_ModuleTypes.RemoveAll(type => excludedTypes.Contains(type.FullName));
            LoadModulesWithTypes(k_ModuleTypes);
        }

        /// <summary>
        /// Unload  all modules
        /// </summary>
        public void UnloadModules()
        {
            isUnloadingModules = true;
            foreach (var module in moduleUnloads)
            {
                try
                {
                    module.UnloadModule();
                }
                catch (Exception e)
                {
                    Debug.LogException(e);
                }
            }

            foreach (var module in moduleUnloads)
            {
                var behavior = module as MonoBehaviour;
                if (behavior != null)
                    DestroyImmediate(behavior.gameObject);
            }

            ClearModules();
            isUnloadingModules = false;

            // Destroy immediate so we don't end up destroying the parent after modules are loaded
            DestroyImmediate(GetModuleParent());

            ModulesAreLoaded = false;
        }

        void ClearModules()
        {
            m_Modules.Clear();
            moduleUnloads.Clear();
            behaviorCallbackModules.Clear();
            sceneCallbackModules.Clear();
            buildCallbackModules.Clear();
            assetCallbackModules.Clear();
        }

        /// <summary>
        /// Get the module of the given type, if it has been loaded
        /// </summary>
        /// <typeparam name="T">The type of module</typeparam>
        /// <returns>The module, if it has been loaded, or the default value for that type</returns>
        public T GetModule<T>() where T : IModule
        {
            foreach (var module in m_Modules)
            {
                if (module is T)
                    return (T)module;
            }

            return default(T);
        }

        internal void LoadModulesWithTypes(List<Type> moduleTypes)
        {
#if UNITY_EDITOR
            if (isBuilding)
                return;
#endif

            var moduleParent = GetModuleParent();
            var hideFlags = ModuleLoaderDebugSettings.instance.moduleHideFlags;
            moduleParent.hideFlags = hideFlags;
            var moduleParentTransform = moduleParent.transform;

            ClearModules();
            var moduleOrder = new Dictionary<IModule, int>();
            var moduleUnloadOrder = new Dictionary<IModule, int>();
            var behaviorOrder = new Dictionary<IModule, int>();
            var sceneOrder = new Dictionary<IModule, int>();
            var buildOrder = new Dictionary<IModule, int>();
            var assetOrder = new Dictionary<IModule, int>();
            var names = new Dictionary<IModule, string>();
            var gameObjects = new List<GameObject>();
            foreach (var moduleType in moduleTypes)
            {
                IModule module;
                if (typeof(ScriptableSettingsBase).IsAssignableFrom(moduleType))
                {
                    module = (IModule)GetInstanceByType(moduleType);
                }
                else if (typeof(MonoBehaviour).IsAssignableFrom(moduleType))
                {
                    // Even without HideFlags, these objects won't show up in the hierarchy or get methods called on it
                    // in play mode because they are created too early
                    var go = new GameObject(moduleType.Name);
                    go.SetActive(false);
                    go.hideFlags = hideFlags;
                    go.transform.SetParent(moduleParentTransform);
                    module = (IModule)go.AddComponent(moduleType);
                    gameObjects.Add(go);
                }
                else
                {
                    module = (IModule)Activator.CreateInstance(moduleType);
                }

                if (module == null)
                {
                    Debug.LogError("Could not load module of type " + moduleType);
                    continue;
                }

                names[module] = moduleType.FullName;
                m_Modules.Add(module);
                moduleOrder[module] = 0;
                moduleUnloads.Add(module);
                moduleUnloadOrder[module] = 0;

                var behaviorModule = module as IModuleBehaviorCallbacks;
                if (behaviorModule != null)
                {
                    behaviorCallbackModules.Add(behaviorModule);
                    behaviorOrder[behaviorModule] = 0;
                }

                var sceneModule = module as IModuleSceneCallbacks;
                if (sceneModule != null)
                {
                    sceneCallbackModules.Add(sceneModule);
                    sceneOrder[sceneModule] = 0;
                }

                var buildModule = module as IModuleBuildCallbacks;
                if (buildModule != null)
                {
                    buildCallbackModules.Add(buildModule);
                    buildOrder[buildModule] = 0;
                }

                var assetModule = module as IModuleAssetCallbacks;
                if (assetModule != null)
                {
                    assetCallbackModules.Add(assetModule);
                    assetOrder[assetModule] = 0;
                }

                var attributes = (ModuleOrderAttribute[])moduleType.GetCustomAttributes(typeof(ModuleOrderAttribute), true);
                foreach (var attribute in attributes)
                {
                    if (attribute is ModuleBehaviorCallbackOrderAttribute)
                    {
                        if (behaviorModule != null)
                            behaviorOrder[behaviorModule] = attribute.order;
                    }
                    else if (attribute is ModuleSceneCallbackOrderAttribute)
                    {
                        if (sceneModule != null)
                            sceneOrder[sceneModule] = attribute.order;
                    }
                    else if (attribute is ModuleBuildCallbackOrderAttribute)
                    {
                        if (buildModule != null)
                            buildOrder[buildModule] = attribute.order;
                    }
                    else if (attribute is ModuleAssetCallbackOrderAttribute)
                    {
                        if (assetModule != null)
                            assetOrder[assetModule] = attribute.order;
                    }
                    else if (attribute is ModuleUnloadOrderAttribute)
                    {
                        moduleUnloadOrder[module] = attribute.order;
                    }
                    else
                    {
                        moduleOrder[module] = attribute.order;
                    }
                }
            }

            m_Modules.Sort(CreateComparison(moduleOrder, names));
            moduleUnloads.Sort(CreateComparison(moduleUnloadOrder, names));
            behaviorCallbackModules.Sort(CreateComparison(behaviorOrder, names));
            sceneCallbackModules.Sort(CreateComparison(sceneOrder, names));
            buildCallbackModules.Sort(CreateComparison(buildOrder, names));
            assetCallbackModules.Sort(CreateComparison(assetOrder, names));

            var interfaces = new List<Type>();
            var dependencyArg = new object[1];
            foreach (var module in m_Modules)
            {
                var type = module.GetType();
                interfaces.Clear();
                type.GetGenericInterfaces(typeof(IModuleDependency<>), interfaces);
                foreach (var dependency in m_Modules)
                {
                    foreach (var @interface in interfaces)
                    {
                        var dependencyType = dependency.GetType();
                        if (dependencyType.IsAssignableFrom(@interface.GetGenericArguments()[0]))
                        {
                            dependencyArg[0] = dependency;
                            try
                            {
                                @interface.GetMethod("ConnectDependency").Invoke(module, dependencyArg);
                            }
                            catch (Exception e)
                            {
                                Debug.LogException(e);
                            }
                        }
                    }
                }
            }

            var fiModule = GetModule<FunctionalityInjectionModule>();
            if (fiModule != null)
            {
                var providers = new List<IFunctionalityProvider>();
                foreach (var module in m_Modules)
                {
                    var item = module as IFunctionalityProvider;
                    if (item != null)
                        providers.Add(item);
                }

                fiModule.PreLoad();
                foreach (var island in fiModule.islands)
                {
                    island.AddProviders(providers);
                }

                var activeIsland = fiModule.activeIsland;
                foreach (var module in m_Modules)
                {
                    activeIsland.InjectFunctionalitySingle(module);
                }
            }

            foreach (var gameObject in gameObjects)
            {
                gameObject.SetActive(true);
            }

            foreach (var module in m_Modules)
            {
                try
                {
                    module.LoadModule();
                }
                catch (Exception e)
                {
                    Debug.LogException(e);
                }
            }

            if (ModuleLoaderDebugSettings.instance.functionalityInjectionModuleLogging && fiModule != null)
                Debug.Log(fiModule.PrintStatus());

            if (ModulesLoaded != null)
                ModulesLoaded();

            ModulesAreLoaded = true;
        }

        static Comparison<IModule> CreateComparison(Dictionary<IModule, int> orders, Dictionary<IModule, string> names)
        {
            return (a, b) =>
            {
                var result = orders[a].CompareTo(orders[b]);

                // Orders can change unpredictably as assembly is modified--use type name to ensure deterministic order
                return result != 0 ? result : names[a].CompareTo(names[b]);
            };
        }

        /// <summary>
        /// Get or create the GameObject to which all MonoBehaviour modules are added as children
        /// </summary>
        /// <returns>The module parent object</returns>
        public GameObject GetModuleParent()
        {
            if (m_ModuleParent)
                return m_ModuleParent;

            m_ModuleParent = GameObject.Find(k_ModuleParentName);
            if (!m_ModuleParent)
                m_ModuleParent = new GameObject(k_ModuleParentName);

            return m_ModuleParent;
        }

        public GameObject GetInactiveParent()
        {
            if (m_InactiveParent)
                return m_InactiveParent;

            var moduleParent = GetModuleParent().transform;

            var inactiveParentTransform = moduleParent.Find(k_InactiveParentName);
            if (inactiveParentTransform != null)
            {
                m_InactiveParent = inactiveParentTransform.gameObject;
            }
            else
            {
                m_InactiveParent = new GameObject(k_InactiveParentName);
                m_InactiveParent.transform.SetParent(moduleParent);
                m_InactiveParent.SetActive(false);
            }

            return m_InactiveParent;
        }
    }

    /// <summary>
    /// Extension methods for ModuleLoaderCore
    /// </summary>
    public static class ModuleLoaderCoreExtensionMethods
    {
        /// <summary>
        /// Ensures that functionality injection has been setup using the active ModuleLoaderCore island.
        /// </summary>
        /// <param name="user">User of functionality injection</param>
        public static void EnsureFunctionalityInjected(this IUsesFunctionalityInjection user)
        {
            var module = (ModuleLoaderCore.instance.GetModule<FunctionalityInjectionModule>());
            if ((module != null) && (module.activeIsland != null))
            {
                module.activeIsland.InjectFunctionalitySingle(user);
            }
        }
    }
}
