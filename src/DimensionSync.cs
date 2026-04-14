using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using KSP.UI.Screens;
 
// DimensionSync
// Watches for diameter/radius KSPField changes on parts in the
// VAB/SPH by subscribing to BaseField.OnValueModified, and propagates
// those changes to axial neighbors.

namespace DimensionSync
{
    /// <summary>
    /// Data describing one field on a PartModule that defines a diameter
    /// </summary>
    internal class DiameterFieldDescriptor
    {
        /// <summary>Name of the KSPField / BaseField.</summary>
        public string FieldName;
 
        /// <summary>
        /// Does this field store a radius instead of a diameter?
        /// </summary>
        /// <remarks>
        /// TRUE  : the field stores a RADIUS value (diameter = value * 2).<br />
        /// FALSE : the field stores a DIAMETER value directly.
        /// </remarks>
        public bool IsRadius;

        /// <summary>
        /// Does this field store an inner diameter instead of an outer or only diameter?
        /// </summary>
        /// <remarks>
        /// TRUE  : the field refers to an INNER diameter or radius.<br />
        /// FALSE : the field refers to an OUTER diameter or radius.
        /// </remarks>
        public bool IsInner;

        /// <summary>
        /// Does this field apply to the top of the part?
        /// </summary>
        /// <remarks>
        /// TRUE  : the field DOES NOT control the top diameter of the part.<br />
        /// FALSE : the field DOES control the top diameter of the part.<br />
        /// This affects diameter propagation both into and out of the part.
        /// </remarks>
        public bool NoTop;

        /// <summary>
        /// Does this field apply to the bottom of the part?
        /// </summary>
        /// <remarks>
        /// TRUE  : the field DOES NOT control the bottom diameter of the part.<br />
        /// FALSE : the field DOES control the bottom diameter of the part.<br />
        /// This affects diameter propagation both into and out of the part.
        /// </remarks>
        public bool NoBottom;
    }

    /// <summary>
    /// Tracks a subscription for updating and unsubscribing purposes
    /// </summary>
    internal class FieldSubscription
    {
        public Part Part;
        public PartModule PartModule;
        public BaseField BaseField;
        public object Value;
        public Callback<object> Callback;
     }
 
    /// <summary>
    /// An axial connection between two Parts.
    /// </summary>
    /// <param name="Part1Top">Is the connection on the "top" of Part1?</param>
    /// <param name="Part2Top">Is the connection on the "top" of Part2?</param>
    /// <returns></returns>
    internal record AxialPartConnection(Part Part1, Part Part2, bool Part1Top, bool Part2Top);

    // =========================================================================
    // Main KSPAddon
    // =========================================================================
    [KSPAddon(KSPAddon.Startup.EditorAny, false)]
    public class DimensionSyncAddon : MonoBehaviour
    {
        /// <summary>
        /// Singleton guard
        /// </summary>
        private static DimensionSyncAddon _instance;
 
        /// <summary>
        /// Active subscriptions keyed by part.persistentId
        /// </summary>
        private readonly Dictionary<uint, List<FieldSubscription>> _subscriptions
            = new Dictionary<uint, List<FieldSubscription>>();
 
        /// <summary>
        /// Prevents backtracking and recursion cycles during propagation
        /// </summary>
        private bool _propagating;
 
        /// <summary>
        /// Description of each known diameter/radius field
        /// </summary>
        //TODO make this procedural based on keywords in the field name?
        private static readonly Dictionary<string, DiameterFieldDescriptor> fieldDescriptors =
            new()
            {
                { "diameter", new() { FieldName = "diameter" } },
                { "radius", new() { FieldName = "radius", IsRadius = true } },
                { "shapeDiameter", new() { FieldName = "shapeDiameter" } },
                { "currentDiameter", new() { FieldName = "currentDiameter" } },
                { "baseRadius", new() { FieldName = "baseRadius", IsRadius = true, NoTop = true } },
                { "topRadius", new() { FieldName = "topRadius", IsRadius = true, NoBottom = true } },
                { "bottomDiameter", new() { FieldName = "bottomDiameter", NoTop = true  } },
                { "topDiameter", new() { FieldName = "topDiameter", NoBottom = true } },
                { "innerDiameter", new() { FieldName = "innerDiameter", IsInner = true} },
                { "outerDiameter", new() { FieldName = "outerDiameter"} },
                { "bottomInnerDiameter", new() { FieldName = "bottomInnerDiameter", NoTop = true, IsInner = true} },
                { "bottomOuterDiameter", new() { FieldName = "bottomOuterDiameter", NoTop = true} },
                { "topInnerDiameter", new() { FieldName = "topInnerDiameter", NoBottom = true, IsInner = true} },
                { "topOuterDiameter", new() { FieldName = "topOuterDiameter", NoBottom = true} },
            };
 
        // =========================================================================
        // Unity lifecycle
        // =========================================================================
        private void Awake()
        {
            if (_instance != null) { Destroy(gameObject); return; }
            _instance = this;
        }
 
        private void Start()
        {
            GameEvents.onEditorPartEvent.Add(OnEditorPartEvent);
            GameEvents.onEditorLoad.Add(OnEditorLoad);
            GameEvents.onEditorStarted.Add(OnEditorStarted);
 
            Debug.Log("[DimensionSync] Addon started.");
        }
 
        private void OnDestroy()
        {
            GameEvents.onEditorPartEvent.Remove(OnEditorPartEvent);
            GameEvents.onEditorLoad.Remove(OnEditorLoad);
            GameEvents.onEditorStarted.Remove(OnEditorStarted);
 
            UnsubscribeAll();
            _instance = null;
        }
 
        //FIXME doesn't currently work
        private void OnEditorStarted()
        {
            RebuildSubscriptionsForVessel();
        }
 
        private void OnEditorLoad(ShipConstruct ship, CraftBrowserDialog.LoadType _)
        {
            RebuildSubscriptionsForVessel();
        }
 
        private void OnEditorPartEvent(ConstructionEventType eventType, Part part)
        {
            switch (eventType)
            {
                case ConstructionEventType.PartCreated:
                    SubscribePart(part);
                    break;
 
                case ConstructionEventType.PartDeleted:
                    UnsubscribePart(part);
                    break;
                
                //TODO perform diameter propagation when parts are attached?
                // case ConstructionEventType.PartAttached:
            }
        }
 
        // =========================================================================
        // Subscription management
        // =========================================================================
 
        private void RebuildSubscriptionsForVessel()
        {
            UnsubscribeAll();
 
            if (EditorLogic.fetch?.ship == null) return;
 
            foreach (Part part in EditorLogic.fetch.ship.Parts)
                SubscribePart(part);
        }
 
        /// <summary>
        /// Hooks BaseField.OnValueModified for every recognised diameter field
        /// found on <paramref name="part"/>.
        /// </summary>
        private void SubscribePart(Part part)
        {
            Debug.Log($"[DimensionSync] SubscribePart {part.partInfo?.title} {part.transform.localPosition.y}");
            if (part == null) return;
            if (_subscriptions.ContainsKey(part.persistentId)) return;
  
            foreach (PartModule module in part.Modules)
            {
                Debug.Log($"[DimensionSync] SubscribePart Module {module.name}");
                foreach (string fieldName in fieldDescriptors.Keys)
                {
                    BaseField bf = module.Fields[fieldName];
                    if (bf == null) continue;
                    Debug.Log($"[DimensionSync] SubscribePart Module Field {fieldName}");

                    var sub = new FieldSubscription
                    {
                        Part = part,
                        PartModule = module,
                        BaseField = bf,
                        Value = bf.GetValue(part),
                    };
                    sub.Callback = newValue => {
                        OnDiameterFieldChanged(sub, newValue);
                    };
 
                    bf.OnValueModified += sub.Callback;

                    if (!_subscriptions.TryGetValue(part.persistentId, out var subList))
                    {
                        subList = new List<FieldSubscription>();
                        _subscriptions[part.persistentId] = subList;
                    }
                    _subscriptions[part.persistentId].Add(sub);
 
                    Debug.Log($"[DimensionSync] Subscribed to " +
                              $"{module.name}.{fieldName} on '{part.partInfo?.title}' {part.transform.localPosition.y}");
                 }
            } 
        }
 
        private void UnsubscribePart(Part part)
        {
            if (part == null) return;
            if (!_subscriptions.TryGetValue(part.persistentId, out List<FieldSubscription> subs)) return;
 
            RemoveHooks(subs);
            _subscriptions.Remove(part.persistentId);
        }
 
        private void UnsubscribeAll()
        {
            foreach (List<FieldSubscription> subs in _subscriptions.Values)
                RemoveHooks(subs);
            _subscriptions.Clear();
        }
 
        private static void RemoveHooks(List<FieldSubscription> subs)
        {
            foreach (FieldSubscription sub in subs)
            {
                if (sub.BaseField != null)
                {
                    try { sub.BaseField.OnValueModified -= sub.Callback; }
                    catch { /* part may already be destroyed */ }
                }
            }
        }
 
        // =========================================================================
        // Core: handle a field value change
        // =========================================================================
        private void OnDiameterFieldChanged(FieldSubscription sub, object newValue)
        {
            // Ignore changes made during propagation
            if (_propagating) return;
 
            float rawValue = ConvertToFloat(newValue);
            if (rawValue < 0f) return;
 
            float radiusFactor = fieldDescriptors[sub.BaseField.name].IsRadius ? 2f : 1f;
            float oldDiameter = ConvertToFloat(sub.Value) * radiusFactor;
            float newDiameter = rawValue * radiusFactor;

            Debug.Log($"[DimensionSync] OnDiameterFieldChanged '{sub.Part.partInfo?.title}' {sub.Part.transform.localPosition.y} " +
                      $"{sub.PartModule.GetType().Name}.{sub.BaseField.name} {oldDiameter:F3} -> {newDiameter:F3}m diameter");
 
            BeginPropagation(sub, newDiameter);
        }
 
        // =========================================================================
        // Propagation logic
        // =========================================================================
        private void BeginPropagation(FieldSubscription sub, float newDiameter)
        {
            _propagating = true;
            try
            {
                var visited = new HashSet<uint> { sub.Part.persistentId };
                HandleUpdatedField(sub, newDiameter, visited);
                float radiusFactor = fieldDescriptors[sub.BaseField.name].IsRadius ? 2f : 1f;
                sub.Value = newDiameter / radiusFactor;
            }
            finally
            {
                _propagating = false;
            }
        }
        private void HandleUpdatedField(FieldSubscription sub, float newDiameter, HashSet<uint> visited)
        {
            DiameterFieldDescriptor desc = fieldDescriptors[sub.BaseField.name];
            float radiusFactor = desc.IsRadius ? 2f : 1f;
            float oldDiameter = ConvertToFloat(sub.Value) * radiusFactor;
            Debug.Log($"[DimensionSync] HandleUpdatedField '{sub.Part.partInfo?.title}' {sub.Part.transform.localPosition.y} " +
                      $"{sub.BaseField.name} {oldDiameter:F3} -> {newDiameter:F3}m diameter");
            foreach (AxialPartConnection apc in GetAxialNeighborConnections(sub.Part))
            {
                // Only propagate in the direction(s) this diameter field applies to
                if (apc.Part1Top && desc.NoTop || !apc.Part1Top && desc.NoBottom) continue;
                // Don't backtrack
                if (visited.Contains(apc.Part2.persistentId)) continue;
                // Skip neighbors without a diameter
                // TODO cache this
                if (!HasDiameterModule(apc.Part2)) continue;

                visited.Add(apc.Part2.persistentId);

                ApplyDiameter(apc.Part2, oldDiameter, newDiameter, apc.Part2Top, visited);
            }
        }
 
        // =========================================================================
        // Enumerate the children and parent of a part
        // =========================================================================
        private static IEnumerable<Part> GetNeighbors(Part part)
        {
            foreach (Part child in part.children)
                yield return child;
            if (part.parent != null)
                yield return part.parent;
        }

        // =========================================================================
        // Enumerate the axial neighbor connections of a part.
        // =========================================================================
        private static IEnumerable<AxialPartConnection> GetAxialNeighborConnections(Part part)
        {
            foreach (Part neighbor in GetNeighbors(part))
            {
                AttachNode node1 = part.FindAttachNodeByPart(neighbor);
                if (node1 == null) continue; // surface-attached
                int part1Direction = GetAxialDirection(node1);
                if (part1Direction == 0) continue;
                AttachNode node2 = neighbor.FindAttachNodeByPart(part);
                if (node2 == null) continue; // surface-attached
                int part2Direction = GetAxialDirection(node2);
                if (part2Direction == 0) continue;
    
                yield return new AxialPartConnection(part,neighbor,part1Direction==1,part2Direction==1);
            }
        }

        /// <returns>-1 for bottom, +1 for top, 0 for other sides/directions or null node</returns>
        private static int GetAxialDirection(AttachNode node)
        {
            if (node == null) return 0;
 
            string id = node.id?.ToLowerInvariant() ?? string.Empty;
            Debug.Log($"[DimensionSync] GetAxialDirection {node.id} {node.orientation.normalized.y}");
            if (id.StartsWith("top") || node.orientation.normalized.y == 1) return 1;
            if (id.StartsWith("bottom") || node.orientation.normalized.y == -1) return -1;

            return 0;
        }
 
        // =========================================================================
        // Applying diameter to a part
        // =========================================================================
        private void ApplyDiameter(Part part, float oldDiameter, float newDiameter, bool fromTop, HashSet<uint> visited)
        {
            Debug.Log($"[DimensionSync] ApplyDiameter '{part.partInfo?.title}' {part.transform.localPosition.y} " +
                      $"{oldDiameter:F3} -> {newDiameter:F3}m diameter from " + 
                      (fromTop?"top":"bottom"));
            foreach (FieldSubscription sub in _subscriptions[part.persistentId])
            {
                DiameterFieldDescriptor desc = fieldDescriptors[sub.BaseField.name];
                if (fromTop && desc.NoTop || !fromTop && desc.NoBottom) continue;
                float radiusFactor = fieldDescriptors[sub.BaseField.name].IsRadius ? 2f : 1f;
                float currentDiameter = ConvertToFloat(sub.BaseField.GetValue(sub.Part)) * radiusFactor;
                if (currentDiameter < 0f) continue;
                if (currentDiameter != oldDiameter) continue;
                float valueToWrite = newDiameter / radiusFactor;

                // Clamp to the field's declared slider range
                if (sub.BaseField.uiControlEditor is UI_FloatRange range)
                    valueToWrite = Mathf.Clamp(valueToWrite, range.minValue, range.maxValue);

                sub.BaseField.SetValue(valueToWrite, sub.PartModule);
                if (sub.BaseField.uiControlEditor.onFieldChanged != null)
                {
                    Debug.Log($"[DimensionSync] Calling uiControlEditor.onFieldChanged for {sub.PartModule.GetType().Name}.{desc.FieldName}");
                    sub.BaseField.uiControlEditor.onFieldChanged?.Invoke(sub.BaseField, valueToWrite);    
                } else
                {
                    Debug.Log($"[DimensionSync] No uiControlEditor.onFieldChanged for {sub.PartModule.GetType().Name}.{desc.FieldName}");
                }
                // TriggerShapeUpdate(sub.PartModule);

                Debug.Log($"[DimensionSync] \\__ {sub.PartModule.GetType().Name}.{desc.FieldName}" +
                        $" = {valueToWrite:F3} on '{part.partInfo?.title}' {part.transform.localPosition.y}");

                HandleUpdatedField(sub, newDiameter, visited);
                sub.Value = valueToWrite;
            }
        }
 
        // =========================================================================
        // Mesh update trigger
        // =========================================================================
        private static readonly string[] UpdateMethodNames =
        {
            "UpdateShape",
            "UpdateMesh",
            "OnShapeChanged",
            "RebuildMesh",
            "UpdateFairing",
            "OnTweakableChanged",
        };
 
        private static void TriggerShapeUpdate(PartModule module)
        {
            Type moduleType = module.GetType();
            foreach (string name in UpdateMethodNames)
            {
                MethodInfo method = moduleType.GetMethod(name,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                    null, Type.EmptyTypes, null);
                if (method == null) continue;
 
                try { method.Invoke(module, null); }
                catch (Exception ex)
                {
                    Debug.LogWarning(
                        $"[DimensionSync] TriggerShapeUpdate {moduleType.Name}.{name}() threw: {ex.Message}");
                }
                return;
            }
        }
 
        // =========================================================================
        // Helpers
        // =========================================================================
        private static bool HasDiameterModule(Part part)
        {
            if (part == null) return false;
            foreach (PartModule module in part.Modules)
                foreach (BaseField bf in module.Fields)
                    if (fieldDescriptors.ContainsKey(bf.name)) return true;
            return false;
        }
 
        private static float ConvertToFloat(object value)
        {
            if (value is float f)  return f;
            if (value is double d) return (float)d;
            if (value is int i)    return i;
            return -1f;
        }
    }
}

// Required boilerplate to enable use of init-only properties, e.g. for records
namespace System.Runtime.CompilerServices
{
    public class IsExternalInit { }
}
