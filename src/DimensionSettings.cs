using System;
using System.IO;
using UnityEngine;

namespace DimensionSync
{
    /// <summary>
    /// Every tunable this mod has, in one place, shared by the editor addon and
    /// the settings window.
    /// </summary>
    /// <remarks>
    /// These live in statics rather than on the addon because the addon is
    /// recreated on every trip into the editor while a setting the player chose
    /// from the toolbar has to outlive that. The values start at the shipped
    /// config's defaults, are overlaid with whatever the player last chose, and
    /// are written back out as soon as anything changes.
    /// </remarks>
    internal static class DimensionSettings
    {
        /// <summary>Where the player's own choices are kept, under GameData.</summary>
        /// <remarks>
        /// PluginData is the conventional home for state a mod writes itself: KSP
        /// does not hand its contents to ModuleManager, so a file here cannot be
        /// mistaken for part config or end up in a player's patch cache.
        /// </remarks>
        private const string SettingsPath = "GameData/DimensionSync/PluginData/Settings.cfg";

        /// <summary>Root node name inside <see cref="SettingsPath"/>.</summary>
        private const string SettingsNode = "DIMENSION_SYNC_SETTINGS";

        /// <summary>Verbose logging to KSP.log.</summary>
        public static bool Debug;

        /// <summary>How close two dimensions have to be to count as the same size.</summary>
        public static float MatchTolerance = 0.01f;

        /// <summary>How far a field has to move before we treat it as edited at all.</summary>
        public static float ChangeEpsilon = 1e-4f;

        /// <summary>Whether a part with no adjustable dimension passes a change along.</summary>
        public static bool PassThroughRigidParts;

        /// <summary>What to do with a gap that was inside the match tolerance.</summary>
        public static MarginMode Margin = MarginMode.None;

        /// <summary>
        /// How far a control surface may sit from its wing's edge and still count as
        /// belonging to it, as a fraction of the surface's own chord.
        /// </summary>
        /// <remarks>
        /// Only ever consulted at the moment the player finishes moving a part, so it
        /// judges a settled arrangement rather than one mid-change. A fraction rather
        /// than a distance, so it means the same on a small flap and a large one.
        /// </remarks>
        public static float FlushTolerance = 0.2f;

        /// <summary>
        /// Whether a control surface's sweep is kept matched to the wing carrying
        /// it, the way B9's own "inherit" button does it.
        /// </summary>
        public static bool MatchControlSurfaceSweep = true;

        /// <summary>
        /// Whether a part that is surface-attached along its host keeps its attached
        /// end where it was when its span changes.
        /// </summary>
        public static bool AnchorSpanChanges = true;

        /// <summary>
        /// Whether a wing segment is moved back onto the tip of the segment inboard
        /// of it when that segment's sweep or span changes.
        /// </summary>
        public static bool AlignWingJoints = true;

        /// <summary>
        /// Whether a change carries on through a run of wings whose edges form one
        /// straight line, rather than stopping at the first chord that differs.
        /// </summary>
        public static bool KeepWingEdgesStraight = true;

        /// <summary>
        /// Whether a field this mod writes is also copied to that field's symmetry
        /// counterparts.
        /// </summary>
        /// <remarks>
        /// Off, because it is usually redundant and sometimes wrong. The edit that
        /// starts a run has already been mirrored by whatever made it - the part
        /// action window does that - so the counterpart's own neighbours change too,
        /// and propagation reaches the far side by itself, from that side's own
        /// parts. Copying as well writes each part twice.
        ///
        /// The second write is not harmless. Symmetry copies field for field, and the
        /// two halves of a mirrored pair do not agree about which of their ends is
        /// which: a mirrored control surface meets its wing's ROOT at its own tip.
        /// So one write lands on the correct end and the copy lands on the opposite
        /// one, and between the two directions every end gets written. That is the
        /// "thickness flipped end for end" a mirrored aircraft shows.
        /// </remarks>
        public static bool MirrorToSymmetryCounterparts;

        /// <summary>Raised whenever a value here changes, so live objects can re-read them.</summary>
        public static event Action Changed;

        /// <summary>True once <see cref="LoadFrom"/> has run, so it only runs once a session.</summary>
        private static bool _loaded;

        /// <summary>
        /// Read the shipped defaults out of the game database, then let the
        /// player's own saved choices override them.
        /// </summary>
        /// <param name="nodes">Every DIMENSION_SYNC node ModuleManager produced.</param>
        /// <remarks>
        /// Runs once per game session. Each setting keeps its previous value when a
        /// node omits it or spells it wrongly, so a malformed patch cannot blank a
        /// default out.
        /// </remarks>
        public static void LoadFrom(ConfigNode[] nodes)
        {
            if (_loaded) return;
            _loaded = true;

            foreach (ConfigNode node in nodes)
            {
                DimensionFieldRegistry.LoadConfig(node);

                if (bool.TryParse(node.GetValue("debug") ?? "", out bool debug)) Debug = debug;
                if (float.TryParse(node.GetValue("tolerance") ?? "", out float tolerance) && tolerance > 0f)
                    MatchTolerance = tolerance;
                if (float.TryParse(node.GetValue("changeEpsilon") ?? "", out float epsilon) && epsilon > 0f)
                    ChangeEpsilon = epsilon;
                if (bool.TryParse(node.GetValue("passThroughRigidParts") ?? "", out bool pass))
                    PassThroughRigidParts = pass;
                if (bool.TryParse(node.GetValue("matchControlSurfaceSweep") ?? "", out bool sweep))
                    MatchControlSurfaceSweep = sweep;
                if (bool.TryParse(node.GetValue("anchorSpanChanges") ?? "", out bool anchor))
                    AnchorSpanChanges = anchor;
                if (bool.TryParse(node.GetValue("alignWingJoints") ?? "", out bool align))
                    AlignWingJoints = align;
                if (bool.TryParse(node.GetValue("keepWingEdgesStraight") ?? "", out bool straight))
                    KeepWingEdgesStraight = straight;
                if (bool.TryParse(node.GetValue("mirrorToSymmetryCounterparts") ?? "", out bool mirror))
                    MirrorToSymmetryCounterparts = mirror;
                if (TryParseMargin(node.GetValue("marginMode"), out MarginMode margin)) Margin = margin;
            }

            LoadPlayerChoices();
        }

        /// <summary>Turn a config spelling of a margin mode into the enum.</summary>
        /// <returns>False for anything unrecognised, leaving the caller's value alone.</returns>
        private static bool TryParseMargin(string text, out MarginMode mode)
        {
            switch ((text ?? "").Trim().ToLowerInvariant())
            {
                case "absolute": mode = MarginMode.Absolute; return true;
                case "proportional": mode = MarginMode.Proportional; return true;
                case "none": mode = MarginMode.None; return true;
                default: mode = MarginMode.None; return false;
            }
        }

        /// <summary>Overlay the file the settings window last wrote, if there is one.</summary>
        /// <remarks>
        /// A missing file is the normal first-run case, not an error: the shipped
        /// config's values simply stand.
        /// </remarks>
        private static void LoadPlayerChoices()
        {
            string path = Path.Combine(KSPUtil.ApplicationRootPath, SettingsPath);
            if (!File.Exists(path)) return;

            ConfigNode file = ConfigNode.Load(path);
            ConfigNode node = file?.GetNode(SettingsNode);
            if (node == null) return;

            if (TryParseMargin(node.GetValue("marginMode"), out MarginMode margin)) Margin = margin;
            if (bool.TryParse(node.GetValue("matchControlSurfaceSweep") ?? "", out bool sweep))
                MatchControlSurfaceSweep = sweep;
            if (bool.TryParse(node.GetValue("anchorSpanChanges") ?? "", out bool anchor))
                AnchorSpanChanges = anchor;
            if (bool.TryParse(node.GetValue("alignWingJoints") ?? "", out bool align))
                AlignWingJoints = align;
            if (bool.TryParse(node.GetValue("keepWingEdgesStraight") ?? "", out bool straight))
                KeepWingEdgesStraight = straight;
            if (float.TryParse(node.GetValue("tolerance") ?? "", out float tolerance) && tolerance > 0f)
                MatchTolerance = tolerance;
        }

        /// <summary>
        /// Write the player's choices out and tell anything live to re-read them.
        /// </summary>
        /// <remarks>
        /// Only the settings the window can reach are written. The rest stay where
        /// the shipped config and any ModuleManager patch put them, so saving from
        /// the window cannot freeze a value a patch is trying to control.
        /// </remarks>
        public static void Save()
        {
            try
            {
                string path = Path.Combine(KSPUtil.ApplicationRootPath, SettingsPath);
                Directory.CreateDirectory(Path.GetDirectoryName(path));

                var node = new ConfigNode(SettingsNode);
                node.AddValue("marginMode", Margin.ToString().ToLowerInvariant());
                node.AddValue("tolerance", MatchTolerance.ToString("R"));
                node.AddValue("matchControlSurfaceSweep", MatchControlSurfaceSweep);
                node.AddValue("anchorSpanChanges", AnchorSpanChanges);
                node.AddValue("alignWingJoints", AlignWingJoints);
                node.AddValue("keepWingEdgesStraight", KeepWingEdgesStraight);

                var file = new ConfigNode();
                file.AddNode(node);
                file.Save(path);
            }
            catch (Exception error)
            {
                // A settings file we cannot write is a nuisance, not a reason to
                // take the editor down with us: the choice still applies this session.
                UnityEngine.Debug.LogWarning(
                    $"{DimensionSyncAddon.LogTag} could not save settings: {error.Message}");
            }

            Changed?.Invoke();
        }

        /// <summary>
        /// Tell everything live to re-read the settings, without writing a file.
        /// </summary>
        /// <remarks>
        /// <see cref="Save"/> both persists and notifies; this only notifies. It is
        /// for changing a setting for the moment rather than for good - the test
        /// harness stepping through the margin modes, say, which should not leave the
        /// player's own choice overwritten afterwards.
        /// </remarks>
        public static void Refresh() => Changed?.Invoke();

        /// <summary>Copy the current settings onto a propagator.</summary>
        /// <param name="propagator">The propagator to bring up to date.</param>
        public static void ApplyTo(DimensionPropagator propagator)
        {
            if (propagator == null) return;
            propagator.MatchTolerance = MatchTolerance;
            propagator.ChangeEpsilon = ChangeEpsilon;
            propagator.PassThroughRigidParts = PassThroughRigidParts;
            propagator.Margin = Margin;
            propagator.Log = Debug
                ? message => UnityEngine.Debug.Log($"{DimensionSyncAddon.LogTag} {message}")
                : (Action<string>)null;
        }
    }
}
