using UnityEngine;

namespace DimensionSync.GameTests
{
    /// <summary>
    /// Records whether Unity delivers a mouse hover to the GameObject it sits on.
    /// </summary>
    /// <remarks>
    /// B9 arms a drag from OnMouseOver, and when that never happens there are two
    /// quite different reasons: B9 declined the input, or Unity never handed it the
    /// message. From outside the process those look identical - the drag simply does
    /// nothing - and no amount of aiming more carefully tells them apart.
    ///
    /// Unity delivers OnMouseOver by SendMessage to a GameObject picked from the
    /// collider it hit. Measured here, that object is the PART ROOT and not the child
    /// carrying the collider - the opposite of what reasoning about it predicted, and
    /// the reason this probe exists rather than an argument.
    /// </remarks>
    public class HoverProbe : MonoBehaviour
    {
        /// <summary>How many times Unity has sent this object a hover.</summary>
        public int Overs;

        /// <summary>How many times the pointer has arrived on this object.</summary>
        public int Enters;

        private void OnMouseOver() { Overs++; }

        private void OnMouseEnter() { Enters++; }

        /// <summary>Attaches a probe to a GameObject, or returns the one already there.</summary>
        public static HoverProbe Attach(GameObject target)
        {
            return target.GetComponent<HoverProbe>() ?? target.AddComponent<HoverProbe>();
        }

        /// <summary>
        /// Probes a part's root and every collider under it, named by where each sits.
        /// </summary>
        public static string Watch(Part part)
        {
            Attach(part.gameObject);
            foreach (Collider collider in part.GetComponentsInChildren<Collider>(true))
                Attach(collider.gameObject);
            return Report(part);
        }

        /// <summary>Total hovers heard anywhere under a part.</summary>
        public static int TotalOvers(Part part)
        {
            int total = 0;
            foreach (HoverProbe probe in part.GetComponentsInChildren<HoverProbe>(true))
                total += probe.Overs;
            return total;
        }

        /// <summary>What every probe under a part has heard.</summary>
        public static string Report(Part part)
        {
            var text = new System.Text.StringBuilder();
            foreach (HoverProbe probe in part.GetComponentsInChildren<HoverProbe>(true))
                text.Append($"[{probe.gameObject.name} " +
                            $"{(probe.gameObject == part.gameObject ? "ROOT" : "child")} " +
                            $"overs {probe.Overs} enters {probe.Enters}] ");
            return text.Length == 0 ? "no probes" : text.ToString();
        }
    }
}
