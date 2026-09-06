using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace DimensionSync
{
    /// <summary>
    /// Notices when somebody else changes a part, and remembers what the value was
    /// before they did.
    /// </summary>
    /// <remarks>
    /// This mod watches fields and reacts to what moved, which cannot distinguish a
    /// player reaching for a slider from another mod writing the same field a frame
    /// later - and the difference decides whether a part gets left where somebody put
    /// it or rearranged. Several bugs here have been that misreading, and the number
    /// they all wanted is the same one: what the field held BEFORE the other mod
    /// wrote it. By the time a field watcher sees the change, nothing anywhere
    /// remembers.
    ///
    /// Surveying the four mods that account for nearly all the parts this mod targets
    /// turned up one seam rather than four:
    ///
    ///   ProceduralParts  BaseField.SetValue(value, counterpartModule)
    ///   ROTanks / ROLib  BaseField.SetValue(...), then onFieldChanged by hand
    ///   Stock            BaseField.SetValue via UIPartActionFieldItem
    ///   B9 PWings        twin.field = (twin.fieldCached = value)   <- raw, no BaseField
    ///
    /// So three of the four go through one KSP method, and only B9 bypasses it by
    /// assigning the backing field directly. That is why this is two hooks and not
    /// four, and why a mod nobody here has heard of is likely to be covered already:
    /// the general hook is on the standard API, not on anybody's particular class.
    ///
    /// Everything is reflection and everything is optional. Harmony ships with some
    /// mods and not others, and DimensionSync has to work either way, so there is no
    /// assembly reference and no Harmony type in any signature. Without Harmony the
    /// stock-variant source still works, the rest report nothing, and the mod behaves
    /// exactly as it did before.
    /// </remarks>
    public static class ForeignWrites
    {
        /// <summary>Somebody else's write to one field of one part.</summary>
        public struct Write
        {
            /// <summary>Which hook saw it, for the log and for telling sources apart.</summary>
            public string Source;

            /// <summary>The frame it happened on.</summary>
            public int Frame;

            /// <summary>What the field held before, which is the whole point.</summary>
            public float Before;

            /// <summary>What it holds now.</summary>
            public float After;
        }

        /// <summary>What each part and field was last changed to by somebody else.</summary>
        private static readonly Dictionary<Part, Dictionary<string, Write>> Seen =
            new Dictionary<Part, Dictionary<string, Write>>();

        /// <summary>Whether any hook is installed.</summary>
        public static bool Installed { get; private set; }

        /// <summary>What was hooked and what was not, for the log.</summary>
        public static string Report { get; private set; } = "not tried yet";

        /// <summary>
        /// Set while this mod is writing a field, so its own writes are not reported
        /// back to it as somebody else's.
        /// </summary>
        /// <remarks>
        /// Held across the single <c>SetValue</c> call and nothing more. Widening it
        /// to cover a whole write - the symmetry pass, the callbacks - would also
        /// swallow what OTHER mods do in reaction to us, which is exactly the traffic
        /// worth seeing: ProceduralParts writing its counterparts from inside our own
        /// <c>onFieldChanged</c> is somebody else's write, not ours.
        /// </remarks>
        public static bool Ours;

        // -----------------------------------------------------------------
        // Asking
        // -----------------------------------------------------------------

        /// <summary>The last write somebody else made to this field of this part.</summary>
        /// <param name="part">The part to ask about.</param>
        /// <param name="field">The field's name.</param>
        /// <param name="write">What happened, if anything did.</param>
        public static bool TryLastWrite(Part part, string field, out Write write)
        {
            write = default;
            return part != null && field != null
                   && Seen.TryGetValue(part, out Dictionary<string, Write> fields)
                   && fields.TryGetValue(field, out write);
        }

        /// <summary>What a field held before somebody else last wrote it.</summary>
        /// <param name="part">The part to ask about.</param>
        /// <param name="field">The field's name.</param>
        /// <param name="before">The displaced value.</param>
        /// <param name="within">How many frames back still counts.</param>
        /// <remarks>
        /// Bounded in time on purpose. A value from twenty frames ago describes a
        /// craft that has since been edited half a dozen times, and answering with it
        /// would be worse than answering "I do not know".
        /// </remarks>
        public static bool TryValueBefore(Part part, string field, out float before, int within = 2)
        {
            before = float.NaN;
            if (!TryLastWrite(part, field, out Write write)) return false;
            if (Time.frameCount - write.Frame > within) return false;
            before = write.Before;
            return !float.IsNaN(before);
        }

        /// <summary>Whether somebody else changed this field of this part just now.</summary>
        /// <param name="part">The part to ask about.</param>
        /// <param name="field">The field's name.</param>
        /// <param name="within">How many frames back still counts.</param>
        public static bool ChangedRecently(Part part, string field, int within = 2)
        {
            return TryLastWrite(part, field, out Write write)
                   && Time.frameCount - write.Frame <= within;
        }

        /// <summary>Forget a part that has gone away.</summary>
        /// <param name="part">The part to drop.</param>
        public static void Forget(Part part)
        {
            if (part != null) Seen.Remove(part);
        }

        /// <summary>Record somebody else's write. Called by the hooks.</summary>
        /// <param name="source">Which hook saw it.</param>
        /// <param name="part">The part written to.</param>
        /// <param name="field">The field's name.</param>
        /// <param name="before">What it held.</param>
        /// <param name="after">What it holds now.</param>
        internal static void Note(string source, Part part, string field, float before, float after)
        {
            if (part == null || string.IsNullOrEmpty(field)) return;
            if (!float.IsNaN(before) && !float.IsNaN(after) && Mathf.Abs(before - after) <= 1e-6f) return;

            if (!Seen.TryGetValue(part, out Dictionary<string, Write> fields))
                Seen[part] = fields = new Dictionary<string, Write>();

            fields[field] = new Write
            {
                Source = source,
                Frame = Time.frameCount,
                Before = before,
                After = after,
            };

            if (DimensionSettings.Debug)
                UnityEngine.Debug.Log($"{DimensionSyncAddon.LogTag} FOREIGN f={Time.frameCount} " +
                                      $"{source} #{part.GetInstanceID()} {field} " +
                                      $"{before:F4}->{after:F4}");
        }

        /// <summary>The innermost message of a wrapped exception.</summary>
        /// <param name="e">The exception to unwrap.</param>
        /// <remarks>
        /// Reflection wraps whatever the target threw in a TargetInvocationException
        /// whose own message is "Exception has been thrown by the target of an
        /// invocation", which says nothing at all about what went wrong.
        /// </remarks>
        private static string Innermost(Exception e)
        {
            while (e.InnerException != null) e = e.InnerException;
            return $"{e.GetType().Name}: {e.Message}";
        }

        /// <summary>A number out of a boxed field value, or NaN if it is not one.</summary>
        internal static float AsNumber(object value)
        {
            switch (value)
            {
                case float f: return f;
                case double d: return (float)d;
                case int i: return i;
                case bool b: return b ? 1f : 0f;
                default: return float.NaN;
            }
        }

        // -----------------------------------------------------------------
        // Installing
        // -----------------------------------------------------------------

        /// <summary>
        /// Subscribe to the stock events. Called once the editor exists, because
        /// GameEvents are not built yet at <c>Startup.Instantly</c> and asking for one
        /// there throws.
        /// </summary>
        public static void InstallSceneHooks()
        {
            if (_sceneHooked) return;
            _sceneHooked = true;
            var notes = new List<string>();
            StockVariants.Install(notes);
            UnityEngine.Debug.Log($"{DimensionSyncAddon.LogTag} foreign scene hooks: " +
                                  string.Join("; ", notes.ToArray()));
        }

        /// <summary>Whether <see cref="InstallSceneHooks"/> has run.</summary>
        private static bool _sceneHooked;

        /// <summary>Install every hook that this install can support. Safe to repeat.</summary>
        public static void Install()
        {
            if (Installed) return;
            var notes = new List<string>();

            var harmony = HarmonyBridge.Open("com.sparr.dimensionsync");
            if (harmony == null)
            {
                notes.Add("Harmony is absent, so field writes by other mods cannot be seen");
            }
            else
            {
                StandardFieldWrites.Install(harmony, notes);
                B9CounterpartMirror.Install(harmony, notes);
            }

            Installed = true;
            Report = string.Join("; ", notes.ToArray());
            UnityEngine.Debug.Log($"{DimensionSyncAddon.LogTag} foreign write hooks: {Report}");
        }

        // =================================================================
        // The hooks
        // =================================================================

        /// <summary>
        /// The general one: anybody writing a part's field through KSP's own API.
        /// </summary>
        /// <remarks>
        /// One patch for ProceduralParts, ROTanks, the stock tweakables and every mod
        /// that has ever written a counterpart the ordinary way. Patching the shared
        /// API rather than each mod's class is what makes this cover mods nobody here
        /// has looked at.
        ///
        /// It is a read-only prefix on a method KSP calls often, so it earns its keep
        /// by getting out of the way fast: not the editor, or our own write, or a
        /// host that is not a part module, and it has done nothing.
        /// </remarks>
        private static class StandardFieldWrites
        {
            public static void Install(HarmonyBridge harmony, List<string> notes)
            {
                try
                {
                    // On BaseField<KSPField>, not on BaseField. The concrete class only
                    // inherits SetValue, and Harmony refuses a method reached through a
                    // derived type - "you can only patch implemented methods" - because
                    // there is no separate body there to patch.
                    Type declaring = typeof(BaseField).BaseType;
                    MethodInfo target = declaring?.GetMethod(
                        "SetValue", BindingFlags.Public | BindingFlags.Instance,
                        null, new[] { typeof(object), typeof(object) }, null);
                    if (target == null) { notes.Add("KSP's BaseField.SetValue is not where expected"); return; }

                    harmony.Patch(target, typeof(StandardFieldWrites).GetMethod(
                        nameof(Before), BindingFlags.NonPublic | BindingFlags.Static), null);
                    notes.Add("field writes by other mods (ProceduralParts, ROTanks, stock)");
                }
                catch (Exception e)
                {
                    notes.Add($"field writes could not be hooked: {Innermost(e)}");
                }
            }

            /// <param name="__instance">The field being written; named for Harmony.</param>
            /// <param name="newValue">The value it is about to take.</param>
            /// <param name="host">The object holding it, normally a PartModule.</param>
            private static void Before(object __instance, object newValue, object host)
            {
                try
                {
                    if (Ours || !HighLogic.LoadedSceneIsEditor) return;
                    if (!(host is PartModule module) || module.part == null) return;
                    if (!(__instance is BaseField field)) return;

                    float after = AsNumber(newValue);
                    if (float.IsNaN(after)) return;      // not a number we track

                    float before = AsNumber(field.GetValue(host));
                    if (float.IsNaN(before)) return;

                    Note("field API", module.part, field.name, before, after);
                }
                catch (Exception e)
                {
                    UnityEngine.Debug.LogWarning($"{DimensionSyncAddon.LogTag} field-write hook failed: {e}");
                }
            }
        }

        /// <summary>
        /// B9 Procedural Wings, which does not use the field API at all.
        /// </summary>
        /// <remarks>
        /// <c>UpdateCounterparts</c> copies a wing's values onto its symmetry
        /// counterparts as
        /// <c>twin.sharedBaseLength = (twin.sharedBaseLengthCached = value)</c> -
        /// value and B9's own cache together, on purpose, so that the counterpart's
        /// <c>CheckAllFieldValues</c> sees nothing and never reacts. The counterpart
        /// therefore experiences no field change, no <c>onFieldChanged</c>, no PAW
        /// event and no <c>SetValue</c>, so the general hook above cannot see it
        /// either. Hence a second hook, for one mod, reached for only because that
        /// mod bypasses the seam everybody else uses.
        ///
        /// Measured when this was first installed: the mirror lands a frame AFTER
        /// this mod's own writes, and what B9 copies across is OUR propagated value.
        /// Every write to half of a symmetric pair therefore comes back as an
        /// apparent outside change to the other half, one frame later.
        /// </remarks>
        private static class B9CounterpartMirror
        {
            /// <summary>The dimension fields worth remembering across a mirror.</summary>
            private static readonly string[] Watched =
            {
                "sharedBaseLength",
                "sharedBaseWidthRoot", "sharedBaseWidthTip",
                "sharedBaseOffsetRoot", "sharedBaseOffsetTip",
                "sharedBaseThicknessRoot", "sharedBaseThicknessTip",
            };

            private static Type _wing;
            private static readonly Dictionary<string, FieldInfo> Fields =
                new Dictionary<string, FieldInfo>();

            /// <summary>What each counterpart held just before the copy landed.</summary>
            private static readonly Dictionary<Part, Dictionary<string, float>> Displaced =
                new Dictionary<Part, Dictionary<string, float>>();

            public static void Install(HarmonyBridge harmony, List<string> notes)
            {
                try
                {
                    _wing = HarmonyBridge.FindType("WingProcedural.WingProcedural");
                    if (_wing == null) { notes.Add("B9 Procedural Wings is not installed"); return; }

                    for (int i = 0; i < Watched.Length; i++)
                    {
                        FieldInfo field = _wing.GetField(Watched[i],
                                                         BindingFlags.Public | BindingFlags.Instance);
                        if (field != null) Fields[Watched[i]] = field;
                    }
                    if (Fields.Count == 0) { notes.Add("B9's dimension fields are not where expected"); return; }

                    MethodInfo target = _wing.GetMethod("UpdateCounterparts",
                                                        BindingFlags.Public | BindingFlags.Instance,
                                                        null, Type.EmptyTypes, null);
                    if (target == null) { notes.Add("B9 has no UpdateCounterparts to patch"); return; }

                    harmony.Patch(target,
                                  typeof(B9CounterpartMirror).GetMethod(
                                      nameof(Before), BindingFlags.NonPublic | BindingFlags.Static),
                                  typeof(B9CounterpartMirror).GetMethod(
                                      nameof(After), BindingFlags.NonPublic | BindingFlags.Static));
                    notes.Add("B9's silent mirror to symmetry counterparts");
                }
                catch (Exception e)
                {
                    notes.Add($"B9's mirror could not be hooked: {Innermost(e)}");
                }
            }

            /// <param name="__instance">The wing about to copy itself; named for Harmony.</param>
            private static void Before(object __instance)
            {
                try
                {
                    Part source = (__instance as PartModule)?.part;
                    if (source?.symmetryCounterparts == null) return;

                    for (int i = 0; i < source.symmetryCounterparts.Count; i++)
                    {
                        Part twin = source.symmetryCounterparts[i];
                        object module = WingOn(twin);
                        if (module == null) continue;

                        if (!Displaced.TryGetValue(twin, out Dictionary<string, float> was))
                            Displaced[twin] = was = new Dictionary<string, float>();

                        foreach (KeyValuePair<string, FieldInfo> field in Fields)
                            was[field.Key] = AsNumber(field.Value.GetValue(module));
                    }
                }
                catch (Exception e)
                {
                    UnityEngine.Debug.LogWarning($"{DimensionSyncAddon.LogTag} B9 mirror prefix failed: {e}");
                }
            }

            /// <param name="__instance">The wing that copied itself; named for Harmony.</param>
            private static void After(object __instance)
            {
                try
                {
                    Part source = (__instance as PartModule)?.part;
                    if (source?.symmetryCounterparts == null) return;

                    for (int i = 0; i < source.symmetryCounterparts.Count; i++)
                    {
                        Part twin = source.symmetryCounterparts[i];
                        object module = WingOn(twin);
                        if (module == null) continue;
                        if (!Displaced.TryGetValue(twin, out Dictionary<string, float> was)) continue;

                        foreach (KeyValuePair<string, FieldInfo> field in Fields)
                        {
                            if (!was.TryGetValue(field.Key, out float before)) continue;
                            Note("B9 mirror", twin, field.Key, before,
                                 AsNumber(field.Value.GetValue(module)));
                        }
                    }
                }
                catch (Exception e)
                {
                    UnityEngine.Debug.LogWarning($"{DimensionSyncAddon.LogTag} B9 mirror postfix failed: {e}");
                }
            }

            /// <summary>The WingProcedural module on a part, as an object.</summary>
            private static object WingOn(Part part)
            {
                if (part == null || _wing == null) return null;
                for (int i = 0; i < part.Modules.Count; i++)
                    if (_wing.IsInstanceOfType(part.Modules[i])) return part.Modules[i];
                return null;
            }
        }

        /// <summary>
        /// Stock part variants, which change a part without touching a tracked field.
        /// </summary>
        /// <remarks>
        /// A variant can move attach nodes and swap meshes, so a part can become a
        /// different size with every number this mod watches unchanged. There is
        /// nothing to record a before-value for, so it is filed under a name no field
        /// can have - the useful statement is "this part changed under you, on this
        /// frame", not by how much.
        ///
        /// KSP announces this one properly, so no patching is involved and this works
        /// on a bare install.
        /// </remarks>
        private static class StockVariants
        {
            /// <summary>The pseudo-field a variant change is filed under.</summary>
            public const string Field = "@variant";

            public static void Install(List<string> notes)
            {
                try
                {
                    // The editor-scene one. Its sibling onVariantApplied also fires in
                    // flight, where nothing here can act on it anyway.
                    GameEvents.onEditorVariantApplied.Add(Sink.OnVariantApplied);
                    notes.Add("stock part variants");
                }
                catch (Exception e)
                {
                    notes.Add($"stock variants could not be watched: {Innermost(e)}");
                    UnityEngine.Debug.LogWarning($"{DimensionSyncAddon.LogTag} stock variant hook: {e}");
                }
            }

            /// <summary>Something for the delegate to be bound to. See <see cref="Handler"/>.</summary>
            private static readonly Handler Sink = new Handler();

            /// <summary>
            /// Holds the handler as an INSTANCE method, because KSP cannot take a
            /// static one.
            /// </summary>
            /// <remarks>
            /// GameEvents wraps every subscriber in an EvtDelegate whose constructor
            /// names it from <c>evt.Target.GetType()</c>, and a delegate over a static
            /// method has a null Target - so subscribing one throws a
            /// NullReferenceException from inside KSP before your handler has ever
            /// run. Nothing in the exception says so; it took a stack trace to see.
            /// </remarks>
            private class Handler
            {
                /// <param name="part">The part whose variant changed.</param>
                /// <param name="variant">The variant applied; unused, but part of the signature.</param>
                public void OnVariantApplied(Part part, PartVariant variant)
                {
                    if (Ours || part == null) return;
                    Note("stock variant", part, Field, 0f, 1f);
                }
            }
        }

        // =================================================================
        // Talking to Harmony without being built against it
        // =================================================================

        /// <summary>Just enough of Harmony to add a prefix and a postfix, by reflection.</summary>
        /// <remarks>
        /// Kept behind this wrapper so no Harmony type appears in any signature the
        /// runtime has to resolve. A missing Harmony then costs a null from
        /// <see cref="Open"/> rather than a TypeLoadException while the mod is
        /// loading.
        /// </remarks>
        private class HarmonyBridge
        {
            private readonly object _instance;
            private readonly MethodInfo _patch;
            private readonly Type _harmonyMethod;

            private HarmonyBridge(object instance, MethodInfo patch, Type harmonyMethod)
            {
                _instance = instance;
                _patch = patch;
                _harmonyMethod = harmonyMethod;
            }

            /// <summary>Open a Harmony instance, or null if Harmony is not installed.</summary>
            /// <param name="id">The patch owner id.</param>
            public static HarmonyBridge Open(string id)
            {
                try
                {
                    Type harmony = FindType("HarmonyLib.Harmony");
                    Type harmonyMethod = FindType("HarmonyLib.HarmonyMethod");
                    if (harmony == null || harmonyMethod == null) return null;

                    MethodInfo patch = harmony.GetMethod("Patch");
                    if (patch == null) return null;

                    return new HarmonyBridge(Activator.CreateInstance(harmony, id), patch, harmonyMethod);
                }
                catch
                {
                    return null;
                }
            }

            /// <summary>Add a prefix and/or postfix to a method.</summary>
            /// <param name="target">The method to patch.</param>
            /// <param name="prefix">Our static method to run before it, or null.</param>
            /// <param name="postfix">Our static method to run after it, or null.</param>
            public void Patch(MethodInfo target, MethodInfo prefix, MethodInfo postfix)
            {
                // Positional and padded to whatever this Harmony's Patch takes, so a
                // version that has since grown a parameter still binds.
                ParameterInfo[] wants = _patch.GetParameters();
                var args = new object[wants.Length];
                args[0] = target;
                if (wants.Length > 1 && prefix != null)
                    args[1] = Activator.CreateInstance(_harmonyMethod, prefix);
                if (wants.Length > 2 && postfix != null)
                    args[2] = Activator.CreateInstance(_harmonyMethod, postfix);
                _patch.Invoke(_instance, args);
            }

            /// <summary>A type by full name, from anything KSP has loaded.</summary>
            /// <param name="fullName">The namespace-qualified type name.</param>
            public static Type FindType(string fullName)
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

    /// <summary>Installs <see cref="ForeignWrites"/> once, as early as KSP allows.</summary>
    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    public class ForeignWritesBootstrap : MonoBehaviour
    {
        /// <summary>Hook on startup, before any editor scene can need it.</summary>
        public void Start()
        {
            ForeignWrites.Install();
        }
    }
}
