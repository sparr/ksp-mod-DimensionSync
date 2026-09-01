using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace DimensionSync.GameTests
{
    /// <summary>
    /// Paints neighbouring parts different colours so a fixture reads at a glance.
    /// </summary>
    /// <remarks>
    /// Several of these tests turn on whether a change stopped at the right part,
    /// and a stack of identical grey tanks makes that impossible to see. Colour is
    /// the cheapest way to make each part in a fixture tell itself apart from the
    /// one it is attached to.
    ///
    /// Everything here goes through reflection. Recolouring belongs to Textures
    /// Unlimited, which is a dependency of some of the mods under test rather than
    /// of the tests themselves, and a run without it should lose the colours rather
    /// than fail to compile or throw.
    /// </remarks>
    internal static class Recolour
    {
        /// <summary>The interface a recolourable PartModule implements, by name.</summary>
        private const string RecolorableInterface = "KSPShaderTools.IRecolorable";

        /// <summary>Cached reflection handles, or null once we know TU is absent.</summary>
        private static MethodInfo _getSectionNames;
        private static MethodInfo _getSectionColors;
        private static MethodInfo _setSectionColors;
        private static FieldInfo _colorField;

        /// <summary>True once we have looked for Textures Unlimited, either way.</summary>
        private static bool _looked;

        /// <summary>
        /// Colours to hand out, in order. Chosen to stay apart from each other and
        /// from the editor's grey and blue backdrop.
        /// </summary>
        private static readonly Color[] Palette =
        {
            new Color(0.85f, 0.85f, 0.88f),   // white
            new Color(0.80f, 0.25f, 0.20f),   // red
            new Color(0.20f, 0.45f, 0.80f),   // blue
            new Color(0.90f, 0.70f, 0.15f),   // amber
            new Color(0.25f, 0.65f, 0.35f),   // green
            new Color(0.55f, 0.35f, 0.70f),   // violet
        };

        /// <summary>
        /// Give every part on the ship a colour different from the one it is
        /// attached to.
        /// </summary>
        /// <remarks>
        /// Each part takes the first colour that is not already on its parent or on
        /// a sibling beside it. Depth alone is not enough: a booster on a tank's
        /// flank and the tank stacked on top of it are the same distance from the
        /// root, so they came out the same colour while sitting right next to each
        /// other.
        ///
        /// The walk is ordered by depth so a parent is always coloured before its
        /// children, and the ship's own part order breaks ties, which keeps the
        /// answer the same every time it is asked.
        /// </remarks>
        public static void ApplyToShip()
        {
            if (EditorLogic.fetch?.ship == null) return;

            var parts = new List<Part>();
            foreach (Part part in EditorLogic.fetch.ship.Parts)
                if (part != null) parts.Add(part);
            parts.Sort((a, b) => DepthOf(a).CompareTo(DepthOf(b)));

            var chosen = new Dictionary<Part, int>();
            foreach (Part part in parts)
            {
                // Symmetry counterparts are the same part as far as the player is
                // concerned, so they get the same colour - two boosters that are
                // mirror images of each other looking different from one another
                // would say something untrue about the craft.
                int shared = ColourOfCounterpart(part, chosen);
                if (shared >= 0)
                {
                    chosen[part] = shared;
                    Apply(part, Palette[shared]);
                    continue;
                }

                var taken = new HashSet<int>();
                if (part.parent != null && chosen.TryGetValue(part.parent, out int parentColour))
                {
                    taken.Add(parentColour);

                    // Anything else already hanging off the same parent.
                    for (int i = 0; i < part.parent.children.Count; i++)
                    {
                        Part sibling = part.parent.children[i];
                        if (sibling != part && sibling != null
                            && chosen.TryGetValue(sibling, out int siblingColour))
                            taken.Add(siblingColour);
                    }
                }

                int colour = 0;
                while (colour < Palette.Length - 1 && taken.Contains(colour)) colour++;

                chosen[part] = colour;
                Apply(part, Palette[colour]);
            }
        }

        /// <summary>The colour already given to one of this part's symmetry counterparts.</summary>
        /// <returns>-1 when it has none, or none of them has been coloured yet.</returns>
        private static int ColourOfCounterpart(Part part, Dictionary<Part, int> chosen)
        {
            if (part.symmetryCounterparts == null) return -1;

            for (int i = 0; i < part.symmetryCounterparts.Count; i++)
            {
                Part twin = part.symmetryCounterparts[i];
                if (twin != null && chosen.TryGetValue(twin, out int colour)) return colour;
            }
            return -1;
        }

        /// <summary>How many attachments separate a part from the ship's root.</summary>
        /// <remarks>
        /// Counted with a cap rather than a plain loop: a malformed ship with a
        /// parent cycle in it would otherwise hang the whole run.
        /// </remarks>
        private static int DepthOf(Part part)
        {
            int depth = 0;
            for (Part walk = part.parent; walk != null && depth < 200; walk = walk.parent) depth++;
            return depth;
        }

        /// <summary>Paint one part, if it can be painted.</summary>
        /// <param name="part">The part to recolour.</param>
        /// <param name="colour">The colour to give every one of its sections.</param>
        /// <returns>False when the part has no recolouring module, or TU is absent.</returns>
        public static bool Apply(Part part, Color colour)
        {
            if (part == null || !Ready()) return false;

            // RO parts drive their own appearance from their config and repaint
            // themselves when they are resized, so a colour set here would not last
            // and would hide what the part is actually doing.
            if (IsRealismOverhaul(part)) return false;

            bool painted = false;
            for (int i = 0; i < part.Modules.Count; i++)
            {
                PartModule module = part.Modules[i];
                if (module == null || !Implements(module.GetType())) continue;
                painted |= PaintModule(module, colour);
            }
            return painted;
        }

        /// <summary>Set every section of one recolourable module to a colour.</summary>
        /// <remarks>
        /// The existing entries are read back and only their colour replaced, so the
        /// specular, metallic and detail values the part shipped with survive.
        /// RecoloringData is a struct, so each entry has to be boxed out of the
        /// array, edited, and put back.
        /// </remarks>
        private static bool PaintModule(PartModule module, Color colour)
        {
            try
            {
                var sections = (string[])_getSectionNames.Invoke(module, null);
                if (sections == null) return false;

                foreach (string section in sections)
                {
                    var colours = (Array)_getSectionColors.Invoke(module, new object[] { section });
                    if (colours == null) continue;

                    for (int i = 0; i < colours.Length; i++)
                    {
                        object entry = colours.GetValue(i);
                        _colorField.SetValue(entry, colour);
                        colours.SetValue(entry, i);
                    }
                    _setSectionColors.Invoke(module, new object[] { section, colours });
                }
                return true;
            }
            catch (Exception error)
            {
                Harness.Log($"could not recolour {module.part.name}: {error.Message}");
                return false;
            }
        }

        /// <summary>Whether a type implements Textures Unlimited's recolouring interface.</summary>
        private static bool Implements(Type type)
        {
            foreach (Type contract in type.GetInterfaces())
                if (contract.FullName == RecolorableInterface) return true;
            return false;
        }

        /// <summary>Whether a part belongs to one of the RO mods.</summary>
        private static bool IsRealismOverhaul(Part part)
        {
            for (int i = 0; i < part.Modules.Count; i++)
            {
                string name = part.Modules[i]?.GetType().Name;
                if (name != null && name.StartsWith("ModuleRO", StringComparison.Ordinal)) return true;
            }
            return false;
        }

        /// <summary>
        /// Find the recolouring methods once, and remember if they are not there.
        /// </summary>
        private static bool Ready()
        {
            if (_looked) return _setSectionColors != null;
            _looked = true;

            Type contract = null;
            Type data = null;
            foreach (AssemblyLoader.LoadedAssembly loaded in AssemblyLoader.loadedAssemblies)
            {
                contract = contract ?? loaded.assembly.GetType(RecolorableInterface, false);
                data = data ?? loaded.assembly.GetType("KSPShaderTools.RecoloringData", false);
                if (contract != null && data != null) break;
            }
            if (contract == null || data == null)
            {
                Harness.Log("Textures Unlimited is not installed; fixtures will not be recoloured");
                return false;
            }

            _getSectionNames = contract.GetMethod("getSectionNames");
            _getSectionColors = contract.GetMethod("getSectionColors");
            _setSectionColors = contract.GetMethod("setSectionColors");
            _colorField = data.GetField("color");

            if (_getSectionNames != null && _getSectionColors != null
                && _setSectionColors != null && _colorField != null) return true;

            Harness.LogError("Textures Unlimited is installed but its recolouring API is not what we expect");
            _setSectionColors = null;
            return false;
        }
    }
}
