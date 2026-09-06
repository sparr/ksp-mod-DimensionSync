using UnityEngine;

namespace DimensionSync
{
    /// <summary>
    /// One ordered line per part this mod physically moves, in the frame of the host
    /// it is mounted on.
    /// </summary>
    /// <remarks>
    /// Five separate rules move control surfaces about - the wing joint, the
    /// reanchor, the edge follow, the sweep turn and the hinge slide - and each used
    /// to log its own business in its own words, saying what it decided rather than
    /// what it did. Working backwards from where a part ended up therefore meant
    /// guessing which of the five had had a hand in it, and four attempts at one bug
    /// went wrong that way: each accounted for two of the movers and shifted the
    /// error onto the other three.
    ///
    /// Measured in the HOST's local frame on purpose. That is what makes the two
    /// halves of a mirrored pair directly comparable - "0.4 m further out along your
    /// own wing" is the same statement on both sides, where world coordinates are
    /// equal and opposite and every comparison has to remember which.
    /// </remarks>
    internal static class MoveLedger
    {
        /// <summary>Record that a rule has just moved a part.</summary>
        /// <param name="rule">Which rule did it, in one word.</param>
        /// <param name="part">The part that moved.</param>
        /// <param name="host">What to measure against; the part's parent if null.</param>
        /// <param name="worldBefore">Its world position taken before the move.</param>
        /// <param name="detail">The numbers that rule decided from.</param>
        public static void Note(string rule, Part part, Part host, Vector3 worldBefore, string detail)
        {
            if (!DimensionSettings.Debug || part == null) return;

            Part frame = host ?? part.parent;
            Transform against = frame == null ? null : frame.transform;
            Vector3 before = against == null ? worldBefore : against.InverseTransformPoint(worldBefore);
            Vector3 after = against == null
                ? part.transform.position
                : against.InverseTransformPoint(part.transform.position);

            // A rule that decided to move a part and then moved it nowhere is worth a
            // line: "considered and declined" and "never looked" are the two cases
            // that reading the old logs could not tell apart.
            Vector3 moved = after - before;
            UnityEngine.Debug.Log($"{DimensionSyncAddon.LogTag} MOVE f={Time.frameCount} {rule} " +
                                  $"#{part.GetInstanceID()} on #{(frame == null ? 0 : frame.GetInstanceID())} " +
                                  $"({before.x:F4},{before.y:F4},{before.z:F4}) -> " +
                                  $"({after.x:F4},{after.y:F4},{after.z:F4}) " +
                                  $"d=({moved.x:F4},{moved.y:F4},{moved.z:F4}) | {detail}");
        }
    }
}
