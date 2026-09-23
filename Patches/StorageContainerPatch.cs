using HarmonyLib;

namespace VisibleLockerInterior
{


    [HarmonyPatch(typeof(StorageContainer), nameof(StorageContainer.Awake))]
    internal class PatchStorageContainerAwake
    {
        [HarmonyPostfix]
        public static void Postfix(StorageContainer __instance)
        {
            Controller.UpdateInterior(__instance);

            //Refresh the interior display as soon as an item is added or removed.
            //Only the locker itself gets the live subscriptions: this patch runs for
            //every StorageContainer in the game (craft tables, storage cubes...), and
            //UpdateInterior early-returns for the ones that are not the locker.
            if (__instance.container != null && "Locker(Clone)" == __instance.prefabRoot.name)
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

    //[HarmonyPatch(typeof(ItemsContainer), nameof(ItemsContainer.OnResize))]
    //internal class PatchItemsContainerResize
    //{
    //    [HarmonyPostfix]
    //    public static void PostFix(ItemsContainer __instance)
    //    {
            
    //    }
    //}

}
