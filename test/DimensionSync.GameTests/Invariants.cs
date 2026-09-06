using System.Collections.Generic;
using UnityEngine;

namespace DimensionSync.GameTests
{
    /// <summary>
    /// Things that must be true of any craft, checked after every settle rather than
    /// where somebody thought to ask.
    /// </summary>
    /// <remarks>
    /// Every scenario asserts the quantity it was written about, and the bugs found by
    /// hand in this mod have almost all been in quantities nobody chose to assert - a
    /// flap's thickness at its tip when the test looked at its root, a surface's place
    /// ALONG its wing when the test measured its distance across to the edge. A list of
    /// fields cannot close that gap either, because the worst of them were relations
    /// rather than values: every number individually defensible and a mirrored pair
    /// disagreeing by two thirds of a metre.
    ///
    /// So these are relations, they are cheap, and they run everywhere. The mirrored
    /// pair check alone would have caught the drift reported from a walkthrough at
    /// 0.015 m on the first step, instead of at 0.671 m several steps later by somebody
    /// looking at the screen.
    /// </remarks>
    internal static class Invariants
    {
        /// <summary>How far apart a mirrored pair may sit before it is worth saying.</summary>
        /// <remarks>
        /// Loose enough to ignore the millimetre a craft is built with - the demo craft
        /// loads with its pairs 0.0004 apart - and tight enough that the smallest drift
        /// found so far, 0.015 m on a first step, is caught.
        /// </remarks>
        private const float MirrorTolerance = 0.01f;

        /// <summary>The B9 dimensions worth comparing between mirrored counterparts.</summary>
        private static readonly string[] Compared =
        {
            "sharedBaseLength",
            "sharedBaseWidthRoot",
            "sharedBaseWidthTip",
            "sharedBaseOffsetRoot",
            "sharedBaseOffsetTip",
            "sharedBaseThicknessRoot",
            "sharedBaseThicknessTip",
        };

        /// <summary>Check what must hold of the ship as it stands, and report what does not.</summary>
        /// <param name="context">The running scenario, for recording failures.</param>
        /// <param name="where">What had just happened, so a failure says when.</param>
        public static void Check(TestContext context, string where)
        {
            ShipConstruct ship = EditorLogic.fetch?.ship;
            if (ship == null || ship.parts.Count == 0) return;

            var complaints = new List<string>();
            NothingIsNaN(ship, complaints);
            MirroredPairsAgree(ship, complaints);

            for (int i = 0; i < complaints.Count; i++)
                context.Result?.Fail($"after {where}: {complaints[i]}");
        }

        /// <summary>
        /// No tracked dimension has gone to NaN or infinity.
        /// </summary>
        /// <remarks>
        /// A NaN spreads: it is written on, propagated to a neighbour, and by the time
        /// anything looks odd on screen the arithmetic that produced it is long past.
        /// Caught where it appears, it names the part and the field.
        /// </remarks>
        private static void NothingIsNaN(ShipConstruct ship, List<string> complaints)
        {
            foreach (Part part in ship.parts)
            {
                PartModule wing = Wing(part);
                if (wing == null) continue;

                for (int i = 0; i < Compared.Length; i++)
                {
                    float value = PartFields.Get(part, "WingProcedural", Compared[i]);
                    if (float.IsNaN(value) || float.IsInfinity(value))
                        complaints.Add($"{part.name} has {Compared[i]} = {value}");
                }
            }
        }

        /// <summary>
        /// Two parts built as mirrors of one another hold the same dimensions, and sit
        /// at the same place along their own wings.
        /// </summary>
        /// <remarks>
        /// The station is the half that matters and the half no gap check can see: a
        /// surface that slides ALONG its wing stays perfectly flush and reads as
        /// correctly placed by every measurement taken across to the edge. That is how
        /// a drift reaching 0.671 m survived in a scenario that was passing.
        /// </remarks>
        private static void MirroredPairsAgree(ShipConstruct ship, List<string> complaints)
        {
            foreach (Part part in ship.parts)
            {
                if (Wing(part) == null || part.symmetryCounterparts == null) continue;

                for (int i = 0; i < part.symmetryCounterparts.Count; i++)
                {
                    Part twin = part.symmetryCounterparts[i];
                    if (twin == null || Wing(twin) == null) continue;

                    // Once per pair, by taking the lower id, so a disagreement is
                    // reported as one complaint rather than as two saying the same
                    // thing from opposite ends.
                    if (twin.GetInstanceID() < part.GetInstanceID()) continue;

                    for (int f = 0; f < Compared.Length; f++)
                    {
                        float mine = PartFields.Get(part, "WingProcedural", Compared[f]);
                        float theirs = PartFields.Get(twin, "WingProcedural", Compared[f]);
                        if (float.IsNaN(mine) || float.IsNaN(theirs)) continue;
                        if (Mathf.Abs(mine - theirs) <= MirrorTolerance) continue;

                        complaints.Add($"a mirrored pair disagrees about {Compared[f]}: " +
                                       $"#{part.GetInstanceID()} has {mine:F4}, " +
                                       $"#{twin.GetInstanceID()} has {theirs:F4}");
                    }

                    if (part.parent == null || twin.parent == null) continue;

                    // By distance, not by signed coordinate: a mirrored part sits at
                    // the mirrored station, so the two read as equal and opposite when
                    // they agree. What matters is how far out along the wing each one
                    // is, and that is the same number on both sides.
                    float here = Mathf.Abs(part.parent.transform
                        .InverseTransformPoint(part.transform.position).x);
                    float there = Mathf.Abs(twin.parent.transform
                        .InverseTransformPoint(twin.transform.position).x);
                    if (Mathf.Abs(here - there) > MirrorTolerance)
                        complaints.Add($"a mirrored pair of {part.name} sits at different " +
                                       $"stations along their wings: #{part.GetInstanceID()} " +
                                       $"at {here:F4} on {part.parent.name}, " +
                                       $"#{twin.GetInstanceID()} at {there:F4} " +
                                       $"on {twin.parent.name}");
                }
            }
        }

        /// <summary>The WingProcedural module on a part, or null.</summary>
        private static PartModule Wing(Part part)
        {
            if (part == null) return null;
            for (int i = 0; i < part.Modules.Count; i++)
                if (part.Modules[i] != null && part.Modules[i].GetType().Name == "WingProcedural")
                    return part.Modules[i];
            return null;
        }
    }
}
