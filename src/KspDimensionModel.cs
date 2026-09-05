using System;
using System.Collections.Generic;
using UnityEngine;

namespace DimensionSync
{
    /// <summary>
    /// A dimension field on a live PartModule.
    /// </summary>
    internal class KspDimensionSlot : IDimensionSlot
    {
        /// <summary>The part this field lives on.</summary>
        public Part Part;

        /// <summary>The module this field lives on.</summary>
        public PartModule Module;

        /// <summary>The field itself.</summary>
        public BaseField Field;

        /// <summary>Last value we saw in the field, in the field's own units.</summary>
        public float CachedRaw;

        /// <summary>False until <see cref="CachedRaw"/> holds a real reading.</summary>
        public bool HasCache;

        /// <summary>What this field means; resolved once when the slot is built.</summary>
        private readonly DimensionDescriptor _descriptor;

        /// <summary>Bind a descriptor to a live field on a live part.</summary>
        public KspDimensionSlot(Part part, PartModule module, BaseField field, DimensionDescriptor descriptor)
        {
            Part = part;
            Module = module;
            Field = field;
            _descriptor = descriptor;
        }

        /// <summary>What this field means; see <see cref="DimensionFieldRegistry"/>.</summary>
        public DimensionDescriptor Descriptor => _descriptor;

        /// <summary>Human-readable "Part title/ModuleName.fieldName", for the log.</summary>
        public string Label => $"{Part.partInfo?.title ?? Part.name}/{Module.GetType().Name}.{Field.name}";

        /// <summary>
        /// A field only counts if something could legitimately have changed it: its
        /// module is switched on and, for the usual case, it is shown in the editor
        /// PAW with a control we can drive.
        /// </summary>
        /// <remarks>
        /// Some mods - B9 Procedural Wings among them - keep their dimensions out of
        /// the PAW entirely and drive them from their own window, re-reading the
        /// fields every frame. Those descriptors clear RequiresEditorControl, and
        /// writing the field is enough to make the part rebuild.
        /// </remarks>
        public bool IsAdjustable
        {
            get
            {
                if (Module == null || Field == null) return false;
                if (!Module.isEnabled || !Module.enabled) return false;

                if (_descriptor.RequireGuiUnits != null &&
                    !string.Equals(Field.guiUnits, _descriptor.RequireGuiUnits, StringComparison.Ordinal))
                    return false;

                if (_descriptor.RequireFieldName != null && !GateFieldAllows()) return false;

                if (!_descriptor.RequiresEditorControl) return true;

                if (!Field.guiActiveEditor) return false;
                UI_Control control = Field.uiControlEditor;
                if (control == null || !control.controlEnabled) return false;
                return true;
            }
        }

        /// <summary>
        /// Read the field in its own units. Returns false for a field that is not a
        /// float, double or int, which is how a name collision with some mod's
        /// string or bool "diameter" is shrugged off.
        /// </summary>
        /// <summary>
        /// Whether the descriptor's gating field is present and holds the required
        /// value. A missing gate field means the dimension does not apply here.
        /// </summary>
        private bool GateFieldAllows()
        {
            BaseField gate = Module.Fields[_descriptor.RequireFieldName];
            if (gate == null) return false;
            object value = gate.GetValue(gate.host);
            return value is bool flag && flag == _descriptor.RequireFieldValue;
        }

        public bool TryReadRaw(out float value)
        {
            value = 0f;
            if (Field == null) return false;
            object boxed = Field.GetValue(Field.host);
            return TryUnbox(boxed, out value);
        }

        /// <summary>NaN when the field cannot be read, which never compares equal to anything.</summary>
        public float Value => TryReadRaw(out float raw) ? _descriptor.ToCommonUnits(raw) : float.NaN;

        /// <summary>
        /// Write <paramref name="value"/> through the part action window's protocol
        /// and report back what actually stuck.
        /// </summary>
        /// <returns>
        /// The value the field ended up holding. It differs from what was asked for
        /// when the editor control clamped it, or when the owning mod's change
        /// handler adjusted it afterwards - either way the caller treats that as
        /// the end of the run.
        /// </returns>
        public float SetValue(float value)
        {
            float wanted = ClampToControl(_descriptor.ToFieldUnits(value));
            object boxed = Box(wanted);
            if (boxed == null) return Value;

            // Tagged as a CHANNEL write: this is one value being carried along a run
            // of parts, and what it leaves in the field is the new size without any of
            // the shaping the conformance rules apply afterwards. Rules that need that
            // intermediate figure ask for it by name rather than reading the field and
            // hoping nothing else has been there.
            DimensionSyncAddon.SetFieldLikeUI(Part, Module, Field, boxed,
                                              DimensionSyncAddon.WriteOrigin.Channel);

            // Read back: the mod's own change handler may have adjusted it.
            return Value;
        }

        /// <summary>Keep writes inside the range the editor control itself allows.</summary>
        private float ClampToControl(float raw)
        {
            switch (Field.uiControlEditor)
            {
                case UI_FloatEdit edit:
                    return Mathf.Clamp(raw, edit.minValue, edit.maxValue);
                case UI_FloatRange range:
                    return Mathf.Clamp(raw, range.minValue, range.maxValue);
                case UI_ScaleEdit scale:
                    return Mathf.Clamp(raw, scale.MinValue(), scale.MaxValue());
                default:
                    return raw;
            }
        }

        /// <summary>Box <paramref name="value"/> as the field's own numeric type.</summary>
        private object Box(float value)
        {
            Type t = Field.FieldInfo?.FieldType;
            if (t == typeof(float)) return value;
            if (t == typeof(double)) return (double)value;
            if (t == typeof(int)) return Mathf.RoundToInt(value);
            return null;
        }

        /// <summary>Convert a boxed numeric field value to float, or fail for any other type.</summary>
        public static bool TryUnbox(object boxed, out float value)
        {
            switch (boxed)
            {
                case float f: value = f; return true;
                case double d: value = (float)d; return true;
                case int i: value = i; return true;
                default: value = 0f; return false;
            }
        }
    }

    /// <summary>
    /// A live part and the joints it takes part in.
    /// </summary>
    internal class KspDimensionNode : IDimensionNode
    {
        /// <summary>The part this node stands for.</summary>
        public readonly Part Part;

        /// <summary>Its dimension fields, rebuilt whenever its module list changes.</summary>
        public readonly List<IDimensionSlot> SlotList = new List<IDimensionSlot>();

        /// <summary>Module count when the slots were last built, used to spot module changes.</summary>
        public int ModuleCount;

        /// <summary>Turns a neighbouring Part into its graph node, or null if untracked.</summary>
        private readonly Func<Part, KspDimensionNode> _resolve;

        /// <summary>
        /// How much of this part each surface-attached neighbour covers, as fractions
        /// of this part's root-to-tip length.
        /// </summary>
        /// <remarks>
        /// Measured once when the ship settles rather than while a change is being
        /// propagated. By the time we notice a wing's span has changed the field
        /// already holds the new number while the parts have not moved yet, so
        /// measuring then would divide the old geometry by the new length and report
        /// a joint that covers two thirds of a wing it actually covers all of.
        /// </remarks>
        private readonly Dictionary<Part, Coverage> _coverage = new Dictionary<Part, Coverage>();

        /// <summary>
        /// How much of this part's span lies on the negative side of its own origin,
        /// as a fraction of the whole. Measured at the last reconcile.
        /// </summary>
        /// <remarks>
        /// A B9 wing runs from its origin out to its tip, so this is 0 for one. A B9
        /// control surface is centred on its origin instead, so this is 0.5, and
        /// lengthening one moves both of its ends. Knowing which is which is what
        /// lets a control surface be grown away from the wing root rather than
        /// through it.
        /// </remarks>
        private float _spanBehindOrigin;

        /// <summary>False when <see cref="_spanBehindOrigin"/> needs measuring again.</summary>
        private bool _spanRangeKnown;

        /// <summary>The measured root-to-tip axis, in this part's own space.</summary>
        private Vector3 _spanAxis = Vector3.right;

        /// <summary>False when <see cref="_spanAxis"/> needs measuring again.</summary>
        private bool _spanAxisKnown;

        /// <summary>Everything this part draws, boxed in the part's own space.</summary>
        private Bounds _localBounds;

        /// <summary>False when <see cref="_localBounds"/> needs measuring again.</summary>
        private bool _localBoundsKnown;

        /// <summary>The stretch of a part that a neighbour lies along.</summary>
        private struct Coverage
        {
            public float From;
            public float To;
        }

        /// <summary>Wrap a live part as a graph node.</summary>
        public KspDimensionNode(Part part, Func<Part, KspDimensionNode> resolve)
        {
            Part = part;
            _resolve = resolve;
        }

        /// <summary>The part's display title, for the log.</summary>
        public string Label => Part.partInfo?.title ?? Part.name;

        /// <summary>Every recognised dimension field on this part.</summary>
        public IList<IDimensionSlot> Slots => SlotList;

        /// <summary>
        /// Every joint this part takes part in: stack joints from paired axial
        /// attach nodes, and span joints from surface attachment.
        /// </summary>
        /// <remarks>
        /// Surface attachment is directional in KSP - the child holds the node and
        /// names its host - so a span joint always meets the child's root and the
        /// parent's tip. That is the arrangement procedural wing segments are built
        /// in, and it is the pairing B9 Procedural Wings' own "inherit" button uses.
        /// </remarks>
        public IEnumerable<AxialLink> Links
        {
            get
            {
                if (Part == null) yield break;

                // Children first, then the parent. A part can be joined to the same
                // neighbour by only one of these, so at most one link per pair per
                // kind comes out of here.
                for (int i = 0; i < Part.children.Count; i++)
                {
                    Part child = Part.children[i];
                    AxialLink stack = StackLink(child);
                    if (stack.Other != null) yield return stack;

                    if (IsSurfaceAttachedTo(child, Part))
                        foreach (AxialLink link in SurfaceLinks(child, childIsAttachedToUs: true))
                            yield return link;
                }

                if (Part.parent != null)
                {
                    AxialLink stack = StackLink(Part.parent);
                    if (stack.Other != null) yield return stack;

                    if (IsSurfaceAttachedTo(Part, Part.parent))
                        foreach (AxialLink link in SurfaceLinks(Part.parent, childIsAttachedToUs: false))
                            yield return link;
                }
            }
        }

        /// <summary>
        /// Turn one surface attachment into the joint or joints it represents.
        /// </summary>
        /// <param name="neighbour">The part on the other side of the attachment.</param>
        /// <param name="childIsAttachedToUs">
        /// True when <paramref name="neighbour"/> is the child holding the surface
        /// node, false when this part is.
        /// </param>
        /// <remarks>
        /// Which kind of joint it is comes from the way the child's surface node
        /// faces relative to its own root-to-tip axis. A wing segment's node faces
        /// back along its span, so it is meeting its neighbour end to end and the
        /// joint pairs the child's root with the parent's tip. A control surface's
        /// node faces across its span, so it is lying against its neighbour's flank
        /// and the joint pairs root with root and tip with tip - which is two links,
        /// because each end is its own connection.
        ///
        /// A side-by-side neighbour mounted the other way round has its ends
        /// swapped, which the span directions settle.
        /// </remarks>
        private IEnumerable<AxialLink> SurfaceLinks(Part neighbour, bool childIsAttachedToUs)
        {
            KspDimensionNode other = _resolve(neighbour);
            if (other == null) yield break;

            Part child = childIsAttachedToUs ? neighbour : Part;
            KspDimensionNode childNode = childIsAttachedToUs ? other : this;
            AttachNode node = child.srfAttachNode;
            if (node == null || node.orientation.sqrMagnitude <= 1e-6f) yield break;

            Vector3 childSpan = childNode.SpanAxis();
            bool endToEnd = Mathf.Abs(Vector3.Dot(node.orientation.normalized, childSpan)) > 0.7f;

            if (endToEnd)
            {
                // The child's root meets our tip, whichever of us is the child.
                yield return childIsAttachedToUs
                    ? new AxialLink(other, PartEnd.Top, PartEnd.Bottom, LinkKind.Span)
                    : new AxialLink(other, PartEnd.Bottom, PartEnd.Top, LinkKind.Span);
                yield break;
            }

            // Side by side. Work out how much of the host the attached part actually
            // covers before deciding how its ends pair up.
            KspDimensionNode host = childIsAttachedToUs ? this : other;
            KspDimensionNode attached = childIsAttachedToUs ? other : this;

            // A control surface the player has taken off its wing is not jointed to it
            // at all. Gating only the arranging rules leaves the ordinary channels still
            // feeding it the wing's dimensions, so it stays where it was put and quietly
            // changes size instead - which is half a fix and reads as a stranger bug
            // than the one it replaced.
            if (!WingConformance.StillBelongsToItsHost(attached.Part, host)) yield break;

            Coverage cover = host.CoverageOf(attached);
            float fromStation = cover.From;
            float toStation = cover.To;

            bool reversed = toStation < fromStation;
            if (reversed)
            {
                float swap = fromStation;
                fromStation = toStation;
                toStation = swap;
            }

            bool fullSpan = fromStation <= 0.02f && toStation >= 0.98f;
            if (DimensionSettings.Debug)
                UnityEngine.Debug.Log($"{DimensionSyncAddon.LogTag} edge link: {Part.name} <-> " +
                                      $"{neighbour.name}, raw cover {cover.From:F3}..{cover.To:F3}, " +
                                      $"reversed={reversed}, fullSpan={fullSpan}, " +
                                      $"childIsAttachedToUs={childIsAttachedToUs}");
            if (fullSpan)
            {
                // One way, from the host outward - the same rule a partial cover
                // already follows, and for the same reason: the wing is the reference
                // surface and the thing lying along it conforms.
                //
                // Two-way here let a control surface drive its own wing, and a value
                // this mod had just written to the flap came back the next frame
                // looking like an edit. It then crossed to the wing's FAR end, which
                // crossed back to the flap's far end, and a change meant for one end
                // of a joint arrived at all four. That reads as a sign error and is
                // really a loop, which is why it only showed up where the flap ran
                // the whole span: a partial one has never been allowed to drive.
                if (!childIsAttachedToUs) yield break;

                yield return new AxialLink(other, PartEnd.Bottom,
                                           reversed ? PartEnd.Top : PartEnd.Bottom, LinkKind.Edge);
                yield return new AxialLink(other, PartEnd.Top,
                                           reversed ? PartEnd.Bottom : PartEnd.Top, LinkKind.Edge);
                yield break;
            }

            // A partial cover is one-way: the host is the reference surface and the
            // attached part conforms to it. Going the other way is not well posed -
            // one value on a short flap does not say what either end of the wing
            // should be - so only the host emits these.
            if (!childIsAttachedToUs) yield break;

            float rootStation = reversed ? toStation : fromStation;
            float tipStation = reversed ? fromStation : toStation;
            float fraction = Mathf.Abs(toStation - fromStation);

            // Both host ends feed an interpolated station, so both are entry points.
            yield return new AxialLink(other, PartEnd.Both, PartEnd.Bottom, LinkKind.Edge,
                                       host.StationTransform(rootStation, fraction));
            yield return new AxialLink(other, PartEnd.Both, PartEnd.Top, LinkKind.Edge,
                                       host.StationTransform(tipStation, fraction));
        }

        /// <summary>
        /// Re-measure every surface-attached neighbour's coverage. Called when the
        /// tracked part set is reconciled, which is the last moment before anything
        /// can be propagated and therefore the last moment the geometry and the
        /// dimension fields still agree with each other.
        /// </summary>
        public void RefreshJointGeometry()
        {
            _coverage.Clear();
            if (Part == null) return;

            _spanRangeKnown = false;
            _spanAxisKnown = false;
            _localBoundsKnown = false;

            for (int i = 0; i < Part.children.Count; i++)
            {
                Part child = Part.children[i];
                if (!IsSurfaceAttachedTo(child, Part)) continue;
                KspDimensionNode node = _resolve(child);
                if (node != null) _coverage[child] = Measure(node);
            }
        }

        /// <summary>
        /// Where along this part an attached part sits, from the measurement taken
        /// at the last reconcile.
        /// </summary>
        /// <param name="attached">The part hanging off this one.</param>
        /// <param name="from">Its inboard end, 0 at this part's root and 1 at its tip.</param>
        /// <param name="to">Its outboard end, on the same scale.</param>
        /// <returns>False when the two are not a measured joint.</returns>
        public bool TryCoverage(Part attached, out float from, out float to)
        {
            from = 0f;
            to = 1f;
            if (attached == null || !_coverage.TryGetValue(attached, out Coverage cover)) return false;

            from = Mathf.Min(cover.From, cover.To);
            to = Mathf.Max(cover.From, cover.To);
            return true;
        }

        /// <summary>
        /// The stretch of this part that <paramref name="attached"/> lies along,
        /// from the measurement taken at the last reconcile. Falls back to measuring
        /// now for a joint that has not been seen yet, and to a full cover when
        /// there is nothing to measure.
        /// </summary>
        private Coverage CoverageOf(KspDimensionNode attached)
        {
            if (attached?.Part == null) return new Coverage { From = 0f, To = 1f };
            if (_coverage.TryGetValue(attached.Part, out Coverage cover)) return cover;

            cover = Measure(attached);
            _coverage[attached.Part] = cover;
            return cover;
        }

        /// <summary>
        /// Where along this part's span the given attached part starts and ends, as
        /// fractions from this part's root (0) to its tip (1). A full cover is the
        /// answer whenever either part's length is unknown.
        /// </summary>
        private Coverage Measure(KspDimensionNode attached)
        {
            var full = new Coverage { From = 0f, To = 1f };

            float hostSpan = SpanLength();
            float attachedSpan = attached.SpanLength();
            if (hostSpan <= 1e-3f || attachedSpan <= 1e-3f) return full;

            SpanEnds(hostSpan, out Vector3 hostRoot, out _);
            Vector3 hostDirection = Part.transform.TransformDirection(SpanAxis());

            // Not every part starts at its own origin: a B9 control surface is
            // centred on its node instead. Ask each part where its ends actually are
            // rather than assuming the root is at zero, or a centred part reads as
            // covering the half of its host it is not on.
            attached.SpanEnds(attachedSpan, out Vector3 attachedRoot, out Vector3 attachedTip);

            var measured = new Coverage
            {
                From = Vector3.Dot(attachedRoot - hostRoot, hostDirection) / hostSpan,
                To = Vector3.Dot(attachedTip - hostRoot, hostDirection) / hostSpan,
            };
            if (DimensionSettings.Debug)
                UnityEngine.Debug.Log($"{DimensionSyncAddon.LogTag} coverage: host {Part.name} " +
                                      $"span {hostSpan:F2} axis {SpanAxis()}, attached " +
                                      $"{attached.Part.name} span {attachedSpan:F2} axis " +
                                      $"{attached.SpanAxis()} behind {attached.SpanBehindOrigin:F2} " +
                                      $"-> {measured.From:F3}..{measured.To:F3}");
            return measured;
        }

        /// <summary>
        /// Build the converter for a joint that meets this part part way along its
        /// span.
        /// </summary>
        /// <param name="station">Where along this part's span the joint sits, 0 to 1.</param>
        /// <param name="fraction">How much of this part's span the attached part covers.</param>
        /// <remarks>
        /// An end-paired dimension - thickness - is read off the line between this
        /// part's root and tip values at that station. A whole-part dimension - span -
        /// is scaled by how much of this part is covered instead, since a flap over
        /// half a wing should be half as long as it.
        /// </remarks>
        private Func<DimensionDescriptor, float, float> StationTransform(float station, float fraction)
        {
            return (descriptor, value) =>
            {
                if (string.Equals(descriptor.Channel, Channels.Span, StringComparison.Ordinal))
                    return value * fraction;

                float farEnd = ValueAtOppositeEnd(descriptor);
                if (float.IsNaN(farEnd)) return value;

                return descriptor.Ends == PartEnd.Bottom
                    ? Mathf.Lerp(value, farEnd, station)
                    : Mathf.Lerp(farEnd, value, station);
            };
        }

        /// <summary>
        /// This part's current value on the same channel at the end the given
        /// descriptor does *not* describe, or NaN when there is no such dimension.
        /// </summary>
        /// <remarks>
        /// That end has not moved during this propagation, so its present value is
        /// correct for working out both the before and the after value at a station
        /// between the two.
        /// </remarks>
        private float ValueAtOppositeEnd(DimensionDescriptor descriptor)
        {
            PartEnd wanted = descriptor.Ends == PartEnd.Bottom ? PartEnd.Top : PartEnd.Bottom;
            for (int i = 0; i < SlotList.Count; i++)
            {
                IDimensionSlot slot = SlotList[i];
                if (!string.Equals(slot.Descriptor.Channel, descriptor.Channel, StringComparison.Ordinal)) continue;
                if ((slot.Descriptor.Ends & wanted) == 0) continue;
                return slot.Value;
            }
            return float.NaN;
        }

        /// <summary>
        /// How far this part reaches from root to tip, from its own span dimension if
        /// it has one, or from its rendered size along the span axis if not.
        /// </summary>
        private float SpanLength()
        {
            for (int i = 0; i < SlotList.Count; i++)
            {
                IDimensionSlot slot = SlotList[i];
                if (!string.Equals(slot.Descriptor.Channel, Channels.Span, StringComparison.Ordinal)) continue;
                float value = slot.Value;
                if (!float.IsNaN(value) && value > 1e-3f) return value;
            }

            Vector3 axis = SpanAxis();
            float widest = 0f;
            foreach (Renderer renderer in Part.GetComponentsInChildren<Renderer>())
            {
                if (!renderer.enabled || renderer is ParticleSystemRenderer) continue;
                Vector3 extents = renderer.bounds.extents;
                widest = Mathf.Max(widest, Mathf.Abs(Vector3.Dot(extents * 2f, axis)));
            }
            return widest;
        }

        /// <summary>
        /// Where this part's two span ends sit in world space.
        /// </summary>
        /// <param name="span">The part's span length, already known to the caller.</param>
        /// <param name="root">The end on the negative side of the span axis.</param>
        /// <param name="tip">The end on the positive side.</param>
        public void SpanEnds(float span, out Vector3 root, out Vector3 tip)
        {
            Vector3 axis = SpanAxis();
            float behind = SpanBehindOrigin * span;
            root = Part.transform.TransformPoint(axis * -behind);
            tip = Part.transform.TransformPoint(axis * (span - behind));
        }

        /// <summary>
        /// How much of this part's span lies behind its origin, 0 to 1, measured on
        /// first use after each reconcile.
        /// </summary>
        /// <remarks>
        /// Measured lazily rather than in <see cref="RefreshJointGeometry"/> because
        /// a part's coverage is worked out by its host, which may be reconciled
        /// before the part itself is.
        /// </remarks>
        public float SpanBehindOrigin
        {
            get
            {
                if (_spanRangeKnown) return _spanBehindOrigin;
                _spanBehindOrigin = Part == null ? 0f : MeasureSpanBehindOrigin();
                _spanRangeKnown = true;
                return _spanBehindOrigin;
            }
        }

        /// <summary>
        /// Work out what fraction of this part's rendered length sits on the
        /// negative side of its own origin.
        /// </summary>
        /// <returns>0 for a part rooted at its origin, 0.5 for one centred on it.</returns>
        /// <remarks>
        /// Renderer bounds are world-axis-aligned, so each one is reduced to a
        /// conservative reach along the span axis rather than being transformed
        /// exactly. That is precise enough to tell "rooted" from "centred", which is
        /// all this is for, and the result is snapped to the nearer of the two.
        /// </remarks>
        private float MeasureSpanBehindOrigin()
        {
            // Ask first whether this is a part whose layout is known outright.
            // Measuring is the fallback, not the first resort: a sheared control
            // surface measures its own centre badly, and being confidently wrong
            // about which end of a part is which is worse than any guess.
            float known = WingConformance.KnownSpanOrigin(Part);
            if (!float.IsNaN(known)) return known;

            MeasureExtent(SpanAxis(), out float lowest, out float highest);

            // Deliberately NOT checked against the length the part says it is. A
            // part whose span field has just been written still has its old mesh for
            // a frame or two, so the two disagree completely at exactly the moment
            // this is asked - and a control surface that was about to be moved back
            // onto its wing gets skipped instead. The answer here is a FRACTION of
            // whatever was measured, so a stale mesh at the old length still gives
            // the right one; scale cancels out. The wrong-axis problem this once
            // guarded against is handled where it belongs, by declaring the axis in
            // config and by not measuring attached child parts as part of this one.

            if (lowest > highest || highest - lowest <= 1e-4f) return 0f;

            // Snap, because the measurement carries the rounding of whatever edge
            // meshes the part has on it and the two cases are far apart.
            float fraction = -lowest / (highest - lowest);
            if (fraction < 0.25f) return 0f;
            if (fraction > 0.75f) return 1f;
            return 0.5f;
        }

        /// <summary>
        /// This part's root-to-tip axis in its own space, pointing from root to tip.
        /// </summary>
        /// <remarks>
        /// A declared axis wins, since config knows better than a measurement. With
        /// nothing declared the axis is measured against the part's own span
        /// dimension: of the three local axes, the one the part reaches along by
        /// about that much is the span. Guessing an axis instead does not survive
        /// contact with B9, which lays its wings and its control surfaces out on
        /// different axes from each other.
        /// </remarks>
        public Vector3 SpanAxis()
        {
            // Asked of the part itself first, for the kinds whose layout is known.
            // Which end of a control surface is its root is not something the
            // descriptor can say: it depends on whether the part is mirrored.
            Vector3 known = WingConformance.KnownSpanAxis(Part);
            if (known != Vector3.zero) return known;

            for (int i = 0; i < SlotList.Count; i++)
            {
                DimensionDescriptor descriptor = SlotList[i].Descriptor;
                if ((descriptor.Link & (LinkKind.Span | LinkKind.Edge)) == 0) continue;
                switch (descriptor.SpanAxis)
                {
                    case Axis.X: return Vector3.right;
                    case Axis.Y: return Vector3.up;
                    case Axis.Z: return Vector3.forward;
                    default: return MeasuredSpanAxis();
                }
            }
            return Vector3.right;
        }

        /// <summary>
        /// The measured span axis, worked out on first use after each reconcile.
        /// </summary>
        private Vector3 MeasuredSpanAxis()
        {
            if (_spanAxisKnown) return _spanAxis;
            _spanAxis = Part == null ? Vector3.right : MeasureSpanAxis();
            _spanAxisKnown = true;
            return _spanAxis;
        }

        /// <summary>
        /// Pick the local axis the part reaches along by about its declared span,
        /// oriented so the part lies on the positive side of its own origin.
        /// </summary>
        /// <returns>Local +X when the part declares no span to measure against.</returns>
        private Vector3 MeasureSpanAxis()
        {
            float span = DeclaredSpan();
            if (float.IsNaN(span) || span <= 1e-3f) return Vector3.right;

            Vector3 best = Vector3.right;
            float closest = float.PositiveInfinity;
            foreach (Vector3 axis in Cardinals)
            {
                MeasureExtent(axis, out float near, out float far);
                if (far < near) continue;

                float error = Mathf.Abs((far - near) - span);
                if (error >= closest) continue;
                closest = error;

                // Point the axis the way the part actually goes, so "root" below
                // consistently means the end nearer the origin.
                best = (far + near) >= 0f ? axis : -axis;
            }

            if (DimensionSettings.Debug)
                UnityEngine.Debug.Log($"{DimensionSyncAddon.LogTag} {Label}: span {span:F3}, " +
                                      $"local box {LocalBounds().size}, span axis {best}");
            return best;
        }

        /// <summary>The part's own span dimension, or NaN when it has none.</summary>
        /// <remarks>
        /// Reads the field rather than going through <see cref="SpanLength"/>, whose
        /// fallback needs the very axis this is helping to choose.
        /// </remarks>
        private float DeclaredSpan()
        {
            for (int i = 0; i < SlotList.Count; i++)
            {
                IDimensionSlot slot = SlotList[i];
                if (!string.Equals(slot.Descriptor.Channel, Channels.Span, StringComparison.Ordinal)) continue;
                float value = slot.Value;
                if (!float.IsNaN(value) && value > 1e-3f) return value;
            }
            return float.NaN;
        }

        /// <summary>The three local axes, allocated once rather than per measurement.</summary>
        private static readonly Vector3[] Cardinals = { Vector3.right, Vector3.up, Vector3.forward };

        /// <summary>
        /// How far everything this part draws reaches along one of its own axes,
        /// relative to its own origin.
        /// </summary>
        /// <param name="localAxis">A unit axis in the part's own space.</param>
        private void MeasureExtent(Vector3 localAxis, out float near, out float far)
        {
            Bounds box = LocalBounds();
            if (box.size.sqrMagnitude <= 1e-9f)
            {
                near = 0f;
                far = 0f;
                return;
            }

            float centre = Vector3.Dot(box.center, localAxis);
            Vector3 extents = box.extents;
            float reach = Mathf.Abs(extents.x * localAxis.x)
                        + Mathf.Abs(extents.y * localAxis.y)
                        + Mathf.Abs(extents.z * localAxis.z);
            near = centre - reach;
            far = centre + reach;
        }

        /// <summary>
        /// Everything this part draws, as one box in the part's own space, measured
        /// on first use after each reconcile.
        /// </summary>
        /// <remarks>
        /// Built from each mesh's own bounds rather than from Renderer.bounds.
        /// Renderer.bounds is a world-axis-aligned box, so for a part standing at an
        /// angle - which a wing on the side of a fuselage always is - it is a loose
        /// box around a diagonal shape, and the reach it reports along any one axis
        /// is inflated by however much of the other two leaked into it. Wings get
        /// measured against their own span to decide which axis that span runs
        /// along, and a wing whose chord and span are similar picks the wrong one.
        /// </remarks>
        private Bounds LocalBounds()
        {
            if (_localBoundsKnown) return _localBounds;
            _localBoundsKnown = true;
            _localBounds = new Bounds(Vector3.zero, Vector3.zero);

            Transform root = Part.transform;
            bool any = false;
            foreach (MeshFilter filter in Part.GetComponentsInChildren<MeshFilter>())
            {
                Renderer renderer = filter.GetComponent<Renderer>();
                if (renderer == null || !renderer.enabled) continue;

                // Attached parts are parented to this one's transform, so the search
                // above walks straight into them. A wing with another wing on its tip
                // would otherwise measure as twice its own length.
                if (filter.GetComponentInParent<Part>() != Part) continue;

                Mesh mesh = filter.sharedMesh;
                if (mesh == null) continue;
                Bounds local = mesh.bounds;
                if (local.size.sqrMagnitude <= 1e-9f) continue;

                // Every corner, because the mesh's own box arrives rotated into part
                // space and only its corners are still on it afterwards.
                Vector3 centre = local.center;
                Vector3 extents = local.extents;
                for (int corner = 0; corner < 8; corner++)
                {
                    var offset = new Vector3(
                        (corner & 1) == 0 ? -extents.x : extents.x,
                        (corner & 2) == 0 ? -extents.y : extents.y,
                        (corner & 4) == 0 ? -extents.z : extents.z);
                    Vector3 point = root.InverseTransformPoint(
                        filter.transform.TransformPoint(centre + offset));

                    if (!any) { _localBounds = new Bounds(point, Vector3.zero); any = true; }
                    else _localBounds.Encapsulate(point);
                }
            }
            return _localBounds;
        }

        /// <summary>True when <paramref name="part"/> hangs off the side of <paramref name="host"/>.</summary>
        private static bool IsSurfaceAttachedTo(Part part, Part host)
        {
            if (part == null || host == null) return false;
            AttachNode node = part.srfAttachNode;
            return node != null && node.attachedPart == host;
        }

        /// <summary>
        /// Build the stack joint between this part and <paramref name="neighbour"/>,
        /// or a default (null-Other) link when they are not joined that way.
        /// </summary>
        /// <remarks>
        /// Both sides have to be axial stack nodes. A pair where either side is a
        /// surface node, a docking node or a radial node is not a stack joint, and
        /// neither is one whose neighbour is not tracked.
        /// </remarks>
        private AxialLink StackLink(Part neighbour)
        {
            if (neighbour == null) return default;

            AttachNode mine = Part.FindAttachNodeByPart(neighbour);
            AttachNode theirs = neighbour.FindAttachNodeByPart(Part);
            if (mine == null || theirs == null) return default;          // surface attached
            if (mine.nodeType != AttachNode.NodeType.Stack) return default;
            if (theirs.nodeType != AttachNode.NodeType.Stack) return default;

            PartEnd myEnd = EndOf(mine);
            PartEnd theirEnd = EndOf(theirs);
            if (myEnd == PartEnd.None || theirEnd == PartEnd.None) return default;

            KspDimensionNode other = _resolve(neighbour);
            if (other == null) return default;

            return new AxialLink(other, myEnd, theirEnd, LinkKind.Stack);
        }

        /// <summary>
        /// Classify a stack attach node as the top or the bottom of its part.
        /// </summary>
        /// <remarks>
        /// The node's orientation is in the part's own space, which is the same
        /// space the "top"/"bottom" dimension fields are named for. Radial nodes
        /// point sideways and are rejected. Node ids are only consulted when the
        /// orientation is ambiguous, since plenty of parts use ids like
        /// "interstage" or "connect" for perfectly axial nodes.
        /// </remarks>
        public static PartEnd EndOf(AttachNode node)
        {
            if (node == null) return PartEnd.None;

            Vector3 orientation = node.orientation;
            if (orientation.sqrMagnitude > 1e-6f)
            {
                float y = orientation.normalized.y;
                if (y >= 0.9f) return PartEnd.Top;
                if (y <= -0.9f) return PartEnd.Bottom;
                return PartEnd.None;   // radial / angled node
            }

            string id = node.id?.ToLowerInvariant() ?? string.Empty;
            if (id.StartsWith("top")) return PartEnd.Top;
            if (id.StartsWith("bottom")) return PartEnd.Bottom;
            return PartEnd.None;
        }
    }
}
