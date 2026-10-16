using BepInEx.Logging;
using System.Collections;
using UnityEngine;

namespace VisibleLockerInterior
{
    //Diagnostics + event-driven self-healing for the "locker shows no items
    //after a menu round-trip" bug. Primary repair path: the StorageContainer
    //Awake/OnOpen/OnClose Harmony postfixes (they fire whenever the base's
    //lockers are (re-)instantiated -- the game re-instantiates the base on
    //every save load). Safety net: RebuildAfterReload, started on
    //SceneManager.sceneLoaded("Main", Single), for containers that survive
    //the round-trip without being re-Awake'd. OnOpen/OnClose are logged with
    //the full container state so any desync stays visible in the log.
    //(A 5-second polling watchdog used to cover this -- FindObjectsOfType
    //every 5s is a scene-wide scan and could rebuild outside the game's flow
    //-- so it was removed in favour of the event.)
    internal static class LockerDiagnostics
    {
        private const string interiorName = "mod_LockerInterior";

        public static void LogOpenClose(StorageContainer sc, string evt)
        {
            try
            {
                Plugin.Log(LogLevel.Info, $"VisibleLockerInterior: {evt} fired: {Describe(sc)}");
            }
            catch (System.Exception ex)
            {
                Plugin.Log(LogLevel.Warning, $"VisibleLockerInterior: {evt} diagnostic failed: {ex.Message}");
            }
        }

        internal static string Describe(StorageContainer sc)
        {
            string rootName = sc.prefabRoot != null ? sc.prefabRoot.name : "<null prefabRoot>";
            bool containerNull = sc.container == null;
            bool storageRootNull = sc.storageRoot == null;
            int items = -1, dummies = -1;
            if (!storageRootNull)
            {
                var sr = sc.storageRoot.transform;
                items = sr != null ? sr.childCount : -1;
            }
            string interiorPart = "interior=-";
            if (sc.prefabRoot != null)
            {
                var interior = sc.prefabRoot.transform.Find(interiorName);
                dummies = interior != null ? interior.childCount : -1;
                if (interior != null)
                {
                    //Existence vs invisibility: after a menu round-trip the dummies may
                    //still exist but be hidden (interior deactivated, dummy deactivated,
                    //or displaced). Report the first dummy's visibility + world position.
                    string d0 = "none";
                    if (interior.childCount > 0)
                    {
                        var d0t = interior.GetChild(0);
                        d0 = $"'{d0t.name}' active={d0t.gameObject.activeInHierarchy} wp={d0t.position}";
                    }
                    interiorPart = $"interiorActive={interior.gameObject.activeInHierarchy} d0={d0} lockerWp={sc.prefabRoot.transform.position}";
                }
            }
            return $"'{rootName}' open={sc.GetOpen()} containerNull={containerNull} storageRootNull={storageRootNull} items={items} dummies={dummies} {interiorPart}";
        }

        //Safety-net rebuild for the save round-trip (menu -> save -> reload).
        //The "Main" scene (Single) load fires long before the world re-stream
        //finishes -- the base re-instantiation takes ~10-30s after the scene
        //load -- so poll instead of using one fixed delay: rebuild every
        //locker interior as soon as at least one locker container exists, and
        //give up after 60s. The Awake postfix is the primary path (it fires
        //when the base's lockers are re-instantiated); this covers containers
        //that survive the round-trip without being re-Awake'd. It logs the
        //pre-rebuild state so a desync that this misses stays visible.
        public static IEnumerator RebuildAfterReload()
        {
            const int attempts = 12; //12 x 5s = up to 60s
            for (int attempt = 1; attempt <= attempts; attempt++)
            {
                yield return new WaitForSeconds(5f);
                try
                {
                    var containers = Object.FindObjectsOfType<StorageContainer>();
                    int count = 0;
                    foreach (var sc in containers)
                    {
                        if (sc == null || sc.prefabRoot == null || sc.container == null || sc.storageRoot == null) continue;
                        if (!sc.prefabRoot.name.StartsWith("Locker", System.StringComparison.Ordinal)) continue;
                        count++;
                        Plugin.Log(LogLevel.Info, $"VisibleLockerInterior: sceneLoaded rebuild: {Describe(sc)}");
                        Controller.UpdateInterior(sc);
                    }
                    if (count > 0)
                    {
                        Plugin.Log(LogLevel.Info, $"VisibleLockerInterior: sceneLoaded rebuild: {count} locker(s) rebuilt (attempt {attempt}/{attempts})");
                        yield break;
                    }
                    Plugin.Log(LogLevel.Info, $"VisibleLockerInterior: sceneLoaded rebuild: no locker found yet (attempt {attempt}/{attempts})");
                }
                catch (System.Exception ex)
                {
                    Plugin.Log(LogLevel.Warning, $"VisibleLockerInterior: sceneLoaded rebuild error: {ex.Message}");
                }
            }
        }

    }
}
