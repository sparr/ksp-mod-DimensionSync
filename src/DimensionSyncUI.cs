using System;
using KSP.UI.Screens;
using UnityEngine;

namespace DimensionSync
{
    /// <summary>
    /// The stock-toolbar button and the settings window behind it.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="DimensionSyncAddon"/> so the two can be reasoned
    /// about apart: the addon does the work and holds no UI, this holds the UI and
    /// does no work. Everything it changes goes through
    /// <see cref="DimensionSettings"/>, which is what the addon reads.
    /// </remarks>
    [KSPAddon(KSPAddon.Startup.EditorAny, false)]
    public class DimensionSyncUI : MonoBehaviour
    {
        /// <summary>Only one button, however many times KSP instantiates the addon.</summary>
        private static DimensionSyncUI _instance;

        /// <summary>Our entry on the stock application launcher, or null before it exists.</summary>
        private ApplicationLauncherButton _button;

        /// <summary>The button's icon, drawn in code so the mod ships no image files.</summary>
        private Texture2D _icon;

        /// <summary>Whether the settings window is on screen.</summary>
        private bool _open;

        /// <summary>Where the window sits, remembered for as long as the scene lasts.</summary>
        private Rect _window = new Rect(200f, 100f, 320f, 0f);

        /// <summary>The id OnGUI needs to tell this window from any other.</summary>
        private readonly int _windowId = ("DimensionSync.Settings").GetHashCode();

        /// <summary>What the tolerance box currently holds, which may not parse yet.</summary>
        /// <remarks>
        /// Kept as text rather than a float so a half-typed number - "0." on the way
        /// to "0.02" - does not get rounded away under the player's cursor.
        /// </remarks>
        private string _toleranceText;

        // =====================================================================
        // Unity lifecycle
        // =====================================================================

        /// <summary>Claim the singleton slot and ask to be told when the launcher is ready.</summary>
        /// <remarks>
        /// The launcher may or may not already exist when an editor addon wakes up,
        /// so we both subscribe and check, and the add itself is guarded against
        /// running twice.
        /// </remarks>
        private void Awake()
        {
            if (_instance != null && _instance != this) { Destroy(this); return; }
            _instance = this;

            GameEvents.onGUIApplicationLauncherReady.Add(AddButton);
            if (ApplicationLauncher.Ready) AddButton();
        }

        /// <summary>Take the button back off the launcher when the editor scene ends.</summary>
        private void OnDestroy()
        {
            GameEvents.onGUIApplicationLauncherReady.Remove(AddButton);
            RemoveButton();
            if (_icon != null) Destroy(_icon);
            if (_instance == this) _instance = null;
        }

        // =====================================================================
        // The launcher button
        // =====================================================================

        /// <summary>Put our button on the stock toolbar, once.</summary>
        private void AddButton()
        {
            if (_button != null || ApplicationLauncher.Instance == null) return;

            _icon = BuildIcon();
            _button = ApplicationLauncher.Instance.AddModApplication(
                onTrue: () => _open = true,
                onFalse: () => _open = false,
                onHover: null, onHoverOut: null, onEnable: null, onDisable: null,
                visibleInScenes: ApplicationLauncher.AppScenes.VAB | ApplicationLauncher.AppScenes.SPH,
                texture: _icon);
        }

        /// <summary>Remove our button from the stock toolbar, if it is on it.</summary>
        private void RemoveButton()
        {
            if (_button == null) return;
            if (ApplicationLauncher.Instance != null)
                ApplicationLauncher.Instance.RemoveModApplication(_button);
            _button = null;
        }

        /// <summary>
        /// Draw the 38x38 toolbar icon: two bars of different widths joined by a
        /// stem, for two parts being brought to the same size.
        /// </summary>
        /// <returns>A texture owned by this addon and destroyed with it.</returns>
        /// <remarks>
        /// Generated rather than loaded so the mod stays a single DLL plus config
        /// with no art to keep in step with it.
        /// </remarks>
        private static Texture2D BuildIcon()
        {
            const int size = 38;
            var texture = new Texture2D(size, size, TextureFormat.ARGB32, false);
            var pixels = new Color32[size * size];

            Color32 clear = new Color32(0, 0, 0, 0);
            Color32 ink = new Color32(240, 240, 240, 255);
            for (int i = 0; i < pixels.Length; i++) pixels[i] = clear;

            // A narrow bar low down, a wide bar high up, and a stem between them:
            // the "make these match" idea in the fewest pixels that read at 38px.
            FillRect(pixels, size, 12, 6, 14, 5, ink);     // narrow bar
            FillRect(pixels, size, 17, 11, 4, 16, ink);    // stem
            FillRect(pixels, size, 5, 27, 28, 5, ink);     // wide bar

            texture.SetPixels32(pixels);
            texture.Apply(false);
            return texture;
        }

        /// <summary>Paint an axis-aligned block of pixels into a flat colour buffer.</summary>
        /// <param name="pixels">The buffer, row-major from the bottom left.</param>
        /// <param name="stride">Row length in pixels.</param>
        private static void FillRect(Color32[] pixels, int stride,
                                     int x, int y, int width, int height, Color32 colour)
        {
            for (int row = y; row < y + height; row++)
            {
                if (row < 0 || row * stride >= pixels.Length) continue;
                for (int column = x; column < x + width; column++)
                {
                    if (column < 0 || column >= stride) continue;
                    pixels[row * stride + column] = colour;
                }
            }
        }

        // =====================================================================
        // The settings window
        // =====================================================================

        /// <summary>Draw the settings window while it is open.</summary>
        private void OnGUI()
        {
            if (!_open) return;
            GUI.skin = HighLogic.Skin;
            _window = GUILayout.Window(_windowId, _window, DrawWindow, "DimensionSync",
                                       GUILayout.Width(320f));
        }

        /// <summary>
        /// Lay the window out and write any change straight back to the settings.
        /// </summary>
        /// <param name="id">Unity's window id, unused but required by the delegate.</param>
        /// <remarks>
        /// Saving on every change rather than behind an "apply" button keeps this
        /// honest: what the window shows is always what the mod is doing.
        /// </remarks>
        private void DrawWindow(int id)
        {
            GUILayout.BeginVertical();

            GUILayout.Label("<b>Matching</b>");
            DrawTolerance();

            GUILayout.Space(6f);
            GUILayout.Label("<b>Margin</b>");
            GUILayout.Label(
                "What happens to a gap that was small enough to count as a match. "
                + "Taking a 3.00 m next to a 3.02 m up to 6.00 m leaves the neighbour "
                + "at 6.00, 6.02 or 6.04.",
                HighLogic.Skin.label);
            DrawMarginChoice("Drop it", MarginMode.None, "neighbour lands on 6.00 m");
            DrawMarginChoice("Keep it in proportion", MarginMode.Proportional, "neighbour lands on 6.04 m");
            DrawMarginChoice("Keep its size", MarginMode.Absolute, "neighbour lands on 6.02 m");

            GUILayout.Space(6f);
            GUILayout.Label("<b>Wings</b>");
            DrawToggle(ref DimensionSettings.MatchControlSurfaceSweep,
                       "Match control surface sweep",
                       "Keep a control surface's sweep matched to the wing carrying it, "
                       + "so it still meets a wing whose tip chord has changed.");
            DrawToggle(ref DimensionSettings.AnchorSpanChanges,
                       "Hold the attached end still",
                       "When a part that runs alongside its host gets longer, grow it away "
                       + "from the host rather than through it.");
            DrawToggle(ref DimensionSettings.KeepWingEdgesStraight,
                       "Keep wing edges straight",
                       "Carry a change on through a run of wing segments whose edges form "
                       + "one straight line, instead of stopping at the first chord that differs.");
            DrawToggle(ref DimensionSettings.AlignWingJoints,
                       "Keep wing joints closed",
                       "Move a wing segment back onto the tip of the segment inboard of it "
                       + "when that one is swept or lengthened, rather than reshaping it.");

            GUILayout.EndVertical();
            GUI.DragWindow();
        }

        /// <summary>One radio-style row of the margin choice.</summary>
        /// <param name="label">What the mode is called in the window.</param>
        /// <param name="mode">The mode this row selects.</param>
        /// <param name="example">A worked example, shown beside the label.</param>
        private void DrawMarginChoice(string label, MarginMode mode, string example)
        {
            bool selected = DimensionSettings.Margin == mode;
            if (GUILayout.Toggle(selected, $" {label}  <i>({example})</i>") && !selected)
            {
                DimensionSettings.Margin = mode;
                DimensionSettings.Save();
            }
        }

        /// <summary>One checkbox bound to a settings field, saved when it changes.</summary>
        /// <param name="setting">The field to read and write.</param>
        /// <param name="label">The checkbox's label.</param>
        /// <param name="help">A sentence under it saying what it does.</param>
        private void DrawToggle(ref bool setting, string label, string help)
        {
            bool value = GUILayout.Toggle(setting, $" {label}");
            GUILayout.Label($"<i>{help}</i>", HighLogic.Skin.label);
            if (value == setting) return;
            setting = value;
            DimensionSettings.Save();
        }

        /// <summary>
        /// The match tolerance box, saved once the text in it parses to something
        /// sensible.
        /// </summary>
        /// <remarks>
        /// Shown as a percentage because that is how the setting reads - "within 1%"
        /// - even though it is stored as a fraction.
        /// </remarks>
        private void DrawTolerance()
        {
            _toleranceText = _toleranceText ?? (DimensionSettings.MatchTolerance * 100f).ToString("0.##");

            GUILayout.BeginHorizontal();
            GUILayout.Label("Two sizes match within", GUILayout.ExpandWidth(true));
            _toleranceText = GUILayout.TextField(_toleranceText, GUILayout.Width(50f));
            GUILayout.Label("%");
            GUILayout.EndHorizontal();

            if (!float.TryParse(_toleranceText, out float percent) || percent <= 0f || percent >= 100f) return;
            float fraction = percent / 100f;
            if (Mathf.Approximately(fraction, DimensionSettings.MatchTolerance)) return;

            DimensionSettings.MatchTolerance = fraction;
            DimensionSettings.Save();
        }
    }
}
