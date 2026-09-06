using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace DimensionSync
{
    /// <summary>
    /// Asks B9 Procedural Wings to say when it changes something, instead of
    /// guessing it from the fields afterwards.
    /// </summary>
    /// <remarks>
    /// B9 declares no events, so there is nothing to subscribe to. What it does have
    /// is <c>UpdateCounterparts</c>, which copies a wing's values onto its symmetry
    /// counterparts like this:
    ///
    ///     twin.sharedBaseLength = (twin.sharedBaseLengthCached = sharedBaseLength);
    ///
    /// The value and B9's own cache are set together, on purpose, so that the
    /// counterpart's <c>CheckAllFieldValues</c> sees nothing and never reacts -
    /// <c>RefreshGeometry</c> is called on it directly instead. The counterpart
    /// therefore experiences no field change, no <c>onFieldChanged</c> and no PAW
    /// event. Any mod watching fields can only discover the mirror later, where it
    /// looks exactly like somebody having edited that side by hand.
    ///
    /// That misreading is the root of a family of bugs here: two halves of a mirrored
    /// pair dealt with on different frames, each measuring against a craft in a
    /// different state. Adding an execution order so this mod ran after B9 did NOT
    /// close that gap - measured, it changed nothing at all - which says the mirror is
    /// not merely happening later in the same frame. A patch is the only way to be
    /// told when it actually happens.
    ///
    /// Everything here is reflection, deliberately. Harmony ships with several mods
    /// and is absent from a bare install, and DimensionSync has to work either way -
    /// so there is no assembly reference to break a build, and no Harmony type in any
    /// signature that could fail to load. Without Harmony, <see cref="Available"/>
    /// stays false and the mod behaves exactly as it did before.
    /// </remarks>
    public static class B9Interop
    {
        /// <summary>Whether the hooks are installed and reporting.</summary>
        public static bool Available { get; private set; }

        /// <summary>Why the hooks are not installed, for the log.</summary>
        public static string Unavailable { get; private set; } = "not tried yet";

        /// <summary>The dimensions worth remembering across a mirror.</summary>
        private static readonly string[] Watched =
        {
            "sharedBaseLength",
            "sharedBaseWidthRoot", "sharedBaseWidthTip",
            "sharedBaseOffsetRoot", "sharedBaseOffsetTip",
            "sharedBaseThicknessRoot", "sharedBaseThicknessTip",
        };

        /// <summary>What each part held just before B9 last copied values onto it.</summary>
        private static readonly Dictionary<Part, Dictionary<string, float>> BeforeMirror =
            new Dictionary<Part, Dictionary<string, float>>();

        /// <summary>The frame B9 last copied values onto each part.</summary>
        private static readonly Dictionary<Part, int> MirroredOn = new Dictionary<Part, int>();

        /// <summary>The WingProcedural type, once found.</summary>
        private static Type _wing;

        /// <summary>Its dimension fields, by name.</summary>
        private static readonly Dictionary<string, FieldInfo> Fields =
            new Dictionary<string, FieldInfo>();

        // -----------------------------------------------------------------
        // What the rest of the mod asks
        // -----------------------------------------------------------------

        /// <summary>Whether B9 copied values onto this part in the last few frames.</summary>
        /// <param name="part">The part to ask about.</param>
        /// <param name="within">How many frames back still counts.</param>
        public static bool WasMirroredRecently(Part part, int within = 2)
        {
            return part != null
                   && MirroredOn.TryGetValue(part, out int frame)
                   && Time.frameCount - frame <= within;
        }

        /// <summary>What a part held before B9 last copied a value onto it.</summary>
        /// <param name="part">The part to ask about.</param>
        /// <param name="field">The dimension field's name.</param>
        /// <param name="value">The value it held before the copy.</param>
        /// <remarks>
        /// This is the number our own snapshots cannot get at. By the time a mirrored
        /// counterpart's turn comes round, its field already holds the copied value
        /// and nothing anywhere remembers what it displaced.
        /// </remarks>
        public static bool TryValueBeforeMirror(Part part, string field, out float value)
        {
            value = float.NaN;
            return part != null
                   && BeforeMirror.TryGetValue(part, out Dictionary<string, float> was)
                   && was.TryGetValue(field, out value);
        }

        /// <summary>Forget a part that has gone away.</summary>
        /// <param name="part">The part to drop.</param>
        public static void Forget(Part part)
        {
            if (part == null) return;
            BeforeMirror.Remove(part);
            MirroredOn.Remove(part);
        }

        // -----------------------------------------------------------------
        // Installing
        // -----------------------------------------------------------------

        /// <summary>Patch B9, if both it and Harmony are here. Safe to call twice.</summary>
        public static void Install()
        {
            if (Available) return;
            try
            {
                _wing = FindType("WingProcedural.WingProcedural");
                if (_wing == null) { Unavailable = "B9 Procedural Wings is not installed"; return; }

                Type harmony = FindType("HarmonyLib.Harmony");
                Type harmonyMethod = FindType("HarmonyLib.HarmonyMethod");
                if (harmony == null || harmonyMethod == null)
                {
                    Unavailable = "Harmony is not installed";
                    return;
                }

                for (int i = 0; i < Watched.Length; i++)
                {
                    FieldInfo field = _wing.GetField(Watched[i],
                                                     BindingFlags.Public | BindingFlags.Instance);
                    if (field != null) Fields[Watched[i]] = field;
                }
                if (Fields.Count == 0) { Unavailable = "B9's dimension fields are not where expected"; return; }

                MethodInfo target = _wing.GetMethod("UpdateCounterparts",
                                                    BindingFlags.Public | BindingFlags.Instance,
                                                    null, Type.EmptyTypes, null);
                if (target == null) { Unavailable = "B9 has no UpdateCounterparts to patch"; return; }

                object instance = Activator.CreateInstance(harmony, "com.sparr.dimensionsync");
                MethodInfo patch = harmony.GetMethod("Patch");
                if (patch == null) { Unavailable = "this Harmony has no Patch method"; return; }

                object before = Activator.CreateInstance(harmonyMethod, Ours(nameof(BeforeCounterparts)));
                object after = Activator.CreateInstance(harmonyMethod, Ours(nameof(AfterCounterparts)));

                // Positional, and padded to whatever this Harmony's Patch takes, so a
                // version that has grown a finalizer parameter still binds.
                ParameterInfo[] wants = patch.GetParameters();
                object[] args = new object[wants.Length];
                for (int i = 0; i < args.Length; i++) args[i] = null;
                args[0] = target;
                if (args.Length > 1) args[1] = before;
                if (args.Length > 2) args[2] = after;
                patch.Invoke(instance, args);

                Available = true;
                Unavailable = null;
                UnityEngine.Debug.Log($"{DimensionSyncAddon.LogTag} B9 interop: hooked " +
                                      "WingProcedural.UpdateCounterparts; mirrored writes will " +
                                      "now be reported as they happen");
            }
            catch (Exception e)
            {
                // Never fatal. A mod that refuses to load because an OPTIONAL
                // interop failed is worse than one that carries on watching fields
                // the way it always has.
                Available = false;
                Unavailable = e.Message;
                UnityEngine.Debug.LogWarning($"{DimensionSyncAddon.LogTag} B9 interop unavailable, " +
                                             $"carrying on without it: {e}");
            }
        }

        /// <summary>One of our own static methods, by name.</summary>
        private static MethodInfo Ours(string name)
        {
            return typeof(B9Interop).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static);
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

        // -----------------------------------------------------------------
        // The hooks themselves
        // -----------------------------------------------------------------

        /// <summary>
        /// Before B9 copies a wing's values onto its counterparts: remember what those
        /// counterparts are about to lose.
        /// </summary>
        /// <param name="__instance">The wing doing the copying; named for Harmony.</param>
        private static void BeforeCounterparts(object __instance)
        {
            try
            {
                Part source = PartOf(__instance);
                if (source?.symmetryCounterparts == null) return;

                for (int i = 0; i < source.symmetryCounterparts.Count; i++)
                {
                    Part twin = source.symmetryCounterparts[i];
                    object module = WingOn(twin);
                    if (module == null) continue;

                    if (!BeforeMirror.TryGetValue(twin, out Dictionary<string, float> was))
                        BeforeMirror[twin] = was = new Dictionary<string, float>();

                    foreach (KeyValuePair<string, FieldInfo> field in Fields)
                    {
                        object raw = field.Value.GetValue(module);
                        if (raw is float number) was[field.Key] = number;
                    }
                }
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogWarning($"{DimensionSyncAddon.LogTag} B9 interop prefix failed: {e}");
            }
        }

        /// <summary>After the copy: note who was written to, and when.</summary>
        /// <param name="__instance">The wing that did the copying; named for Harmony.</param>
        private static void AfterCounterparts(object __instance)
        {
            try
            {
                Part source = PartOf(__instance);
                if (source?.symmetryCounterparts == null) return;

                for (int i = 0; i < source.symmetryCounterparts.Count; i++)
                {
                    Part twin = source.symmetryCounterparts[i];
                    if (twin == null) continue;
                    MirroredOn[twin] = Time.frameCount;

                    if (!DimensionSettings.Debug) continue;
                    object module = WingOn(twin);
                    if (module == null) continue;

                    // Only the fields that actually moved. B9 copies all forty of them
                    // every time, and a line naming all forty says nothing.
                    var moved = new List<string>();
                    BeforeMirror.TryGetValue(twin, out Dictionary<string, float> was);
                    foreach (KeyValuePair<string, FieldInfo> field in Fields)
                    {
                        if (!(field.Value.GetValue(module) is float now)) continue;
                        if (was != null && was.TryGetValue(field.Key, out float then)
                            && Mathf.Abs(then - now) > 1e-4f)
                            moved.Add($"{field.Key} {then:F4}->{now:F4}");
                    }
                    if (moved.Count == 0) continue;

                    UnityEngine.Debug.Log($"{DimensionSyncAddon.LogTag} MIRROR f={Time.frameCount} " +
                                          $"#{source.GetInstanceID()} -> #{twin.GetInstanceID()}: " +
                                          string.Join(", ", moved.ToArray()));
                }
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogWarning($"{DimensionSyncAddon.LogTag} B9 interop postfix failed: {e}");
            }
        }

        /// <summary>The part a WingProcedural belongs to.</summary>
        private static Part PartOf(object module)
        {
            return (module as PartModule)?.part;
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

    /// <summary>Installs <see cref="B9Interop"/> once, as early as KSP allows.</summary>
    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    public class B9InteropBootstrap : MonoBehaviour
    {
        /// <summary>Patch on startup, before any editor scene can need it.</summary>
        public void Start()
        {
            B9Interop.Install();
        }
    }
}
