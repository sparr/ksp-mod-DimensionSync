using System.Collections.Generic;
using UnityEngine;

namespace DimensionSync.GameTests
{
    /// <summary>
    /// The on-screen panel for a guided run: shows what is about to happen, waits
    /// for the player, then shows what did.
    /// </summary>
    /// <remarks>
    /// Only created when KSP was started with -dstest-walkthrough. In an ordinary
    /// run there is no UI and nothing ever waits.
    /// </remarks>
    internal class WalkthroughUI : MonoBehaviour
    {
        /// <summary>Identifies our input lock so it can be lifted again.</summary>
        private const string LockId = "DimensionSyncWalkthrough";

        /// <summary>
        /// Nothing. This used to lock every editor action so a click aimed at the
        /// panel could not pick a part up - which also meant the parts could not be
        /// picked up ON PURPOSE. Hovering highlighted them and clicking did nothing,
        /// with no indication why.
        ///
        /// A guided run is for looking at what happened, and looking at a part often
        /// means turning it over. The panel advances only on its own buttons or on
        /// space, so there is nothing here that an accidental click can spoil that a
        /// deliberate one should not be allowed to do anyway.
        /// </summary>
        private const ControlTypes EditLocks = ControlTypes.None;

        /// <summary>
        /// Low and left: clear of the editor's part list, its top bar, and - more to
        /// the point - clear of the middle of the screen, which is where the parts
        /// being talked about are. Draggable if it is still in the way.
        /// </summary>
        private Rect _window = new Rect(300f, 415f, 450f, 100f);
        private string _heading = "";
        private string _detail = "";
        private string _scenario = "";
        private readonly List<string> _notes = new List<string>();
        private Vector2 _scroll;

        /// <summary>True while a step is waiting for the player to press Next.</summary>
        public bool Waiting { get; private set; }

        /// <summary>
        /// False until the advance keys have been seen released since this step
        /// began. A held key repeats, and X11 delivers each repeat as a fresh key
        /// press, so without this one stuck space bar walks the whole run.
        /// </summary>
        private bool _keyArmed;


        /// <summary>When the current step started, for the minimum dwell below.</summary>
        private float _stepStarted;

        /// <summary>
        /// A step cannot be dismissed faster than this. Cheap insurance against
        /// anything that hammers the keyboard, so a run cannot silently skip past
        /// the thing you were trying to look at.
        /// </summary>
        private const float MinimumDwell = 0.3f;

        /// <summary>Set by "Run the rest"; from then on nothing pauses.</summary>
        public bool AutoRun { get; private set; }

        /// <summary>Set by "Stop"; the runner finishes up and writes its report.</summary>
        public bool Aborted { get; private set; }

        /// <summary>Set by "Skip"; cleared at the start of the next scenario.</summary>
        public bool SkipRequested { get; private set; }

        /// <summary>Start a new scenario: clear the previous one's notes and skip flag.</summary>
        public void BeginScenario(int index, int count, string name, string explain)
        {
            _scenario = $"{index}/{count}  {name}";
            _previous = null;
            Number = index;
            Count = count;
            _notes.Clear();
            SkipRequested = false;
            RerunRequested = false;
            Prompt("About to run this scenario", explain);
        }

        /// <summary>Show a step and, unless we are in auto-run, start waiting for the player.</summary>
        public void Prompt(string heading, string detail)
        {
            // Keep the step just finished. When something looks wrong the question is
            // always "what did that", and by the time it is noticed the panel is
            // already describing the next thing.
            if (!string.IsNullOrEmpty(_heading) && _heading != heading) _previous = _heading;

            _heading = heading;
            _detail = detail ?? "";
            if (AutoRun || Aborted) return;
            // Every time, not once at startup. Clearing the ship between scenarios
            // leaves the editor with no root part, so it drops back into "place a
            // root part" mode and takes its startup locks again - and the next
            // scenario puts a root part in directly, which is not the flow that lifts
            // them. Releasing them here means they are gone whenever the panel is
            // waiting, which is exactly when somebody might want to grab something.
            EditorBuilder.ReleaseEditorStartupLock();

            Waiting = true;
            _keyArmed = false;
            _stepStarted = Time.realtimeSinceStartup;
            InputLockManager.SetControlLock(EditLocks, LockId);
        }

        /// <summary>Add a line to the running commentary for the current scenario.</summary>
        public void Note(string note) => _notes.Add(note);

        /// <summary>
        /// Set when the player asks for the scenario to be built and run again from
        /// the beginning. Cleared by the runner once it has acted on it.
        /// </summary>
        /// <remarks>
        /// Worth having because some of what these scenarios are checking is only
        /// visible while it happens: a part that flinches as it is resized, or a
        /// mesh that rebuilds a frame late. Being able to replay the same scenario
        /// without restarting the game turns "I think I saw something" into
        /// something anyone can look at twice.
        /// </remarks>
        public bool RerunRequested { get; set; }

        /// <summary>Stop waiting and let the coroutine carry on.</summary>
        private void Continue()
        {
            Waiting = false;
            InputLockManager.RemoveControlLock(LockId);
        }

        /// <summary>Whether this step has been on screen long enough to be dismissed.</summary>
        private bool Settled => Time.realtimeSinceStartup - _stepStarted >= MinimumDwell;

        /// <summary>Whether the keyboard belongs to a text field rather than to us.</summary>
        /// <remarks>
        /// Unity gives a focused control a non-zero keyboardControl, and KSP locks
        /// keyboard input while its own name and description fields have focus. Either
        /// is enough to mean a keystroke is somebody's typing.
        /// </remarks>
        private static bool TypingSomewhere =>
            GUIUtility.keyboardControl != 0
            || !InputLockManager.IsUnlocked(ControlTypes.KEYBOARDINPUT);

        /// <summary>Space and Return advance, so the player need not aim at the button.</summary>
        private void Update()
        {
            if (!Waiting) return;

            // Not while somebody is typing. The editor's craft name and description
            // are ordinary text fields, and a space belongs to whichever of them has
            // the keyboard - taking it to advance the walkthrough means a name cannot
            // be typed with a space in it at all.
            if (TypingSomewhere) { _keyArmed = false; return; }

            bool held = Input.GetKey(KeyCode.Space) || Input.GetKey(KeyCode.Return) ||
                        Input.GetKey(KeyCode.KeypadEnter);

            // Arm only once the keys are all up, so a key that was already down when
            // this step appeared - or one stuck down entirely - cannot advance it.
            if (!held)
            {
                if (Settled) _keyArmed = true;
                return;
            }

            if (!_keyArmed || !Settled) return;

            if (Input.GetKeyDown(KeyCode.Space) || Input.GetKeyDown(KeyCode.Return) ||
                Input.GetKeyDown(KeyCode.KeypadEnter))
                Continue();
        }

        /// <summary>
        /// Close any stock dialog that is in the way. A fresh save greets the player
        /// with a tutorial popup and an expansion splash, both of which sit on top of
        /// this panel and neither of which a scripted run can click away.
        /// </summary>
        public static void DismissStockPopups()
        {
            foreach (PopupDialog dialog in FindObjectsOfType<PopupDialog>())
                if (dialog != null) dialog.Dismiss();

        }

        /// <summary>

        /// <summary>Draw the panel.</summary>
        private void OnGUI()
        {
            GUI.skin = HighLogic.Skin;
            _window = GUILayout.Window(GetInstanceID(), _window, DrawWindow,
                                       "DimensionSync walkthrough", GUILayout.Width(450f));
        }

        /// <summary>Contents of the panel.</summary>
        private void DrawWindow(int id)
        {
            GUILayout.BeginVertical();

            // Controls first, before anything whose height depends on what it says.
            // With them underneath, a step with a long explanation moves every button
            // down the screen, so the one you are about to click is somewhere else by
            // the time the panel redraws.
            // The controls are always drawn, greyed out rather than taken away while
            // a step runs. Swapping them for a "working" label made the panel a
            // different height for a moment, so everything below it jumped up and
            // then back down again between every step - which reads as the text
            // arriving in pieces even though it is replaced all at once.
            bool ready = Waiting;
            GUI.enabled = ready;

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Next  (space)") && ready) Continue();
            if (GUILayout.Button("Run again") && ready) { RerunRequested = true; Continue(); }
            if (GUILayout.Button("Next test") && ready) { SkipRequested = true; Continue(); }
            if (GUILayout.Button("Run the rest") && ready) { AutoRun = true; Continue(); }
            if (GUILayout.Button("Stop") && ready) { Aborted = true; Continue(); }
            GUILayout.EndHorizontal();

            DrawJumpTo(ready);
            GUI.enabled = true;
            GUILayout.Space(6f);


            GUILayout.Label($"<b>{_scenario}</b>");
            GUILayout.Space(4f);
            if (!string.IsNullOrEmpty(_previous))
                GUILayout.Label($"<i>after: {_previous}</i>");
            GUILayout.Label($"<b>{_heading}</b>");
            if (_detail.Length > 0) GUILayout.Label(_detail);

            if (_notes.Count > 0)
            {
                GUILayout.Space(4f);
                _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.MaxHeight(110f));
                foreach (string note in _notes) GUILayout.Label(note);
                GUILayout.EndScrollView();
            }

            GUILayout.EndVertical();
            GUI.DragWindow();
        }

        /// <summary>
        /// A box for jumping straight to a scenario by number.
        /// </summary>
        /// <remarks>
        /// Stepping to a scenario near the end of the list otherwise means clicking
        /// through everything before it, which is several clicks each. When the thing
        /// being investigated is the twenty-first scenario, the twenty before it are
        /// just in the way.
        /// </remarks>
        private void DrawJumpTo(bool ready)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label($"Scenario {Number} of {Count}. Jump to", GUILayout.ExpandWidth(true));
            _jumpText = GUILayout.TextField(_jumpText ?? "", GUILayout.Width(40f));

            if (GUILayout.Button("Go", GUILayout.Width(40f))
                && ready
                && int.TryParse(_jumpText, out int wanted)
                && wanted >= 1 && wanted <= Count)
            {
                // Everything between here and there is skipped outright - no fixture
                // built, no steps run - so the jump is as quick as it looks.
                SkipToScenario = wanted;
                SkipRequested = true;
                _jumpText = "";
                Continue();
            }
            GUILayout.EndHorizontal();
        }

        /// <summary>The heading of the step before this one, or null at the start.</summary>
        private string _previous;

        /// <summary>What is currently typed in the jump box.</summary>
        private string _jumpText;

        /// <summary>Which scenario is on screen, counting from one.</summary>
        public int Number { get; private set; }

        /// <summary>How many scenarios there are in total.</summary>
        public int Count { get; private set; }

        /// <summary>
        /// The scenario to run next, counting from one, or 0 for "just carry on".
        /// </summary>
        /// <remarks>Cleared by the runner once it has arrived.</remarks>
        public int SkipToScenario { get; set; }

        /// <summary>Make sure the input lock never outlives the panel.</summary>
        private void OnDestroy() => InputLockManager.RemoveControlLock(LockId);
    }
}
