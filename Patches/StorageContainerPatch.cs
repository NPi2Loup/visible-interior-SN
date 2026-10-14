using BepInEx.Logging;
using System;
using HarmonyLib;
using UnityEngine;

namespace VisibleLockerInterior
{


    [HarmonyPatch(typeof(StorageContainer), nameof(StorageContainer.Awake))]
    internal class PatchStorageContainerAwake
    {
        //Reload diagnostics: after an in-game menu round-trip the second world load
        //produced NO UpdateInterior logs at all (neither Info nor per-name warnings).
        //Log the first Awake calls per process (plus any Locker* root) to tell
        //"patch dead" from "containers not re-instantiated" from "unexpected name".
        private static int awakeCallCount;

        [HarmonyPostfix]
        public static void Postfix(StorageContainer __instance)
        {
            int n = ++awakeCallCount;
            string rootName = __instance.prefabRoot != null ? __instance.prefabRoot.name : "<null prefabRoot>";
            if (n <= 20 || rootName.StartsWith("Locker", StringComparison.Ordinal))
            {
                Plugin.Log(LogLevel.Info,
                    $"VisibleLockerInterior: StorageContainer.Awake #{n}: '{rootName}' worldPos={(__instance.prefabRoot != null ? __instance.prefabRoot.transform.position : Vector3.zero)}");
            }

            Controller.UpdateInterior(__instance);

            //Refresh the interior display as soon as an item is added or removed.
            //Only the locker itself gets the live subscriptions: this patch runs for
            //every StorageContainer in the game (craft tables, storage cubes...), and
            //UpdateInterior early-returns for the ones that are not the locker.
            if (__instance.container != null && rootName.StartsWith("Locker", StringComparison.Ordinal))
            {
                //onAddItem fires AFTER the item has been parented under storageRoot,
                //so it is already visible to GetSortedItems.
                __instance.container.onAddItem += (item) => Controller.UpdateInterior(__instance);

                //onRemoveItem fires BEFORE the item is un-parented from storageRoot.
                //Exclude the removed item so its dummy is actually destroyed
                //(without this, the dummy of the last removed item stays visible).
                __instance.container.onRemoveItem += (item) =>
                    Controller.UpdateInterior(__instance, item?.item != null ? item.item.gameObject : null);
            }
        }
    }


        //OnClose is protected in the game assembly (Subnautica 1.22), so it cannot be
        //referenced with nameof() from here; patch it by name (Harmony resolves at runtime).
    [HarmonyPatch(typeof(StorageContainer), "OnClose")]
    internal class PatchCloseAction
    {
        [HarmonyPostfix]
        public static void Postfix(StorageContainer __instance) =>
            Controller.UpdateInterior(__instance);
    }

    //OnOpen (also protected, patch by name) rebuilds the interior on every open.
    //Recovery for the menu round-trip case: the surviving locker container never
    //re-Awakes after the second world load (world entities are reused, "found
    //existing ... at cell"), while the dummies were destroyed during the first
    //teardown (OnClose fired while the container's items were unparented). Opening
    //the locker re-runs UpdateInterior and rebuilds the dummies from the items.
    [HarmonyPatch(typeof(StorageContainer), "OnOpen")]
    internal class PatchOpenAction
    {
        [HarmonyPostfix]
        public static void Postfix(StorageContainer __instance) =>
            Controller.UpdateInterior(__instance);
    }

    //[HarmonyPatch(typeof(ItemsContainer), nameof(ItemsContainer.OnResize))]
    //internal class PatchItemsContainerResize
    //{
    //    [HarmonyPostfix]
    //    public static void PostFix(ItemsContainer __instance)
    //    {
            
    //    }
    //}

}
