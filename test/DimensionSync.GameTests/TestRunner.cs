using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace DimensionSync.GameTests
{
    /// <summary>The outcome of one scenario, as it will appear in the JSON report.</summary>
    internal class TestResult
    {
        /// <summary>Scenario name; becomes the pytest case id.</summary>
        public string Name;

        /// <summary>passed | failed | skipped | error.</summary>
        public string Status = "passed";

        /// <summary>Assertion failures, skip reasons and exception text.</summary>
        public readonly List<string> Messages = new List<string>();

        /// <summary>
        /// Record a failed assertion. A scenario can fail several checks and report
        /// all of them, which says more about what went wrong than the first one
        /// alone. An error outranks a failure and is not downgraded.
        /// </summary>
        public void Fail(string message)
        {
            if (Status != "error") Status = "failed";
            Messages.Add(message);
        }

        /// <summary>
        /// Record that the scenario could not run - usually a mod that is not
        /// installed. Never downgrades a scenario that has already failed.
        /// </summary>
        public void Skip(string message)
        {
            if (Status == "passed") Status = "skipped";
            Messages.Add(message);
        }

        /// <summary>Record an exception. Outranks every other status.</summary>
        public void Error(string message)
        {
            Status = "error";
            Messages.Add(message);
        }
    }

    /// <summary>What a scenario body is given to talk to the harness.</summary>
    internal class TestContext
    {
        /// <summary>Where this scenario's assertions land.</summary>
        public TestResult Result;

        /// <summary>The walkthrough panel, or null in an ordinary headless run.</summary>
        public WalkthroughUI UI;

        /// <summary>
        /// Announce what is about to happen and, in a guided run, wait for the
        /// player before doing it. In a headless run this only writes to the log.
        /// </summary>
        /// <param name="heading">One line: the operation about to be performed.</param>
        /// <param name="detail">What to expect from it, and what should not change.</param>
        public IEnumerator Say(string heading, string detail = null)
        {
            // Panel first, log second. Harness.Log flushes KSP.log to disk on every
            // line, and doing that before putting the text on screen leaves the panel
            // showing the previous step for as long as the write takes.
            LastSaid = heading;
            UI?.Prompt(heading, detail);
            Harness.Log(detail == null ? heading : heading + " | " + detail);

            // Named for the step it comes AFTER, not the one about to run: this fires
            // when a step is announced, so what is on screen is the result of the
            // previous edit. Naming it by the coming edit would label every craft with
            // the change it does not yet contain.
            if (Harness.SaveFixture) SaveFixtureCraft(_previousHeading);
            _previousHeading = heading;

            if (UI == null) yield break;
            while (UI.Waiting) yield return null;
        }

        /// <summary>How many steps of this scenario have been written to craft files.</summary>
        private int _fixturesSaved;

        /// <summary>The step announced before this one, or null at the start.</summary>
        private string _previousHeading;

        /// <summary>Write the ship as it now stands to a craft file named for the scenario.</summary>
        /// <remarks>
        /// Saved at EVERY prompt, numbered, because the state worth correcting by hand
        /// is not always the one the fixture starts in: a step part way through that
        /// leaves a part in the wrong place is exactly the thing to hand to somebody who
        /// can put it right, and a craft they hand back is worth more than a
        /// description of what looked wrong.
        /// </remarks>
        private void SaveFixtureCraft(string heading)
        {
            try
            {
                ShipConstruct ship = EditorLogic.fetch?.ship;
                if (ship == null) { Harness.LogError("no ship to save"); return; }

                // SaveShip takes a NAME, not a path: it works out the save folder and
                // the extension itself, and handed a full path it builds a second one
                // inside the first.
                // The step's own words in the name, trimmed to what a craft name can
                // hold, so the right one can be picked out of the load dialogue.
                string label = (heading ?? string.Empty).Replace('/', '-').Replace('\\', '-')
                                                        .Replace(':', ' ').Replace(".", string.Empty)
                                                        .Trim();
                if (label.Length > 40) label = label.Substring(0, 40).Trim();
                string scenario = (Result?.Name ?? "scenario").Replace("pwings_probe_", string.Empty);
                if (label.Length == 0) label = "as built";
                string name = $"FIX {scenario} {_fixturesSaved:D2} after {label}";
                _fixturesSaved++;
                ship.shipName = name;
                ShipConstruction.SaveShip(ship, name);
                Harness.Log($"saved fixture craft '{name}'");
            }
            catch (System.Exception error)
            {
                Harness.LogError($"could not save the fixture craft: {error.Message}");
            }
        }

        /// <summary>Let the editor - and DimensionSync's LateUpdate poll - run.</summary>
        public IEnumerator Frames(int count)
        {
            for (int i = 0; i < count; i++) yield return null;
        }

        /// <summary>
        /// Wait for everything set off by the last change to have finished.
        /// </summary>
        /// <remarks>
        /// A handful of frames is not enough. B9 Procedural Wings defers part of its
        /// work by half a second of wall clock - UpdateAeroDelayed - and other mods
        /// do similar, so a check made four frames after a write is looking at a part
        /// that has not finished reacting. That gap is why a batch of these scenarios
        /// passed unattended and then failed in the walkthrough, where seconds pass
        /// between steps because a person is reading them.
        ///
        /// Rather than always waiting out the worst case, this watches the ship and
        /// returns once it has stopped moving. A step that set nothing off costs a
        /// fraction of a second; one that woke a deferred rebuild waits for it and no
        /// longer. The cap is there so a mod that never settles cannot hang the run.
        /// </remarks>
        public IEnumerator Settled()
        {
            yield return SettleQuietly();
            Invariants.Check(this, LastSaid ?? "a change");
        }

        /// <summary>The heading of the last step announced, for naming a failure.</summary>
        public string LastSaid;

        /// <summary>
        /// Start watching every number on the ship, so a later
        /// <see cref="FieldWatch.NothingElseChanged"/> can report anything that moved
        /// which the step did not claim.
        /// </summary>
        public FieldWatch WatchEverything()
        {
            return new FieldWatch(this);
        }

        /// <summary>Wait until the ship stops changing. See <see cref="Settled"/>.</summary>
        private IEnumerator SettleQuietly()
        {
            // The quiet window has to be longer than the longest deferral any mod
            // under test uses, or this returns while work is still pending. B9
            // Procedural Wings sets updateTimeDelay = 0.5f and rebuilds after it, so
            // 0.2s here meant returning first, the walkthrough advancing, and the
            // rebuild landing visually on the NEXT step - which reads as a part
            // spontaneously resizing after the step that resized it had finished.
            float quiet = 0.7f * Harness.SettleScale;   // how long nothing may change before we call it done
            float cap = 2.5f * Harness.SettleScale;     // never wait longer than this

            // A settle that ends the moment the fingerprint stops moving can still end
            // between one deferred rebuild and the next. The lead-in gives whatever was
            // set off a chance to START before the quiet window is allowed to count.
            float leadIn = 0.1f * Harness.SettleScale;
            float untilLeadIn = Time.realtimeSinceStartup + leadIn;
            while (Time.realtimeSinceStartup < untilLeadIn) yield return null;

            float started = Time.realtimeSinceStartup;
            float lastChange = started;
            float previous = Fingerprint();

            while (Time.realtimeSinceStartup - started < cap)
            {
                yield return null;

                float current = Fingerprint();
                if (Mathf.Abs(current - previous) > 1e-4f)
                {
                    previous = current;
                    lastChange = Time.realtimeSinceStartup;
                    continue;
                }

                if (Time.realtimeSinceStartup - lastChange >= quiet) yield break;
            }
        }

        /// <summary>
        /// Sit idle for a while and report whether the ship stayed put.
        /// </summary>
        /// <param name="seconds">How long to watch for.</param>
        /// <param name="drift">How far the ship moved or grew while nothing was asked of it.</param>
        /// <remarks>
        /// A rule that adjusts a part on every propagation can feed on its own
        /// output, growing it a little each pass. That is invisible to a check made
        /// straight after a change and obvious to somebody reading a walkthrough
        /// panel for a minute - which is exactly how it was found, twice. Watching an
        /// idle ship is the only way an unattended run sees it at all.
        /// </remarks>
        public IEnumerator HoldStill(float seconds, Ref drift)
        {
            float before = Fingerprint();
            float until = Time.realtimeSinceStartup + seconds;
            while (Time.realtimeSinceStartup < until) yield return null;
            drift.Value = Mathf.Abs(Fingerprint() - before);
        }

        /// <summary>A single value a coroutine can hand back, since it cannot have an out parameter.</summary>
        public class Ref
        {
            /// <summary>The value the coroutine produced.</summary>
            public float Value;
        }

        /// <summary>
        /// A cheap number that changes whenever any part on the ship moves or
        /// changes shape.
        /// </summary>
        /// <remarks>
        /// Positions catch a part being repositioned, and renderer bounds catch a
        /// procedural mesh being rebuilt - which is the slow, deferred half of what
        /// we are waiting for and does not show up in a transform at all.
        /// </remarks>
        internal static float Fingerprint()
        {
            ShipConstruct ship = EditorLogic.fetch?.ship;
            if (ship == null) return 0f;

            float total = ship.parts.Count;
            for (int i = 0; i < ship.parts.Count; i++)
            {
                Part part = ship.parts[i];
                if (part == null) continue;

                Vector3 position = part.transform.localPosition;
                total += position.x + position.y * 3f + position.z * 7f;

                foreach (Renderer renderer in part.GetComponentsInChildren<Renderer>())
                {
                    if (!renderer.enabled || renderer is ParticleSystemRenderer) continue;
                    Vector3 size = renderer.bounds.size;
                    total += size.x + size.y * 3f + size.z * 7f;
                }
            }
            return total;
        }

        /// <summary>
        /// Assert a numeric value, reporting both numbers on failure. NaN is called
        /// out separately because it means the field was missing rather than wrong.
        /// </summary>
        public void Check(string what, float actual, float expected, float tolerance = 0.002f)
        {
            if (float.IsNaN(actual))
            {
                Result.Fail($"{what}: value unavailable, expected {expected:F4}");
                UI?.Note($"<color=#ff8080>x {what}: value unavailable</color>");
                return;
            }
            if (Mathf.Abs(actual - expected) > tolerance)
            {
                Result.Fail($"{what}: expected {expected:F4}, got {actual:F4}");
                UI?.Note($"<color=#ff8080>x {what}: expected {expected:F4}, got {actual:F4}</color>");
            }
            else
            {
                UI?.Note($"<color=#80ff80>ok {what}: {actual:F4}</color>");
            }
        }

        /// <summary>Assert a condition, using <paramref name="what"/> as the failure message.</summary>
        public void CheckTrue(string what, bool condition)
        {
            if (!condition) Result.Fail(what);
            UI?.Note(condition ? $"<color=#80ff80>ok {what}</color>" : $"<color=#ff8080>x {what}</color>");
        }

        /// <summary>Give up on this scenario for a reason that is not a failure.</summary>
        public void Skip(string why)
        {
            Result.Skip(why);
            UI?.Note($"<i>skipped: {why}</i>");
        }
    }

    /// <summary>One named scenario, run as a coroutine so it can let frames pass.</summary>
    internal class Scenario
    {
        /// <summary>Becomes the pytest case id, so keep it stable.</summary>
        public string Name;

        /// <summary>One sentence on what this scenario pins down, shown in a guided run.</summary>
        public string Explain;

        /// <summary>The scenario itself.</summary>
        public Func<TestContext, IEnumerator> Body;
    }

    /// <summary>
    /// Runs the scenarios once the editor is up, writes a JSON report and quits.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.EditorAny, false)]
    public class TestRunner : MonoBehaviour
    {
        /// <summary>The editor can be re-entered; only ever run the scenarios once.</summary>
        private static bool _ran;

        /// <summary>Results so far, written out once every scenario has run.</summary>
        private readonly List<TestResult> _results = new List<TestResult>();

        /// <summary>The guided-run panel, or null for an ordinary headless run.</summary>
        private WalkthroughUI _ui;

        /// <summary>Kick the run off, unless KSP was started without -dstest.</summary>
        private void Start()
        {
            if (!Harness.RunScenarios) { Destroy(gameObject); return; }
            if (_ran) { Destroy(gameObject); return; }
            _ran = true;
            StartCoroutine(RunAll());
        }

        /// <summary>Run every scenario in turn, then write the report and quit the game.</summary>
        /// <remarks>
        /// Each scenario starts from an empty ship and is guarded separately, so one
        /// that throws costs its own result rather than the whole run. Building the
        /// fixture is guarded too, since a missing part shows up there first.
        /// </remarks>
        private IEnumerator RunAll()
        {
            // Give the editor a moment to finish coming up.
            for (int i = 0; i < 60; i++) yield return null;
            while (EditorLogic.fetch == null || EditorLogic.fetch.ship == null) yield return null;

            Harness.Log("starting scenarios");

            // Again here, not only at spawn time: the editor re-takes these as it
            // finishes coming up, which is after the first parts have been placed.
            EditorBuilder.ReleaseEditorStartupLock();

            // Who is holding the editor's input locks. Hover highlighting works while
            // clicks do nothing whenever something has taken EDITOR_PAD_PICK_PLACE,
            // and nothing on screen says which mod that was.
            Harness.Log($"input locks: {InputLockManager.PrintLockStack()}");
            Harness.Log($"pick/place unlocked: " +
                        $"{InputLockManager.IsUnlocked(ControlTypes.EDITOR_PAD_PICK_PLACE)}, " +
                        $"gizmos unlocked: {InputLockManager.IsUnlocked(ControlTypes.EDITOR_GIZMO_TOOLS)}");

            if (Harness.Walkthrough)
            {
                WalkthroughUI.DismissStockPopups();
                EditorBuilder.ReleaseEditorStartupLock();
                _ui = gameObject.AddComponent<WalkthroughUI>();
                Harness.Log("walkthrough mode: stepping through each scenario; " +
                            $"camera controls available: {InputLockManager.IsUnlocked(ControlTypes.CAMERACONTROLS)}");
            }

            var scenarios = new List<Scenario>(Scenarios.All());
            if (!string.IsNullOrEmpty(Harness.Only))
            {
                // Comma-separated, so several unrelated scenarios can be run together
                // without either running the whole suite or paying startup twice.
                string[] wanted = Harness.Only.Split(',');
                scenarios.RemoveAll(s =>
                {
                    foreach (string want in wanted)
                    {
                        string trimmed = want.Trim();
                        if (trimmed.Length == 0) continue;
                        if (s.Name.IndexOf(trimmed, System.StringComparison.OrdinalIgnoreCase) >= 0)
                            return false;
                    }
                    return true;
                });
                Harness.Log($"-dstest-only '{Harness.Only}' left {scenarios.Count} scenario(s)");
            }
            for (int i = 0; i < scenarios.Count; i++)
            {
                Scenario scenario = scenarios[i];

                // Jumping: everything between here and the target is passed over
                // without building a fixture or running a step, so the jump costs
                // nothing. Backwards works too - winding the index back and letting
                // the loop's own increment land on the target, rather than only ever
                // being able to skip forward, which quietly turned "go to 3" into
                // "go to the next one" whenever 3 was behind us.
                if (_ui != null && _ui.SkipToScenario > 0)
                {
                    if (_ui.SkipToScenario > i + 1) continue;
                    if (_ui.SkipToScenario < i + 1)
                    {
                        i = _ui.SkipToScenario - 2;
                        continue;
                    }
                    _ui.SkipToScenario = 0;
                }

                // A rerun replaces the previous attempt's result rather than adding
                // to it, so the report still has one entry per scenario however many
                // times the player watched it.
                _results.RemoveAll(existing => existing.Name == scenario.Name);

                var result = new TestResult { Name = scenario.Name };
                var context = new TestContext { Result = result, UI = _ui };
                Harness.Log($"--- scenario {scenario.Name}");

                // Checked per scenario rather than once at startup: the locks come
                // back every time the ship is emptied, so a single reading taken
                // before any of that happened proves nothing about the rest of a run.
                EditorBuilder.ReleaseEditorStartupLock();
                if (!InputLockManager.IsUnlocked(ControlTypes.EDITOR_PAD_PICK_PLACE))
                    Harness.LogError($"{scenario.Name}: the editor is locked against picking parts up");

                // The lock is only half of it: the editor also has to be in a state
                // where a click means "pick that part up".
                Harness.Log($"editor FSM state: {EditorBuilder.CurrentEditorState()}");

                _ui?.BeginScenario(i + 1, scenarios.Count, scenario.Name, scenario.Explain);
                while (_ui != null && _ui.Waiting) yield return null;

                IEnumerator body = null;
                try
                {
                    EditorBuilder.ClearShip();
                    body = scenario.Body(context);
                }
                catch (Exception ex)
                {
                    result.Error(ex.ToString());
                }

                if (body != null) yield return RunGuarded(body, result);

                Harness.Log($"--- scenario {scenario.Name}: {result.Status}");
                foreach (string message in result.Messages) Harness.Log($"      {message}");
                _results.Add(result);

                // Resizing can leave the stack reaching below the editor floor, so
                // re-seat it before holding on the outcome - but leave the camera
                // where it is, since by now the player may have moved it to look at
                // something.
                EditorBuilder.PresentShip(reframeCamera: false);

                // Watch the ship across the result step. Nothing should change while
                // it is on screen - every manipulation is finished by now - so if
                // something does, say so rather than leaving it to be noticed and
                // described. This step is the one a player dwells on longest, which
                // makes it where slow drift shows up first.
                float settledAt = TestContext.Fingerprint();

                // Hold on the outcome so the player can look at the parts before the
                // ship is cleared for the next scenario - unless they have already
                // asked to move on, in which case holding them here is one more click
                // between them and the scenario they actually want.
                if (_ui == null || !_ui.SkipRequested)
                {
                    yield return context.Say($"Result: {result.Status}",
                                             result.Messages.Count == 0
                                                 ? "Every check above passed."
                                                 : string.Join("\n", result.Messages.ToArray()));
                }

                // Take the parts away now rather than on the way into the next
                // scenario, so the next "about to run" is read against an empty
                // editor instead of the leftovers of the last test.
                float drifted = Mathf.Abs(TestContext.Fingerprint() - settledAt);
                if (drifted > 0.05f)
                    Harness.LogError($"{scenario.Name}: the ship changed by {drifted:F3} while the " +
                                     "result was on screen, with nothing asking it to");

                if (_ui != null && _ui.Aborted)
                {
                    Harness.Log("walkthrough stopped by the player");
                    break;
                }

                EditorBuilder.ClearShip();

                // Step this scenario back so the loop builds and runs it again from
                // the top, with a fresh ship.
                if (_ui != null && _ui.RerunRequested)
                {
                    _ui.RerunRequested = false;
                    Harness.Log($"--- scenario {scenario.Name}: running again");
                    i--;
                }

                yield return null;
            }

            WriteReport();

            if (Harness.KeepOpen)
            {
                _ui?.Prompt("Walkthrough finished", $"{_results.Count} scenario(s) run. " +
                            "The report has been written; the game is left running.");
                yield break;
            }

            Harness.Log("done, quitting");
            yield return null;
            Application.Quit();
        }

        /// <summary>Step a scenario coroutine, recording any exception as an error.</summary>
        /// <remarks>
        /// C# will not let a try/catch span a yield, so the coroutine is driven by
        /// hand: MoveNext inside the guard, yield outside it. Nested IEnumerators are
        /// recursed into rather than handed to Unity, so a helper that throws is
        /// caught the same way the scenario body would be.
        /// </remarks>
        private IEnumerator RunGuarded(IEnumerator body, TestResult result)
        {
            while (true)
            {
                // Skip and Stop take effect between steps rather than mid-operation,
                // so a scenario is never left half-built.
                if (_ui != null && (_ui.Aborted || _ui.SkipRequested)) yield break;

                object current;
                try
                {
                    if (!body.MoveNext()) yield break;
                    current = body.Current;
                }
                catch (Exception ex)
                {
                    // A scenario holds references to the parts it built, so deleting one
                    // while looking at it ends the scenario. Saying that is more use than
                    // a stack trace, which reads as the mod having crashed.
                    result.Error(EditorBuilder.AnySpawnedPartMissing()
                        ? "a part this scenario was using has been removed, so it cannot carry on"
                        : ex.ToString());
                    yield break;
                }

                if (current is IEnumerator nested) yield return RunGuarded(nested, result);
                else yield return current;
            }
        }

        /// <summary>
        /// Write the results as JSON for the pytest driver to read. Hand-rolled
        /// because KSP's .NET profile has no serializer worth pulling in for this.
        /// </summary>
        private void WriteReport()
        {
            var json = new StringBuilder();
            json.Append("{\"scenarios\":[");
            for (int i = 0; i < _results.Count; i++)
            {
                TestResult result = _results[i];
                if (i > 0) json.Append(',');
                json.Append("{\"name\":").Append(Quote(result.Name))
                    .Append(",\"status\":").Append(Quote(result.Status))
                    .Append(",\"messages\":[");
                for (int m = 0; m < result.Messages.Count; m++)
                {
                    if (m > 0) json.Append(',');
                    json.Append(Quote(result.Messages[m]));
                }
                json.Append("]}");
            }
            json.Append("]}");

            string path = Harness.OutputPath;
            try
            {
                File.WriteAllText(path, json.ToString());
                Harness.Log($"wrote {_results.Count} scenario results to {path}");
            }
            catch (Exception ex)
            {
                Harness.LogError($"could not write {path}: {ex.Message}");
            }
        }

        /// <summary>JSON-quote a string, escaping the control characters exception text is full of.</summary>
        private static string Quote(string value)
        {
            var sb = new StringBuilder("\"");
            foreach (char c in value ?? string.Empty)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.Append('"').ToString();
        }
    }
}
