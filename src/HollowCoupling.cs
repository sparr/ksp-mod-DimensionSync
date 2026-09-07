using System.Collections.Generic;
using UnityEngine;

namespace DimensionSync
{
    /// <summary>
    /// Keeps a hollow part's inner and outer diameters in whatever relationship the
    /// player has chosen.
    /// </summary>
    /// <remarks>
    /// Everything else in this mod copies a value onto a neighbour that already
    /// matches it. This does not: it works within ONE part, between two dimensions
    /// that describe the same wall from opposite sides, and the two are never equal.
    /// So it cannot be a channel in the graph - the walk carries a change only while
    /// the values agree - and it has to be its own rule.
    ///
    /// What it produces is fed back in as an ordinary change rather than written and
    /// forgotten. A stack of matching cylinders already resizes in one pass because
    /// the walk recurses; the same has to be true of a diameter this rule moved, or
    /// nesting would survive a direct edit and not a coupled one, which is arbitrary
    /// from the player's side.
    /// </remarks>
    internal static class HollowCoupling
    {
        /// <summary>Every tracked part, as of this frame.</summary>
        private static Dictionary<Part, KspDimensionNode> _nodes;

        /// <summary>
        /// Under soft coupling, lower a bore that is about to refuse a write to the
        /// outer diameter.
        /// </summary>
        /// <param name="slot">The field about to be written.</param>
        /// <param name="wantedRaw">The value it is being asked for, in field units.</param>
        /// <remarks>
        /// This is the only place the refusal can be seen. ProceduralParts keeps its
        /// controls honest - the outer diameter's minimum is the bore plus the least
        /// wall it allows - and a write clamped by that limit reports the clamped
        /// value afterwards, so nothing downstream can tell that anything was asked
        /// for and denied. By the time the coupling rule reads the change it sees a
        /// tidy move to 2.01 and a bore sitting comfortably inside it.
        ///
        /// Hard coupling wants exactly that: the bore holds and the change stops
        /// short. Soft coupling has to act here, before the clamp, while the number
        /// that was wanted still exists.
        ///
        /// The bore is lowered and then PP is asked to recompute its own bounds,
        /// rather than this working out what the new limit ought to be. PP owns that
        /// arithmetic and has changed it before; borrowing the answer costs one
        /// reflected call and cannot drift.
        /// </remarks>
        public static void MakeRoomFor(KspDimensionSlot slot, float wantedRaw)
        {
            if (DimensionSettings.Hollow != HollowMode.SoftIndependent) return;
            if (slot?.Part == null || slot.Descriptor?.Channel != Channels.Outer) return;
            if (_nodes == null || !_nodes.TryGetValue(slot.Part, out KspDimensionNode node)) return;

            if (!(slot.Field?.uiControlEditor is UI_FloatEdit edit)) return;
            if (wantedRaw >= edit.minValue - 1e-5f) return;      // it fits; nothing to do

            KspDimensionSlot bore = PartnerOf(node, slot, movedInner: false);
            if (bore == null) return;

            // The wall comes from the limit itself, not from a guess. PP sets this
            // control's floor to the bore plus its own least wall, so the difference
            // between the floor and the bore IS that number - exactly, and by PP's
            // own arithmetic rather than a reimplementation of it. Guessing from the
            // field's increment instead gave a 0.125 wall where PP wanted 0.01, and
            // the bore gave up twelve times more room than it needed to.
            float wall = Mathf.Max(edit.minValue - bore.Value, 1e-4f);
            float room = wantedRaw - wall;
            float floor = bore.Field?.uiControlEditor is UI_FloatEdit boreEdit ? boreEdit.minValue : 0f;
            float target = Mathf.Max(room, floor);

            float before = bore.Value;
            if (target >= before - 1e-4f) return;                // the bore is not what is in the way

            bore.SetValue(target);
            Recompute(slot.Module);

            if (DimensionSettings.Debug)
                UnityEngine.Debug.Log($"{DimensionSyncAddon.LogTag} hollow: {slot.Label} wants " +
                                      $"{wantedRaw:F4} but its floor is {edit.minValue:F4}; soft " +
                                      $"coupling drops {bore.Label} {before:F4} -> {bore.Value:F4}, " +
                                      $"new floor {edit.minValue:F4}");
        }

        /// <summary>Ask a ProceduralParts shape to work out its control limits again.</summary>
        /// <param name="module">The shape module that owns the diameters.</param>
        private static void Recompute(PartModule module)
        {
            module?.GetType()
                  .GetMethod("AdjustDimensionBounds",
                             System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                  ?.Invoke(module, null);
        }

        /// <summary>
        /// Move the partner of every inner or outer diameter that just changed, and
        /// report those moves so they propagate like any other change.
        /// </summary>
        /// <param name="nodes">Every tracked part.</param>
        /// <param name="changes">This frame's changes; coupled ones are appended.</param>
        public static void Couple(Dictionary<Part, KspDimensionNode> nodes,
                                  List<DimensionChange> changes, int from = 0)
        {
            // Kept for MakeRoomFor, which runs later in the same frame from inside a
            // write and has only the slot to go on.
            _nodes = nodes;

            if (changes == null || changes.Count == 0) return;

            // Snapshot the count: this appends, and a coupled change must not itself
            // be coupled again. Moving the outer to suit the inner and then the inner
            // to suit the outer is how a pair of dimensions walks away together.
            int original = changes.Count;

            // And start where the caller says, not at the beginning. This is called a
            // second time for what the nesting rule moved, and without a starting
            // point that second call re-couples everything the first one already did:
            // a proportional bore went to 2.25 where 1.5 was wanted, and a nested
            // hollow part ran away to its host's own diameter. Eight scenarios failed
            // on a change that was meant to fix one.
            for (int i = Mathf.Max(from, 0); i < original; i++)
            {
                DimensionChange change = changes[i];
                KspDimensionSlot moved = change.Slot;
                if (moved?.Part == null) continue;

                string channel = moved.Descriptor?.Channel;
                bool isInner = channel == Channels.Inner;
                bool isOuter = channel == Channels.Outer;
                if (!isInner && !isOuter) continue;

                if (!nodes.TryGetValue(moved.Part, out KspDimensionNode node)) continue;

                KspDimensionSlot partner = PartnerOf(node, moved, isInner);
                if (partner == null) continue;

                float inner = isInner ? change.NewValue : partner.Value;
                float outer = isInner ? partner.Value : change.NewValue;
                float wasInner = isInner ? change.OldValue : partner.Value;
                float wasOuter = isInner ? partner.Value : change.OldValue;

                float wanted = Wanted(DimensionSettings.Hollow, isInner,
                                      wasInner, wasOuter, inner, outer, partner);
                if (float.IsNaN(wanted)) continue;

                float before = partner.Value;
                if (Mathf.Abs(wanted - before) <= 1e-4f) continue;

                float took = partner.SetValue(wanted);
                if (Mathf.Abs(took - before) <= 1e-4f) continue;

                if (DimensionSettings.Debug)
                    UnityEngine.Debug.Log($"{DimensionSyncAddon.LogTag} hollow: {moved.Label} " +
                                          $"{change.OldValue:F4} -> {change.NewValue:F4} " +
                                          $"({DimensionSettings.Hollow}) moves {partner.Label} " +
                                          $"{before:F4} -> {took:F4}");

                changes.Add(new DimensionChange
                {
                    Slot = partner,
                    OldValue = before,
                    NewValue = took,
                });
            }
        }

        /// <summary>
        /// Where the partner diameter should end up, or NaN to leave it alone.
        /// </summary>
        /// <param name="mode">The relationship the player asked for.</param>
        /// <param name="movedInner">True when it was the inner diameter that moved.</param>
        /// <param name="wasInner">The inner diameter before the change.</param>
        /// <param name="wasOuter">The outer diameter before the change.</param>
        /// <param name="inner">The inner diameter now.</param>
        /// <param name="outer">The outer diameter now.</param>
        /// <param name="partner">The field about to be written, for its own limits.</param>
        private static float Wanted(HollowMode mode, bool movedInner,
                                    float wasInner, float wasOuter,
                                    float inner, float outer, KspDimensionSlot partner)
        {
            switch (mode)
            {
                case HollowMode.Proportional:
                {
                    // The ratio the player had. Meaningless if either side was zero,
                    // in which case there is no ratio to keep and this does nothing.
                    if (wasOuter <= 1e-4f || wasInner <= 1e-4f) return float.NaN;
                    float ratio = wasInner / wasOuter;
                    return movedInner ? inner / ratio : outer * ratio;
                }

                case HollowMode.Constant:
                {
                    float gap = wasOuter - wasInner;
                    if (gap <= 0f) return float.NaN;
                    // A shrinking outer eventually cannot hold the gap: the inner
                    // reaches its own floor and after that the wall is what gives.
                    return movedInner ? inner + gap : Mathf.Max(outer - gap, 0f);
                }

                default:
                {
                    // Hard and soft both leave the partner alone here.
                    //
                    // Hard by definition: the bore never moves and a change that will
                    // not fit around it stops short. Soft moves it too, but not from
                    // here - it acts in MakeRoomFor, before the write is clamped,
                    // because that is the only moment at which anyone knows a value
                    // was asked for and refused. By the time a change reaches this
                    // rule it reports the clamped number and there is nothing left to
                    // react to.
                    //
                    // The collision the other way round - a bore raised past the
                    // outside - cannot happen either: PP caps the bore's control at
                    // the outer diameter less the same least wall.
                    return float.NaN;
                }
            }
        }

        /// <summary>The other diameter of the same wall, or null if the part has none.</summary>
        /// <param name="node">The part's node.</param>
        /// <param name="moved">The dimension that changed.</param>
        /// <param name="movedInner">True when that was the inner one.</param>
        /// <remarks>
        /// Matched on the ends as well as the channel, so a hollow cone's top pairs
        /// with its top and not with its bottom.
        /// </remarks>
        private static KspDimensionSlot PartnerOf(KspDimensionNode node, KspDimensionSlot moved,
                                                  bool movedInner)
        {
            string wanted = movedInner ? Channels.Outer : Channels.Inner;
            IList<IDimensionSlot> slots = node.Slots;

            for (int i = 0; i < slots.Count; i++)
            {
                if (!(slots[i] is KspDimensionSlot slot)) continue;
                if (!slot.IsAdjustable) continue;
                if (slot.Descriptor?.Channel != wanted) continue;
                if (slot.Descriptor.Ends != moved.Descriptor.Ends) continue;
                if (slot.Part != moved.Part) continue;
                return slot;
            }
            return null;
        }

    }
}
