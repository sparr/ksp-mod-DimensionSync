using System.Collections;
using System.IO;
using UnityEngine;

namespace DimensionSync.GameTests
{
    /// <summary>
    /// Loads a craft the player saved and writes down how its wings and control
    /// surfaces are actually oriented.
    /// </summary>
    /// <remarks>
    /// The scenarios in this suite build every fixture programmatically, with each
    /// part's orientation set explicitly - which means they produce the same geometry
    /// whichever editor they run in, and can never reproduce a problem that comes
    /// from how KSP orients a part when a PERSON places it. The Spaceplane Hangar
    /// lays a craft out along different axes from the VAB, so the same click produces
    /// a differently-oriented part there, and none of that is reachable from here.
    ///
    /// This closes that gap the only way that is honest: by reading a craft somebody
    /// actually built and reporting what is really in it.
    /// </remarks>
    [KSPAddon(KSPAddon.Startup.EditorAny, false)]
    public class CraftInspector : MonoBehaviour
    {
        /// <summary>Wait for the editor, then load and report.</summary>
        private void Start()
        {
            if (string.IsNullOrEmpty(Harness.InspectCraft)) { Destroy(gameObject); return; }
            StartCoroutine(Run());
        }

        /// <summary>Load the named craft and describe every wing joint in it.</summary>
        private IEnumerator Run()
        {
            for (int i = 0; i < 90; i++) yield return null;
            while (EditorLogic.fetch == null) yield return null;

            string craft = Harness.InspectCraft;
            string path = FindCraft(craft);
            if (path == null)
            {
                Harness.LogError($"no craft named '{craft}' found under saves/*/Ships");
                yield break;
            }

            Harness.Log($"loading craft {path}");
            EditorLogic.LoadShipFromFile(path);
            for (int i = 0; i < 120; i++) yield return null;

            ShipConstruct ship = EditorLogic.fetch.ship;
            if (ship == null) { Harness.LogError("the craft did not load"); yield break; }

            Harness.Log($"craft '{ship.shipName}' has {ship.parts.Count} parts, " +
                        $"editor is {EditorDriver.editorFacility}, pass {_pass + 1}");

            foreach (Part part in ship.parts) Describe(part);

            if (Harness.InspectEdit) yield return Edit(ship);

            // Second pass: leave the editor and come back, with the craft still
            // loaded, then make the same edit again. That is the difference between
            // "load and change it" and "load, exit, re-enter, change it" - the same
            // craft and the same edit, and only one of them misbehaves.
            if (Harness.InspectEdit && _pass == 0)
            {
                _pass = 1;
                Harness.Log("leaving the editor and coming back with the craft still loaded");
                EditorDriver.StartupBehaviour = EditorDriver.StartupBehaviours.LOAD_FROM_CACHE;
                EditorDriver.StartEditor(EditorDriver.editorFacility);
                yield break;
            }

            Harness.Log("inspection finished");
        }

        /// <summary>How many times the craft has been looked at this session.</summary>
        /// <remarks>Static so it survives the editor scene being reloaded.</remarks>
        private static int _pass;

        /// <summary>
        /// Thicken one wing's root and report what every wing and surface ends up at.
        /// </summary>
        /// <remarks>
        /// Written through the same route a part action window uses, so the mod sees
        /// it exactly as it would see a person doing it.
        /// </remarks>
        private IEnumerator Edit(ShipConstruct ship)
        {
            // Default: thicken the first wing's root, which is what this was written
            // to do. -dstest-inspect-set replaces that with whatever is worth asking.
            string which = "wing";
            string field = "sharedBaseThicknessRoot";
            float value = 1f;

            string spec = Harness.InspectSet;
            if (!string.IsNullOrEmpty(spec))
            {
                int colon = spec.IndexOf(':');
                int equals = spec.IndexOf('=');
                if (colon < 0 || equals < colon)
                {
                    Harness.LogError($"cannot read '-dstest-inspect-set {spec}', " +
                                     "expected <wing|ctrl>:<field>=<value>");
                    yield break;
                }
                which = spec.Substring(0, colon).Trim();
                field = spec.Substring(colon + 1, equals - colon - 1).Trim();
                if (!float.TryParse(spec.Substring(equals + 1).Trim(), out value))
                {
                    Harness.LogError($"cannot read a number from '{spec}'");
                    yield break;
                }
            }

            bool wantControl = which.StartsWith("ctrl", System.StringComparison.OrdinalIgnoreCase);
            Part target = null;
            foreach (Part part in ship.parts)
            {
                PartModule module = Module(part, "WingProcedural");
                if (module == null) continue;
                bool control = module.Fields["isCtrlSrf"]?.GetValue(module) is bool c && c;
                if (control != wantControl) continue;
                target = part;
                break;
            }
            if (target == null)
            {
                Harness.LogError($"no {(wantControl ? "control surface" : "wing")} to edit");
                yield break;
            }

            Harness.Log($"BEFORE  {Thicknesses(ship)}");
            foreach (Part part in ship.parts) Describe(part);
            Harness.Log($"setting '{target.name}' {field} to {value}");
            PartFields.Set(target, "WingProcedural", field, value,
                           WriteMode.PartActionWindow);

            for (int i = 0; i < 180; i++) yield return null;
            Harness.Log($"AFTER   {Thicknesses(ship)}");
            foreach (Part part in ship.parts) Describe(part);
        }

        /// <summary>Every wing and surface's root and tip thickness, on one line.</summary>
        private static string Thicknesses(ShipConstruct ship)
        {
            var text = new System.Text.StringBuilder();
            foreach (Part part in ship.parts)
            {
                PartModule module = Module(part, "WingProcedural");
                if (module == null) continue;

                bool control = module.Fields["isCtrlSrf"]?.GetValue(module) is bool c && c;
                text.Append(control ? "flap " : "wing ");
                text.Append($"{Get(module, "sharedBaseThicknessRoot"):F2}/" +
                            $"{Get(module, "sharedBaseThicknessTip"):F2}  ");
            }
            return text.ToString();
        }

        /// <summary>Report one part's orientation relative to whatever it is attached to.</summary>
        /// <remarks>
        /// Everything is reported in the PARENT's frame, because that is the frame
        /// every rule in the mod reasons in. A part whose own axes come out reversed
        /// there is one those rules will get backwards.
        /// </remarks>
        private static void Describe(Part part)
        {
            PartModule wing = Module(part, "WingProcedural");
            if (wing == null) return;

            bool control = wing.Fields["isCtrlSrf"]?.GetValue(wing) is bool flag && flag;
            string kind = control ? "control surface" : "wing";

            if (part.parent == null)
            {
                Harness.Log($"  {kind} '{part.name}' has no parent");
                return;
            }

            Transform parent = part.parent.transform;
            Vector3 span = parent.InverseTransformDirection(part.transform.right);
            Vector3 chord = parent.InverseTransformDirection(part.transform.up);
            Vector3 thick = parent.InverseTransformDirection(part.transform.forward);
            Vector3 where = parent.InverseTransformPoint(part.transform.position);

            // Layers and colliders, because a part the harness builds and one the game
            // builds differ there - and that decides whether the part can be hovered or
            // picked at all, however right its geometry is.
            var colliders = new System.Text.StringBuilder();
            foreach (Collider collider in part.GetComponentsInChildren<Collider>(true))
                colliders.Append($"{collider.name}(layer {collider.gameObject.layer}, " +
                                 $"trigger {collider.isTrigger}, " +
                                 $"onRoot {collider.gameObject == part.gameObject}) ");
            Harness.Log($"  LAYERS '{part.name}': part layer {part.gameObject.layer}, " +
                        $"colliders {colliders}");

            Harness.Log($"  {kind} '{part.name}' on '{part.parent.name}' " +
                        $"({(part.attachMode == AttachModes.SRF_ATTACH ? "surface" : "stack")})");
            Harness.Log($"      at {Round(where)} in its parent's frame");
            Harness.Log($"      its span axis points {Round(span)}, chord {Round(chord)}, thickness {Round(thick)}");
            Harness.Log($"      span reversed: {span.x < 0f}, chord reversed: {chord.y < 0f}, " +
                        $"upside down: {thick.z < 0f}");
            Harness.Log($"      length {Get(wing, "sharedBaseLength"):F3}, " +
                        $"chords {Get(wing, "sharedBaseWidthRoot"):F3}/{Get(wing, "sharedBaseWidthTip"):F3}, " +
                        $"thickness {Get(wing, "sharedBaseThicknessRoot"):F3}/{Get(wing, "sharedBaseThicknessTip"):F3}, " +
                        $"offsets {Get(wing, "sharedBaseOffsetRoot"):F3}/{Get(wing, "sharedBaseOffsetTip"):F3}");
        }

        /// <summary>Two decimal places, so a near-axis vector reads as one.</summary>
        private static string Round(Vector3 v) => $"({v.x:F2}, {v.y:F2}, {v.z:F2})";

        /// <summary>A float field's value, or NaN.</summary>
        private static float Get(PartModule module, string name)
        {
            object value = module.Fields[name]?.GetValue(module);
            return value is float number ? number : float.NaN;
        }

        /// <summary>A named PartModule, or null.</summary>
        private static PartModule Module(Part part, string className)
        {
            for (int i = 0; i < part.Modules.Count; i++)
                if (part.Modules[i]?.GetType().Name == className) return part.Modules[i];
            return null;
        }

        /// <summary>Find a .craft by name under any save's Ships folder.</summary>
        /// <remarks>Searched rather than built from a path so the caller only needs the name.</remarks>
        private static string FindCraft(string name)
        {
            string saves = Path.Combine(KSPUtil.ApplicationRootPath, "saves");
            if (!Directory.Exists(saves)) return null;

            foreach (string file in Directory.GetFiles(saves, "*.craft", SearchOption.AllDirectories))
            {
                if (Path.GetFileNameWithoutExtension(file).Equals(name, System.StringComparison.OrdinalIgnoreCase))
                    return file;
            }
            return null;
        }
    }
}
