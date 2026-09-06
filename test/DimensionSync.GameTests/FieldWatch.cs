using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace DimensionSync.GameTests
{
    /// <summary>
    /// Records every number on every part, and afterwards reports anything that moved
    /// which the scenario did not say was allowed to.
    /// </summary>
    /// <remarks>
    /// The point is to flip the default. A scenario that asserts the three quantities
    /// its author was thinking about is silent about the other four hundred, and this
    /// mod's real bugs have lived in those: a flap whose tip thickness stayed behind
    /// while its root followed, a wing that quietly shortened itself as a side effect
    /// of a rule about offsets. Neither test was wrong about what it checked.
    ///
    /// Deliberately NOT an expected-value table. Declaring what each field should
    /// become is where such suites rot: every rule change invalidates a wall of
    /// numbers, and bulk-updating them back to green is indistinguishable from
    /// deleting the test. Declaring only WHICH fields may move survives a rule change
    /// untouched, because a rule change alters values far more often than it alters
    /// which parts a step is entitled to touch.
    /// </remarks>
    internal class FieldWatch
    {
        /// <summary>What a number may drift by before it counts as having changed.</summary>
        /// <remarks>
        /// Positions wobble in the last few decimal places between frames without
        /// anything having happened. This sits above that and well below the smallest
        /// change any scenario makes on purpose.
        /// </remarks>
        private const float Noise = 1e-3f;

        /// <summary>Fields that move on their own and say nothing about correctness.</summary>
        /// <remarks>
        /// Kept as small as it can be. Every name here is a hole in the check, so the
        /// bar for adding one is that it changes without any part changing - not that
        /// it is inconvenient.
        /// </remarks>
        private static readonly HashSet<string> Ignored = new HashSet<string>
        {
            "temperature", "skinTemperature", "thermalMass", "resourceMass",
            "requestedMass", "physicsMass", "crashTolerance", "maxTemp",
            // B9 recomputes these from the dimensions on every rebuild. They are
            // read-outs, not state: a scenario that has pinned a wing's span has
            // already pinned its semispan and its lift coefficient, and letting them
            // count would mean every step listing five derived numbers per part it
            // legitimately changed - which is how an exception list becomes long
            // enough that nobody reads it.
            "stockLiftCoefficient", "deflectionLiftCoeff",
        };

        /// <summary>Field-name prefixes covered by <see cref="Ignored"/>.</summary>
        private static readonly string[] IgnoredPrefixes = { "aeroStat", "aeroUI", "display" };

        /// <summary>Whether a field is a derived read-out rather than state.</summary>
        private static bool Boring(string name)
        {
            if (Ignored.Contains(name)) return true;
            for (int i = 0; i < IgnoredPrefixes.Length; i++)
                if (name.StartsWith(IgnoredPrefixes[i], StringComparison.Ordinal)) return true;
            return false;
        }

        private readonly TestContext _context;
        private readonly Dictionary<string, float> _before = new Dictionary<string, float>();
        private readonly HashSet<string> _allowedFields = new HashSet<string>();
        private readonly HashSet<int> _allowedParts = new HashSet<int>();
        private readonly Dictionary<int, string> _names = new Dictionary<int, string>();

        public FieldWatch(TestContext context)
        {
            _context = context;
            Read(_before);
        }

        /// <summary>Say that one field on one part is entitled to change.</summary>
        /// <param name="part">The part the step is expected to affect.</param>
        /// <param name="field">The field on it that may move.</param>
        public FieldWatch Allow(Part part, string field)
        {
            foreach (Part each in WithCounterparts(part)) _allowedFields.Add(Key(each, field));
            return this;
        }

        /// <summary>Say that a whole part is entitled to change, in any way.</summary>
        /// <param name="part">The part to stop watching.</param>
        /// <remarks>
        /// Blunter than naming fields, and worth reaching for only where the step
        /// really does rebuild a part wholesale. Naming fields is what catches a rule
        /// reaching one field further than it should on a part it was right to touch.
        /// </remarks>
        public FieldWatch AllowAnything(Part part)
        {
            foreach (Part each in WithCounterparts(part)) _allowedParts.Add(each.GetInstanceID());
            return this;
        }

        /// <summary>Say that a part is entitled to move on its parent.</summary>
        /// <param name="part">The part allowed to shift.</param>
        public FieldWatch AllowMove(Part part)
        {
            return Allow(part, "@x").Allow(part, "@y").Allow(part, "@z");
        }

        /// <summary>A part together with everything mirrored from it.</summary>
        /// <remarks>
        /// Allowing a part always allows its counterparts. A step that is entitled to
        /// change a wing is entitled to change the wing on the other side, and making
        /// scenarios name both would be noise that says nothing - the two agreeing is
        /// already enforced, everywhere, by <see cref="Invariants"/>.
        /// </remarks>
        private static IEnumerable<Part> WithCounterparts(Part part)
        {
            if (part == null) yield break;
            yield return part;
            if (part.symmetryCounterparts == null) yield break;
            for (int i = 0; i < part.symmetryCounterparts.Count; i++)
                if (part.symmetryCounterparts[i] != null) yield return part.symmetryCounterparts[i];
        }

        /// <summary>
        /// Fail the scenario if anything moved that was not allowed to.
        /// </summary>
        /// <param name="what">What the step was, so a failure says which one.</param>
        public void NothingElseChanged(string what)
        {
            var now = new Dictionary<string, float>();
            Read(now);

            var strays = new List<string>();
            foreach (var pair in now)
            {
                float was;
                if (!_before.TryGetValue(pair.Key, out was)) continue;   // a new part is not a stray
                if (Mathf.Abs(pair.Value - was) <= Noise) continue;
                if (_allowedFields.Contains(pair.Key)) continue;
                if (_allowedParts.Contains(PartIdOf(pair.Key))) continue;
                strays.Add($"{Describe(pair.Key)} {was:F4} -> {pair.Value:F4}");
            }

            // Reported as one failure rather than as one per field. A rule that runs
            // over a whole wing moves a dozen numbers at once, and a dozen failures
            // saying the same thing buries the run's other results.
            if (strays.Count == 0) return;
            strays.Sort(StringComparer.Ordinal);
            var said = new StringBuilder();
            said.Append($"{what}: {strays.Count} value(s) changed that the step did not claim:");
            for (int i = 0; i < strays.Count && i < 40; i++) said.Append("\n    " + strays[i]);
            if (strays.Count > 40) said.Append($"\n    ... and {strays.Count - 12} more");
            _context.Result?.Fail(said.ToString());
        }

        /// <summary>Read every number on every part of the ship into <paramref name="into"/>.</summary>
        private void Read(Dictionary<string, float> into)
        {
            ShipConstruct ship = EditorLogic.fetch?.ship;
            if (ship == null) return;

            foreach (Part part in ship.parts)
            {
                if (part == null) continue;
                int id = part.GetInstanceID();
                if (!_names.ContainsKey(id)) _names[id] = part.name;

                // Where the part sits on its parent, which no module field records and
                // which is exactly where a surface sliding along its wing shows up.
                if (part.parent != null)
                {
                    Vector3 local = part.parent.transform
                        .InverseTransformPoint(part.transform.position);
                    into[Key(part, "@x")] = local.x;
                    into[Key(part, "@y")] = local.y;
                    into[Key(part, "@z")] = local.z;
                }

                for (int m = 0; m < part.Modules.Count; m++)
                {
                    PartModule module = part.Modules[m];
                    if (module == null || module.Fields == null) continue;

                    for (int f = 0; f < module.Fields.Count; f++)
                    {
                        BaseField field = module.Fields[f];
                        if (field == null || Boring(field.name)) continue;

                        object value;
                        try { value = field.GetValue(field.host); }
                        catch { continue; }

                        float number;
                        if (value is float) number = (float)value;
                        else if (value is double) number = (float)(double)value;
                        else if (value is int) number = (int)value;
                        else if (value is bool) number = ((bool)value) ? 1f : 0f;
                        else continue;

                        if (float.IsNaN(number) || float.IsInfinity(number)) continue;
                        into[Key(part, field.name)] = number;
                    }
                }
            }
        }

        private static string Key(Part part, string field)
        {
            return part.GetInstanceID() + "/" + field;
        }

        private static int PartIdOf(string key)
        {
            int slash = key.IndexOf('/');
            int id;
            return int.TryParse(key.Substring(0, slash), out id) ? id : 0;
        }

        private string Describe(string key)
        {
            int slash = key.IndexOf('/');
            int id = PartIdOf(key);
            string name;
            if (!_names.TryGetValue(id, out name)) name = "part";
            string field = key.Substring(slash + 1);
            if (field == "@x") field = "its place along its parent";
            else if (field == "@y") field = "its place across its parent";
            else if (field == "@z") field = "its place through its parent";
            return $"{name} #{id} {field}";
        }
    }
}
