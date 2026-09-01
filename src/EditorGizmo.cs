using System.Reflection;
using UnityEngine;

namespace DimensionSync
{
    /// <summary>
    /// Keeps the editor's move and rotate gizmos on the part they are attached to when
    /// this mod moves it.
    /// </summary>
    /// <remarks>
    /// A gizmo puts itself where its part is when it latches on, and then stays there.
    /// Nothing else in the editor moves a part while somebody is holding its handle, so
    /// nothing else needs it to follow - nothing, that is, except a mod like this one.
    ///
    /// MEASURED, not reasoned about. Before: part and gizmo both at (-0.8, 12.9, 2.5).
    /// After this mod moved the part: part at (-0.8, 12.4, 2.5) and the gizmo still at
    /// (-0.8, 12.9, 2.5). An earlier attempt at this wrote to trfPos0 instead, which the
    /// same measurement shows sitting at zero throughout and playing no part in it, and
    /// forced the coordinate system to absolute on the way past.
    ///
    /// Everything is reached by reflection and every step is optional: a KSP that names
    /// these differently loses the correction and keeps working.
    /// </remarks>
    internal static class EditorGizmo
    {
        /// <summary>Move whichever gizmo is holding this part to where the part now is.</summary>
        public static void PartMoved(Part part)
        {
            if (part == null || !HighLogic.LoadedSceneIsEditor) return;

            Resolve();
            Follow(_offsetType, part);
            Follow(_rotateType, part);
        }

        private static bool _resolved;
        private static System.Type _offsetType;
        private static System.Type _rotateType;

        private static void Resolve()
        {
            if (_resolved) return;
            _resolved = true;

            foreach (Assembly assembly in System.AppDomain.CurrentDomain.GetAssemblies())
            {
                _offsetType = _offsetType ?? assembly.GetType("EditorGizmos.GizmoOffset");
                _rotateType = _rotateType ?? assembly.GetType("EditorGizmos.GizmoRotate");
                if (_offsetType != null && _rotateType != null) break;
            }
        }

        private static void Follow(System.Type type, Part part)
        {
            if (type == null) return;

            Object[] gizmos = Object.FindObjectsOfType(type);
            if (gizmos == null) return;

            foreach (Object found in gizmos)
            {
                if (!(found is Component gizmo)) continue;

                // Only the gizmo holding THIS part.
                FieldInfo hostField = type.GetField("host", Flags);
                if (!(hostField?.GetValue(gizmo) is Transform host) || host != part.transform) continue;

                // Never mid-drag. The player is moving it and their reference is the
                // right one; ours would fight them for it.
                FieldInfo draggingField = type.GetField("isDragging", Flags);
                if (draggingField?.GetValue(gizmo) is bool dragging && dragging) continue;

                gizmo.transform.position = part.transform.position;

                // Orientation only when the gizmo is showing the part's OWN axes. In
                // absolute mode it is showing the world's, and turning it to follow the
                // part would be wrong. The coordinate system is read to decide that and
                // never written: setting it is how an earlier attempt at this dragged
                // everybody from local back to absolute.
                FieldInfo spaceField = type.GetField("coordSpace", Flags);
                if (spaceField?.GetValue(null) is Space space && space == Space.Self)
                    gizmo.transform.rotation = part.transform.rotation;

                if (DimensionSettings.Debug)
                    UnityEngine.Debug.Log($"{DimensionSyncAddon.LogTag} moved the editor's {type.Name} " +
                                          $"onto {part.name}, which this mod had just moved out from " +
                                          "under it");
            }
        }

        private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic
                                           | BindingFlags.Instance | BindingFlags.Static;
    }
}
