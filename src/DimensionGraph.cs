using System;
using System.Collections.Generic;

namespace DimensionSync
{
    /// <summary>
    /// One adjustable dimension on one part.
    /// </summary>
    /// <remarks>
    /// Deliberately free of any KSP type so that the propagation rules can be
    /// exercised without the game running; <c>KspDimensionSlot</c> is the real
    /// implementation.
    /// </remarks>
    internal interface IDimensionSlot
    {
        /// <summary>What this dimension means: channel, joint kind, which end it describes.</summary>
        DimensionDescriptor Descriptor { get; }

        /// <summary>False when the field is hidden, its module is switched off, or it has no editor control.</summary>
        bool IsAdjustable { get; }

        /// <summary>
        /// The current value in the units the propagator works in: metres of
        /// diameter for a stack dimension, metres for a wing chord or thickness.
        /// </summary>
        float Value { get; }

        /// <summary>
        /// Write <paramref name="value"/> and return what actually took effect,
        /// which may differ if the field clamped it.
        /// </summary>
        float SetValue(float value);

        /// <summary>Identifies this dimension in the log.</summary>
        string Label { get; }
    }

    /// <summary>A joint between two parts that a dimension can travel over.</summary>
    internal struct AxialLink
    {
        /// <summary>The part on the far side of the joint.</summary>
        public IDimensionNode Other;

        /// <summary>Which end of *this* part the joint leaves from.</summary>
        public PartEnd MyEnd;

        /// <summary>Which end of the neighbour the joint arrives at.</summary>
        public PartEnd OtherEnd;

        /// <summary>What kind of joint this is; a dimension only follows its own kind.</summary>
        public LinkKind Kind;

        /// <summary>
        /// Converts a value on this part's side of the joint into the matching value
        /// on the other's, or null when the joint carries values unchanged.
        /// </summary>
        /// <remarks>
        /// Needed when the two parts do not line up end to end. A control surface
        /// covering the outboard half of a wing should be as thick as the wing is
        /// half way along, not as thick as the wing's root, so the value crossing
        /// that joint is interpolated along the span rather than copied.
        /// </remarks>
        public Func<DimensionDescriptor, float, float> Transform;

        /// <summary>Describe one joint as seen from one of the two parts it connects.</summary>
        public AxialLink(IDimensionNode other, PartEnd myEnd, PartEnd otherEnd,
                         LinkKind kind = LinkKind.Stack,
                         Func<DimensionDescriptor, float, float> transform = null)
        {
            Other = other;
            MyEnd = myEnd;
            OtherEnd = otherEnd;
            Kind = kind;
            Transform = transform;
        }
    }

    /// <summary>One part in the propagation graph.</summary>
    internal interface IDimensionNode
    {
        /// <summary>Identifies this part in the log.</summary>
        string Label { get; }

        /// <summary>Every dimension this part has.</summary>
        IList<IDimensionSlot> Slots { get; }

        /// <summary>Every joint leaving this part, of whatever kind.</summary>
        IEnumerable<AxialLink> Links { get; }
    }

    /// <summary>What to do with a neighbour that was near, but not exactly, the same size.</summary>
    /// <summary>
    /// How the two surfaces of a hollow arrangement follow one another: a part's own
    /// inner and outer diameters, or the clearance between a part and one nested
    /// inside it.
    /// </summary>
    internal enum HollowMode
    {
        /// <summary>
        /// The bore never moves. A change that cannot fit around it stops short.
        /// </summary>
        /// <remarks>
        /// The default, because it is the only mode that never changes a number the
        /// player did not ask about. A stack change refused by the bore leaves the
        /// craft visibly mismatched - a 1.00 m tank sitting on a 2.01 m one - which is
        /// information, not damage: something the player asked for did not fit, and
        /// they can see exactly where.
        /// </remarks>
        HardIndependent = 0,

        /// <summary>
        /// The bore holds until the outside needs the room, and then gives up exactly
        /// as much as it must.
        /// </summary>
        /// <remarks>
        /// Same promise as hard for every change that fits. The difference shows only
        /// where hard would stop short: the bore drops to the outer diameter less the
        /// least wall the part allows, so the outside can go where it was sent and the
        /// stack matches.
        /// </remarks>
        SoftIndependent = 1,

        /// <summary>Hold the ratio: a bore half the outer diameter stays half of it.</summary>
        Proportional = 2,

        /// <summary>
        /// Hold the difference: the wall, or the clearance, stays the thickness it was.
        /// </summary>
        /// <remarks>
        /// Until the outer runs out of room. A shrinking outer diameter drives the
        /// inner one down to its own minimum and then the wall has to give, because
        /// the alternative is an inner surface outside the outer one.
        /// </remarks>
        Constant = 3,
    }

    internal enum MarginMode
    {
        /// <summary>Bring it to the new size exactly, closing the gap.</summary>
        None = 0,

        /// <summary>Keep the gap as a fixed amount: 3.02 against 3.00 going to 6.00 lands at 6.02.</summary>
        Absolute = 1,

        /// <summary>Keep the gap as a ratio: 3.02 against 3.00 going to 6.00 lands at 6.04.</summary>
        Proportional = 2,
    }

    /// <summary>
    /// Walks a run of connected parts applying a dimension change, stopping
    /// wherever the existing value differs from the value that was in place before
    /// the change.
    /// </summary>
    internal class DimensionPropagator
    {
        /// <summary>
        /// How far apart two parts may be and still count as the same size, as a
        /// fraction of the larger of them. Parts that were meant to match are rarely
        /// identical to the last decimal, and a run should not stop dead because a
        /// neighbour is a hundredth of a metre out.
        /// </summary>
        public float MatchTolerance = 0.01f;

        /// <summary>
        /// The smallest movement in a field that counts as a change at all. Kept far
        /// below <see cref="MatchTolerance"/> on purpose: a slider dragged in
        /// millimetre steps must still be noticed, even though a millimetre is well
        /// inside what counts as the same size.
        /// </summary>
        public float ChangeEpsilon = 1e-4f;

        /// <summary>What to do about a neighbour that was near but not exactly equal.</summary>
        public MarginMode Margin = MarginMode.None;

        /// <summary>
        /// When true, a part that has nothing on the channel being propagated (a
        /// stock decoupler in a run of procedural tanks, say) does not stop the
        /// walk; the change carries on to whatever is attached on its far side.
        /// </summary>
        public bool PassThroughRigidParts;

        /// <summary>
        /// Optional sink for a running commentary of the walk. Left null unless
        /// DIMENSION_SYNC/debug is on, so the string interpolation is skipped.
        /// </summary>
        public Action<string> Log { get; set; }

        /// <summary>
        /// Propagate a change that has already been applied to <paramref name="origin"/>.
        /// </summary>
        /// <param name="origin">The part whose dimension the player (or another mod) just changed.</param>
        /// <param name="descriptor">Descriptor of the field that changed.</param>
        /// <param name="oldValue">What that field held before the change.</param>
        /// <param name="newValue">What it holds now.</param>
        /// <returns>How many neighbouring fields were updated.</returns>
        public int Propagate(IDimensionNode origin, DimensionDescriptor descriptor,
                             float oldValue, float newValue)
        {
            if (origin == null || descriptor == null) return 0;
            if (!Changed(oldValue, newValue)) return 0;

            // Visits are tracked per end rather than per part, because a part can be
            // reached at each of its ends by a different joint: a control surface
            // covering part of a wing hangs off two connections, one feeding its root
            // and one its tip, and blocking the second would leave half the job done.
            //
            // Both of the origin's ends start out visited: its own field has already
            // been written by whoever made the change, and the walk must not come
            // back and treat it as a neighbour.
            var visited = new HashSet<(IDimensionNode, PartEnd)>
            {
                (origin, PartEnd.Bottom),
                (origin, PartEnd.Top),
                (origin, PartEnd.Both),
            };
            return ContinueOut(origin, descriptor.Ends, descriptor, oldValue, newValue, visited);
        }

        /// <summary>
        /// Arrive at <paramref name="node"/> through its <paramref name="entryEnd"/>
        /// and apply the change if that end still matches the pre-change value.
        /// </summary>
        /// <param name="entryEnd">Which end of this part the joint we came in on meets.</param>
        /// <param name="arrivedOver">
        /// The kind of joint we came in on. It decides whether the change may carry
        /// through to this part's far end.
        /// </param>
        /// <param name="visited">
        /// Part ends already dealt with. The part graph is a tree, so this is really
        /// a backtracking guard, but it also makes a malformed graph terminate.
        /// </param>
        /// <returns>How many fields this part and everything past it updated.</returns>
        private int Walk(IDimensionNode node, PartEnd entryEnd, DimensionDescriptor descriptor,
                         float oldValue, float newValue, HashSet<(IDimensionNode, PartEnd)> visited,
                         LinkKind arrivedOver)
        {
            if (!visited.Add((node, entryEnd))) return 0;

            // Local rather than reused: Walk recurses, and a shared buffer would
            // be a trap for anyone who later moves the recursion earlier.
            var matches = new List<IDimensionSlot>();
            bool hasAnyOnChannel = false;
            bool entryMatches = false;

            // A change arriving end-to-end may carry through to this part's far end,
            // which is what lets a uniform run of parts resize together. A change
            // arriving side-by-side may not: each end of that joint is its own
            // connection, so a wing's tip thickness has no business setting the root
            // thickness of the control surface lying against it. Mirrored channels
            // never carry through either - they describe a joint, not a part.
            bool mayCarryThrough = arrivedOver != LinkKind.Edge && !descriptor.Mirrored;

            // Work out three things in one pass over the part's dimensions: does it
            // have anything on this channel at all, does the end we arrived at still
            // hold the pre-change value, and which slots should be updated.
            IList<IDimensionSlot> slots = node.Slots;
            for (int i = 0; i < slots.Count; i++)
            {
                IDimensionSlot slot = slots[i];
                DimensionDescriptor other = slot.Descriptor;
                if (!slot.IsAdjustable) continue;
                if (!SameChannel(other, descriptor)) continue;
                if (other.Ends == PartEnd.None) continue;
                hasAnyOnChannel = true;

                bool coversEntry = (other.Ends & entryEnd) != 0;
                if (!mayCarryThrough && !coversEntry) continue;
                if (!Same(slot.Value, oldValue)) continue;

                matches.Add(slot);
                if (coversEntry) entryMatches = true;
            }

            if (!entryMatches)
            {
                // The end we arrived at is a different size than before the change:
                // this is where synchronisation stops.
                if (hasAnyOnChannel || !PassThroughRigidParts)
                {
                    Log?.Invoke($"stop at {node.Label} (entry {entryEnd} does not match {oldValue:F4})");
                    return 0;
                }
                // A part with nothing on this channel is transparent.
                return ContinueOut(node, Opposite(entryEnd), descriptor, oldValue, newValue, visited);
            }

            // Write the matches - including any covering the far end, which held the
            // same value before the change and so keep the run synchronised through
            // the part - and remember which ends came out at the value asked for. An
            // end that was clamped short is now a different size than the rest of the
            // run, so the walk must not continue past it.
            PartEnd resized = PartEnd.None;
            int changed = 0;
            for (int i = 0; i < matches.Count; i++)
            {
                IDimensionSlot slot = matches[i];

                // A neighbour that was a little off keeps, closes or scales that gap
                // depending on how the margin is configured. What carries on down the
                // run is still the original pair, so every part measures its own gap
                // against the part that actually changed rather than against the last
                // one adjusted.
                float target = TargetFor(slot.Value, oldValue, newValue);
                float applied = slot.SetValue(target);
                changed++;
                if (Changed(applied, target))
                    Log?.Invoke($"{slot.Label} clamped to {applied:F4}, not propagating past it");
                else
                    resized |= slot.Descriptor.Ends;
            }

            return changed + ContinueOut(node, resized, descriptor, oldValue, newValue, visited);
        }

        /// <summary>
        /// Carry on out of <paramref name="node"/> through every joint that leaves
        /// one of the <paramref name="ends"/> just resized.
        /// </summary>
        /// <param name="ends">Which ends of this part now hold the new value.</param>
        /// <returns>How many fields the rest of the walk updated.</returns>
        private int ContinueOut(IDimensionNode node, PartEnd ends, DimensionDescriptor descriptor,
                                float oldValue, float newValue, HashSet<(IDimensionNode, PartEnd)> visited)
        {
            if (ends == PartEnd.None) return 0;

            int changed = 0;
            foreach (AxialLink link in node.Links)
            {
                // A dimension only follows the kinds of joint it names: diameters
                // travel stack nodes, a wing's chord only travels end-to-end joints,
                // its thickness travels those and side-by-side joints too.
                if ((link.Kind & descriptor.Link) == 0) continue;
                if ((link.MyEnd & ends) == 0) continue;
                if (link.Other == null) continue;
                if (visited.Contains((link.Other, link.OtherEnd))) continue;

                // A mirrored channel flips sign every time it crosses a joint, and a
                // joint whose parts do not line up end to end converts the value on
                // the way over. Both the before and after values are converted, so
                // the far side still recognises what it was holding.
                float sign = descriptor.Mirrored ? -1f : 1f;
                float oldAcross = oldValue * sign;
                float newAcross = newValue * sign;
                if (link.Transform != null)
                {
                    oldAcross = link.Transform(descriptor, oldAcross);
                    newAcross = link.Transform(descriptor, newAcross);
                }

                changed += Walk(link.Other, link.OtherEnd, descriptor,
                                oldAcross, newAcross, visited, link.Kind);
            }
            return changed;
        }

        /// <summary>
        /// Whether two dimensions describe the same quantity travelling over the
        /// same kind of joint - the test that keeps a wing's chord out of its
        /// thickness.
        /// </summary>
        /// <remarks>
        /// With one exception, added deliberately. A bore and an outside diameter are
        /// both a circle around the same axis, and a part can be built to fit either:
        /// a plug sized to the bore of the tank above it is as ordinary as a tank
        /// sized to its outside. So the two channels match each other.
        ///
        /// What keeps that from running wild is the test the caller applies straight
        /// afterwards - a change carries through a part only while its value is still
        /// the one that changed. Inside a single hollow part the bore and the outside
        /// are never equal, so this can never make one of them set the other; it only
        /// bites across a joint, where the neighbour really was built to that size.
        /// </remarks>
        private static bool SameChannel(DimensionDescriptor a, DimensionDescriptor b)
        {
            if ((a.Link & b.Link) == 0) return false;
            if (string.Equals(a.Channel, b.Channel, StringComparison.Ordinal)) return true;
            return RoundTheSameAxis(a.Channel) && RoundTheSameAxis(b.Channel);
        }

        /// <summary>Whether a channel describes a circle about the stack axis.</summary>
        /// <param name="channel">The channel's name.</param>
        private static bool RoundTheSameAxis(string channel) =>
            string.Equals(channel, Channels.Outer, StringComparison.Ordinal)
            || string.Equals(channel, Channels.Inner, StringComparison.Ordinal);

        /// <summary>
        /// The far end of a part. A dimension covering both ends has no far end, so
        /// it reports both and lets the joint filter decide.
        /// </summary>
        private static PartEnd Opposite(PartEnd end)
        {
            switch (end)
            {
                case PartEnd.Top: return PartEnd.Bottom;
                case PartEnd.Bottom: return PartEnd.Top;
                default: return PartEnd.Both;
            }
        }

        /// <summary>
        /// Whether two values count as the same size. The tolerance is treated as
        /// relative above 1, so a 40 m fairing is not held to the same absolute
        /// precision as a 0.6 m probe core. NaN never matches anything, which is
        /// how an unreadable field takes itself out of the comparison.
        /// </summary>
        /// <summary>
        /// What a neighbour currently holding <paramref name="current"/> should become
        /// when the part it follows goes from <paramref name="oldValue"/> to
        /// <paramref name="newValue"/>.
        /// </summary>
        public float TargetFor(float current, float oldValue, float newValue)
        {
            switch (Margin)
            {
                case MarginMode.Absolute:
                    return newValue + (current - oldValue);

                case MarginMode.Proportional:
                    // A zero starting size has no ratio to preserve.
                    return Math.Abs(oldValue) > 1e-6f ? newValue * (current / oldValue) : newValue;

                default:
                    return newValue;
            }
        }

        /// <summary>
        /// Whether two values count as the same size, within
        /// <see cref="MatchTolerance"/> of the larger of them.
        /// </summary>
        /// <remarks>
        /// Relative rather than absolute, so a 40 m fairing and a 0.6 m probe core are
        /// held to the same proportional standard, with a small floor so values near
        /// zero still compare. NaN never matches anything, which is how an unreadable
        /// field takes itself out of the comparison.
        /// </remarks>
        public bool Same(float a, float b)
        {
            float scale = Math.Max(Math.Abs(a), Math.Abs(b));
            return Math.Abs(a - b) <= Math.Max(ChangeEpsilon, MatchTolerance * scale);
        }

        /// <summary>
        /// Whether two values differ by enough to count as a change at all. Far
        /// tighter than <see cref="Same"/>: this is about noticing a field move, not
        /// about deciding whether two parts are the same size.
        /// </summary>
        public bool Changed(float a, float b)
        {
            float scale = Math.Max(Math.Abs(a), Math.Abs(b));
            return Math.Abs(a - b) > Math.Max(ChangeEpsilon, ChangeEpsilon * scale);
        }
    }
}
