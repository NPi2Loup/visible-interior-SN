using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using System.Reflection;
using UnityEngine;
using UnityEngine.SceneManagement;


namespace VisibleLockerInterior
{
    [BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
    public class Plugin : BaseUnityPlugin
    {

        public static void Log(LogLevel level, string message) =>
            MainLogger?.Log(level, message ?? "NULL");

        private static BepInEx.Logging.ManualLogSource? MainLogger;

        //public static ConfigEntry<bool> modEnabled;
        public Harmony Harmony { get; } = new Harmony(MyPluginInfo.PLUGIN_GUID);

        private bool _initialized;

        private void Awake()
        {
            //BepInEx 5 plugin hosts are NOT DontDestroyOnLoad. The game's menu
            //round-trip loads the "Cleaner" scene in Single mode, which used to
            //destroy this host on every save: OnDisable then unpatched every
            //Harmony patch and the mod went silent for the rest of the session
            //(log: zero mod lines after the first Cleaner, empty lockers after
            //the reload even though the base -- and its lockers -- are
            //re-instantiated). Keeping the host alive keeps the patches, the
            //sceneLoaded subscription and the rebuild coroutines alive.
            DontDestroyOnLoad(gameObject);
        }

        private void OnEnable()
        {
            MainLogger = this.Logger;

            //Idempotent: with a DDOL host OnEnable should fire exactly once,
            //but if it ever re-fires, re-patching would double-fire every
            //postfix.
            if (_initialized) return;
            _initialized = true;

            //modEnabled = Config.Bind<bool>("General", "Enabled", true, "Enable this mod");
            Harmony.PatchAll(Assembly.GetExecutingAssembly());
            Log(LogLevel.Info, $"{MyPluginInfo.PLUGIN_NAME} {MyPluginInfo.PLUGIN_VERSION} is loaded!");

            //Safety-net repair: when the game restores the main scene after a
            //save round-trip, rebuild the locker interiors (covers containers
            //that survive the round-trip without being re-Awake'd; the Awake
            //postfix is the primary path for re-instantiated bases).
            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        private void OnDisable()
        {
            //Deliberate no-op: Harmony patches are bytecode-level and must
            //persist across scene round-trips (BepInEx cleans them up at
            //application quit). Unpatching here is what silently killed the
            //mod after the first "Cleaner" wipe.
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode loadMode)
        {
            //The menu round-trip loads "Cleaner" (Single) to wipe the world,
            //then restores "Main" (Single) with the re-streamed world. That is
            //the cue that the base is back and the locker interiors may need a
            //rebuild.
            if (loadMode == LoadSceneMode.Single && scene.name == "Main")
                StartCoroutine(LockerDiagnostics.RebuildAfterReload());
        }
    }
}
