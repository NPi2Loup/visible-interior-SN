using BepInEx.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace VisibleLockerInterior
{
    internal class Controller
    {
        private const int BaselineItemsPerShelf = 8;
        private const string interiorName = "mod_LockerInterior";
        private const int Shelves = 6;
        private const float ShelfWidth = 1.072f;  //Will not change regardless of changes to number of slots in locker.

        private static int totalSlots = 48;
        //Shelves are at varying positions--they aren't consistent enough to use mathematics to calculate.
        private static readonly float[] ShelfPosition =
            { 1.58803f, 1.32938f, 0.966707f, 0.57335f, 0.288304f, 0.046258f };

        private const float zPos = -0.03f;

        //Placement measurement caches, filled once per item type (classId) per process.
        //colliderBoundsCache: the item footprint as the game defines it (diagnostics/pinning data).
        //rendererBoundsCache: the visible geometry extent (the dynamic placement fallback).
        private static readonly Dictionary<string, Bounds> colliderBoundsCache = new Dictionary<string, Bounds>();
        private static readonly Dictionary<string, Bounds> rendererBoundsCache = new Dictionary<string, Bounds>();
        private static readonly HashSet<string> boundsLogged = new HashSet<string>();

        // exclude: GameObject of an item that is being removed from the container right now.
        // The game fires onRemoveItem BEFORE the item is un-parented from storageRoot,
        // so without this exclusion its dummy would never be removed (the "last item
        // remains visible" bug reported on the Nexus page).
        public static void UpdateInterior(StorageContainer sc, GameObject? exclude = null)
        {
            if ("Locker(Clone)" != sc.prefabRoot.name) return;
            if (sc.container == null || sc.storageRoot == null) return;

            var storageRoot = sc.storageRoot.gameObject;
            var interior = GetInteriorInstance(sc);
            var items = GetSortedItems(storageRoot, exclude);
            var dummies = GetSortedDummies(interior);

            Plugin.Log(LogLevel.Info,
                $"VisibleLockerInterior: UpdateInterior called for {sc.prefabRoot.name}. " +
                $"Storage Container height={sc.height}, width={sc.width}, internal container size={sc.container.sizeX}x{sc.container.sizeY}. " +
                $"storageRoot children={storageRoot.transform.childCount}, items={items.Count}, dummies={dummies.Count}");
            if (items.Count > totalSlots)
                Plugin.Log(LogLevel.Warning,
                    $"VisibleLockerInterior: {items.Count} stored items exceed totalSlots={totalSlots}; items with the highest TechType will not be displayed.");

            //Original hard-coded to 8 items on a row, but the storage has only 6 items on a row.
            //However, the visual of the free-standing locker shows only 6 rows, not 8, so to be able
            // to render 48 items, each shelf must have 8 items on it.
            int itemsPerShelf = totalSlots / Shelves;

            float itemRowSpacing = ShelfWidth / itemsPerShelf;

            for (int i = 0, j = 0; (i < items.Count && i < totalSlots) || (j < dummies.Count && j < totalSlots);)
            {
                float x = -((i % itemsPerShelf) * itemRowSpacing - (ShelfWidth / 2 - itemRowSpacing / 2));

                float y = ShelfPosition[(i / itemsPerShelf)];

                var targetPosition =
                    new Vector3(x, y, zPos);
                int cmp =
                    i == items.Count ? 1 :
                    j == dummies.Count ? -1 :
                    CompareTechType(
                        GetItemTechType(items[i]),
                        GetDummyTechType(dummies[j])
                        );
                if (cmp == -1)
                {
                    var dummy = CreateDummy(interior, items[i]);
                    if (dummy != null)
                    {
                        RepositionDummy(dummy, targetPosition, itemsPerShelf);
                    }
                    i++;
                }
                else if (cmp == 0)
                {
                    RepositionDummy(dummies[j].gameObject, targetPosition, itemsPerShelf);
                    i++; j++;
                }
                else
                {
                    Plugin.Log(LogLevel.Debug, $"VisibleLockerInterior: destroying stale dummy '{dummies[j].name}' (techType={GetDummyTechType(dummies[j])}).");
                    GameObject.Destroy(dummies[j].gameObject);
                    j++;
                }
            }
        }

        private static GameObject? CreateDummy(GameObject interior, GameObject src)
        {
            var techType = GetItemTechType(src);
            var classId = src.GetComponent<PrefabIdentifier>()?.ClassId ?? "";

            if (techType == TechType.None && string.IsNullOrEmpty(classId))
            {
                Plugin.Log(LogLevel.Warning, $"VisibleLockerInterior: skipping '{src.name}' -- no TechType and no ClassId, cannot track it.");
                return null;
            }

            GameObject dummy;
            try
            {
                dummy = GameObject.Instantiate(src, interior.transform);
            }
            catch (Exception ex)
            {
                Plugin.Log(LogLevel.Warning, $"VisibleLockerInterior: Instantiate failed for '{src.name}' (techType={techType}, classId={classId}): {ex}");
                return null;
            }
            dummy.SetActive(true);
            CaptureColliderBounds(dummy, classId, techType);
            LogComponentInventory(dummy);

            EnforceWorldModelOnly(dummy);

            SanitizeObject(dummy, techType);

            //Sanitization removes inactive children (view models, hidden "x" variants...).
            //If that left the dummy with no visible geometry at all (for example because the
            //game deactivated the world model when the item was last held in the player's hand),
            //reactivate the renderer-bearing children one last time before giving up.
            if (GetVisibleMeshRenderers(dummy).Length == 0)
            {
                int restored = ReactivateRendererChildren(dummy);
                if (restored > 0)
                    Plugin.Log(LogLevel.Info, $"VisibleLockerInterior: '{dummy.name}' had no visible renderer after sanitization; reactivated {restored} renderer child(ren).");
            }
            if (GetVisibleMeshRenderers(dummy).Length == 0)
            {
                Plugin.Log(LogLevel.Warning, $"VisibleLockerInterior: '{src.name}' (techType={techType}, classId={classId}) has no visible renderer after sanitization -- skipping.");
                GameObject.Destroy(dummy);
                return null;
            }

            var dummyComp = dummy.AddComponent<VisibleLockerInteriorDummyData>();
            dummyComp.techType = techType;
            dummyComp.prefabId = classId;
            return dummy;
        }

        private static void RepositionDummy(GameObject dummy, Vector3 targetPosition, int itemsPerShelf)
        {
            var comp = dummy.GetComponent<VisibleLockerInteriorDummyData>();
            var perceived = GetIdealBounds(dummy);
            var key = CacheKey(comp.prefabId, comp.techType);

            //One-shot diagnostics per item type: the boxes available for manual pinning.
            if (boundsLogged.Add(key))
            {
                string colliderPart = "none";
                if (colliderBoundsCache.TryGetValue(key, out var collider))
                    colliderPart = $"c={F3(collider.center)} s={F3(collider.size)}";
                string renderersPart = "not cached";
                if (rendererBoundsCache.TryGetValue(key, out var renderersBox))
                    renderersPart = $"c={F3(renderersBox.center)} s={F3(renderersBox.size)}";
                Plugin.Log(LogLevel.Info,
                    $"VisibleLockerInterior: bounds for '{dummy.name}' (techType={comp.techType}, classId={comp.prefabId}): " +
                    $"perceived c={F3(perceived.center)} s={F3(perceived.size)} | " +
                    $"collider {colliderPart} | renderers {renderersPart}");
            }

            const float magicNumber = 0.13f;  //Original programmer used this figure--for unknown reason.  That's why it's magic.

            dummy.transform.localRotation = GetIdealRotation(comp.prefabId, comp.techType);

            //was hard-coded to: 0.13, 0.14, 0.27
            float scale = new[] {
                magicNumber / (perceived.size.x * (itemsPerShelf / BaselineItemsPerShelf)),
                (magicNumber + 0.01f) / (perceived.size.y * (itemsPerShelf / BaselineItemsPerShelf)),
                (magicNumber * 2 + 0.01f) / (perceived.size.z * (itemsPerShelf / BaselineItemsPerShelf))
            }.Min() * GetIdealDeltaScale(dummy);

            if (float.IsNaN(scale) || float.IsInfinity(scale) || scale <= 0f)
                Plugin.Log(LogLevel.Warning, $"VisibleLockerInterior: '{dummy.name}' has degenerate bounds {perceived.size} -> scale={scale}, item will likely be invisible.");

            //The bottom of the perceived footprint rests on the shelf (2547 behaviour).
            //Item-specific overrides in Quirk shift this for items whose mesh extent
            //differs from their footprint (e.g. the Precursor keys).
            var offset = -perceived.center + perceived.extents.y * Vector3.up;

            dummy.transform.localScale = scale * Vector3.one;

            dummy.transform.localPosition = targetPosition + offset * scale;

            Plugin.Log(LogLevel.Info, $"Placed {dummy.name}, scale={scale}, classId={comp.prefabId}");
        }

        //Removes physics/particles/non-mesh renderers/game behaviours from a dummy so it is an
        //inert visual. A problem on one child (inactive object, "x" name, missing script) must
        //never make the whole item disappear: the 2547 behaviour is to discard the offending
        //child and keep the item. Every removal is logged.
        private static void SanitizeObject(GameObject obj, TechType techType)
        {
            int missingScripts = 0;
            foreach (var mb in obj.GetComponents<MonoBehaviour>())
            {
                if (mb == null) missingScripts++;
            }
            if (missingScripts > 0)
            {
                //A "missing" MonoBehaviour is inert at runtime (Unity keeps it as a null slot).
                //We leave the slot in place and continue, so the item still renders.
                Plugin.Log(LogLevel.Warning, $"VisibleLockerInterior: '{obj.name}' has {missingScripts} missing MonoBehaviour script(s) -- leaving them in place (inert) and continuing.");
            }

            var destroyList = new List<Component>();
            do
            {
                destroyList.Clear();
                destroyList.AddRange(obj.GetComponents<Collider>());
                destroyList.AddRange(obj.GetComponents<Rigidbody>());
                destroyList.AddRange(obj.GetComponents<ParticleSystem>());
                foreach (var r in obj.GetComponents<Renderer>())
                {
                    if (r is MeshRenderer || r is SkinnedMeshRenderer)
                    {
                        continue;
                    }
                    else
                    {
                        destroyList.Add(r);
                    }
                }
                foreach (var b in obj.GetComponents<Behaviour>())
                {
                    //Missing script components surface as null entries here--skip them.
                    if (b == null)
                    {
                        continue;
                    }
                    if (b is SkyApplier)
                    {
                        b.enabled = true;
                    }
                    else if (b is Animator && Quirk.MustHaveAnimator(techType))
                    {
                        b.enabled = false;
                    }
                    else
                    {
                        destroyList.Add(b);
                    }
                }
                destroyList.Reverse();
                foreach (var comp in destroyList)
                {
                    if (comp != null)
                    {
                        GameObject.DestroyImmediate(comp);
                    }
                }
            } while (destroyList.Count > 0);

            if (Quirk.IsKelp(techType))
                foreach (var r in obj.GetComponents<Renderer>())
                {
                    foreach (var m in r.materials)
                    {
                        m.DisableKeyword("FX_KELP");
                    }
                }

            foreach (Transform childTransform in obj.transform)
            {
                var child = childTransform.gameObject;
                if (!child.activeSelf || child.name.StartsWith("x"))
                {
                    //Inactive children are usually the in-hand "view model" (inactive by design
                    //in the prefab) or hidden "x" variants: drop them, keep the item.
                    Plugin.Log(LogLevel.Debug, $"VisibleLockerInterior: removing child '{child.name}' of '{obj.name}' (inactive={!child.activeSelf}, x-prefixed={child.name.StartsWith("x")}).");
                    GameObject.Destroy(child);
                    continue;
                }
                SanitizeObject(child, techType);
            }
        }

        //Some items (e.g. Precursor keys) carry both a "world" model and an in-hand "view" model
        //as children, and the prefab can have the view model active. A locker dummy must only
        //show the world model: keep it (and reactivate it if the game deactivated it), drop the
        //view model subtree. Detected via the component exposing worldModel/viewModel fields,
        //with a name heuristic fallback.
        private static void EnforceWorldModelOnly(GameObject obj)
        {
            GameObject? view = null;
            foreach (var mb in obj.GetComponents<MonoBehaviour>())
            {
                if (mb == null) continue;
                var type = mb.GetType();
                FieldInfo? worldField = null;
                FieldInfo? viewField = null;
                while (type != null && type != typeof(Component))
                {
                    worldField = type.GetField("worldModel", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    viewField = type.GetField("viewModel", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (worldField != null && viewField != null) break;
                    type = type.BaseType;
                }
                if (worldField == null || viewField == null) continue;
                var world = worldField.GetValue(mb) as GameObject;
                view = viewField.GetValue(mb) as GameObject;
                if (view != null)
                    Plugin.Log(LogLevel.Debug, $"VisibleLockerInterior: '{obj.name}' -- item carries a world model ('{(world != null ? world.name : "?")}') and an in-hand view model ('{view.name}'); keeping the world model only.");
                if (world != null && world != obj && !world.activeSelf)
                    world.SetActive(true);
                break;
            }
            if (view == null)
            {
                foreach (Transform child in obj.transform)
                {
                    if (child.name.Equals("ViewModel", StringComparison.OrdinalIgnoreCase)
                        || child.name.Equals("view model", StringComparison.OrdinalIgnoreCase))
                    {
                        view = child.gameObject;
                        break;
                    }
                }
            }
            if (view != null && view != obj)
            {
                Plugin.Log(LogLevel.Debug, $"VisibleLockerInterior: '{obj.name}' -- dropping in-hand view model '{view.name}' (the dummy shows the world model).");
                GameObject.Destroy(view);
            }
        }

        private static void LogComponentInventory(GameObject go)
        {
            var sb = new StringBuilder();
            int missing = 0;
            foreach (var c in go.GetComponents<Component>())
            {
                if (c == null)
                {
                    missing++;
                    sb.Append(" [MISSING-SCRIPT]");
                }
                else
                {
                    sb.Append(' ').Append(c.GetType().Name);
                }
            }
            for (int i = 0; i < go.transform.childCount; i++)
            {
                var child = go.transform.GetChild(i).gameObject;
                int childMissing = 0;
                foreach (var c in child.GetComponents<Component>())
                {
                    if (c == null) childMissing++;
                }
                sb.Append($" | child '{child.name}' missing={childMissing}");
            }
            Plugin.Log(LogLevel.Debug, $"VisibleLockerInterior: component inventory of '{go.name}':{sb}{(missing > 0 ? $" ({missing} missing script(s) on root)" : "")}");
        }

        private static Renderer[] GetVisibleMeshRenderers(GameObject go)
        {
            return go.GetComponentsInChildren<Renderer>(true)
                     .Where(r => r != null && r.gameObject.activeInHierarchy && (r is MeshRenderer || r is SkinnedMeshRenderer))
                     .ToArray();
        }

        private static int ReactivateRendererChildren(GameObject go)
        {
            int count = 0;
            foreach (Transform t in go.GetComponentsInChildren<Transform>(true))
            {
                var child = t.gameObject;
                if (child == go || child.activeSelf) continue;
                if (child.GetComponent<MeshRenderer>() != null || child.GetComponent<SkinnedMeshRenderer>() != null)
                {
                    child.SetActive(true);
                    count++;
                }
            }
            return count;
        }

        private static Quaternion GetIdealRotation(string classId, TechType techType)
        {
            if (Quirk.LegacyTuningEnabled && Quirk.overrideRotation.ContainsKey(classId))
                return Quirk.overrideRotation[classId];
            return Quirk.GetIdealRotationByTechType(techType);
        }

        private static Bounds GetIdealBounds(GameObject dummy)
        {
            var comp = dummy.GetComponent<VisibleLockerInteriorDummyData>();
            if (Quirk.LegacyTuningEnabled)
            {
                if (Quirk.overrideBounds.ContainsKey(comp.prefabId))
                    return Quirk.overrideBounds[comp.prefabId];
                if (Quirk.overrideBoundsByTechType.ContainsKey(comp.techType))
                    return Quirk.overrideBoundsByTechType[comp.techType];
            }
            //Dynamic fallback (2547 behaviour): the visible geometry extent. Items whose mesh
            //extent is a poor size reference (inflated hitboxes on creatures, far-flung
            //symbol geometry on the Precursor keys...) get manual overrideBounds entries.
            return EnsureRendererBounds(dummy);
        }

        private static float GetIdealDeltaScale(GameObject dummy)
        {
            var comp = dummy.GetComponent<VisibleLockerInteriorDummyData>();
            if (Quirk.LegacyTuningEnabled && Quirk.overrideDeltaScale.ContainsKey(comp.prefabId))
                return Quirk.overrideDeltaScale[comp.prefabId];
            return 1;
        }

        //The item footprint as the game defines it (its colliders), measured in the display
        //frame (ideal rotation, scale 1, at the origin) before sanitization removes them.
        //Diagnostics/pinning data only -- placement uses the renderer extent (or overrides).
        private static void CaptureColliderBounds(GameObject dummy, string classId, TechType techType)
        {
            var key = CacheKey(classId, techType);
            if (colliderBoundsCache.ContainsKey(key)) return;

            var rot = GetIdealRotation(classId, techType);
            var curRot = dummy.transform.rotation;
            var curScale = dummy.transform.localScale;
            var curPos = dummy.transform.position;
            dummy.transform.rotation = rot;
            dummy.transform.localScale = Vector3.one;
            dummy.transform.position = Vector3.zero;

            var box = new Bounds(Vector3.zero, Vector3.zero);
            bool any = false;
            foreach (var c in dummy.GetComponentsInChildren<BoxCollider>(true))
            {
                if (c == null) continue;
                for (int i = 0; i < 8; i++)
                {
                    var cornerLocal = new Vector3(
                        c.center.x + ((i & 1) == 0 ? -1f : 1f) * 0.5f * c.size.x,
                        c.center.y + ((i & 2) == 0 ? -1f : 1f) * 0.5f * c.size.y,
                        c.center.z + ((i & 4) == 0 ? -1f : 1f) * 0.5f * c.size.z);
                    var corner = dummy.transform.InverseTransformPoint(c.transform.TransformPoint(cornerLocal));
                    if (!any) { box = new Bounds(corner, Vector3.zero); any = true; }
                    else box.Encapsulate(corner);
                }
            }
            foreach (var c in dummy.GetComponentsInChildren<SphereCollider>(true))
            {
                if (c == null) continue;
                var center = dummy.transform.InverseTransformPoint(c.transform.TransformPoint(c.center));
                if (!any) { box = new Bounds(center, Vector3.one * 2f * c.radius); any = true; }
                else
                {
                    box.Encapsulate(center - Vector3.one * c.radius);
                    box.Encapsulate(center + Vector3.one * c.radius);
                }
            }

            dummy.transform.rotation = curRot;
            dummy.transform.localScale = curScale;
            dummy.transform.position = curPos;

            if (any)
                colliderBoundsCache[key] = box;
        }

        //The true extent of the visible geometry, in the display frame (ideal rotation,
        //scale 1, at the origin). Skinned (animated) items measure in the pose they were
        //captured in; creatures whose placement must be stable get manual overrideBounds
        //entries instead (e.g. the Floater).
        private static Bounds ComputeRendererBounds(GameObject dummy)
        {
            var comp = dummy.GetComponent<VisibleLockerInteriorDummyData>();
            var rot = GetIdealRotation(comp.prefabId, comp.techType);
            var curRot = dummy.transform.rotation;
            var curScale = dummy.transform.localScale;
            var curPos = dummy.transform.localPosition;

            dummy.transform.rotation = rot;
            dummy.transform.localScale = Vector3.one;
            dummy.transform.position = Vector3.zero;
            var b = new Bounds(Vector3.zero, Vector3.zero);
            var renderers = dummy.GetComponentsInChildren<Renderer>();
            if (renderers.Length > 0)
            {
                b = renderers[0].bounds;
                foreach (Renderer r in renderers)
                {
                    if (r is MeshRenderer || r is SkinnedMeshRenderer)
                    {
                        b.Encapsulate(r.bounds);
                    }
                }
            }

            dummy.transform.rotation = curRot;
            dummy.transform.localScale = curScale;
            dummy.transform.localPosition = curPos;
            return b;
        }

        private static Bounds EnsureRendererBounds(GameObject dummy)
        {
            var comp = dummy.GetComponent<VisibleLockerInteriorDummyData>();
            var key = CacheKey(comp.prefabId, comp.techType);
            if (rendererBoundsCache.TryGetValue(key, out var cached))
                return cached;
            var b = ComputeRendererBounds(dummy);
            rendererBoundsCache[key] = b;
            return b;
        }

        private static string CacheKey(string classId, TechType techType)
        {
            return string.IsNullOrEmpty(classId) ? "tech:" + techType.ToString() : classId;
        }

        private static string F3(Vector3 v)
        {
            return $"({v.x:F3}, {v.y:F3}, {v.z:F3})";
        }

        private static GameObject GetInteriorInstance(StorageContainer sc)
        {
            var locker = sc.prefabRoot;
            var interiorTransform = locker.transform.Find(interiorName);
            if (interiorTransform) return interiorTransform.gameObject;
            var interiorObject = new GameObject(interiorName);
            interiorObject.transform.SetParent(locker.transform);
            interiorObject.transform.localRotation = Quaternion.identity;
            interiorObject.transform.localPosition = Vector3.zero;
            return interiorObject;
        }

        private static List<GameObject> GetSortedItems(GameObject storageRoot, GameObject? exclude = null)
        {
            var result = new List<GameObject>(storageRoot.transform.childCount);
            int ignored = 0;
            foreach (Transform item in storageRoot.transform)
            {
                var go = item.gameObject;
                if (exclude != null && go == exclude) continue;  //removal in progress, not stored anymore
                var pickupable = go.GetComponent<Pickupable>();
                var uniqueId = go.GetComponent<UniqueIdentifier>();
                if (pickupable == null && uniqueId == null)
                {
                    ignored++;
                    Plugin.Log(LogLevel.Debug, $"VisibleLockerInterior: ignoring storageRoot child '{go.name}' (activeSelf={go.activeSelf}) -- no Pickupable, no UniqueIdentifier.");
                    continue;
                }
                if (pickupable == null)
                    Plugin.Log(LogLevel.Debug, $"VisibleLockerInterior: including storageRoot child '{go.name}' via UniqueIdentifier (has no Pickupable).");
                result.Add(go);
            }
            if (ignored > 0)
                Plugin.Log(LogLevel.Debug, $"VisibleLockerInterior: {ignored} storageRoot child(ren) ignored (no Pickupable/UniqueIdentifier).");
            result.Sort(
                (g1, g2) =>
                    CompareTechType(
                        GetItemTechType(g1),
                        GetItemTechType(g2)
                    )
            );
            return result;
        }

        private static TechType GetItemTechType(GameObject go)
        {
            var pickupable = go.GetComponent<Pickupable>();
            if (pickupable != null)
                return pickupable.GetTechType();
            try
            {
                return CraftData.GetTechType(go);
            }
            catch (Exception ex)
            {
                Plugin.Log(LogLevel.Warning, $"VisibleLockerInterior: CraftData.GetTechType failed for '{go.name}': {ex.Message}");
                return TechType.None;
            }
        }

        private static TechType GetDummyTechType(GameObject dummy)
        {
            var comp = dummy.GetComponent<VisibleLockerInteriorDummyData>();
            return comp != null ? comp.techType : TechType.None;
        }

        private static List<GameObject> GetSortedDummies(GameObject lockerInterior)
        {
            var result = new List<GameObject>(lockerInterior.transform.childCount);
            foreach (Transform dummyTransform in lockerInterior.transform)
                result.Add(dummyTransform.gameObject);
            result.Sort(
                (g1, g2) =>
                    CompareTechType(
                        GetDummyTechType(g1),
                        GetDummyTechType(g2)
                    )
            );
            return result;
        }

        private static int CompareTechType(TechType t1, TechType t2)
        {
            return t1.CompareTo(t2);

            //As of the August 2025 Subnautica update, "GetItemSize" is no longer available
            //(it moved to TechData). This method only sorts the locker interior, so plain
            //TechType ordering is used.

            // Original code retained for reference.
            //var size1 = CraftData.GetItemSize(t1);
            //var size2 = CraftData.GetItemSize(t2);
            //if (size1.Equals(size2)) return t1.CompareTo(t2);
            //var l1 = Math.Max(size1.x, size1.y);
            //var l2 = Math.Max(size2.x, size2.y);
            //if (l1 != l2) return l2.CompareTo(l1);
            //var a1 = size1.x * size1.y;
            //var a2 = size2.x * size2.y;
            //return a1 == a2 ? size2.y.CompareTo(size1.y) : a2.CompareTo(a1);
        }
    }

    internal class VisibleLockerInteriorDummyData : MonoBehaviour
    {
        public TechType techType;
        public string prefabId = "";
    }


}
