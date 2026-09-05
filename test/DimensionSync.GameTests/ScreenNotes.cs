using UnityEngine;

namespace DimensionSync.GameTests
{
    /// <summary>
    /// Draws a running commentary, and the pointer, over the game while it runs.
    /// </summary>
    /// <remarks>
    /// For recordings that somebody else has to make sense of. A video of an automated
    /// run is a craft sitting still and then changing, with no indication of what was
    /// being attempted or what the harness thought was happening - and the thing most
    /// worth seeing here is WHERE THE POINTER IS, which a screen capture of an X
    /// display does not draw at all.
    ///
    /// Written from the same process that does the work, so the caption cannot drift
    /// out of step with what it describes.
    /// </remarks>
    internal static class ScreenNotes
    {
        /// <summary>What is happening now. Shown until something replaces it.</summary>
        public static string Text = string.Empty;

        /// <summary>A point being aimed at, in Unity screen coordinates, or null.</summary>
        public static Vector2? Target;

        /// <summary>Start drawing, if not already.</summary>
        public static void Show()
        {
            if (_drawer != null) return;
            var holder = new GameObject("DimensionSyncScreenNotes");
            Object.DontDestroyOnLoad(holder);
            _drawer = holder.AddComponent<Drawer>();
        }

        /// <summary>Set the caption and log the same words, so the two can be lined up.</summary>
        public static void Say(string text)
        {
            Text = text;
            Harness.Log($"SCREEN {text}");
        }

        private static Drawer _drawer;

        /// <summary>The MonoBehaviour that actually paints it.</summary>
        private class Drawer : MonoBehaviour
        {
            private Texture2D _dot;

            private void OnGUI()
            {
                if (_dot == null)
                {
                    _dot = new Texture2D(1, 1);
                    _dot.SetPixel(0, 0, Color.white);
                    _dot.Apply();
                }

                // The pointer, drawn as a cross. X does not composite a cursor into a
                // captured display, so without this a recording shows the effects of a
                // drag with no sign of what is doing it.
                Vector3 mouse = Input.mousePosition;
                float mouseY = Screen.height - mouse.y;
                GUI.color = new Color(1f, 0.2f, 0.2f, 0.9f);
                GUI.DrawTexture(new Rect(mouse.x - 14f, mouseY - 1f, 28f, 2f), _dot);
                GUI.DrawTexture(new Rect(mouse.x - 1f, mouseY - 14f, 2f, 28f), _dot);

                // Where the harness MEANT to aim, so the two can be compared by eye.
                if (Target.HasValue)
                {
                    GUI.color = new Color(0.2f, 1f, 0.4f, 0.8f);
                    float targetY = Screen.height - Target.Value.y;
                    GUI.DrawTexture(new Rect(Target.Value.x - 10f, targetY - 10f, 20f, 2f), _dot);
                    GUI.DrawTexture(new Rect(Target.Value.x - 10f, targetY + 8f, 20f, 2f), _dot);
                    GUI.DrawTexture(new Rect(Target.Value.x - 10f, targetY - 10f, 2f, 20f), _dot);
                    GUI.DrawTexture(new Rect(Target.Value.x + 8f, targetY - 10f, 2f, 20f), _dot);
                }

                GUI.color = Color.white;
                if (string.IsNullOrEmpty(Text)) return;

                var style = new GUIStyle(GUI.skin.label)
                {
                    fontSize = 20,
                    wordWrap = true,
                    alignment = TextAnchor.UpperLeft,
                };

                var area = new Rect(20f, 20f, Screen.width - 40f, 90f);
                GUI.color = new Color(0f, 0f, 0f, 0.65f);
                GUI.DrawTexture(area, _dot);
                GUI.color = Color.white;
                GUI.Label(new Rect(area.x + 10f, area.y + 8f, area.width - 20f, area.height - 16f),
                          Text, style);
            }
        }
    }
}
