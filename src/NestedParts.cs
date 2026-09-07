using System.Collections.Generic;
using UnityEngine;

namespace DimensionSync
{
    /// <summary>
    /// Keeps a part that sits inside another part's bore fitting inside it.
    /// </summary>
    /// <remarks>
    /// This is the one relationship in the mod that is not two fields holding the
    /// same number. A nested part is deliberately SMALLER than the bore around it -
    /// that gap is the point - so the graph walk can never carry a change between
    /// them: it stops the moment a value stops matching, and here it never matched.
    ///
    /// What has to be preserved is the relationship rather than the value, which puts
    /// this with the wing rules that hold an edge collinear or a surface flat, not
    /// with the channels.
    ///
    /// A pair counts as nested when the smaller part lies inside the bore AND one of
    /// the four end-plane pairings is flush. Four, not one: a part can be turned end
    /// for end about its joint, leaving the same face touching but the opposite end
    /// pointing down the bore, and it can be slid until its far face lines up with
    /// the far face of its host rather than the near one. A rule that only looked at
    /// the face the two were attached by would miss both.
    /// </remarks>
    internal static class NestedParts
    {
        /// <summary>How close two end planes must be to count as flush, in metres.</summary>
        /// <remarks>
        /// Generous enough for the millimetre a craft is built with, tight enough that
        /// a part slid deliberately off a face is not caught up in this.
        /// </remarks>
        private const float Flush = 0.02f;

        /// <summary>
        /// The room soft coupling leaves when it has to move something, in metres of
        /// diameter.
        /// </summary>
        /// <remarks>
        /// Matches what soft coupling leaves between a part's own two surfaces: enough
        /// that they are not touching, and no more. Soft's whole promise is to give up
        /// the least it can.
        /// </remarks>
        private const float LeastClearance = 0.01f;

        /// <summary>
        /// Resize anything nested inside a bore that just moved, and report those
        /// changes so they carry on outward.
        /// </summary>
        /// <param name="nodes">Every tracked part.</param>
        /// <param name="changes">This frame's changes; anything moved here is appended.</param>
        /// <remarks>
        /// Driven from the change list rather than from a snapshot taken earlier in
        /// the frame. The first version did the latter and could never have worked: a
        /// change is noticed by seeing that a field has ALREADY moved, so by the time
        /// any rule runs the previous value is gone from the field. It recorded the
        /// new bore, compared it against itself, concluded the bore had held still,
        /// and every nesting scenario reported the part unchanged. The change list is
        /// the only thing that still knows both numbers.
        /// </remarks>
        public static void Follow(Dictionary<Part, KspDimensionNode> nodes,
                                  List<DimensionChange> changes)
        {
            if (DimensionSettings.Hollow == HollowMode.HardIndependent) return;
            if (changes == null || changes.Count == 0) return;

            // Snapshot the count: this appends, and a part moved by nesting must not
            // itself be treated as a bore that moved.
            int original = changes.Count;

            if (DimensionSettings.Debug)
            {
                var seen = new List<string>();
                for (int i = 0; i < original; i++)
                    seen.Add($"{changes[i].Slot?.Label} [{changes[i].Slot?.Descriptor?.Channel}] " +
                             $"{changes[i].OldValue:F3}->{changes[i].NewValue:F3}");
                UnityEngine.Debug.Log($"{DimensionSyncAddon.LogTag} nested? this frame's changes: " +
                                      (seen.Count == 0 ? "none" : string.Join("; ", seen.ToArray())));
            }

            for (int i = 0; i < original; i++)
            {
                DimensionChange change = changes[i];
                if (change.Slot?.Part == null) continue;

                // A nested part resized by hand pushes its host's bore instead. The
                // relationship reads the same way from either end - the player has
                // said how much room there should be between these two surfaces - so
                // it would be strange for it to hold when the bore moves and not when
                // the thing inside it does.
                if (change.Slot.Descriptor?.Channel == Channels.Outer)
                {
                    BoreMakesRoom(nodes, change, changes);
                    continue;
                }

                if (change.Slot.Descriptor?.Channel != Channels.Inner) continue;
                if (!nodes.TryGetValue(change.Slot.Part, out KspDimensionNode host)) continue;

                int seen = 0;
                foreach (AxialLink link in host.Links)
                {
                    seen++;
                    if (!(link.Other is KspDimensionNode neighbour)) continue;
                    KspDimensionSlot outside = OutsideOf(neighbour);
                    if (outside == null)
                    {
                        if (DimensionSettings.Debug)
                            UnityEngine.Debug.Log($"{DimensionSyncAddon.LogTag} nested? " +
                                                  $"{neighbour.Label} has no outer diameter");
                        continue;
                    }

                    if (DimensionSettings.Debug)
                        UnityEngine.Debug.Log($"{DimensionSyncAddon.LogTag} nested? bore " +
                                              $"{change.OldValue:F3}->{change.NewValue:F3} on " +
                                              $"{change.Slot.Label}, neighbour {neighbour.Label} " +
                                              $"at {outside.Value:F3}: " +
                                              Explain(change.Slot.Part, neighbour.Part,
                                                      change.OldValue, outside.Value));

                    // Judged against the bore as it WAS. The neighbour was built to fit
                    // the old bore, and asking whether it fits the new one would reject
                    // exactly the parts this exists to bring along.
                    if (!IsNestedIn(change.Slot.Part, neighbour.Part,
                                    change.OldValue, outside.Value)) continue;

                    float wanted = Wanted(DimensionSettings.Hollow, change.OldValue,
                                          change.NewValue, outside.Value);
                    if (float.IsNaN(wanted)) continue;

                    float before = outside.Value;
                    if (Mathf.Abs(wanted - before) <= 1e-4f) continue;

                    float took = outside.SetValue(wanted);
                    if (Mathf.Abs(took - before) <= 1e-4f) continue;

                    if (DimensionSettings.Debug)
                        UnityEngine.Debug.Log($"{DimensionSyncAddon.LogTag} nested: {outside.Label} " +
                                              $"{before:F4} -> {took:F4}, following the bore " +
                                              $"{change.OldValue:F4} -> {change.NewValue:F4} " +
                                              $"({DimensionSettings.Hollow})");

                    changes.Add(new DimensionChange
                    {
                        Slot = outside,
                        OldValue = before,
                        NewValue = took,
                    });
                }
            }
        }

        /// <summary>
        /// A nested part has been resized; move the bore around it to suit.
        /// </summary>
        /// <param name="nodes">Every tracked part.</param>
        /// <param name="change">The change to the nested part's outside diameter.</param>
        /// <param name="changes">This frame's changes; the bore's move is appended.</param>
        /// <remarks>
        /// The mirror image of the other direction, and it has to look at the joint
        /// from the other side: the part that changed is the one INSIDE, so its
        /// neighbours are searched for a host with a bore rather than the other way
        /// about.
        /// </remarks>
        private static void BoreMakesRoom(Dictionary<Part, KspDimensionNode> nodes,
                                          DimensionChange change, List<DimensionChange> changes)
        {
            if (!nodes.TryGetValue(change.Slot.Part, out KspDimensionNode inside)) return;

            foreach (AxialLink link in inside.Links)
            {
                if (!(link.Other is KspDimensionNode hostNode)) continue;
                KspDimensionSlot bore = BoreOf(hostNode);
                if (bore == null) continue;

                // Judged on how things stood before the part was resized, for the same
                // reason the other direction is: asking whether it still fits after
                // the change would reject a part that has just been made too big,
                // which is precisely when the bore needs to move.
                if (!IsNestedIn(hostNode.Part, change.Slot.Part, bore.Value, change.OldValue)) continue;

                // Worked out here rather than by reusing Wanted with its arguments
                // swapped. That looked symmetric and is not: Wanted takes the
                // clearance as bore-minus-part, so handing it the part as though it
                // were the bore produces a negative clearance and it gives up. The
                // relationship is symmetric; the expression for it is not.
                float clearance = bore.Value - change.OldValue;
                if (clearance <= 0f) continue;

                float wanted;
                switch (DimensionSettings.Hollow)
                {
                    case HollowMode.Proportional:
                        if (change.OldValue <= 1e-4f) continue;
                        wanted = bore.Value * (change.NewValue / change.OldValue);
                        break;

                    case HollowMode.Constant:
                        wanted = change.NewValue + clearance;
                        break;

                    case HollowMode.SoftIndependent:
                        // The same the other way about: while the part still fits, the
                        // bore stays where it is.
                        if (change.NewValue <= bore.Value - LeastClearance) continue;
                        wanted = change.NewValue + LeastClearance;
                        break;

                    default:
                        continue;
                }

                float before = bore.Value;
                if (Mathf.Abs(wanted - before) <= 1e-4f) continue;

                float took = bore.SetValue(wanted);
                if (Mathf.Abs(took - before) <= 1e-4f) continue;

                if (DimensionSettings.Debug)
                    UnityEngine.Debug.Log($"{DimensionSyncAddon.LogTag} nested: {bore.Label} " +
                                          $"{before:F4} -> {took:F4}, making room for " +
                                          $"{change.Slot.Label} {change.OldValue:F4} -> " +
                                          $"{change.NewValue:F4} ({DimensionSettings.Hollow})");

                changes.Add(new DimensionChange
                {
                    Slot = bore,
                    OldValue = before,
                    NewValue = took,
                });
            }
        }

        /// <summary>The bore of a part, or null if it has none.</summary>
        /// <param name="node">The part's node.</param>
        private static KspDimensionSlot BoreOf(KspDimensionNode node)
        {
            IList<IDimensionSlot> slots = node.Slots;
            for (int i = 0; i < slots.Count; i++)
            {
                if (!(slots[i] is KspDimensionSlot slot)) continue;
                if (!slot.IsAdjustable) continue;
                if (slot.Descriptor?.Channel != Channels.Inner) continue;
                if (slot.Descriptor.Ends != PartEnd.Both) continue;
                return slot;
            }
            return null;
        }

        /// <summary>Where a nested part's outside should end up.</summary>
        /// <param name="mode">The relationship the player asked for.</param>
        /// <param name="wasBore">The surface that moved, before it moved.</param>
        /// <param name="bore">That surface now.</param>
        /// <param name="outside">The other surface, which has not moved.</param>
        /// <remarks>
        /// Named for the commoner direction, but the arithmetic is symmetric: hand it
        /// the nested part's old and new diameters and the bore, and it says where the
        /// bore should go.
        /// </remarks>
        private static float Wanted(HollowMode mode, float wasBore, float bore, float outside)
        {
            float clearance = wasBore - outside;
            if (clearance <= 0f) return float.NaN;

            switch (mode)
            {
                case HollowMode.Proportional:
                    if (wasBore <= 1e-4f) return float.NaN;
                    return bore * (outside / wasBore);

                case HollowMode.Constant:
                    return bore - clearance;

                case HollowMode.SoftIndependent:
                    // Only when the part no longer FITS. This used to ask whether the
                    // part was smaller than the constant-gap target and then move it
                    // to exactly that target - which is constant coupling wearing
                    // soft's name: a bore shrinking from 2.8 to 2.0 dragged a 1.5 m
                    // part down to 0.7 while there was still ample room for it.
                    //
                    // Soft leaves things where the player put them until they will not
                    // fit, and then gives up the least it can.
                    return outside <= bore - LeastClearance
                        ? float.NaN
                        : bore - LeastClearance;

                default:
                    return float.NaN;
            }
        }

        /// <summary>
        /// Whether <paramref name="inside"/> sits within <paramref name="host"/>'s
        /// bore with a face flush.
        /// </summary>
        /// <param name="host">The hollow part.</param>
        /// <param name="inside">The part that may be nested in it.</param>
        /// <param name="bore">The host's bore.</param>
        /// <param name="outside">The nested part's outside diameter.</param>
        private static bool IsNestedIn(Part host, Part inside, float bore, float outside)
        {
            if (host == null || inside == null) return false;

            float hostHalf = HalfLength(host);
            float insideHalf = HalfLength(inside);
            if (hostHalf <= 0f || insideHalf <= 0f) return false;

            Vector3 offset = host.transform.InverseTransformPoint(inside.transform.position);
            float across = new Vector2(offset.x, offset.z).magnitude;

            // Room for it across the bore. Off-centre is allowed: nothing about
            // nesting says the two must share a centre line.
            if (across + outside / 2f >= bore / 2f) return false;

            // Inside it along the axis.
            if (Mathf.Abs(offset.y) + insideHalf > hostHalf + Flush) return false;

            // And one of the four end-plane pairings flush: both ends of each part
            // against both ends of the other.
            float[] hostFaces = { hostHalf, -hostHalf };
            float[] insideFaces = { offset.y + insideHalf, offset.y - insideHalf };
            for (int h = 0; h < hostFaces.Length; h++)
                for (int i = 0; i < insideFaces.Length; i++)
                    if (Mathf.Abs(hostFaces[h] - insideFaces[i]) <= Flush)
                        return true;

            return false;
        }

        /// <summary>Why a pair is or is not nested, in one line.</summary>
        /// <param name="host">The hollow part.</param>
        /// <param name="inside">The part that may be nested in it.</param>
        /// <param name="bore">The host's bore.</param>
        /// <param name="outside">The nested part's outside diameter.</param>
        /// <remarks>
        /// Five conditions have to hold and any one of them failing looks identical
        /// from outside - the part simply does not move. Saying which one it was costs
        /// a line in a debug log and saves guessing.
        /// </remarks>
        private static string Explain(Part host, Part inside, float bore, float outside)
        {
            if (host == null || inside == null) return "a part is missing";

            float hostHalf = HalfLength(host);
            float insideHalf = HalfLength(inside);
            if (hostHalf <= 0f) return "the host has no measurable length";
            if (insideHalf <= 0f) return "the neighbour has no measurable length";

            Vector3 offset = host.transform.InverseTransformPoint(inside.transform.position);
            float across = new Vector2(offset.x, offset.z).magnitude;

            string room = across + outside / 2f >= bore / 2f
                ? $"NO ROOM across ({across:F3}+{outside / 2f:F3} vs {bore / 2f:F3})"
                : "fits across";
            string along = Mathf.Abs(offset.y) + insideHalf > hostHalf + Flush
                ? $"NOT INSIDE along ({Mathf.Abs(offset.y):F3}+{insideHalf:F3} vs {hostHalf:F3})"
                : "inside along";

            float[] hostFaces = { hostHalf, -hostHalf };
            float[] insideFaces = { offset.y + insideHalf, offset.y - insideHalf };
            float closest = float.MaxValue;
            for (int h = 0; h < hostFaces.Length; h++)
                for (int i = 0; i < insideFaces.Length; i++)
                    closest = Mathf.Min(closest, Mathf.Abs(hostFaces[h] - insideFaces[i]));

            return $"{room}; {along}; closest faces {closest:F4} " +
                   $"(need {Flush:F2}); halves host {hostHalf:F3} inside {insideHalf:F3}, " +
                   $"offset y {offset.y:F3}";
        }

        /// <summary>Half a part's length along its own stack axis.</summary>
        /// <param name="part">The part to measure.</param>
        /// <remarks>
        /// From the renderers rather than a length field, because the field is called
        /// different things by different mods - "length" to ProceduralParts and
        /// "currentLength" to ROLib - and asking either for the other's name returns
        /// nothing at all. Bounds are a question every part can answer.
        /// </remarks>
        private static float HalfLength(Part part)
        {
            Renderer[] renderers = part.GetComponentsInChildren<Renderer>();
            if (renderers == null || renderers.Length == 0) return 0f;

            float lowest = float.MaxValue, highest = float.MinValue;
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null || !renderer.enabled) continue;

                // The mesh's own bounds, carried through the renderer's transform -
                // NOT renderer.bounds, which is a world-space box around the whole
                // thing. That box grows when a part gets WIDER, so measuring a
                // cylinder's length from it made a tank read as half again as long
                // when its diameter changed, and the part nested inside it appeared to
                // fall out. It is wrong for a rotated part too, which is the case this
                // rule exists to handle.
                Mesh mesh = null;
                if (renderer is SkinnedMeshRenderer skinned)
                {
                    mesh = skinned.sharedMesh;
                }
                else
                {
                    MeshFilter filter = renderer.GetComponent<MeshFilter>();
                    if (filter != null) mesh = filter.sharedMesh;
                }
                if (mesh == null) continue;      // nothing measurable here

                Bounds local = mesh.bounds;

                for (int corner = 0; corner < 8; corner++)
                {
                    var at = new Vector3(
                        (corner & 1) == 0 ? local.min.x : local.max.x,
                        (corner & 2) == 0 ? local.min.y : local.max.y,
                        (corner & 4) == 0 ? local.min.z : local.max.z);
                    float y = part.transform.InverseTransformPoint(
                        renderer.transform.TransformPoint(at)).y;
                    lowest = Mathf.Min(lowest, y);
                    highest = Mathf.Max(highest, y);
                }
            }
            return highest <= lowest ? 0f : (highest - lowest) / 2f;
        }

        /// <summary>The outside diameter of a part, or null if it has none.</summary>
        /// <param name="node">The part's node.</param>
        private static KspDimensionSlot OutsideOf(KspDimensionNode node)
        {
            IList<IDimensionSlot> slots = node.Slots;
            for (int i = 0; i < slots.Count; i++)
            {
                if (!(slots[i] is KspDimensionSlot slot)) continue;
                if (!slot.IsAdjustable) continue;
                if (slot.Descriptor?.Channel != Channels.Outer) continue;
                if (slot.Descriptor.Ends != PartEnd.Both) continue;
                return slot;
            }
            return null;
        }
    }
}
