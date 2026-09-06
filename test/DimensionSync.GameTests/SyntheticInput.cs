using System.Diagnostics;
using UnityEngine;

namespace DimensionSync.GameTests
{
    /// <summary>
    /// Moves the real pointer and holds real keys, through xdotool, on the throwaway
    /// display an automated run owns.
    /// </summary>
    /// <remarks>
    /// The only way to reach the parts of a mod that read Unity's legacy Input. Those
    /// cannot be driven in process: Input.mousePosition and Input.GetKey report what
    /// the windowing system says and nothing else, so a test that wants to know what
    /// happens when somebody DRAGS has to make the pointer actually move.
    ///
    /// B9's wing handles are exactly that. It reads a mouse delta every frame while a
    /// key is held - B for root chord, T for tip chord, G to translate - and no amount
    /// of calling its methods reproduces it, because the numbers it uses never come
    /// from its arguments.
    ///
    /// Refuses to do anything unless the display is the one xvfb-run made for this run.
    /// Synthetic input goes to whatever window has focus, and on somebody's desktop
    /// that is whatever they happen to be doing - so this checks it is on :99 and
    /// otherwise reports itself unavailable, and the scenarios that need it skip.
    /// </remarks>
    internal static class SyntheticInput
    {
        /// <summary>The display an automated headless run is given.</summary>
        private const string HeadlessDisplay = ":99";

        /// <summary>Whether the pointer may be moved at all.</summary>
        /// <remarks>
        /// False on anything but the throwaway display, and false if xdotool is not
        /// installed. Both are reasons to skip rather than to fail: a scenario that
        /// cannot drive the pointer has not found a bug.
        /// </remarks>
        public static bool Available
        {
            get
            {
                string display = System.Environment.GetEnvironmentVariable("DISPLAY");
                if (string.IsNullOrEmpty(display) || !display.StartsWith(HeadlessDisplay)) return false;
                return Run("getdisplaygeometry", quiet: true);
            }
        }

        /// <summary>Why the pointer cannot be driven, for a skip message.</summary>
        public static string Unavailable
        {
            get
            {
                string display = System.Environment.GetEnvironmentVariable("DISPLAY");
                if (string.IsNullOrEmpty(display) || !display.StartsWith(HeadlessDisplay))
                    return $"the display is '{display}', not the throwaway {HeadlessDisplay} - " +
                           "the pointer is somebody's, so this will not touch it";
                return "xdotool is not installed";
            }
        }

        /// <summary>
        /// Give this process's own window the keyboard focus.
        /// </summary>
        /// <remarks>
        /// Pointer motion reaches whatever is under it, but keystrokes go to whatever
        /// has focus - so without this the pointer moves onto the wing and the key that
        /// is supposed to be held goes nowhere, and the drag reads as the mod ignoring
        /// it.
        ///
        /// Found by PID rather than by window name. Matching windows by title is how
        /// the wrong window gets picked, and the wrong window here is somebody's.
        /// </remarks>
        public static bool FocusOwnWindow()
        {
            int pid = Process.GetCurrentProcess().Id;
            return Run($"search --pid {pid} windowactivate --sync %1")
                   || Run($"search --pid {pid} windowfocus %1");
        }

        /// <summary>Where X thinks the pointer is, for checking it went where it was sent.</summary>
        public static string PointerLocation() => Capture("getmouselocation");

        /// <summary>
        /// The camera Unity delivers mouse events through.
        /// </summary>
        /// <remarks>
        /// Unity's picking - what decides which collider gets OnMouseOver - goes through
        /// the camera tagged MainCamera, which need not be the one a mod does its own
        /// arithmetic with. Projecting a world point through the wrong one puts the
        /// pointer at a screen position that means something else entirely, so the aim
        /// verifies as correct while the hover lands on a neighbouring part.
        /// </remarks>
        public static Camera PickingCamera => Camera.main ?? EditorCamera.Instance?.cam;

        /// <summary>The raw projection of a world point, for saying WHY an aim missed.</summary>
        /// <remarks>
        /// "Not on screen" covers two quite different problems - behind the camera, or
        /// outside the window - and they want opposite responses. Reporting the numbers
        /// rather than the verdict is the difference between fixing it and guessing.
        /// </remarks>
        public static string Project(Vector3 world)
        {
            Camera camera = PickingCamera;
            if (camera == null) return "no camera";

            Vector3 point = camera.WorldToScreenPoint(world);
            return $"raw ({point.x:F0}, {point.y:F0}, z {point.z:F1}) vs {Screen.width}x{Screen.height}";
        }

        /// <summary>Which cameras are in play, for working out why an aim missed.</summary>
        public static string Cameras()
        {
            Camera main = Camera.main;
            Camera editor = EditorCamera.Instance?.cam;
            return $"main '{(main == null ? "none" : main.name)}', " +
                   $"editor '{(editor == null ? "none" : editor.name)}', " +
                   $"same {(main != null && main == editor)}";
        }

        /// <summary>
        /// Put the pointer at a position inside the game's own window.
        /// </summary>
        /// <remarks>
        /// Window-relative, NOT screen-relative, because the two are not the same and
        /// the difference is silent. The window does not necessarily sit at the screen
        /// origin - on the run this was found on it sat at (128, 15) - so a pointer
        /// sent to a SCREEN position lands that far from where the caller meant, over
        /// empty space, while every check agrees the aim was right: the projection, the
        /// raycast and the part lookup all work in the window's coordinates and so all
        /// share the mistake. What disagrees is Unity's own Input.mousePosition, which
        /// is why <see cref="PointerAgrees"/> exists and why callers should use it.
        /// </remarks>
        public static void MoveTo(int x, int y)
        {
            string window = GameWindow;
            Run(window == null
                    ? $"mousemove --sync {x} {y}"
                    : $"mousemove --window {window} --sync {x} {y}");
        }

        /// <summary>
        /// Throw away the cached window id so the next move looks it up again.
        /// </summary>
        /// <remarks>
        /// The id is cached because resolving it costs two xdotool calls, but a cached
        /// id that has gone stale is worse than no cache: every move is sent to a
        /// window that is not there, xdotool reports nothing wrong, and the pointer
        /// simply never arrives - which reads from inside the game as Unity seeing the
        /// mouse at the origin, or still at wherever it was last put.
        /// </remarks>
        public static void ForgetWindow() => _gameWindow = null;

        /// <summary>Whether xdotool is answering at all, for telling a stale id from a dead tool.</summary>
        public static bool ToolResponds => Run("getactivewindow", quiet: true)
                                           || Run("getdisplaygeometry", quiet: true);

        /// <summary>The X id of this process's game window, or null.</summary>
        private static string _gameWindow;

        /// <summary>
        /// This process's drawing window, chosen by size rather than by being first.
        /// </summary>
        /// <remarks>
        /// A process can own several windows and only one of them is the one being
        /// drawn into. Picking the first is picking arbitrarily; the one whose geometry
        /// matches what Unity reports as the screen is the one whose coordinates the
        /// projections are in.
        /// </remarks>
        private static string GameWindow
        {
            get
            {
                if (_gameWindow != null) return _gameWindow.Length == 0 ? null : _gameWindow;

                _gameWindow = "";
                int pid = Process.GetCurrentProcess().Id;
                string found = Capture($"search --pid {pid}");
                if (string.IsNullOrEmpty(found)) return null;

                foreach (string line in found.Split('\n'))
                {
                    string id = line.Trim();
                    if (id.Length == 0) continue;

                    string geometry = Capture($"getwindowgeometry --shell {id}");
                    if (geometry == null) continue;
                    if (geometry.Contains($"WIDTH={Screen.width}")
                        && geometry.Contains($"HEIGHT={Screen.height}"))
                    {
                        _gameWindow = id;
                        break;
                    }
                }
                return _gameWindow.Length == 0 ? null : _gameWindow;
            }
        }

        /// <summary>
        /// Whether Unity agrees the pointer is where it was just sent.
        /// </summary>
        /// <remarks>
        /// The one check that cannot confirm its own mistake. Everything else the
        /// harness knows about the pointer comes from asking X where it put it, which
        /// is the same source that would be wrong; this asks the GAME, in the game's
        /// coordinates, and so catches a pointer that went somewhere else entirely.
        ///
        /// Takes the caller's coordinates - rows counted from the top, as
        /// <see cref="ScreenPoint"/> hands back - and flips them to Unity's before
        /// comparing, so a caller never has to hold two conventions at once.
        /// </remarks>
        public static bool PointerAgrees(int x, int y, out string detail)
        {
            Vector3 unity = Input.mousePosition;
            float wantedY = Screen.height - y;
            float offBy = Mathf.Max(Mathf.Abs(unity.x - x), Mathf.Abs(unity.y - wantedY));
            detail = $"aimed at ({x}, {y} from the top = {wantedY} in Unity's rows), " +
                     $"Unity sees ({unity.x:F0}, {unity.y:F0}), out by {offBy:F0}px";
            return offBy <= 2f;
        }

        /// <summary>Press and hold the left button, for a drag that spans many frames.</summary>
        /// <remarks>
        /// A click is press and release together, which is no use for dragging: the
        /// thing being pulled has to see the button held down across every frame the
        /// pointer moves. KSP's offset gizmo in particular only moves a part while one
        /// of its handles is held.
        /// </remarks>
        public static void ButtonDown() => Run("mousedown 1");

        /// <summary>Release the left button.</summary>
        public static void ButtonUp() => Run("mouseup 1");

        /// <summary>Click at the pointer's current position.</summary>
        public static void Click() => Run("click --clearmodifiers 1");

        /// <summary>Click the right button, which is what opens a part action window.</summary>
        public static void RightClick() => Run("click --clearmodifiers 3");

        /// <summary>Type text, as at a keyboard.</summary>
        /// <param name="text">What to type. Digits and a decimal point, here.</param>
        /// <remarks>
        /// A real key-by-key send rather than pasting: the input field this is aimed
        /// at validates as it goes, and a value that arrives all at once does not
        /// exercise that.
        /// </remarks>
        public static void TypeText(string text) => Run($"type --clearmodifiers -- '{text}'");

        /// <summary>
        /// Where a piece of user interface sits on screen, in the same top-left
        /// coordinates the pointer is driven in.
        /// </summary>
        /// <param name="rect">The element to locate.</param>
        /// <param name="x">Its horizontal position.</param>
        /// <param name="y">Its vertical position.</param>
        /// <remarks>
        /// Not <see cref="ScreenPoint"/>: that projects a point in the WORLD through
        /// the editor camera, and a button lives in a canvas instead. An overlay
        /// canvas is already in screen coordinates and must be converted with no
        /// camera at all, while one rendered through a camera needs that camera -
        /// passing the wrong one puts the pointer somewhere plausible and wrong,
        /// which is the failure this whole suite has learnt to distrust.
        /// </remarks>
        public static bool ScreenPointOfUI(RectTransform rect, out int x, out int y)
        {
            x = y = 0;
            if (rect == null) return false;

            Canvas canvas = rect.GetComponentInParent<Canvas>();
            if (canvas == null) return false;

            Camera camera = canvas.renderMode == RenderMode.ScreenSpaceOverlay
                ? null
                : canvas.worldCamera;
            // From the corners rather than from rect.position: a pivot that is not in
            // the middle puts "the position" on an edge, and for a small button that
            // is the difference between hitting it and hitting what is behind it.
            var corners = new Vector3[4];
            rect.GetWorldCorners(corners);
            Vector2 point = Vector2.zero;
            for (int i = 0; i < 4; i++)
                point += RectTransformUtility.WorldToScreenPoint(camera, corners[i]);
            point /= 4f;

            x = Mathf.RoundToInt(point.x);
            y = Mathf.RoundToInt(Screen.height - point.y);
            return x >= 0 && y >= 0 && x < Screen.width && y < Screen.height;
        }

        /// <summary>Press and release a key.</summary>
        public static void Press(string key) => Run($"key --clearmodifiers {key}");

        /// <summary>Hold a key down until <see cref="KeyUp"/>.</summary>
        public static void KeyDown(string key) => Run($"keydown {key}");

        /// <summary>Release a key.</summary>
        public static void KeyUp(string key) => Run($"keyup {key}");

        /// <summary>
        /// Where a point in the world falls on the screen, in X's coordinates.
        /// </summary>
        /// <remarks>
        /// Unity counts screen rows from the bottom and X counts them from the top, so
        /// the vertical axis is flipped. Getting that wrong does not fail loudly - it
        /// drags the wrong way and looks like the mod responding backwards.
        /// </remarks>
        public static bool ScreenPoint(Vector3 world, out int x, out int y)
        {
            x = y = 0;
            Camera camera = PickingCamera;
            if (camera == null) return false;

            Vector3 point = camera.WorldToScreenPoint(world);
            if (point.z <= 0f) return false;              // behind the camera

            x = Mathf.RoundToInt(point.x);
            y = Mathf.RoundToInt(Screen.height - point.y);
            return x >= 0 && y >= 0 && x < Screen.width && y < Screen.height;
        }

        /// <summary>
        /// Which part the editor camera sees at a screen position, or null.
        /// </summary>
        /// <remarks>
        /// Aiming at a part's centre is not the same as aiming AT the part: a control
        /// surface lying along a wing's edge can be the thing under the pointer at a
        /// point that belongs, geometrically, to the wing. Asking what the ray actually
        /// hits is the difference between a test that drags what it meant to and one
        /// that quietly drags its neighbour and reports the mod ignored it.
        /// </remarks>
        public static Part PartAt(int x, int y)
        {
            Camera camera = PickingCamera;
            if (camera == null) return null;

            // Back to Unity's way round for the ray.
            var screen = new Vector3(x, Screen.height - y, 0f);
            return Physics.Raycast(camera.ScreenPointToRay(screen), out RaycastHit hit, 10000f)
                ? hit.collider?.GetComponentInParent<Part>()
                : null;
        }

        /// <summary>
        /// Everything on the ray at a screen position, nearest first, across ALL layers.
        /// </summary>
        /// <remarks>
        /// A plain Raycast uses DefaultRaycastLayers, which leaves some layers out;
        /// Unity's own mouse picking uses the camera's eventMask instead. Something
        /// sitting in front on a layer the first ignores is invisible to every check
        /// while being decisive for the hover - so this asks for all of them and names
        /// what it finds.
        /// </remarks>
        public static string Everything(int x, int y)
        {
            Camera camera = PickingCamera;
            if (camera == null) return "no camera";

            var screen = new Vector3(x, Screen.height - y, 0f);
            RaycastHit[] hits = Physics.RaycastAll(camera.ScreenPointToRay(screen), 10000f, ~0);
            if (hits.Length == 0) return "nothing on the ray";

            System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
            var text = new System.Text.StringBuilder();
            for (int i = 0; i < hits.Length && i < 6; i++)
            {
                Collider collider = hits[i].collider;
                Part part = collider?.GetComponentInParent<Part>();
                text.Append($"[{hits[i].distance:F1}m {collider?.name} layer {collider?.gameObject.layer} " +
                            $"trigger {collider?.isTrigger} part {(part == null ? "none" : part.name)}] ");
            }
            return text.ToString();
        }

        /// <summary>Run one xdotool command and hand back what it printed.</summary>
        private static string Capture(string arguments)
        {
            try
            {
                var start = new ProcessStartInfo("xdotool", arguments)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                using (Process process = Process.Start(start))
                {
                    if (process == null) return string.Empty;
                    string output = process.StandardOutput.ReadToEnd();
                    process.StandardError.ReadToEnd();
                    process.WaitForExit(2000);
                    return output.Trim().Replace('\n', ' ');
                }
            }
            catch (System.Exception)
            {
                return string.Empty;
            }
        }

        /// <summary>Run one xdotool command, returning whether it succeeded.</summary>
        private static bool Run(string arguments, bool quiet = false)
        {
            try
            {
                var start = new ProcessStartInfo("xdotool", arguments)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                using (Process process = Process.Start(start))
                {
                    if (process == null) return false;
                    process.StandardOutput.ReadToEnd();
                    process.StandardError.ReadToEnd();
                    process.WaitForExit(2000);
                    return process.HasExited && process.ExitCode == 0;
                }
            }
            catch (System.Exception error)
            {
                if (!quiet) Harness.LogError($"xdotool {arguments} failed: {error.Message}");
                return false;
            }
        }
    }
}
