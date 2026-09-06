using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace DimensionSync.GameTests
{
    /// <summary>
    /// Finds the text boxes in B9's own editor window, so the pointer can be aimed at
    /// them.
    /// </summary>
    /// <remarks>
    /// B9's dimensions are the one family this suite cannot reach through a part
    /// action window: every one of them is declared guiActiveEditor = false, so they
    /// never appear there and the "#" numeric boxes never exist for them. Their only
    /// user interface is B9's own window, and that is IMGUI - drawn fresh every frame
    /// with no objects to find, no RectTransforms, and nothing to ask where anything
    /// is.
    ///
    /// The way in is that IMGUI hands the rectangle to the draw call. B9 renders each
    /// dimension as a label and then a text box, one after the other in the same
    /// function:
    ///
    ///     GUI.Label(val, "  " + name, uiStyleLabelHint);
    ///     GUI.TextField(val5, value.ToString("F3"), uiStyleInputField);
    ///
    /// so a postfix on each records the pairing as B9 draws it, with the exact
    /// rectangle B9 used and the label it belongs to. Nothing is guessed at, nothing
    /// is recomputed from layout arithmetic, and the mapping cannot drift out of date
    /// because it is rebuilt every frame from the drawing itself.
    ///
    /// Both patches filter on the STYLE, by reference, so they ignore every other
    /// piece of IMGUI in the game - and the game has plenty.
    /// </remarks>
    internal static class B9Window
    {
        /// <summary>Every box drawn this pass, in the order B9 drew them.</summary>
        /// <remarks>
        /// A list and not a map, because the labels are not unique. B9 names the
        /// base group's chord fields "Width (root)" and "Width (tip)", and then names
        /// the leading and trailing EDGE groups' fields exactly the same - so keying
        /// by label alone silently returned the last group drawn, and a scenario aimed
        /// at a wing's chord would have typed into its edge instead and been perfectly
        /// happy about it. Order is what separates them: the base group is drawn
        /// first.
        /// </remarks>
        private static readonly List<KeyValuePair<string, Vector2>> Drawn =
            new List<KeyValuePair<string, Vector2>>();

        /// <summary>The frame the list belongs to, so each pass starts clean.</summary>
        private static int _drawnOn = -1;

        /// <summary>
        /// What B9's window flags were before this touched them, to put them back.
        /// </summary>
        /// <remarks>
        /// Restoring these is not tidiness. Leaving B9's window up costs the three
        /// gizmo scenarios that run after this one: they click a part to select it,
        /// B9's window is over the top of it, and every one of twenty-five candidate
        /// points reports nothing under the pointer. That reads as "the part could not
        /// be clicked", which is a skip - so the suite stays green while three
        /// scenarios quietly stop running. Found exactly that way, by three skips
        /// appearing in a run that had none before.
        /// </remarks>
        private static readonly Dictionary<string, object> Restore =
            new Dictionary<string, object>();

        /// <summary>The label drawn most recently, which the next box belongs to.</summary>
        private static string _pending;

        /// <summary>B9's style objects, compared by reference to filter the patches.</summary>
        private static GUIStyle _hintStyle;
        private static GUIStyle _inputStyle;

        /// <summary>How often each patch has run at all, to tell silence apart.</summary>
        /// <remarks>
        /// "No boxes found" has three quite different causes - the patch never ran,
        /// B9 never drew, or the style filter rejected everything - and they look
        /// identical from the outside. Counting each separately is the only way to say
        /// which.
        /// </remarks>
        private static int _labelCalls, _fieldCalls, _labelKept, _fieldKept;

        private static Type _wing;
        private static Type _utility;

        /// <summary>Whether the patches are in and B9 is present.</summary>
        public static bool Installed { get; private set; }

        /// <summary>Why not, when not.</summary>
        public static string Unavailable { get; private set; } = "not tried yet";

        // -----------------------------------------------------------------
        // Setting the window up
        // -----------------------------------------------------------------

        /// <summary>Patch B9's drawing so boxes can be found. Safe to call twice.</summary>
        public static bool Install()
        {
            if (Installed) return true;
            try
            {
                _wing = FindType("WingProcedural.WingProcedural");
                _utility = FindType("WingProcedural.UIUtility");
                if (_wing == null || _utility == null)
                {
                    Unavailable = "B9 Procedural Wings is not installed";
                    return false;
                }

                object harmony = OpenHarmony("com.sparr.dimensionsync.tests");
                if (harmony == null) { Unavailable = "Harmony is not installed"; return false; }

                // DoLabel and DoTextField, not the public Label and TextField.
                //
                // The public string overloads are one-liners - Label(position,
                // GUIContent.Temp(text), style) and the like - so the runtime inlines
                // them into B9's own code and a patch on them is never reached. That
                // is not a hypothetical: patching them ran the postfix exactly zero
                // times while B9 was plainly drawing its window. These two do the work
                // and are too substantial to disappear the same way.
                const BindingFlags anyStatic = BindingFlags.Public | BindingFlags.NonPublic
                                                                   | BindingFlags.Static;
                MethodInfo label = typeof(GUI).GetMethod(
                    "DoLabel", anyStatic, null,
                    new[] { typeof(Rect), typeof(GUIContent), typeof(GUIStyle) }, null);
                MethodInfo textField = typeof(GUI).GetMethod(
                    "DoTextField", anyStatic, null,
                    new[]
                    {
                        typeof(Rect), typeof(int), typeof(GUIContent),
                        typeof(bool), typeof(int), typeof(GUIStyle),
                    }, null);
                if (label == null || textField == null)
                {
                    Unavailable = "Unity's GUI.DoLabel/GUI.DoTextField are not where expected";
                    return false;
                }

                Patch(harmony, label, Mine(nameof(AfterLabel)));
                Patch(harmony, textField, Mine(nameof(AfterTextField)));

                Installed = true;
                Unavailable = null;
                return true;
            }
            catch (Exception e)
            {
                Unavailable = $"{e.GetType().Name}: {e.Message}";
                Harness.Log($"B9WINDOW could not hook B9's drawing: {e}");
                return false;
            }
        }

        /// <summary>Show B9's window for one wing, as pressing its key does.</summary>
        /// <param name="part">The wing whose window to show.</param>
        public static bool Open(Part part)
        {
            if (_wing == null || part == null) return false;
            FieldInfo active = _wing.GetField("uiWindowActive", BindingFlags.Public | BindingFlags.Static);
            FieldInfo target = _wing.GetField("uiInstanceIDTarget", BindingFlags.Public | BindingFlags.Static);
            if (active == null || target == null) return false;

            Drawn.Clear();
            active.SetValue(null, true);
            target.SetValue(null, part.GetInstanceID());

            // Showing the window is not the same as showing the dimensions. B9 keeps
            // its groups collapsed between sessions and draws nothing inside a
            // collapsed one, so the base group has to be opened or there is simply
            // nothing to type into - which reads from outside as the window not
            // working. Edit mode likewise: without it the window is a label telling
            // you how to arm it.
            foreach (string flag in WindowFlags)
            {
                Remember(_wing, flag);
                SetStatic(_wing, flag, true);
            }

            return true;
        }

        /// <summary>Put the window into typed mode, which is its own "#".</summary>
        /// <param name="on">Which way to leave it.</param>
        public static bool SetNumeric(bool on)
        {
            FieldInfo numeric = _utility?.GetField("numericInput", BindingFlags.Public | BindingFlags.Static);
            if (numeric == null) return false;
            numeric.SetValue(null, on);
            Drawn.Clear();
            return true;
        }

        /// <summary>Put B9's window away.</summary>
        /// <summary>The window flags this touches, and therefore has to put back.</summary>
        private static readonly string[] WindowFlags =
        {
            "uiEditMode",
            "sharedFieldGroupBaseStatic",
            "sharedFieldGroupEdgeLeadingStatic",
            "sharedFieldGroupEdgeTrailingStatic",
        };

        /// <summary>Put B9's window away and every flag back as it was found.</summary>
        public static void Close()
        {
            _wing?.GetField("uiWindowActive", BindingFlags.Public | BindingFlags.Static)
                 ?.SetValue(null, false);
            SetStatic(_wing, "uiInstanceIDTarget", 0);

            foreach (KeyValuePair<string, object> was in Restore)
                SetStatic(_wing, was.Key, was.Value);
            Restore.Clear();

            // B9 locks the editor while the pointer is over its window and unlocks it
            // when the pointer leaves - but both of those happen inside OnGUI, which
            // returns immediately once the window is not being shown. Close the window
            // with the pointer still inside its rectangle and the unlock never runs,
            // so the lock outlives the window and the editor quietly ignores clicks
            // for the rest of the session.
            //
            // That is B9's bug, not ours, but this is what provokes it: typing leaves
            // the pointer in the middle of the window.
            //
            // Released here rather than by clicking B9's own close button, which does
            // release it properly. This mirrors what that button's handler does. The
            // difference matters only if B9 ever changes its teardown, at which point
            // this copy drifts and clicking the real button would not have.
            //
            // Recorded honestly: three gizmo scenarios were skipping when this was
            // written and this looked like the cause, but they are equally explained
            // by two sessions sharing one display, which was also true at the time.
            // The leak is plain in B9's source; that it ever broke anything here is
            // not established.
            EditorLogic.fetch?.Unlock("WingProceduralWindow");

            SetNumeric(false);
            Drawn.Clear();
        }

        /// <summary>Note a flag's current value so <see cref="Close"/> can put it back.</summary>
        /// <param name="type">The type declaring it.</param>
        /// <param name="name">The field's name.</param>
        private static void Remember(Type type, string name)
        {
            FieldInfo field = type?.GetField(name, BindingFlags.Public | BindingFlags.Static);
            if (field != null && !Restore.ContainsKey(name)) Restore[name] = field.GetValue(null);
        }

        // -----------------------------------------------------------------
        // Asking where things are
        // -----------------------------------------------------------------

        /// <summary>Every box seen so far, for the log.</summary>
        public static string Seen()
        {
            if (Drawn.Count == 0)
                return $"none (GUI.Label ran {_labelCalls}x keeping {_labelKept}, " +
                       $"GUI.TextField ran {_fieldCalls}x keeping {_fieldKept}; " +
                       $"hint style {(_hintStyle == null ? "unresolved" : "found")}, " +
                       $"input style {(_inputStyle == null ? "unresolved" : "found")})";

            // In draw order, numbered, because that order is what tells the base
            // group's fields from the identically named ones in the edge groups.
            var said = new List<string>();
            for (int i = 0; i < Drawn.Count; i++)
                said.Add($"[{i}] '{Drawn[i].Key}' at ({(int)Drawn[i].Value.x},{(int)Drawn[i].Value.y})");
            return string.Join("; ", said.ToArray());
        }

        /// <summary>Where the box for a named dimension is, in pointer coordinates.</summary>
        /// <param name="label">The name B9 draws beside it.</param>
        /// <param name="x">Its horizontal position.</param>
        /// <param name="y">Its vertical position.</param>
        public static bool TryBoxFor(string label, out int x, out int y, int occurrence = 0)
        {
            x = y = 0;
            if (label == null) return false;

            int seen = 0;
            for (int i = 0; i < Drawn.Count; i++)
            {
                if (Drawn[i].Key != label) continue;
                if (seen++ != occurrence) continue;
                x = Mathf.RoundToInt(Drawn[i].Value.x);
                y = Mathf.RoundToInt(Drawn[i].Value.y);
                return x >= 0 && y >= 0 && x < Screen.width && y < Screen.height;
            }
            return false;
        }

        // -----------------------------------------------------------------
        // The patches
        // -----------------------------------------------------------------

        /// <param name="position">Where the label was drawn.</param>
        /// <param name="content">What it says, which is the dimension's name.</param>
        /// <param name="style">The style it was drawn in, used to tell B9's apart.</param>
        private static void AfterLabel(Rect position, GUIContent content, GUIStyle style)
        {
            try
            {
                if (Event.current == null || Event.current.type != EventType.Repaint) return;

                _labelCalls++;
                if (_hintStyle == null) _hintStyle = StyleNamed("uiStyleLabelHint");
                if (style == null || style != _hintStyle) return;
                _labelKept++;
                string text = content?.text;
                if (!string.IsNullOrEmpty(text)) _pending = text.Trim();
            }
            catch { /* never let a test hook break the game's drawing */ }
        }

        /// <param name="position">Where the box was drawn - the whole point.</param>
        /// <param name="id">Unity's control id; unused, but part of the signature.</param>
        /// <param name="content">What it holds.</param>
        /// <param name="multiline">Unused, but part of the signature.</param>
        /// <param name="maxLength">Unused, but part of the signature.</param>
        /// <param name="style">The style it was drawn in, used to tell B9's apart.</param>
        /// <remarks>
        /// Converted to screen coordinates here rather than later, because
        /// GUIToScreenPoint only means anything inside the window's own drawing pass -
        /// it is what accounts for the window having been dragged somewhere.
        /// </remarks>
        private static void AfterTextField(Rect position, int id, GUIContent content,
                                           bool multiline, int maxLength, GUIStyle style)
        {
            try
            {
                // Repaint only. IMGUI walks the window at least twice a frame - once to
                // lay it out and once to draw it - and during the layout pass the
                // rectangles are not decided yet, so every box reports the same
                // meaningless position. Recording both passes produced a list of
                // twenty-four boxes of which the first twelve were all at one point.
                if (Event.current == null || Event.current.type != EventType.Repaint) return;

                _fieldCalls++;
                if (_inputStyle == null) _inputStyle = StyleNamed("uiStyleInputField");
                if (style == null || style != _inputStyle) return;
                _fieldKept++;
                if (string.IsNullOrEmpty(_pending)) return;

                if (_drawnOn != Time.frameCount)
                {
                    _drawnOn = Time.frameCount;
                    Drawn.Clear();
                }

                Vector2 middle = GUIUtility.GUIToScreenPoint(
                    new Vector2(position.x + position.width / 2f, position.y + position.height / 2f));
                Drawn.Add(new KeyValuePair<string, Vector2>(_pending, middle));
                _pending = null;
            }
            catch { /* as above */ }
        }

        /// <summary>Set one of B9's public statics, if it has it.</summary>
        /// <param name="type">The type declaring it.</param>
        /// <param name="name">The field's name.</param>
        /// <param name="value">What to set it to.</param>
        /// <remarks>
        /// Quiet when the field is absent: these are B9's own layout flags rather than
        /// anything it promises, and a fork that renamed one should cost this a group
        /// left collapsed, not an exception.
        /// </remarks>
        private static void SetStatic(Type type, string name, object value)
        {
            type?.GetField(name, BindingFlags.Public | BindingFlags.Static)?.SetValue(null, value);
        }

        /// <summary>One of B9's public static GUIStyles, by field name.</summary>
        private static GUIStyle StyleNamed(string field)
        {
            return _utility?.GetField(field, BindingFlags.Public | BindingFlags.Static)
                           ?.GetValue(null) as GUIStyle;
        }

        // -----------------------------------------------------------------
        // Just enough Harmony, by reflection
        // -----------------------------------------------------------------

        /// <summary>One of our own static methods, by name.</summary>
        private static MethodInfo Mine(string name)
        {
            return typeof(B9Window).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static);
        }

        /// <summary>A Harmony instance, or null if Harmony is not installed.</summary>
        private static object OpenHarmony(string id)
        {
            Type harmony = FindType("HarmonyLib.Harmony");
            return harmony == null ? null : Activator.CreateInstance(harmony, id);
        }

        /// <summary>Add a postfix to a method.</summary>
        private static void Patch(object harmony, MethodInfo target, MethodInfo postfix)
        {
            Type harmonyMethod = FindType("HarmonyLib.HarmonyMethod");
            MethodInfo patch = harmony.GetType().GetMethod("Patch");
            ParameterInfo[] wants = patch.GetParameters();
            var args = new object[wants.Length];
            args[0] = target;
            if (wants.Length > 2) args[2] = Activator.CreateInstance(harmonyMethod, postfix);
            patch.Invoke(harmony, args);
        }

        /// <summary>A type by full name, from anything KSP has loaded.</summary>
        private static Type FindType(string fullName)
        {
            foreach (AssemblyLoader.LoadedAssembly loaded in AssemblyLoader.loadedAssemblies)
            {
                Type found = loaded.assembly?.GetType(fullName, false);
                if (found != null) return found;
            }
            return null;
        }
    }
}
