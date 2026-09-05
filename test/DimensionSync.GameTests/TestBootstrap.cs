using System;
using System.Reflection;
using UnityEngine;

namespace DimensionSync.GameTests
{
    /// <summary>
    /// Shared plumbing: command-line options and the log tag.
    /// </summary>
    internal static class Harness
    {
        /// <summary>Prefix on every line the harness writes to KSP.log.</summary>
        public const string Tag = "[DimensionSyncGameTests]";

        /// <summary>True when KSP was started with -dstest.</summary>
        public static bool Enabled => HasFlag("-dstest") || Walkthrough;

        /// <summary>
        /// Whether the scenarios should actually run, as opposed to just getting an
        /// editor open.
        /// </summary>
        /// <remarks>
        /// Inspecting a craft still needs the bootstrap: it has to create a game and
        /// get into the editor, because nothing else does. Only the scenario RUNNER
        /// stands down. Switching the whole harness off instead left the game sitting
        /// on the main menu with an editor addon waiting for an editor that never
        /// arrived.
        /// </remarks>
        public static bool RunScenarios => Enabled && string.IsNullOrEmpty(InspectCraft);

        /// <summary>Where the JSON report goes; -dstest-out &lt;path&gt;.</summary>
        public static string OutputPath =>
            GetOption("-dstest-out") ?? System.IO.Path.Combine(KSPUtil.ApplicationRootPath, "dstest-results.json");

        /// <summary>
        /// Step through the scenarios with an explanation before each operation;
        /// -dstest-walkthrough. Implies -dstest and leaves the game running.
        /// </summary>
        public static bool Walkthrough => HasFlag("-dstest-walkthrough");

        /// <summary>
        /// Which editor to run in; -dstest-sph for the Spaceplane Hangar.
        /// </summary>
        /// <remarks>
        /// Not a detail. The two editors lay a craft out along different axes, so a
        /// wing in the SPH sits at a different orientation to the world than the same
        /// wing in the VAB. Anything that reasons in world space instead of in a
        /// part's own space behaves differently between them - and a suite that only
        /// ever runs in one of them cannot tell the difference.
        /// </remarks>
        public static EditorFacility Facility =>
            HasFlag("-dstest-sph") ? EditorFacility.SPH : EditorFacility.VAB;

        /// <summary>
        /// A craft to load and describe instead of running scenarios;
        /// -dstest-inspect &lt;name&gt;.
        /// </summary>
        public static string InspectCraft => GetOption("-dstest-inspect");

        /// <summary>
        /// Make an edit to the inspected craft, twice: once as loaded, and once
        /// after leaving the editor and coming back; -dstest-inspect-edit.
        /// </summary>
        public static bool InspectEdit => HasFlag("-dstest-inspect-edit");

        /// <summary>
        /// Run only scenarios whose name contains this; -dstest-only &lt;substring&gt;.
        /// </summary>
        /// <remarks>
        /// For iterating on one scenario. A full run is minutes of game startup for a
        /// single answer, which is long enough that it discourages the extra run that
        /// would have settled a question properly.
        /// </remarks>
        public static string Only => GetOption("-dstest-only");

        /// <summary>
        /// What to change during an inspection; -dstest-inspect-set
        /// &lt;wing|ctrl&gt;:&lt;field&gt;=&lt;value&gt;.
        /// </summary>
        /// <remarks>
        /// So a craft a player saved to demonstrate something can be poked in exactly
        /// the way they poked it, rather than only in the one way this was first
        /// written to do. Without it the answer to "what happens when I change THIS"
        /// needs a code change every time the question changes.
        /// </remarks>
        public static string InspectSet => GetOption("-dstest-inspect-set");

        /// <summary>
        /// Save each scenario's fixture as a craft as soon as it is built, then stop;
        /// -dstest-save-fixture.
        /// </summary>
        /// <remarks>
        /// The way to get a fixture in front of a person who can edit it. Parts built
        /// by the harness cannot be selected with the mouse - an unsolved problem - so
        /// a guided run is no use for putting something right by hand. Written to a
        /// craft file instead, it opens in an ordinary session where the editor behaves
        /// normally, and comes back as a craft that can be measured against the one the
        /// harness built.
        /// </remarks>
        public static bool SaveFixture => HasFlag("-dstest-save-fixture");

        /// <summary>
        /// Multiplier on every settling wait; -dstest-settle &lt;factor&gt;, default 1.
        /// </summary>
        /// <remarks>
        /// For telling a real failure from a timing one. A result that changes when the
        /// waits get longer was never measuring geometry - it was measuring whether
        /// some mod had finished deferring its work yet. Running the same scenario at
        /// several factors and comparing is the cheapest way to find out which kind a
        /// flaky number is, and a value that holds still from 0.5 through 3 is one
        /// worth reasoning about.
        /// </remarks>
        public static float SettleScale
        {
            get
            {
                string value = GetOption("-dstest-settle");
                return !string.IsNullOrEmpty(value)
                       && float.TryParse(value, out float factor) && factor > 0f
                    ? factor
                    : 1f;
            }
        }

        /// <summary>Leave KSP running after the report is written; -dstest-keep-open.</summary>
        public static bool KeepOpen => HasFlag("-dstest-keep-open") || Walkthrough;

        /// <summary>Write a tagged line to KSP.log, which is where a failing run is diagnosed.</summary>
        /// <remarks>
        /// Flushed straight away rather than left in KSPLog's buffer. See
        /// <see cref="FlushLog"/> for why that matters.
        /// </remarks>
        public static void Log(string message)
        {
            Debug.Log($"{Tag} {message}");
            FlushLog();
        }

        /// <summary>As <see cref="Log"/>, but flagged so it stands out in the log.</summary>
        public static void LogError(string message)
        {
            Debug.LogError($"{Tag} {message}");
            FlushLog();
        }

        /// <summary>KSPLog's own writer, found once by reflection.</summary>
        private static System.IO.StreamWriter _logStream;

        /// <summary>False until we have tried to find <see cref="_logStream"/>.</summary>
        private static bool _lookedForLogStream;

        /// <summary>
        /// Push everything written so far out to KSP.log.
        /// </summary>
        /// <remarks>
        /// KSPLog flushes its file every twentieth line and on nothing else - no
        /// timer, no flush when the game goes quiet. That is invisible during a
        /// normal run, where lines arrive in their thousands, but it is exactly wrong
        /// for a walkthrough: the harness announces that it is ready and waiting,
        /// then by design stops producing output, so the announcement sits in the
        /// buffer for as long as the player takes. Anyone watching the log to find
        /// out whether the game is ready concludes that it has hung.
        ///
        /// KSPLog's writer is private, so this reaches it by reflection and gives up
        /// quietly if a future version moves it: an unflushed log is a nuisance, not
        /// a reason to fail a test run.
        /// </remarks>
        public static void FlushLog()
        {
            if (_logStream == null && !_lookedForLogStream)
            {
                try
                {
                    // KSPLog.Instance can still be null very early in startup, so a
                    // miss is only given up on once the log is actually running.
                    KSPLog instance = KSPLog.Instance;
                    if (instance != null)
                    {
                        _lookedForLogStream = true;
                        FieldInfo field = typeof(KSPLog).GetField(
                            "fileStream", BindingFlags.Instance | BindingFlags.NonPublic);
                        _logStream = field?.GetValue(instance) as System.IO.StreamWriter;

                        if (_logStream == null)
                            Debug.Log($"{Tag} could not reach KSPLog's writer; the log will lag behind");
                    }
                }
                catch (Exception)
                {
                    _lookedForLogStream = true;
                    _logStream = null;
                }
            }

            try
            {
                _logStream?.Flush();
            }
            catch (Exception)
            {
                // A writer KSP has closed under us is not worth a report.
                _logStream = null;
            }
        }

        /// <summary>Whether the given bare flag appears on KSP's command line.</summary>
        private static bool HasFlag(string flag)
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length; i++)
                if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>
        /// The argument following <paramref name="name"/> on KSP's command line, or
        /// null when the option is absent or is the last argument.
        /// </summary>
        private static string GetOption(string name)
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
            return null;
        }
    }

    /// <summary>
    /// Takes the game from the main menu into a sandbox VAB without anybody
    /// touching the mouse.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.MainMenu, true)]
    public class TestBootstrap : MonoBehaviour
    {
        /// <summary>The main menu is entered more than once; only bootstrap from the first.</summary>
        private static bool _started;

        /// <summary>
        /// Create a fresh sandbox save and go straight to the VAB, so a run needs no
        /// pre-existing save and starts from the same state every time.
        /// </summary>
        private void Start()
        {
            if (!Harness.Enabled) { Destroy(gameObject); return; }
            if (_started) return;
            _started = true;

            StartCoroutine(Bootstrap());
        }

        /// <summary>
        /// Let the main menu finish building itself, then take over.
        /// </summary>
        /// <remarks>
        /// Starting the game in the same frame the menu loads tears the scene out from
        /// under KSP's own UI spawner, which is part way through waking the toolbar
        /// apps. The Messages app dies mid-setup - MessageSystemAppFrame.Reposition
        /// throws, its button never registers, and its window is left open over the
        /// editor's save and load buttons with no way to close it. The apps that had
        /// already woken then wake AGAIN for the editor, which is where the duplicated
        /// toolbar buttons come from.
        ///
        /// Waiting for the launcher to say it is ready, and then a few frames more,
        /// costs a second at startup and leaves the game's own UI intact.
        /// </remarks>
        private System.Collections.IEnumerator Bootstrap()
        {
            for (int i = 0; i < 600 && !KSP.UI.Screens.ApplicationLauncher.Ready; i++)
                yield return null;
            for (int i = 0; i < 30; i++) yield return null;

            Harness.Log($"bootstrapping a sandbox editor session in the {Harness.Facility}");

            // CreateNewGame does what "start a new sandbox game" does from the
            // menu, including creating the save folder and its Ships/VAB tree.
            // Skipping that leaves EditorDriver.Start throwing on the missing
            // directories and the editor only half built.
            Game game = GamePersistence.CreateNewGame(
                "DimensionSyncGameTests",
                Game.Modes.SANDBOX,
                GameParameters.GetDefaultParameters(Game.Modes.SANDBOX, GameParameters.Preset.Normal),
                "Squad/Flags/default",
                GameScenes.EDITOR,
                Harness.Facility);

            // Start with an empty ship rather than whatever was last cached.
            game.Parameters.Editor.startUpMode = (int)EditorDriver.StartupBehaviours.START_CLEAN;

            // A brand new save otherwise opens the editor behind the "Welcome to the
            // Vehicle Assembly Building" tutorial, which covers the walkthrough panel.
            game.RemoveProtoScenarioModule(typeof(ScenarioNewGameIntro));

            HighLogic.CurrentGame = game;

            // EditorDriver.Start reads the save back off disk; without a
            // persistent.sfs it throws and the editor comes up half built.
            GamePersistence.SaveGame(game, "persistent", HighLogic.SaveFolder, SaveMode.OVERWRITE);

            game.Start();
        }
    }
}
