using System;
using System.Collections.Generic;
using UnityEngine;
using KSP.UI.Screens;

// DimensionSync
//
// Keeps the dimensions of connected parts in step. Whenever a part's diameter
// changes - because the player dragged a PAW slider, or because another mod
// wrote the field - the new size is pushed along the run of parts it belongs
// to, stopping at the first part whose facing end was a different size than the
// changed part was before the change. Procedural wings work the same way, with
// chord and thickness travelling from one segment's tip to the next one's root.
//
// Detection is by polling rather than by subscribing to BaseField.OnValueModified.
// That event only fires for changes routed through BaseField.SetValue, and the
// procedural-part mods this is meant to work with frequently assign their
// fields directly. Comparing each tracked field against its value from the end
// of the previous frame catches every route into the field, whichever mod took
// it.

namespace DimensionSync
{
    /// <summary>
    /// One field that moved between the previous poll and this one, recorded so
    /// the whole batch can be collected before any of it is propagated.
    /// </summary>
    internal struct DimensionChange
    {
        /// <summary>The field that moved.</summary>
        public KspDimensionSlot Slot;

        /// <summary>What it held at the end of the previous frame, in common units.</summary>
        public float OldValue;

        /// <summary>What it holds now, in common units.</summary>
        public float NewValue;
    }

    /// <summary>
    /// The editor-scene addon: tracks every dimension field on the ship, watches
    /// them for changes, and hands anything that moved to the propagator.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.EditorAny, false)]
    public class DimensionSyncAddon : MonoBehaviour
    {
        /// <summary>Prefix on every line this mod writes to KSP.log.</summary>
        internal const string LogTag = "[DimensionSync]";

        /// <summary>The live addon, used to reject the duplicate KSP sometimes spawns.</summary>
        private static DimensionSyncAddon _instance;

        /// <summary>Verbose logging, from DIMENSION_SYNC/debug in config.</summary>
        internal static bool Debug;

        /// <summary>
        /// True while we are the ones writing fields, so the editor events our own
        /// writes provoke are not mistaken for somebody else's edits.
        /// </summary>
        private bool _propagating;

        /// <summary>Set when the ship's part list may have changed.</summary>
        private bool _partsDirty = true;

        /// <summary>Every ship part we are tracking, and its dimension fields.</summary>
        private readonly Dictionary<Part, KspDimensionNode> _nodes = new Dictionary<Part, KspDimensionNode>();

        /// <summary>Changes seen in the current poll. Reused to keep the poll allocation-free.</summary>
        private readonly List<DimensionChange> _changes = new List<DimensionChange>();

        /// <summary>Scratch list of parts to drop from <see cref="_nodes"/>. Reused.</summary>
        private readonly List<Part> _stale = new List<Part>();

        /// <summary>Scratch set of parts currently on the ship. Reused.</summary>
        private readonly HashSet<Part> _seen = new HashSet<Part>();

        /// <summary>
        /// The span each part held before this frame's edits, for the parts whose
        /// span is one of them. Reused.
        /// </summary>
        private readonly Dictionary<Part, float> _spansBeforeChange = new Dictionary<Part, float>();

        /// <summary>
        /// What every field changing this frame held before it changed. Reused.
        /// </summary>
        private readonly Dictionary<WingConformance.PartField, float> _valuesBeforeChange =
            new Dictionary<WingConformance.PartField, float>();

        /// <summary>Parts whose own span somebody else changed this frame. Reused.</summary>
        private readonly HashSet<Part> _lengthsEdited = new HashSet<Part>();

        /// <summary>Wings whose tip chord or tip offset changed this frame. Reused.</summary>
        private readonly HashSet<Part> _tipsEdited = new HashSet<Part>();

        /// <summary>The game-independent propagation rules.</summary>
        private readonly DimensionPropagator _propagator = new DimensionPropagator();

        /// <summary>Set when a field we wrote asked for the usual ship-modified event.</summary>
        private static bool _shipModifiedPending;

        /// <summary>Parts whose PAW needs a refresh after the current propagation.</summary>
        private static readonly HashSet<Part> _windowsToRefresh = new HashSet<Part>();

        // =====================================================================
        // Unity lifecycle
        // =====================================================================

        /// <summary>
        /// Claim the singleton slot and read config. KSP can instantiate an addon
        /// more than once across a scene change, so a second copy destroys itself.
        /// </summary>
        private void Awake()
        {
            if (_instance != null && _instance != this) { Destroy(this); return; }
            _instance = this;
            LoadConfig();
        }

        /// <summary>Subscribe to the editor events that can invalidate the tracked part list.</summary>
        private void Start()
        {
            GameEvents.onEditorPartEvent.Add(OnEditorPartEvent);
            GameEvents.onEditorLoad.Add(OnEditorLoad);
            GameEvents.onEditorRestart.Add(MarkDirty);
            GameEvents.onEditorShipModified.Add(OnEditorShipModified);
            GameEvents.onEditorUndo.Add(OnEditorUndoRedo);
            GameEvents.onEditorRedo.Add(OnEditorUndoRedo);
            DimensionSettings.Changed += OnSettingsChanged;

            _partsDirty = true;
            UnityEngine.Debug.Log($"{LogTag} Addon started.");
        }

        /// <summary>Unsubscribe and drop every tracked part when the editor scene ends.</summary>
        private void OnDestroy()
        {
            GameEvents.onEditorPartEvent.Remove(OnEditorPartEvent);
            GameEvents.onEditorLoad.Remove(OnEditorLoad);
            GameEvents.onEditorRestart.Remove(MarkDirty);
            GameEvents.onEditorShipModified.Remove(OnEditorShipModified);
            GameEvents.onEditorUndo.Remove(OnEditorUndoRedo);
            GameEvents.onEditorRedo.Remove(OnEditorUndoRedo);
            DimensionSettings.Changed -= OnSettingsChanged;

            _nodes.Clear();
            if (_instance == this) _instance = null;
        }

        /// <summary>
        /// Merge every DIMENSION_SYNC node in the game database - the one this mod
        /// ships plus anything ModuleManager patched on top of it - and bring this
        /// addon's propagator up to date with the result.
        /// </summary>
        /// <remarks>
        /// Later nodes win, which is what lets another mod add or override a field
        /// entry with a plain <c>@DIMENSION_SYNC</c> patch. The reading itself only
        /// happens once a session; the copy onto the propagator happens on every
        /// trip into the editor, because this addon and its propagator are new each
        /// time while the settings are not.
        /// </remarks>
        private void LoadConfig()
        {
            DimensionSettings.LoadFrom(GameDatabase.Instance.GetConfigNodes("DIMENSION_SYNC"));
            Debug = DimensionSettings.Debug;
            DimensionSettings.ApplyTo(_propagator);
        }

        /// <summary>Re-read the settings after the settings window changed one.</summary>
        private void OnSettingsChanged()
        {
            Debug = DimensionSettings.Debug;
            DimensionSettings.ApplyTo(_propagator);
        }

        // =====================================================================
        // Editor events - all they do is invalidate the tracked part list
        // =====================================================================

        /// <summary>Note that the tracked part set needs rebuilding on the next poll.</summary>
        private void MarkDirty() => _partsDirty = true;

        /// <summary>A craft was loaded, so every tracked part has been replaced.</summary>
        private void OnEditorLoad(ShipConstruct ship, CraftBrowserDialog.LoadType type) => _partsDirty = true;

        /// <summary>Undo and redo rebuild the ship from a snapshot, replacing every part.</summary>
        private void OnEditorUndoRedo(ShipConstruct ship) => _partsDirty = true;

        /// <summary>
        /// The ship changed in some way. Attaching, detaching and symmetry changes
        /// all come through here, as do the events our own writes provoke - which
        /// is why this ignores anything raised while we are propagating.
        /// </summary>
        private void OnEditorShipModified(ShipConstruct ship)
        {
            if (!_propagating) _partsDirty = true;
        }

        /// <summary>A part was created, attached, detached, copied or deleted.</summary>
        private void OnEditorPartEvent(ConstructionEventType eventType, Part part)
        {
            _partsDirty = true;

            // KSP says when the PLAYER has moved something, which is worth knowing: a
            // control surface they have pulled off its wing should stay where they put
            // it rather than being arranged back onto an edge they took it off.
            //
            // The event is the whole point. Whether a surface is off its wing cannot be
            // told from geometry alone while anything is moving - one this mod is part
            // way through repositioning looks exactly the same - but "did somebody just
            // move this?" has an answer, and this is it.
            switch (eventType)
            {
                case ConstructionEventType.PartOffset:
                case ConstructionEventType.PartRotated:
                    WingConformance.NoteMovedByPlayer(part);
                    break;

                case ConstructionEventType.PartAttached:
                case ConstructionEventType.PartDetached:
                    WingConformance.NoteReattached(part);
                    break;
            }
        }

        // =====================================================================
        // Poll
        // =====================================================================

        /// <summary>
        /// The per-frame poll, in LateUpdate so it runs after the part action
        /// window has applied whatever the player did this frame.
        /// </summary>
        private void LateUpdate()
        {
            if (!HighLogic.LoadedSceneIsEditor) return;
            if (EditorLogic.fetch == null || EditorLogic.fetch.ship == null) return;

            if (_partsDirty) ReconcileParts();

            DetectAndPropagate();
        }

        /// <summary>
        /// Bring the tracked part set in line with the ship, adding nodes for new
        /// parts and dropping nodes for parts that are gone.
        /// </summary>
        private void ReconcileParts()
        {
            _partsDirty = false;

            List<Part> parts = EditorLogic.fetch.ship.parts;
            if (parts == null) return;

            // First pass: make sure every part on the ship has an up-to-date node,
            // and remember which parts we saw so the second pass can spot the rest.
            _seen.Clear();
            for (int i = 0; i < parts.Count; i++)
            {
                Part part = parts[i];
                if (part == null) continue;
                _seen.Add(part);

                if (_nodes.TryGetValue(part, out KspDimensionNode node))
                {
                    // A part can gain or lose modules (part variants, module
                    // switchers); rebuild the slots when that happens.
                    if (node.ModuleCount != part.Modules.Count) BuildSlots(node);
                }
                else
                {
                    node = new KspDimensionNode(part, ResolveNode);
                    BuildSlots(node);
                    _nodes[part] = node;
                }
            }

            // Second pass: drop nodes for parts that have left the ship. Collected
            // first because a dictionary cannot be modified while it is enumerated.
            _stale.Clear();
            foreach (KeyValuePair<Part, KspDimensionNode> entry in _nodes)
                if (entry.Key == null || !_seen.Contains(entry.Key)) _stale.Add(entry.Key);
            for (int i = 0; i < _stale.Count; i++) _nodes.Remove(_stale[i]);
            _stale.Clear();

            // Third pass: measure the joints, now that every part on the ship has a
            // node to be measured against. This has to happen before anything is
            // propagated, while the parts and their dimension fields still agree.
            foreach (KeyValuePair<Part, KspDimensionNode> entry in _nodes)
                entry.Value.RefreshJointGeometry();

            // Fourth pass: watch where each attached part now SITS, not just what it
            // holds. A flap slid along its wing changes no field at all, on itself or
            // on the wing, so nothing driven by field changes can notice it moved -
            // and on a tapered wing it is now the wrong thickness for where it is.
            _propagating = true;
            try
            {
                WingConformance.RefitMovedSurfaces(_nodes);
            }
            finally
            {
                _propagating = false;
            }
        }

        /// <summary>
        /// Find the graph node for a part, or null when it is not tracked. Handed
        /// to each node so it can turn its neighbouring Parts into graph nodes.
        /// </summary>
        private KspDimensionNode ResolveNode(Part part)
        {
            if (part == null) return null;
            return _nodes.TryGetValue(part, out KspDimensionNode node) ? node : null;
        }

        /// <summary>Find every recognised dimension field on a part.</summary>
        /// <remarks>
        /// Runs over the part's whole module list, which is why the registry offers
        /// a cheap name-only pre-filter: the great majority of fields are rejected
        /// without a dictionary lookup or any reflection.
        /// </remarks>
        private static void BuildSlots(KspDimensionNode node)
        {
            node.SlotList.Clear();
            Part part = node.Part;
            node.ModuleCount = part.Modules.Count;

            for (int i = 0; i < part.Modules.Count; i++)
            {
                PartModule module = part.Modules[i];
                if (module == null) continue;

                foreach (BaseField field in module.Fields)
                {
                    if (field == null) continue;
                    if (!DimensionFieldRegistry.IsKnownFieldName(field.name)) continue;

                    DimensionDescriptor descriptor = DimensionFieldRegistry.Lookup(module.GetType(), field.name);
                    if (descriptor == null) continue;        // unknown here, or disabled
                    if (field.FieldInfo == null) continue;   // a property-backed field we cannot write

                    var slot = new KspDimensionSlot(part, module, field, descriptor);
                    if (!slot.TryReadRaw(out float raw)) continue;   // not a numeric field

                    // Seed the cache from the value the part starts with, so simply
                    // adding a part never looks like somebody changing it.
                    slot.CachedRaw = raw;
                    slot.HasCache = true;
                    node.SlotList.Add(slot);

                    if (Debug)
                        UnityEngine.Debug.Log($"{LogTag} tracking {slot.Label} = {raw:F4} ({descriptor})");
                }
            }
        }

        /// <summary>
        /// Compare every tracked field against last frame and propagate whatever moved.
        /// </summary>
        /// <remarks>
        /// One reflected field read per tracked dimension per frame. Even a large
        /// craft only has a few hundred of those, and this only runs in the editor.
        /// </remarks>
        private void DetectAndPropagate()
        {
            _changes.Clear();

            // Collect the whole batch before touching anything: propagation writes
            // to other parts' fields, and comparing those against their caches
            // afterwards would report our own writes as fresh player edits.
            foreach (KeyValuePair<Part, KspDimensionNode> entry in _nodes)
            {
                List<IDimensionSlot> slots = entry.Value.SlotList;
                for (int i = 0; i < slots.Count; i++)
                {
                    var slot = (KspDimensionSlot)slots[i];
                    if (!slot.TryReadRaw(out float raw)) continue;

                    if (!slot.HasCache)
                    {
                        slot.CachedRaw = raw;
                        slot.HasCache = true;
                        continue;
                    }

                    // Detection uses the tight epsilon, not the match tolerance: a
                    // millimetre of slider movement has to be noticed even though a
                    // millimetre is well inside what counts as the same size.
                    if (!_propagator.Changed(raw, slot.CachedRaw)) continue;

                    float oldRaw = slot.CachedRaw;
                    slot.CachedRaw = raw;

                    // Fields nobody could have edited still get their cache moved
                    // on - so they do not fire later - but nothing is propagated
                    // from them. ProceduralParts' unselected shape modules are the
                    // usual case.
                    if (!slot.IsAdjustable) continue;

                    _changes.Add(new DimensionChange
                    {
                        Slot = slot,
                        OldValue = slot.Descriptor.ToCommonUnits(oldRaw),
                        NewValue = slot.Descriptor.ToCommonUnits(raw),
                    });
                }
            }

            if (_changes.Count == 0)
            {
                // A frame where nothing is changing is the only honest moment to ask
                // whether a control surface is off its wing. Asked while something is
                // moving, a surface part way through being repositioned looks exactly
                // like one somebody pulled off - and because the answer is remembered,
                // that misreading strands it for good.
                WingConformance.NotePlayerPlacements(_nodes);
                return;
            }

            // A control surface whose length somebody else just changed has told us
            // what fraction of its wing it should keep from now on. Read that before
            // propagating, while the change is still attributable to whoever made it.
            _lengthsEdited.Clear();
            for (int i = 0; i < _changes.Count; i++)
            {
                DimensionChange edit = _changes[i];
                if (edit.Slot.Descriptor.Channel != Channels.Span) continue;
                WingConformance.NoteLengthEdited(edit.Slot.Part);
                _lengthsEdited.Add(edit.Slot.Part);
            }

            _propagating = true;
            _shipModifiedPending = false;
            _windowsToRefresh.Clear();

            // Where every attached part sits on its host, taken before anything moves
            // so a part whose span changes can be put back afterwards. The spans that
            // are about to change go with it, because by now they have already
            // changed in the fields we would otherwise read them from.
            _spansBeforeChange.Clear();
            for (int i = 0; i < _changes.Count; i++)
            {
                DimensionChange change = _changes[i];
                if (change.Slot.Descriptor.Channel != Channels.Span) continue;
                _spansBeforeChange[change.Slot.Part] = change.OldValue;
            }
            WingConformance.Snapshot(_nodes, _spansBeforeChange);
            // Every tracked wing's values, not only the ones somebody edited. The
            // rules that run after propagation ask what a wing looked like BEFORE this
            // frame, and until this was captured in full they got a field's current
            // value for anything this mod itself had just written - so a wing resized
            // by propagation appeared never to have changed, and the control surfaces
            // hanging off it were never moved to follow it.
            _valuesBeforeChange.Clear();
            WingConformance.CaptureWingValues(_nodes, _valuesBeforeChange);

            // The player's own edits override, because those fields have ALREADY moved
            // by the time this runs - the change is what we noticed - so what was just
            // captured for them is the new value, not the old one.
            //
            // In raw field units to match the rest, hence the conversion back: a
            // change carries its values in common units, where a radius has been
            // doubled into a diameter.
            for (int i = 0; i < _changes.Count; i++)
            {
                DimensionChange edit = _changes[i];
                _valuesBeforeChange[new WingConformance.PartField
                {
                    Part = edit.Slot.Part,
                    Name = edit.Slot.Field.name,
                }] = edit.Slot.Descriptor.ToFieldUnits(edit.OldValue);
            }
            // Anything that bends the line a wing's edges run along, or the plane its
            // surfaces lie in, starts a carry-through into whatever is bolted to its
            // tip.
            //
            // Both thickness fields, not only the tip. The tip one is what reaches the
            // child's root through the thickness channel, but either of them tilts the
            // plane the parent's surfaces lie in, and a child whose surfaces ran flat
            // into that plane stops doing so the moment it tilts.
            //
            // The tip chord does it by moving both edges apart or together, and the
            // tip offset by moving them the same way. So does the SPAN: the same tip
            // held at the same width over a shorter wing is a steeper edge, and a
            // child whose edges were collinear with its parent's stops being collinear
            // the moment the parent is shortened. Leaving length out was why
            // shortening a parent wing from 4 m to 3 m left its child at the old angle
            // while every other edit carried correctly.
            _tipsEdited.Clear();
            for (int i = 0; i < _changes.Count; i++)
            {
                string field = _changes[i].Slot.Field.name;
                if (field == "sharedBaseWidthTip" || field == "sharedBaseOffsetTip"
                    || field == "sharedBaseLength" || field == "sharedBaseThicknessTip"
                    || field == "sharedBaseThicknessRoot")
                    _tipsEdited.Add(_changes[i].Slot.Part);
            }

            WingConformance.NoteStraightEdges(_nodes, _valuesBeforeChange);
            try
            {
                // Each changed field starts its own walk. Two fields can change in
                // the same frame - a symmetry pair, or a hollow part's bore and
                // outside together - and their runs are independent.
                for (int i = 0; i < _changes.Count; i++)
                {
                    DimensionChange change = _changes[i];
                    if (!_nodes.TryGetValue(change.Slot.Part, out KspDimensionNode origin)) continue;

                    if (Debug)
                        UnityEngine.Debug.Log($"{LogTag} {change.Slot.Label} {change.OldValue:F4} -> " +
                                              $"{change.NewValue:F4}, propagating");

                    int changed = _propagator.Propagate(origin, change.Slot.Descriptor,
                                                        change.OldValue, change.NewValue);
                    if (changed > 0)
                        UnityEngine.Debug.Log($"{LogTag} synced {changed} field(s) to " +
                                              $"{change.NewValue:F4} from {change.Slot.Label}");
                }

                // Two joint rules that a value walk cannot express, run once the
                // walks are done and the geometry has settled on its new numbers.
                WingConformance.Forget(_nodes);
                WingConformance.KeepEdgesStraight(_nodes, _tipsEdited);
                WingConformance.AlignWingJoints(_nodes);
                // Position before orientation. MatchSweep turns a control surface
                // about a point on its wing's span, and turning about a point the
                // part is not yet near translates it - so it has to be put where it
                // belongs along the span first. The other way round, changing a
                // wing's length left its flap correctly angled and pushed off the
                // edge by an amount that grew with the size of the change.
                WingConformance.Reanchor(_nodes);
                WingConformance.MatchSweep(_nodes, _lengthsEdited);
                WingConformance.RefitAfterLengthEdit(_nodes, _lengthsEdited);

                foreach (Part part in _windowsToRefresh)
                    if (part != null) MonoUtilities.RefreshPartContextWindow(part);

                // One ship-modified event for the whole batch rather than one per
                // field, and still inside the propagating guard so it does not
                // look to us like somebody else touched the ship.
                if (_shipModifiedPending && EditorLogic.fetch != null && EditorLogic.fetch.ship != null)
                    GameEvents.onEditorShipModified.Fire(EditorLogic.fetch.ship);
            }
            catch (Exception ex)
            {
                // A misbehaving mod's change handler must not take the editor down
                // or leave the guard latched on.
                UnityEngine.Debug.LogError($"{LogTag} propagation failed: {ex}");
            }
            finally
            {
                _windowsToRefresh.Clear();
                _shipModifiedPending = false;
                _propagating = false;
            }

            // Re-read everything so values we just wrote - including any the
            // receiving mod adjusted afterwards - are not mistaken for a new
            // player edit on the next poll.
            Recache();
        }

        /// <summary>
        /// Snapshot every tracked field, making the current state the baseline the
        /// next poll compares against.
        /// </summary>
        private void Recache()
        {
            foreach (KeyValuePair<Part, KspDimensionNode> entry in _nodes)
            {
                List<IDimensionSlot> slots = entry.Value.SlotList;
                for (int i = 0; i < slots.Count; i++)
                {
                    var slot = (KspDimensionSlot)slots[i];
                    if (slot.TryReadRaw(out float raw))
                    {
                        slot.CachedRaw = raw;
                        slot.HasCache = true;
                    }
                }
            }
        }

        // =====================================================================
        // Writing a field the way the part action window would
        // =====================================================================

        /// <summary>
        /// Set a field exactly as <c>UIPartActionFieldItem.SetFieldValue</c> does, so
        /// the owning mod cannot tell the difference between this and the player
        /// dragging the slider.
        /// </summary>
        /// <param name="part">The part the field belongs to, used for symmetry and the PAW refresh.</param>
        /// <param name="module">The module the field belongs to, used to find the counterpart module.</param>
        /// <param name="field">The field to write.</param>
        /// <param name="newValue">The new value, already boxed as the field's own numeric type.</param>
        /// <remarks>
        /// The two details that matter most: symmetry counterparts are updated
        /// first, and <c>onFieldChanged</c> is handed the field's *previous*
        /// value. Several mods - ProceduralParts and Procedural Fairings among
        /// them - compare that argument against the field's current value and do
        /// nothing at all when the two match, so passing the new value makes the
        /// callback a no-op and the mesh never rebuilds.
        /// </remarks>
        internal static void SetFieldLikeUI(Part part, PartModule module, BaseField field, object newValue)
        {
            if (field == null) return;

            object oldValue = field.GetValue(field.host);
            bool changed = !Equals(oldValue, newValue);

            // Symmetry first, exactly as the stock UI does it, and note that a
            // counterpart alone being out of step counts as a change even when this
            // part's own field already holds the new value.
            UI_Control control = field.uiControlEditor;
            if (DimensionSettings.MirrorToSymmetryCounterparts
                && control != null && (control.affectSymCounterparts & UI_Scene.Editor) != UI_Scene.None)
                changed |= SetSymmetryCounterparts(part, module, field, newValue);

            if (!changed) return;

            field.SetValue(newValue, field.host);

            if (control != null)
            {
                control.onFieldChanged?.Invoke(field, oldValue);
                // Fields that suppress the event do so because their own handler
                // fires something better; respect that and only note the rest.
                if (!control.suppressEditorShipModified) _shipModifiedPending = true;
            }

            if (part != null) _windowsToRefresh.Add(part);

            if (Debug)
                UnityEngine.Debug.Log($"{LogTag} set {part?.partInfo?.title}/{module?.GetType().Name}." +
                                      $"{field.name} {oldValue} -> {newValue}");
        }

        /// <summary>
        /// Mirror of <c>UIPartActionFieldItem.SetSymCounterpartValue</c>: write the
        /// same field on each symmetry counterpart and fire its symmetry callback.
        /// </summary>
        /// <returns>True when at least one counterpart was holding a different value.</returns>
        private static bool SetSymmetryCounterparts(Part part, PartModule module, BaseField field, object newValue)
        {
            if (part == null || module == null) return false;
            if (part.symmetryCounterparts == null || part.symmetryCounterparts.Count == 0) return false;

            int index = part.Modules.IndexOf(module);
            bool changed = false;

            foreach (Part counterpart in part.symmetryCounterparts)
            {
                if (counterpart == null) continue;

                // Counterparts are clones, so the module usually sits at the same
                // index; look it up by class name only when it does not, which is
                // what stock does and what keeps this working across mods that add
                // or remove modules at runtime.
                PartModule counterpartModule;
                if (index >= 0 && index < counterpart.Modules.Count &&
                    counterpart.Modules[index] != null &&
                    counterpart.Modules[index].GetType() == module.GetType())
                {
                    counterpartModule = counterpart.Modules[index];
                }
                else
                {
                    counterpartModule = counterpart.Modules[module.ClassName];
                }
                if (counterpartModule == null) continue;

                // NOTE: copied field for field, which is wrong for a mirrored pair
                // whose two halves disagree about which of their ends is which - see
                // pwings_mirrored_wings_both_follow_thickness, which fails on it. Two
                // attempts at deciding when to swap the ends were both worse than
                // this: one compared each part's axes against its PARENT'S, which is
                // meaningless when the parent is a fuselage, and the other asked
                // which way the part sits from its parent, which got the wings wrong
                // too. Both cost the wings their own edit, which is a worse failure
                // than the one being fixed. Left alone until the test that tells them
                // apart is written.
                BaseField counterpartField = counterpartModule.Fields[field.name];
                if (counterpartField == null) continue;

                if (!Equals(counterpartField.GetValue(counterpartField.host), newValue)) changed = true;
                counterpartField.SetValue(newValue, counterpartField.host);

                // Stock passes the *primary* part's field and the new value here,
                // which reads oddly next to onFieldChanged but is what mods expect.
                UI_Control control = counterpartField.uiControlEditor;
                control?.onSymmetryFieldChanged?.Invoke(field, newValue);

                _windowsToRefresh.Add(counterpart);
            }

            return changed;
        }
    }
}
