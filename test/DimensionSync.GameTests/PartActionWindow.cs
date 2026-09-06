using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;

namespace DimensionSync.GameTests
{
    /// <summary>
    /// Drives KSP's real part action window, rather than imitating what it does.
    /// </summary>
    /// <remarks>
    /// Every other scenario in this suite reaches a field through
    /// <see cref="WriteMode.PartActionWindow"/>, which is our own reimplementation of
    /// the window's behaviour - set the field, fire onFieldChanged, copy to the
    /// symmetry counterparts. It is a good imitation and it is still an imitation, so
    /// it can only ever confirm what we already believed the window does. Anything
    /// KSP does that we did not think to copy is invisible to every one of those
    /// scenarios, and so is anything another mod does in response to the real thing.
    ///
    /// This opens the actual window, finds the actual widget for a field, and calls
    /// the handler its buttons are wired to. The click is synthesised at the handler
    /// rather than at the pixel: locating a small button in screen space and hitting
    /// it with the pointer is what makes the gizmo scenarios flaky, and none of that
    /// risk buys anything here, because what is under test is KSP's field-setting
    /// path and not Unity's ability to route a mouse click.
    ///
    /// Note there is nothing to type into. A UI_FloatEdit renders its value as a
    /// label with increment buttons and a slider beside it, so "entering a value" in
    /// this window means tapping those buttons, which is what this does.
    /// </remarks>
    internal static class PartActionWindow
    {
        /// <summary>Open the real part action window for a part.</summary>
        /// <param name="part">The part whose window to open.</param>
        /// <param name="why">What went wrong, when it does.</param>
        public static bool Open(Part part, out string why)
        {
            why = null;
            if (part == null) { why = "no part"; return false; }
            if (UIPartActionController.Instance == null)
            {
                why = "there is no part action controller in this scene";
                return false;
            }

            // overrideSymmetry false, so the window behaves as it does when a player
            // right-clicks a part of a symmetric set - which is the case worth testing.
            UIPartActionController.Instance.SpawnPartActionWindow(part, false);
            return true;
        }

        /// <summary>Close whatever windows are open, so one scenario cannot leak into the next.</summary>
        public static void CloseAll()
        {
            // Deselect, not a close: the controller has no ClearAll, and Deselect is
            // what the editor itself calls when the player clicks away. false leaves
            // the resource panel alone, which nothing here has touched.
            UIPartActionController.Instance?.Deselect(false);
        }

        /// <summary>The open window for a part, or null.</summary>
        /// <param name="part">The part to look for.</param>
        public static UIPartActionWindow WindowFor(Part part)
        {
            return part == null ? null : UIPartActionController.Instance?.GetItem(part);
        }

        /// <summary>
        /// The float-edit widget the window is showing for one field of one module.
        /// </summary>
        /// <param name="part">The part whose window to search.</param>
        /// <param name="moduleName">The module class the field belongs to.</param>
        /// <param name="fieldName">The field's name.</param>
        /// <remarks>
        /// Matched on the module as well as the field name because several mods put
        /// the same name on more than one module of a part - ProceduralParts keeps a
        /// diameter on every shape module it has - and the window only shows the one
        /// that is live.
        /// </remarks>
        public static UIPartActionFloatEdit FloatEditFor(Part part, string moduleName, string fieldName)
        {
            UIPartActionWindow window = WindowFor(part);
            if (window == null) return null;

            List<UIPartActionItem> items = window.ListItems;
            if (items == null) return null;

            UIPartActionFloatEdit anyName = null;
            for (int i = 0; i < items.Count; i++)
            {
                if (!(items[i] is UIPartActionFloatEdit edit)) continue;
                if (edit.Field == null || edit.Field.name != fieldName) continue;

                anyName = anyName ?? edit;
                if (edit.Field.host != null && edit.Field.host.GetType().Name == moduleName) return edit;
            }
            // The name matched but the module did not, which is still likelier to be
            // the field wanted than nothing at all - said plainly rather than hidden.
            return anyName;
        }

        /// <summary>
        /// What Unity's own event system finds under a screen position, top first.
        /// </summary>
        /// <param name="x">Horizontal position, measured from the left.</param>
        /// <param name="y">Vertical position, measured from the TOP, as the pointer is driven.</param>
        /// <remarks>
        /// A click that lands where it was aimed and does nothing has two very
        /// different explanations - the aim was wrong, or the thing there does not
        /// take clicks - and no amount of staring at coordinates separates them. This
        /// asks the same machinery a real click goes through.
        /// </remarks>
        public static string WhatIsUnder(int x, int y)
        {
            if (EventSystem.current == null) return "there is no event system";

            var where = new PointerEventData(EventSystem.current)
            {
                position = new Vector2(x, Screen.height - y),
            };
            var hits = new List<RaycastResult>();
            EventSystem.current.RaycastAll(where, hits);
            if (hits.Count == 0) return "nothing";

            var names = new List<string>();
            for (int i = 0; i < hits.Count && i < 3; i++) names.Add(hits[i].gameObject.name);
            return string.Join(" / ", names.ToArray());
        }

        /// <summary>Where each of a float-edit's buttons is, and what is over it.</summary>
        /// <param name="edit">The widget to survey.</param>
        /// <remarks>
        /// Kept even though the click now lands, because without it "the click did
        /// nothing" is unreadable. It was this that showed the pointer sitting on the
        /// scroll viewport instead of the button, which was a pivot problem in the
        /// projection and looked from the outside exactly like a mod ignoring input.
        /// </remarks>
        public static string DescribeButtons(UIPartActionFloatEdit edit)
        {
            if (edit == null) return "no control";
            var said = new List<string>();
            AddButton(said, "incLarge", edit.incLarge);
            AddButton(said, "incSmall", edit.incSmall);
            AddButton(said, "decLarge", edit.decLarge);
            AddButton(said, "slider", edit.slider);
            return string.Join("; ", said.ToArray());
        }

        /// <summary>One entry of <see cref="DescribeButtons"/>.</summary>
        private static void AddButton(List<string> into, string name, MonoBehaviour button)
        {
            if (button == null) return;
            if (SyntheticInput.ScreenPointOfUI(button.transform as RectTransform, out int x, out int y))
                into.Add($"{name} ({x},{y}) over {WhatIsUnder(x, y)}");
            else
                into.Add($"{name} off screen");
        }

        /// <summary>How a float-edit control is set up, for a log line.</summary>
        /// <param name="edit">The widget to describe.</param>
        public static string Describe(UIPartActionFloatEdit edit)
        {
            if (edit == null) return "no control";
            var control = edit.Field?.uiControlEditor as UI_FloatEdit;
            string increments = control == null
                ? "no UI_FloatEdit"
                : $"incLarge {control.incrementLarge:F4} incSmall {control.incrementSmall:F4} " +
                  $"slide {control.incrementSlide:F4} range {control.minValue:F3}..{control.maxValue:F3}";
            return $"{increments}; incLarge button active " +
                   $"{edit.incLarge != null && edit.incLarge.gameObject.activeInHierarchy}";
        }

        /// <summary>
        /// Tap a float-edit's increment or decrement button, as a player clicking it
        /// does.
        /// </summary>
        /// <param name="edit">The widget to tap.</param>
        /// <param name="up">True for the increment side, false for the decrement side.</param>
        /// <param name="large">True for the coarse button, false for the fine one.</param>
        /// <remarks>
        /// These are the very methods the buttons' listeners are bound to in
        /// <c>UIPartActionFloatEdit.Setup</c>, so everything downstream - the clamp
        /// against the control's own limits, KSP's SetFieldValue, the write out to
        /// symmetry counterparts, onFieldChanged - runs exactly as it does for a
        /// click.
        /// </remarks>
        public static void Tap(UIPartActionFloatEdit edit, bool up, bool large)
        {
            if (edit == null) return;
            if (up && large) edit.OnTap_incLarge();
            else if (up) edit.OnTap_incSmall();
            else if (large) edit.OnTap_decLarge();
            else edit.OnTap_decSmall();
        }
    }
}
