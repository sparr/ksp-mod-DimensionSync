using System;
using System.Reflection;
using UnityEngine;

namespace DimensionSync.GameTests
{
    /// <summary>How a test writes a value into a part's field.</summary>
    internal enum WriteMode
    {
        /// <summary>
        /// Exactly what the part action window does: set the field, then hand
        /// <c>onFieldChanged</c> the previous value.
        /// </summary>
        PartActionWindow,

        /// <summary>
        /// A bare field assignment with no events at all, which is how several
        /// mods change their own dimensions internally.
        /// </summary>
        DirectAssignment,

        /// <summary>
        /// Set the field and then run B9's own panel logic over it, as pressing J
        /// and dragging a slider does.
        /// </summary>
        /// <remarks>
        /// The other two modes reach B9 only through KSPField plumbing, which its J
        /// window does not use: that window compares each shared field against a
        /// cache every frame and applies its OWN limits before rebuilding anything.
        /// Those limits are not the same as the KSPField ones - a control surface is
        /// capped at 2 m of chord where a wing is allowed 40 - so a value this mod
        /// writes can sit happily in the field until the moment the player opens B9's
        /// panel, at which point it is clamped and the part visibly changes size for
        /// no reason the player did. Writing through this mode is the only way a test
        /// can see that coming.
        /// </remarks>
        B9Window,
    }

    /// <summary>
    /// Reads and writes KSPFields on live parts by module class name and field
    /// name, so scenarios can talk about "ProceduralShapeCylinder.diameter" without
    /// referencing any mod assembly.
    /// </summary>
    internal static class PartFields
    {
        /// <summary>Find a module on a part by class name, enabled or not.</summary>
        public static PartModule Module(Part part, string moduleClassName)
        {
            if (part == null) return null;
            for (int i = 0; i < part.Modules.Count; i++)
            {
                PartModule module = part.Modules[i];
                if (module == null) continue;
                if (module.GetType().Name != moduleClassName) continue;
                return module;
            }
            return null;
        }

        /// <summary>The enabled module of the given class, or null.</summary>
        public static PartModule ActiveModule(Part part, string moduleClassName)
        {
            PartModule module = Module(part, moduleClassName);
            if (module == null) return null;
            return module.isEnabled && module.enabled ? module : null;
        }

        /// <summary>Find a named field on a named module, or null when either is missing.</summary>
        public static BaseField Field(Part part, string moduleClassName, string fieldName)
        {
            PartModule module = Module(part, moduleClassName);
            return module?.Fields[fieldName];
        }

        /// <summary>
        /// Read a numeric field. Returns NaN when the field is absent or not
        /// numeric, which the assertion helper reports as "value unavailable"
        /// rather than as a wrong number.
        /// </summary>
        public static float Get(Part part, string moduleClassName, string fieldName)
        {
            BaseField field = Field(part, moduleClassName, fieldName);
            if (field == null) return float.NaN;
            object value = field.GetValue(field.host);
            switch (value)
            {
                case float f: return f;
                case double d: return (float)d;
                case int i: return i;
                default: return float.NaN;
            }
        }

        /// <summary>
        /// Write a numeric field the way <paramref name="mode"/> says, so a scenario
        /// can choose whether DimensionSync is being asked to notice a part action
        /// window edit or a mod's bare field assignment.
        /// </summary>
        public static void Set(Part part, string moduleClassName, string fieldName, float value, WriteMode mode)
        {
            BaseField field = Field(part, moduleClassName, fieldName);
            if (field == null)
            {
                Harness.LogError($"no field {moduleClassName}.{fieldName} on {part?.name}");
                return;
            }

            object boxed = Box(field.FieldInfo.FieldType, value);
            if (boxed == null)
            {
                Harness.LogError($"{moduleClassName}.{fieldName} is not numeric");
                return;
            }

            SetRaw(part, moduleClassName, fieldName, boxed, mode);
        }

        /// <summary>
        /// Write any already-boxed value, for the non-numeric fields a fixture
        /// sometimes needs - a texture name, or a flag that changes how a mod
        /// textures itself.
        /// </summary>
        public static void SetRaw(Part part, string moduleClassName, string fieldName, object value, WriteMode mode)
        {
            PartModule module = Module(part, moduleClassName);
            BaseField field = module?.Fields[fieldName];
            if (field == null)
            {
                Harness.LogError($"no field {moduleClassName}.{fieldName} on {part?.name}");
                return;
            }

            if (mode == WriteMode.DirectAssignment)
            {
                field.FieldInfo.SetValue(module, value);
                return;
            }

            if (mode == WriteMode.B9Window)
            {
                field.FieldInfo.SetValue(module, value);
                RunB9FieldCheck(module);
                return;
            }

            object oldValue = field.GetValue(field.host);
            field.SetValue(value, field.host);
            UI_Control control = field.uiControlEditor;
            control?.onFieldChanged?.Invoke(field, oldValue);

            // The real part action window pushes an edit out to the part's symmetry
            // counterparts before anything else sees it. Skipping that leaves a
            // fixture in a state a player could not produce - one booster resized and
            // its mirror image not - and then blames whatever runs next for it.
            if (control != null && (control.affectSymCounterparts & UI_Scene.Editor) != UI_Scene.None)
                SetCounterparts(part, module, field, value);
        }

        /// <summary>
        /// Run B9's panel logic over a part without changing anything, as opening the
        /// J window does.
        /// </summary>
        /// <remarks>
        /// For asking "would B9 accept what is on this part right now?". Anything that
        /// moves as a result was a value B9 was never going to keep.
        /// </remarks>
        public static void OpenB9Panel(Part part, string moduleClassName)
        {
            PartModule module = Module(part, moduleClassName);
            if (module != null) RunB9FieldCheck(module);
        }

        /// <summary>Run B9's own per-frame field check over a WingProcedural module.</summary>
        /// <remarks>
        /// CheckAllFieldValues is what B9's J window calls: it compares every shared
        /// field against its cache, clamps anything outside the part's limits, and
        /// reports whether the geometry or the aerodynamics need rebuilding. Private,
        /// so it is reached by reflection - the alternative is duplicating B9's limit
        /// table here, which would then be a second copy to keep in step and would
        /// pass happily while the real one disagreed.
        ///
        /// Quietly does nothing for a module that has no such method, so this mode
        /// stays usable on parts from other mods.
        /// </remarks>
        private static void RunB9FieldCheck(PartModule module)
        {
            System.Reflection.MethodInfo check = module.GetType().GetMethod(
                "CheckAllFieldValues",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (check == null) return;

            object[] args = { false, false };
            check.Invoke(module, args);

            bool geometry = args[0] is bool g && g;
            if (!geometry) return;

            System.Reflection.MethodInfo update = module.GetType().GetMethod(
                "UpdateGeometry",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            update?.Invoke(module, new object[] { args[1] is bool a && a });
        }

        /// <summary>Write the same value to a part's symmetry counterparts.</summary>
        /// <remarks>
        /// Mostly a copy of what UIPartActionFieldItem does: set the field and fire
        /// onSymmetryFieldChanged with the NEW value, which is the convention stock
        /// uses there even though onFieldChanged takes the old one.
        ///
        /// It also fires onFieldChanged on the counterpart, which stock does not, and
        /// that is a deliberate difference. ProceduralParts only wires
        /// onSymmetryFieldChanged for a couple of fields - shape selection and fillet
        /// clamping - and leaves it null on the plain dimension sliders, so the
        /// symmetry path alone sets the number and never rebuilds the mesh. The
        /// counterpart ends up holding the right diameter while still drawn at the
        /// old one. What this method owes its callers is the state a player's edit
        /// would leave behind, not a bit-exact reproduction of the route stock takes
        /// to get there.
        /// </remarks>
        private static void SetCounterparts(Part part, PartModule module, BaseField field, object value)
        {
            if (part.symmetryCounterparts == null) return;

            int index = part.Modules.IndexOf(module);
            foreach (Part counterpart in part.symmetryCounterparts)
            {
                if (counterpart == null) continue;

                PartModule twin = index >= 0 && index < counterpart.Modules.Count
                                  && counterpart.Modules[index]?.GetType() == module.GetType()
                    ? counterpart.Modules[index]
                    : counterpart.Modules[module.ClassName];

                BaseField twinField = twin?.Fields[field.name];
                if (twinField == null) continue;

                object oldValue = twinField.GetValue(twinField.host);
                twinField.SetValue(value, twinField.host);

                UI_Control twinControl = twinField.uiControlEditor;
                twinControl?.onSymmetryFieldChanged?.Invoke(field, value);
                twinControl?.onFieldChanged?.Invoke(twinField, oldValue);
            }
        }


        /// <summary>Box a float as the field's own numeric type, or null for a type we cannot write.</summary>
        private static object Box(Type type, float value)
        {
            if (type == typeof(float)) return value;
            if (type == typeof(double)) return (double)value;
            if (type == typeof(int)) return Mathf.RoundToInt(value);
            return null;
        }

        /// <summary>
        /// Ask ProceduralParts what diameter its mesh was last built at.
        /// </summary>
        /// <remarks>
        /// This is the check that catches a field being written without the owning
        /// mod being told: the KSPField holds the new number while the part still
        /// looks the old size.
        /// </remarks>
        public static float ProceduralPartsBuiltDiameter(Part part)
        {
            PartModule proceduralPart = Module(part, "ProceduralPart");
            if (proceduralPart == null) return float.NaN;

            PropertyInfo shapeProperty = proceduralPart.GetType()
                .GetProperty("CurrentShape", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            object shape = shapeProperty?.GetValue(proceduralPart, null);
            if (shape == null) return float.NaN;

            PropertyInfo maxDiameter = shape.GetType()
                .GetProperty("MaxDiameter", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            object value = maxDiameter?.GetValue(shape, null);
            return value is float f ? f : float.NaN;
        }

        /// <summary>Name of the ProceduralParts shape module currently selected on a part.</summary>
        public static string ProceduralPartsShapeModule(Part part)
        {
            PartModule proceduralPart = Module(part, "ProceduralPart");
            if (proceduralPart == null) return null;
            PropertyInfo shapeProperty = proceduralPart.GetType()
                .GetProperty("CurrentShape", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            object shape = shapeProperty?.GetValue(proceduralPart, null);
            return shape?.GetType().Name;
        }

        /// <summary>Switch a ProceduralParts part to a named shape, e.g. "Cone".</summary>
        public static bool SetProceduralPartsShape(Part part, string displayName)
        {
            PartModule proceduralPart = Module(part, "ProceduralPart");
            MethodInfo setShapeName = proceduralPart?.GetType()
                .GetMethod("SetShapeName", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (setShapeName == null) return false;
            setShapeName.Invoke(proceduralPart, new object[] { displayName });
            return true;
        }

        /// <summary>DimensionSync's settings type, found once by reflection.</summary>
        private static Type _settings;

        /// <summary>
        /// Turn one of DimensionSync's boolean settings on or off for the moment.
        /// </summary>
        /// <param name="name">The field's name, as declared on DimensionSettings.</param>
        /// <param name="value">What to set it to.</param>
        /// <returns>False when the mod is not loaded or has no such setting.</returns>
        /// <remarks>
        /// For scenarios that need to see what happens with one of the mod's rules
        /// switched OFF - which is the only honest way to find out how much of an
        /// effect that rule is actually having, as opposed to how much it looks like
        /// it is having.
        /// </remarks>
        public static bool SetModFlag(string name, bool value)
        {
            FindSettings();
            FieldInfo field = _settings?.GetField(name,
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (field == null || field.FieldType != typeof(bool)) return false;

            field.SetValue(null, value);
            _settings.GetMethod("Refresh", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                     ?.Invoke(null, null);
            return true;
        }

        /// <summary>Locate DimensionSync's settings type, once.</summary>
        private static void FindSettings()
        {
            if (_settings != null) return;
            foreach (AssemblyLoader.LoadedAssembly loaded in AssemblyLoader.loadedAssemblies)
            {
                _settings = loaded.assembly.GetType("DimensionSync.DimensionSettings", false);
                if (_settings != null) return;
            }
        }

        /// <summary>
        /// Put DimensionSync into one of its margin modes for the moment.
        /// </summary>
        /// <param name="mode">"none", "absolute" or "proportional".</param>
        /// <returns>False when the mod is not loaded or does not know that mode.</returns>
        /// <remarks>
        /// Reflection rather than a direct reference: the harness is built against
        /// the mod but the setting is internal to it, and prising it open just so the
        /// tests can reach it would widen the mod's surface for no one's benefit.
        /// Refresh rather than Save, so a test run does not leave its last choice
        /// sitting in the player's settings file.
        /// </remarks>
        /// <summary>
        /// Choose how a hollow part's two diameters follow one another, for the
        /// moment.
        /// </summary>
        /// <param name="mode">independent, proportional or constant.</param>
        public static bool SetHollowMode(string mode)
        {
            // Config vocabulary in, enum member out. The two deliberately differ -
            // "hard" and "soft" read better in a config file than HardIndependent -
            // and a scenario should be able to name the mode the way the player does.
            // Passing the config word straight to Enum.Parse silently failed for
            // exactly the two modes whose names are not their config words, and the
            // skip message blamed the setting for being absent.
            switch (mode.Trim().ToLowerInvariant())
            {
                case "hard":
                case "independent": mode = "HardIndependent"; break;
                case "soft": mode = "SoftIndependent"; break;
                case "proportional": mode = "Proportional"; break;
                case "constant": mode = "Constant"; break;
            }
            return SetEnumSetting("Hollow", mode);
        }

        public static bool SetMarginMode(string mode)
        {
            return SetEnumSetting("Margin", mode);
        }

        /// <summary>Set one of DimensionSync's enum settings by name.</summary>
        /// <param name="field">The static field on DimensionSettings.</param>
        /// <param name="mode">The value's name, matched without regard to case.</param>
        private static bool SetEnumSetting(string field, string mode)
        {
            FindSettings();
            if (_settings == null) return false;

            FieldInfo margin = _settings.GetField(field,
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (margin == null) return false;

            try
            {
                margin.SetValue(null, Enum.Parse(margin.FieldType, mode, ignoreCase: true));
            }
            catch (Exception)
            {
                return false;
            }

            _settings.GetMethod("Refresh", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                     ?.Invoke(null, null);
            return true;
        }
    }
}