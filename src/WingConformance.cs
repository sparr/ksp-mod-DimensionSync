using System.Collections.Generic;
using UnityEngine;

namespace DimensionSync
{
    /// <summary>
    /// The two things a wing joint needs that plain value propagation cannot say.
    /// </summary>
    /// <remarks>
    /// Everything else in this mod copies one number onto another. These two do
    /// not fit that shape:
    ///
    /// <list type="bullet">
    /// <item>A control surface's sweep is a RATE - B9 stores it as offset per unit
    /// of span - so the wing's own offset cannot simply be handed across. It has
    /// to be recomputed from the wing's whole planform, which is what B9's own
    /// "inherit" button does.</item>
    /// <item>A control surface's span grows from its middle rather than from its
    /// root, so making one longer moves the end that was against the fuselage.
    /// Holding that end still means moving the part, not writing a field.</item>
    /// </list>
    /// </remarks>
    internal static class WingConformance
    {
        /// <summary>B9's module name, the only one these rules know anything about.</summary>
        private const string WingModule = "WingProcedural";

        /// <summary>
        /// A B9 wing's chord axis in its own space, pointing toward its leading edge.
        /// </summary>
        private static readonly Vector3 ChordAxis = Vector3.up;

        /// <summary>A B9 wing's thickness axis in its own space.</summary>
        private static readonly Vector3 ThicknessAxis = Vector3.forward;

        /// <summary>
        /// Whether a part meets its host end to end rather than lying along it.
        /// </summary>
        /// <remarks>
        /// The same test the propagator uses: a part whose surface node faces along
        /// its own span is being joined tip to root, while one whose node faces
        /// across it is lying alongside.
        /// </remarks>
        private static bool IsEndToEnd(Part part, KspDimensionNode node)
        {
            AttachNode attach = part.srfAttachNode;
            if (attach == null || attach.orientation.sqrMagnitude <= 1e-6f) return false;
            return Mathf.Abs(Vector3.Dot(attach.orientation.normalized, node.SpanAxis())) > 0.7f;
        }

        /// <summary>
        /// Where one attached part sat on its host before a propagation, so the
        /// move afterwards can be worked out.
        /// </summary>
        private struct SpanSnapshot
        {
            /// <summary>The part's span when the snapshot was taken.</summary>
            public float Span;

            /// <summary>The HOST's span when the snapshot was taken.</summary>
            /// <remarks>
            /// Kept because a part's place on its host stops being valid when either
            /// span changes, not only when its own does. A flap whose wing has grown
            /// has not changed size at all and still needs moving.
            /// </remarks>
            public float HostSpan;

            /// <summary>The part this one is attached to.</summary>
            public Part Host;

            /// <summary>
            /// How far along the host's span the end being held sat, 0 at the host's
            /// root and 1 at its tip.
            /// </summary>
            public float Station;

            /// <summary>True when the end to hold is the far one rather than the near one.</summary>
            public bool HoldFarEnd;
        }

        /// <summary>Snapshots taken by <see cref="Snapshot"/>, consumed by <see cref="Reanchor"/>.</summary>
        private static readonly Dictionary<Part, SpanSnapshot> Snapshots =
            new Dictionary<Part, SpanSnapshot>();

        // =====================================================================
        // Holding an attached part's place on its host
        // =====================================================================

        /// <summary>
        /// Record where every side-by-side attached part sits on its host, before
        /// anything is propagated.
        /// </summary>
        /// <param name="nodes">Every part being tracked this frame.</param>
        /// <remarks>
        /// Taken for all of them rather than only the ones about to change, because
        /// which parts a walk will reach is not known until it has run.
        /// </remarks>
        /// <param name="pendingSpans">
        /// The span each host held BEFORE this frame's edits, for any host whose own
        /// span is one of them.
        /// </param>
        public static void Snapshot(Dictionary<Part, KspDimensionNode> nodes,
                                    Dictionary<Part, float> pendingSpans)
        {
            Snapshots.Clear();
            if (!DimensionSettings.AnchorSpanChanges) return;

            foreach (KeyValuePair<Part, KspDimensionNode> entry in nodes)
            {
                Part part = entry.Key;
                KspDimensionNode node = entry.Value;
                if (part == null || node == null) continue;

                // Only a part that is centred on its own origin can drift; one rooted
                // at its origin already grows away from its host.
                if (node.SpanBehindOrigin <= 0.01f) continue;

                // Only a part hanging off the side of something: a part standing on
                // its own has no place on a host to keep.
                KspDimensionNode host = HostOf(part, nodes);
                if (host == null) continue;
                if (!StillBelongsToItsHost(part, host)) continue;

                // A part whose OWN span somebody just edited is left where it is.
                // Anchoring is for keeping a part's place when its HOST changes shape
                // underneath it; a player dragging a flap's own length slider is not
                // that, and holding an end there means the flap can only ever grow
                // outboard. B9 grows it about its own centre, half each way, and that
                // is what the player is expecting to see.
                //
                // Only a DIRECT edit appears here - the mod's own writes are made
                // while propagation is guarded, so a length this mod gives a flap in
                // response to its wing growing does not count and still anchors.
                if (pendingSpans != null && pendingSpans.ContainsKey(part)) continue;

                float span = SpanOf(node);

                // The host's span as it was before the edit that set this
                // propagation off. The edit has already landed in the field by the
                // time we notice it, so reading the field now would measure this
                // part's place on a host that has already changed shape - and an
                // aileron on the outboard half of an 8 m wing would be recorded as
                // covering the middle third of a 12 m one.
                float hostSpan = pendingSpans != null
                                 && pendingSpans.TryGetValue(host.Part, out float before)
                    ? before
                    : SpanOf(host);
                if (float.IsNaN(span) || span <= 1e-3f) continue;
                if (float.IsNaN(hostSpan) || hostSpan <= 1e-3f) continue;

                node.SpanEnds(span, out Vector3 near, out Vector3 far);
                host.SpanEnds(hostSpan, out Vector3 hostRoot, out Vector3 hostTip);

                // Hold whichever end is nearer the host's root, since that is the end
                // the player lined up with whatever the host is mounted on.
                bool holdFar = (far - hostRoot).sqrMagnitude < (near - hostRoot).sqrMagnitude;
                if (DimensionSettings.Debug)
                    UnityEngine.Debug.Log($"{DimensionSyncAddon.LogTag} anchor: {node.Label} " +
                                          $"span {span:F3} behind {node.SpanBehindOrigin:F2} on " +
                                          $"{host.Label} span {hostSpan:F3}, station " +
                                          $"{StationOf(holdFar ? far : near, hostRoot, hostTip):F3}, " +
                                          $"holdFar {holdFar}");
                Snapshots[part] = new SpanSnapshot
                {
                    Span = span,
                    HostSpan = hostSpan,
                    Host = host.Part,
                    Station = StationOf(holdFar ? far : near, hostRoot, hostTip),
                    HoldFarEnd = holdFar,
                };
            }
        }

        /// <summary>
        /// Put every part whose span changed back on the stretch of its host it was
        /// on before.
        /// </summary>
        /// <param name="nodes">Every part being tracked this frame.</param>
        /// <remarks>
        /// The rule is that an attached part keeps its PLACE on its host, not its
        /// place in the world: an aileron on the outboard half of a wing is still on
        /// the outboard half after the wing has been lengthened. Holding a world
        /// position instead would leave it where the middle of the wing used to be.
        ///
        /// Worked out from the numbers rather than by re-measuring, because B9
        /// rebuilds its meshes a frame or two after the field is written and the
        /// bounds are still the old ones at this point.
        /// </remarks>
        public static void Reanchor(Dictionary<Part, KspDimensionNode> nodes)
        {
            if (Snapshots.Count == 0) return;

            foreach (KeyValuePair<Part, SpanSnapshot> entry in Snapshots)
            {
                Part part = entry.Key;
                SpanSnapshot before = entry.Value;
                if (part == null || !nodes.TryGetValue(part, out KspDimensionNode node)) continue;
                if (before.Host == null || !nodes.TryGetValue(before.Host, out KspDimensionNode host)) continue;

                float span = SpanOf(node);
                float hostSpan = SpanOf(host);

                // Every way out of here is logged. A part that is quietly skipped
                // looks exactly like a part that was corrected to no effect, and
                // telling those two apart by watching the ship is impossible.
                string skip = null;
                if (float.IsNaN(span) || span <= 1e-3f) skip = $"its own span reads {span:F3}";
                else if (float.IsNaN(hostSpan) || hostSpan <= 1e-3f) skip = $"host span reads {hostSpan:F3}";
                // Either span changing invalidates the part's place on its host.
                // Gating on the part's own span alone missed the commonest case
                // entirely: when a WING is lengthened, the flap's own span is changed
                // by propagation a frame later, so at this point it still reads as
                // unchanged and gets skipped - and by the frame it does change, the
                // station has already been re-measured against the wing's NEW length,
                // so the correction is to where the part already is. The baseline had
                // moved under it, exactly as the covered-fraction one used to.
                else if (Mathf.Abs(span - before.Span) <= 1e-4f
                         && Mathf.Abs(hostSpan - before.HostSpan) <= 1e-4f)
                    skip = $"span unchanged at {span:F3}, host span at {hostSpan:F3}";

                if (skip != null)
                {
                    if (DimensionSettings.Debug)
                        UnityEngine.Debug.Log($"{DimensionSyncAddon.LogTag} reanchor SKIPPED " +
                                              $"{node.Label}: {skip}");
                    continue;
                }

                node.SpanEnds(span, out Vector3 near, out Vector3 far);
                host.SpanEnds(hostSpan, out Vector3 hostRoot, out Vector3 hostTip);

                Vector3 held = before.HoldFarEnd ? far : near;

                if (DimensionSettings.Debug)
                {
                    Vector3 nearIn = host.Part.transform.InverseTransformPoint(near);
                    Vector3 farIn = host.Part.transform.InverseTransformPoint(far);
                    UnityEngine.Debug.Log($"{DimensionSyncAddon.LogTag} reanchor ends: {node.Label} " +
                                          $"near x={nearIn.x:F3} far x={farIn.x:F3} holdFar={before.HoldFarEnd} " +
                                          $"station={before.Station:F3} hostSpan={hostSpan:F2} " +
                                          $"spanAxis={node.SpanAxis()} behind={node.SpanBehindOrigin:F2}");
                }
                Vector3 wanted = Vector3.LerpUnclamped(hostRoot, hostTip, before.Station);

                // Only along the host's span. Where the part sits across the joint is
                // the player's business - a flap is meant to hang off the wing's
                // trailing edge, not to be dragged onto its centre line.
                Vector3 along = (hostTip - hostRoot).normalized;
                Vector3 correction = along * Vector3.Dot(wanted - held, along);
                if (DimensionSettings.Debug)
                    UnityEngine.Debug.Log($"{DimensionSyncAddon.LogTag} reanchor: {node.Label} " +
                                          $"span {before.Span:F3} -> {span:F3}, host span {hostSpan:F3}, " +
                                          $"station {before.Station:F3}, moving {correction}");
                if (correction.sqrMagnitude <= 1e-8f) continue;

                part.transform.position += correction;

                // attPos0 is what the editor restores the part to when it re-lays the
                // ship out, so a move that does not update it is undone on the next
                // symmetry or undo pass.
                if (part.attachMode == AttachModes.SRF_ATTACH)
                    part.attPos0 = part.transform.localPosition;
                EditorGizmo.PartMoved(part);
            }

            Snapshots.Clear();
        }

        /// <summary>How far along a host a point sits, 0 at its root and 1 at its tip.</summary>
        /// <remarks>Falls back to the root for a host with no length to divide by.</remarks>
        private static float StationOf(Vector3 point, Vector3 hostRoot, Vector3 hostTip)
        {
            Vector3 axis = hostTip - hostRoot;
            float length = axis.sqrMagnitude;
            return length <= 1e-8f ? 0f : Vector3.Dot(point - hostRoot, axis) / length;
        }

        // =====================================================================
        // Keeping a wing joint closed
        // =====================================================================

        /// <summary>
        /// Put every wing segment back on the tip of the segment inboard of it.
        /// </summary>
        /// <param name="nodes">Every part being tracked this frame.</param>
        /// <remarks>
        /// B9's own answer to a swept tip is to shear the next segment out to match,
        /// which closes the joint by changing the neighbour's shape. This closes it
        /// by moving the neighbour instead: the segment outboard keeps the planform
        /// it was given and simply follows the tip it is attached to. That also keeps
        /// the joint closed when the inboard segment's SPAN changes, which nothing
        /// else does.
        ///
        /// The wing's own numbers say where the joint should be. A B9 wing's chord
        /// straddles its origin, and B9 measures the two ends from opposite
        /// directions: the root chord's midline sits at +sharedBaseOffsetRoot along
        /// the part's chord axis, the tip chord's at -sharedBaseOffsetTip. So the
        /// outboard segment's origin belongs at its parent's tip midline, less
        /// wherever its own root midline sits relative to its origin.
        /// </remarks>
        public static void AlignWingJoints(Dictionary<Part, KspDimensionNode> nodes)
        {
            if (!DimensionSettings.AlignWingJoints) return;

            foreach (KeyValuePair<Part, KspDimensionNode> entry in nodes)
            {
                Part part = entry.Key;
                if (part == null || part.attachMode != AttachModes.SRF_ATTACH) continue;

                PartModule child = FindWingModule(part);
                PartModule parent = FindWingModule(part.parent);
                if (child == null || parent == null) continue;
                if (IsControlSurface(child) || IsControlSurface(parent)) continue;
                if (!nodes.TryGetValue(part, out KspDimensionNode node)) continue;
                if (!nodes.TryGetValue(part.parent, out KspDimensionNode host)) continue;
                if (!IsEndToEnd(part, node)) continue;

                float parentSpan = Read(parent, "sharedBaseLength");
                float parentOffsetTip = Read(parent, "sharedBaseOffsetTip");
                float childOffsetRoot = Read(child, "sharedBaseOffsetRoot");
                if (float.IsNaN(parentSpan) || float.IsNaN(parentOffsetTip)
                    || float.IsNaN(childOffsetRoot)) continue;

                Vector3 wanted = host.SpanAxis() * parentSpan
                                 + ChordAxis * (-parentOffsetTip - childOffsetRoot);
                if ((part.transform.localPosition - wanted).sqrMagnitude <= 1e-8f) continue;

                if (DimensionSettings.Debug)
                    UnityEngine.Debug.Log($"{DimensionSyncAddon.LogTag} wing joint: {node.Label} " +
                                          $"{part.transform.localPosition} -> {wanted}");

                part.transform.localPosition = wanted;
                part.attPos0 = wanted;
                EditorGizmo.PartMoved(part);
            }
        }

        // =====================================================================
        // Keeping a run of wings on one straight edge
        // =====================================================================

        /// <summary>Which of each wing joint's edges ran straight through it, before the change.</summary>
        private static readonly Dictionary<Part, EdgeMatch> Straight = new Dictionary<Part, EdgeMatch>();

        /// <summary>Whether a joint's leading and trailing edges were collinear.</summary>
        private struct EdgeMatch
        {
            /// <summary>The leading edges ran straight through the joint.</summary>
            public bool Leading;

            /// <summary>The trailing edges ran straight through the joint.</summary>
            public bool Trailing;
        }

        /// <summary>
        /// Record which wing joints had a straight edge running through them, before
        /// anything is changed.
        /// </summary>
        /// <param name="nodes">Every part being tracked this frame.</param>
        /// <param name="before">
        /// What each field about to change held BEFORE it changed, keyed by part and
        /// field name.
        /// </param>
        public static void NoteStraightEdges(Dictionary<Part, KspDimensionNode> nodes,
                                             Dictionary<PartField, float> before)
        {
            Straight.Clear();

            // Recorded first: other rules read pre-change values through this, and
            // they must not go blind because a setting they have nothing to do with
            // happens to be switched off.
            _before = before;
            if (!DimensionSettings.KeepWingEdgesStraight) return;

            foreach (KeyValuePair<Part, KspDimensionNode> entry in nodes)
            {
                Part part = entry.Key;
                if (part == null || part.attachMode != AttachModes.SRF_ATTACH) continue;

                PartModule child = FindWingModule(part);
                PartModule parent = FindWingModule(part.parent);
                if (child == null || parent == null) continue;
                if (IsControlSurface(child) || IsControlSurface(parent)) continue;
                if (!nodes.TryGetValue(part, out KspDimensionNode node) || !IsEndToEnd(part, node)) continue;

                // Judged on how things stood BEFORE the edit. The change that set
                // this propagation off has already landed in its field, so asking now
                // whether the two edges line up asks about a joint that has already
                // been bent - and the answer is always no, for the one joint that
                // matters most.
                Straight[part] = new EdgeMatch
                {
                    Leading = SlopesAgree(parent, child, trailing: false),
                    Trailing = SlopesAgree(parent, child, trailing: true),
                };
            }
        }

        /// <summary>
        /// Carry a change on through a run of wings whose edges form one straight
        /// line, so the shape they make together survives it.
        /// </summary>
        /// <param name="nodes">Every part being tracked this frame.</param>
        /// <remarks>
        /// Whether a chord crosses a joint is decided by the two parts being the same
        /// size, which is right for a stack of tanks and wrong for a wing. A delta
        /// built from three tapering segments has no two chords the same anywhere,
        /// and yet its leading edge is a single straight line - so narrowing one
        /// segment leaves a kink in it, and stopping there leaves the kink.
        ///
        /// What matters at a wing joint is whether the edge runs straight through it.
        /// Where it did, this puts the outboard segment's tip wherever it has to be
        /// for the edge to run straight again, and carries on outward. A triangle
        /// stays a triangle.
        /// </remarks>
        /// <param name="tipEdited">
        /// The wings whose tip chord or tip offset somebody just changed. Only those
        /// start a carry-through.
        /// </param>
        public static void KeepEdgesStraight(Dictionary<Part, KspDimensionNode> nodes,
                                             HashSet<Part> tipEdited)
        {
            if (!DimensionSettings.KeepWingEdgesStraight || Straight.Count == 0) return;

            // Sweeping a tip and narrowing it both bend the line the edges run
            // along, so both carry outward. What never crosses is the neighbour's
            // ROOT offset: closing the joint by shearing the neighbour is B9's own
            // answer and it changes a planform the player chose. The neighbour is
            // moved onto the tip instead, and only its far end is reshaped, which is
            // what continues the sweep rather than kinking it.
            var driving = new HashSet<Part>(tipEdited);

            // Inboard first: each segment's root is set from the segment before it,
            // so a run of them has to be walked in order or the far end is worked out
            // from a neighbour that has not been corrected yet.
            var joints = new List<Part>(Straight.Keys);
            joints.Sort((a, b) => DepthOf(a).CompareTo(DepthOf(b)));

            for (int i = 0; i < joints.Count; i++)
            {
                Part part = joints[i];
                if (part == null || !nodes.ContainsKey(part)) continue;

                EdgeMatch was = Straight[part];
                if (!was.Leading && !was.Trailing) continue;
                if (!driving.Contains(part.parent)) continue;

                PartModule child = FindWingModule(part);
                PartModule parent = FindWingModule(part.parent);
                if (child == null || parent == null) continue;

                // This segment's own tip has now moved, so whatever is outboard of it
                // is next: that is what carries a change along a run of wings.
                if (Continue(parent, child, part, was)) driving.Add(part);
            }
        }

        /// <summary>Run one joint's straight edges on into the outboard segment.</summary>
        /// <param name="parent">The inboard segment's module.</param>
        /// <param name="child">The outboard segment's module.</param>
        /// <param name="part">The outboard segment's part, for writing fields.</param>
        /// <param name="was">Which of the two edges were straight before the change.</param>
        /// <returns>True when the outboard segment's tip was actually moved.</returns>
        private static bool Continue(PartModule parent, PartModule child, Part part, EdgeMatch was)
        {
            float span = Read(child, "sharedBaseLength");
            if (float.IsNaN(span) || span <= 1e-3f) return false;

            // The root follows the tip it is bolted to, which is what makes this
            // cascade: the segment beyond this one is worked out from the tip set
            // here, not from the one it had before.
            float parentTip = Read(parent, "sharedBaseWidthTip");
            if (!float.IsNaN(parentTip)) Write(part, child, "sharedBaseWidthRoot", parentTip);

            float rootLead = EdgeAt(child, trailing: false, station: 0f);
            float rootTrail = EdgeAt(child, trailing: true, station: 0f);
            if (float.IsNaN(rootLead) || float.IsNaN(rootTrail)) return false;

            // Each edge continues at the inboard segment's slope where the two used
            // to line up, and at its own where they did not.
            float leadSlope = was.Leading ? SlopeOf(parent, trailing: false) : SlopeOf(child, trailing: false);
            float trailSlope = was.Trailing ? SlopeOf(parent, trailing: true) : SlopeOf(child, trailing: true);
            if (float.IsNaN(leadSlope) || float.IsNaN(trailSlope)) return false;

            float tipLead = rootLead + leadSlope * span;
            float tipTrail = rootTrail + trailSlope * span;

            // Chord is how far apart the two edges end up; offset is where their
            // midpoint lands. B9 measures a tip offset from the opposite direction to
            // a root one, hence the negation.
            float widthTip = tipLead - tipTrail;
            float offsetTip = -(tipLead + tipTrail) / 2f;

            // With only ONE edge collinear there is a spare degree of freedom, and it
            // is spent on the cheapest dimension rather than on the chord.
            //
            // Two collinear edges are two constraints against a tip's two numbers, so
            // the chord and the offset are both pinned and there is nothing to choose.
            // One collinear edge is one constraint, and the offset alone can meet it:
            // moving it shifts both edges together, which is exactly what turning the
            // whole tip about the root does. Solving both numbers anyway costs the
            // child its tip chord to hold an angle on an edge that was never lined up
            // with anything - on a cranked planform that halves a tip the player chose
            // and never asked to have changed.
            bool bothLinedUp = was.Leading && was.Trailing;
            if (!bothLinedUp)
            {
                float keepWidth = Read(child, "sharedBaseWidthTip");
                if (!float.IsNaN(keepWidth))
                {
                    // Solved from whichever edge has a line to hold.
                    float target = was.Leading ? tipLead : tipTrail;
                    float wanted = was.Leading
                        ? keepWidth / 2f - target
                        : -keepWidth / 2f - target;

                    // The offset is spent first, but only as far as B9 will carry it.
                    // Past that the chord has to make up the rest, which is the next
                    // dimension along and still cheaper than the span.
                    float limited = wanted;
                    if (LimitsOf(child, "sharedBaseOffsetTip", out float low, out float high)
                        && !float.IsNaN(low) && !float.IsNaN(high))
                        limited = Mathf.Clamp(wanted, low, high);

                    widthTip = Mathf.Abs(limited - wanted) <= 1e-5f
                        ? keepWidth
                        : (was.Leading ? 2f * (target + limited) : -2f * (target + limited));
                    offsetTip = limited;
                }
            }

            // The taper runs out INSIDE this segment: the two edges, continued at the
            // parent's angles, meet before they reach its tip. Rather than give up -
            // which leaves a kink at the joint and makes the result depend on how big
            // a step the change arrived in - the segment is shortened to the station
            // where its chord reaches B9's minimum. The edges then keep the parent's
            // angles for the whole of what is left of it, which is the planform the
            // player drew; there is simply less wing beyond the point where it closes.
            if (widthTip < MinimumChord(child))
            {
                float rootWidth = rootLead - rootTrail;

                // How fast the chord closes per metre of span. Negative when the edges
                // are converging, which is the only case that gets here.
                float closing = leadSlope - trailSlope;
                if (closing >= -1e-6f) return false;

                float reach = (MinimumChord(child) - rootWidth) / closing;

                // Nothing usefully left of this segment. Shortening it to a sliver
                // would be worse than leaving it: the player still has a part there
                // and would rather see it unchanged than reduced to nothing.
                if (float.IsNaN(reach) || reach <= MinimumSpan(child)) return false;
                if (reach >= span) return false;          // never lengthen from here

                span = reach;
                tipLead = rootLead + leadSlope * span;
                tipTrail = rootTrail + trailSlope * span;
                widthTip = tipLead - tipTrail;
                offsetTip = -(tipLead + tipTrail) / 2f;

                if (DimensionSettings.Debug)
                    UnityEngine.Debug.Log($"{DimensionSyncAddon.LogTag} straight edge: " +
                                          $"{part.partInfo?.title} taper runs out early, " +
                                          $"shortening span to {span:F3} to hold the parent's angles");

                Write(part, child, "sharedBaseLength", span);
            }

            if (DimensionSettings.Debug)
                UnityEngine.Debug.Log($"{DimensionSyncAddon.LogTag} straight edge: " +
                                      $"{part.partInfo?.title} tip chord -> {widthTip:F3}, " +
                                      $"offset -> {offsetTip:F3}");

            Write(part, child, "sharedBaseWidthTip", widthTip);
            Write(part, child, "sharedBaseOffsetTip", offsetTip);
            return true;
        }

        /// <summary>
        /// Whether a part's own root end is the one nearer its host's root.
        /// </summary>
        /// <param name="node">The attached part.</param>
        /// <param name="host">The part it is attached to.</param>
        /// <remarks>
        /// False for a part mounted end for end, which nothing stops a player doing
        /// and which reverses the meaning of every "root" and "tip" on it. Worked out
        /// by asking where the two ends actually ARE rather than by reading a
        /// rotation, because there is more than one way to end up reversed - turned
        /// end for end, or flipped over - and only the answer matters.
        /// </remarks>
        private static bool RootFacesTheHostsRoot(KspDimensionNode node, KspDimensionNode host)
        {
            float own = SpanOf(node);
            float hostSpan = SpanOf(host);
            if (float.IsNaN(own) || float.IsNaN(hostSpan) || own <= 1e-3f || hostSpan <= 1e-3f) return true;

            node.SpanEnds(own, out Vector3 near, out Vector3 far);
            host.SpanEnds(hostSpan, out Vector3 hostRoot, out _);

            return (near - hostRoot).sqrMagnitude <= (far - hostRoot).sqrMagnitude;
        }

        /// <summary>The slope of one of a wing's edges, across its own span.</summary>
        private static float SlopeOf(PartModule wing, bool trailing)
        {
            float span = Read(wing, "sharedBaseLength");
            if (float.IsNaN(span) || span <= 1e-3f) return float.NaN;

            float root = EdgeAt(wing, trailing, 0f);
            float tip = EdgeAt(wing, trailing, 1f);
            if (float.IsNaN(root) || float.IsNaN(tip)) return float.NaN;
            return (tip - root) / span;
        }

        /// <summary>A part and one of its field names, for looking up a previous value.</summary>
        internal struct PartField
        {
            /// <summary>The part the field is on.</summary>
            public Part Part;

            /// <summary>The KSPField's name.</summary>
            public string Name;
        }

        /// <summary>Previous values for the fields changing this frame, or null.</summary>
        /// <summary>
        /// The wing fields every pre-change rule reads through <see cref="ReadBefore"/>.
        /// </summary>
        private static readonly string[] BeforeFields =
        {
            "sharedBaseLength",
            "sharedBaseWidthRoot",
            "sharedBaseWidthTip",
            "sharedBaseOffsetRoot",
            "sharedBaseOffsetTip",
        };

        /// <summary>
        /// Record what every tracked wing holds, before anything is propagated.
        /// </summary>
        /// <remarks>
        /// Complete rather than only the fields somebody edited, and that is the whole
        /// point. <see cref="ReadBefore"/> falls back to a field's CURRENT value when
        /// nothing was recorded for it, which is right while nothing has moved yet and
        /// wrong the moment this mod starts writing: the rules that run after
        /// propagation then read "before" values that are really "after" ones, and a
        /// wing this mod resized looks to them as though it had always been that size.
        ///
        /// The visible symptom was a control surface on an OUTER wing that never
        /// followed its edge. The inner wing's tip chord was propagated to the outer
        /// wing's root correctly, and then the shift that should have moved the flap
        /// worked out as edgeHere minus edgeHere, which is nothing.
        ///
        /// This mirrors what <see cref="Snapshot"/> already does for spans, which is
        /// why span anchoring never had the problem: it walks every node rather than
        /// only the changed ones.
        ///
        /// Raw field units, matching what <see cref="Read"/> returns, so that a
        /// recorded value and a fallback value cannot mean different things.
        /// </remarks>
        /// <param name="nodes">Every part being tracked this frame.</param>
        /// <param name="before">Filled in; the caller overrides the entries it knows better.</param>
        public static void CaptureWingValues(Dictionary<Part, KspDimensionNode> nodes,
                                             Dictionary<PartField, float> before)
        {
            if (nodes == null || before == null) return;

            foreach (KeyValuePair<Part, KspDimensionNode> entry in nodes)
            {
                Part part = entry.Key;
                if (part == null) continue;

                PartModule module = FindWingModule(part);
                if (module == null) continue;

                for (int i = 0; i < BeforeFields.Length; i++)
                {
                    float value = Read(module, BeforeFields[i]);
                    if (float.IsNaN(value)) continue;
                    before[new PartField { Part = part, Name = BeforeFields[i] }] = value;
                }
            }
        }

        private static Dictionary<PartField, float> _before;

        /// <summary>A field's value as it stood before this frame's edit.</summary>
        private static float ReadBefore(PartModule module, string name)
        {
            if (_before != null
                && _before.TryGetValue(new PartField { Part = module.part, Name = name }, out float was))
                return was;
            return Read(module, name);
        }

        /// <summary>
        /// The slope of one of a wing's edges as it stood before this frame's edit.
        /// </summary>
        private static float SlopeBefore(PartModule wing, bool trailing)
        {
            float span = ReadBefore(wing, "sharedBaseLength");
            if (float.IsNaN(span) || span <= 1e-3f) return float.NaN;

            float offsetRoot = Fallback(ReadBefore(wing, "sharedBaseOffsetRoot"));
            float offsetTip = Fallback(ReadBefore(wing, "sharedBaseOffsetTip"));
            float widthRoot = ReadBefore(wing, "sharedBaseWidthRoot");
            float widthTip = ReadBefore(wing, "sharedBaseWidthTip");
            if (float.IsNaN(widthRoot) || float.IsNaN(widthTip)) return float.NaN;

            float root = trailing ? offsetRoot - widthRoot / 2f : offsetRoot + widthRoot / 2f;
            float tip = trailing ? -offsetTip - widthTip / 2f : -offsetTip + widthTip / 2f;
            return (tip - root) / span;
        }

        /// <summary>
        /// Where one of a wing's edges sat before this frame's edit.
        /// </summary>
        /// <param name="wing">The wing's WingProcedural module.</param>
        /// <param name="trailing">True for the trailing edge.</param>
        /// <param name="station">0 at the root, 1 at the tip.</param>
        private static float EdgeAtBefore(PartModule wing, bool trailing, float station)
        {
            float offsetRoot = Fallback(ReadBefore(wing, "sharedBaseOffsetRoot"));
            float offsetTip = Fallback(ReadBefore(wing, "sharedBaseOffsetTip"));
            float widthRoot = ReadBefore(wing, "sharedBaseWidthRoot");
            float widthTip = ReadBefore(wing, "sharedBaseWidthTip");
            if (float.IsNaN(widthRoot) || float.IsNaN(widthTip)) return float.NaN;

            float centre = Mathf.Lerp(offsetRoot, -offsetTip, station);
            float half = Mathf.Lerp(widthRoot, widthTip, station) / 2f;
            return trailing ? centre - half : centre + half;
        }

        /// <summary>Treat a missing offset as no offset.</summary>
        private static float Fallback(float value) => float.IsNaN(value) ? 0f : value;

        /// <summary>Whether two segments' matching edges run at the same slope.</summary>
        /// <remarks>
        /// Compared against the same tolerance that decides whether two parts are the
        /// same size, applied to the slope. Two edges a fraction of a degree apart
        /// were meant to be one line.
        /// </remarks>
        private static bool SlopesAgree(PartModule parent, PartModule child, bool trailing)
        {
            // The two edges have to MEET as well as run parallel. Two rectangular
            // wings of different chords have identical slopes and a step between
            // them; that is two edges, not one.
            float parentTip = ReadBefore(parent, "sharedBaseWidthTip");
            float childRoot = ReadBefore(child, "sharedBaseWidthRoot");
            if (float.IsNaN(parentTip) || float.IsNaN(childRoot)) return false;

            float scale = Mathf.Max(Mathf.Abs(parentTip), Mathf.Abs(childRoot));
            if (Mathf.Abs(parentTip - childRoot) > Mathf.Max(1e-4f, DimensionSettings.MatchTolerance * scale))
                return false;

            float one = SlopeBefore(parent, trailing);
            float other = SlopeBefore(child, trailing);
            if (float.IsNaN(one) || float.IsNaN(other)) return false;
            return Mathf.Abs(one - other) <= Mathf.Max(DimensionSettings.MatchTolerance, 1e-3f);
        }

        /// <summary>How many attachments separate a part from the ship's root.</summary>
        private static int DepthOf(Part part)
        {
            int depth = 0;
            for (Part walk = part?.parent; walk != null && depth < 200; walk = walk.parent) depth++;
            return depth;
        }

        // =====================================================================
        // Matching a control surface to the wing edge it is on
        // =====================================================================

        /// <summary>
        /// Lay every control surface back along the wing edge it is mounted on.
        /// </summary>
        /// <param name="nodes">Every part being tracked this frame.</param>
        /// <remarks>
        /// A wing whose two chords differ has swept edges, and a control surface on
        /// one of them has to follow. Three things move together, and doing any of
        /// them without the others makes the fit worse rather than better:
        ///
        /// <list type="number">
        /// <item>The part is TURNED so its hinge lies along the edge.</item>
        /// <item>It is MOVED so the hinge is on the edge rather than beside it.</item>
        /// <item>Its offsets are set to the edge's slope, which is what shears its
        /// inboard and outboard ends back to streamwise - B9 stores a control
        /// surface's offset as a sweep rate per metre of span, not as a distance.
        /// Setting that alone, without turning the part, simply skews it.</item>
        /// </list>
        /// </remarks>
        public static void MatchSweep(Dictionary<Part, KspDimensionNode> nodes,
                                      HashSet<Part> lengthEdited = null)
        {
            if (!DimensionSettings.MatchControlSurfaceSweep) return;

            foreach (KeyValuePair<Part, KspDimensionNode> entry in nodes)
            {
                Part part = entry.Key;
                if (part == null || part.attachMode != AttachModes.SRF_ATTACH) continue;

                PartModule surface = FindWingModule(part);
                PartModule wing = FindWingModule(part.parent);
                if (surface == null || wing == null) continue;
                if (!IsControlSurface(surface) || IsControlSurface(wing)) continue;

                // A control surface chained off the END of a wing is a different
                // joint - the plain span rules already handle that one.
                if (!nodes.TryGetValue(part, out KspDimensionNode node)) continue;
                if (!nodes.TryGetValue(part.parent, out KspDimensionNode host)) continue;
                if (IsEndToEnd(part, node)) continue;
                if (!StillBelongsToItsHost(part, host)) continue;

                Conform(part, node, host, surface, wing,
                        lengthEdited != null && lengthEdited.Contains(part));
            }
        }

        /// <summary>Turn, move and shear one control surface onto its wing's edge.</summary>
        /// <param name="part">The control surface part.</param>
        /// <param name="node">Its tracked node.</param>
        /// <param name="host">The wing's tracked node.</param>
        /// <param name="surface">The control surface's WingProcedural module.</param>
        /// <param name="wing">The wing's WingProcedural module.</param>
        private static void Conform(Part part, KspDimensionNode node, KspDimensionNode host,
                                    PartModule surface, PartModule wing, bool ownLengthEdited)
        {
            float span = Read(wing, "sharedBaseLength");
            if (float.IsNaN(span) || span <= 1e-3f) return;

            // Read before anything below updates it: this is the stretch the part is
            // already carrying, and it is what the flap's current length has to be
            // measured against to say what fraction of the wing it covers.
            float already = StretchAlreadyApplied(part, node, host);

            bool trailing = IsOnTrailingEdge(part, wing);
            float edgeRoot = EdgeAt(wing, trailing, 0f);
            float edgeTip = EdgeAt(wing, trailing, 1f);
            if (float.IsNaN(edgeRoot) || float.IsNaN(edgeTip)) return;

            // How the edge slopes across the wing, as rise over run. This is the
            // number B9 wants in the control surface's offset fields, and its arc
            // tangent is how far the part has to turn.
            float slope = (edgeTip - edgeRoot) / span;
            float wanted = Mathf.Atan(slope) * Mathf.Rad2Deg;

            // Where along the wing the surface starts, so the turn can be made about
            // the end that is meant to stay put.
            host.TryCoverage(part, out float from, out _);
            float edgeHere = Mathf.Lerp(edgeRoot, edgeTip, from);

            // FOLLOW THE EDGE. Widening a wing's root pushes its trailing edge back;
            // narrowing its tip pulls the edge forward. The surface has to travel
            // with it, and turning about a point on the edge does not do that on its
            // own - a turn only moves the part when there is an angle to turn
            // through, and a wing whose chords change together keeps the same slope
            // throughout. Left out, the flap sits skewed correctly in mid-air a
            // little way off the wing it is supposed to be hinged to.
            //
            // How far the edge moved, worked out from the wing's values before and
            // after this frame's edit rather than from anything remembered. A
            // remembered value has to be seeded, the seeding happens the first time
            // this runs, and the first time this runs is DURING the first edit - so
            // the very first move is always the one that gets missed, and the flap
            // carries that error for the rest of its life. Reading both sides of the
            // change cannot be seeded wrongly because it is not seeded at all.
            float shift = edgeHere - Mathf.Lerp(EdgeAtBefore(wing, trailing, 0f),
                                                EdgeAtBefore(wing, trailing, 1f), from);
            if (Mathf.Abs(shift) > 1e-4f)
            {
                part.transform.position += host.Part.transform.TransformDirection(ChordAxis) * shift;
                part.attPos0 = part.transform.localPosition;
                EditorGizmo.PartMoved(part);

                if (DimensionSettings.Debug)
                    UnityEngine.Debug.Log($"{DimensionSyncAddon.LogTag} edge moved: {node.Label} " +
                                          $"following its wing's {(trailing ? "trailing" : "leading")} " +
                                          $"edge {shift:F3} m along the chord");
            }

            // Taken after the move, so the part is actually touching the point it is
            // about to be turned about.
            Vector3 pivotLocal = host.SpanAxis() * (from * span) + ChordAxis * edgeHere;
            Vector3 pivot = host.Part.transform.TransformPoint(pivotLocal);

            // Only the CHANGE in the edge's slope is applied, because the part is
            // already lying at whatever slope the edge had when it was mounted.
            //
            // The first time a surface is seen, that starting slope is MEASURED off
            // the part rather than assumed to be zero. Assuming zero says "this part
            // is square to its wing", which is false for every control surface B9
            // mounts on a swept edge - B9 lays it along the edge already - so the
            // whole of the wing's sweep got applied a second time on top of the sweep
            // the part already had. On a 7 degree edge the part came out at 14, which
            // reads as a sign error and is really a double application. It showed up
            // whenever anything first touched a surface on a swept wing, including a
            // change to the surface's own length that should not have moved it at all.
            float applied = SweepApplied.TryGetValue(part, out float previous)
                ? previous
                : MountedSlopeOf(node, host);
            float turn = wanted - applied;

            if (Mathf.Abs(turn) > 0.01f)
            {
                // No sign flip for the leading edge: the two edges sweep opposite
                // ways for the same planform, and the slope measured above already
                // says so. Flipping again turns the leading surface the wrong way,
                // which looks right at the root - the end it pivots about - and is
                // out by twice the taper at the tip.
                Vector3 axis = host.Part.transform.TransformDirection(ThicknessAxis);
                Quaternion rotation = Quaternion.AngleAxis(turn, axis);

                part.transform.rotation = rotation * part.transform.rotation;
                part.transform.position = pivot + rotation * (part.transform.position - pivot);
                part.attPos0 = part.transform.localPosition;
                EditorGizmo.PartMoved(part);
                part.attRotation0 = part.transform.localRotation;

                if (DimensionSettings.Debug)
                    UnityEngine.Debug.Log($"{DimensionSyncAddon.LogTag} sweep: {node.Label} on " +
                                          $"{(trailing ? "trailing" : "leading")} edge, slope {slope:F4}, " +
                                          $"turning {turn:F2} deg about station {from:F2}");
            }

            SweepApplied[part] = wanted;

            // The shear, which is what pulls the surface's inboard and outboard ends
            // back to streamwise once it has been turned. It is expressed in the
            // surface's own frame, and the two edges' surfaces face opposite ways, so
            // the trailing one takes the edge's slope negated and the leading one
            // takes it as it stands.
            //
            // NOT B9's InheritCtrlOffset formula, which this used to copy. That
            // formula leaves sharedBaseOffsetRoot out of its arithmetic altogether,
            // so it agrees with the edge's actual slope only while the wing's root
            // offset is zero. Sweep the root and the two part company: the surface
            // gets turned to follow the real edge while being sheared to match a
            // different one, and its ends come out visibly askew. Deriving both from
            // the same slope keeps them consistent by construction, and reproduces
            // B9's own numbers exactly wherever B9's assumption holds.
            // Tracked as a CHANGE, like the turn above, and for the same reason: a
            // flap somebody placed by hand is rectangular whatever its wing is doing,
            // and a rectangular flap is what they meant to put there. Writing the
            // edge's whole slope in here instead sheared every flap the moment the
            // mod first touched it, including on edits that had nothing to do with
            // the wing's shape.
            //
            // Following the change still gives the behaviour worth having: place a
            // flap on a square wing, then sweep or taper the wing, and the flap
            // follows it. What it no longer does is decide that a flap already on a
            // swept wing was always meant to be sheared.
            // Both sides of the change are read, rather than one side remembered.
            // Remembering it does not work here: Conform only runs while something is
            // propagating, so the first time it sees a flap IS the first edit, and a
            // value seeded then records the slope the wing has already changed to.
            // The difference is zero for ever after and the flap never follows its
            // wing at all - which is precisely the trap the edge shift above avoids
            // the same way.
            // An ABSOLUTE target, worked out from a baseline remembered once, rather
            // than a change added to whatever the field happens to hold now.
            //
            // Adding a change to the current value cannot be run twice, and this does
            // run twice: KSP mirrors a field write to a part's symmetry counterparts
            // but does not mirror a transform, so writing one flap's offsets already
            // shears its twin, and the twin's own wing then comes round on a later
            // frame and the whole correction is made again. Worse, on the first of
            // those frames the twin's wing has NOT changed yet - so anything that
            // assumes the two sides are in step will shear it backwards.
            //
            // Written as a target, all of that stops mattering. Running twice writes
            // the same number twice, and a pass that catches a side mid-change writes
            // that side's own correct answer for the shape it currently has.
            if (!ShearBaseShear.ContainsKey(part))
            {
                float baseSlope = SlopeBefore(wing, trailing);
                ShearBaseShear[part] = Fallback(Read(surface, "sharedBaseOffsetRoot"));
                ShearBaseSlope[part] = float.IsNaN(baseSlope) ? slope : baseSlope;
            }

            bool endsSwapped = EndsSwapped(node, host);
            float shearChange = slope - ShearBaseSlope[part];
            float shearTarget = ShearBaseShear[part]
                                + (trailing ? -shearChange : shearChange) * (endsSwapped ? -1f : 1f);
            float shearNow = Fallback(Read(surface, "sharedBaseOffsetRoot"));

            if (Mathf.Abs(shearTarget - shearNow) > 1e-4f)
            {
                // A shear is written in the SURFACE's own frame, so which way round
                // that frame lies decides its sign. A flap mounted end for end, or one
                // B9 builds end for end because it considers it mirrored, needs the
                // opposite sign to give the same shape on screen - and the two cancel
                // when both are true, which is why an ordinary mirrored pair looks
                // right while a flap flipped on one wing does not.
                //
                // Getting it wrong is not a near miss: writing +s where -s belongs
                // leaves the flap skewed by twice the amount, the wrong way.
                Write(part, surface, "sharedBaseOffsetRoot", shearTarget);
                Write(part, surface, "sharedBaseOffsetTip", shearTarget);

                if (DimensionSettings.Debug)
                    UnityEngine.Debug.Log($"{DimensionSyncAddon.LogTag} shear: {node.Label} " +
                                          $"following its wing's edge slope " +
                                          $"{ShearBaseSlope[part]:F4} -> {slope:F4}, " +
                                          $"shear {shearNow:F4} -> {shearTarget:F4}" +
                                          (endsSwapped ? " (ends swapped)" : ""));
            }

            // The hinge is the hypotenuse of the span it covers, so a surface on a
            // swept edge has to be longer than the wing or it stops short of the tip.
            //
            // Every term here comes from the WING, and the covered fraction is the
            // one measured when this joint was first seen rather than a fresh
            // measurement. That matters: measuring the surface each time means
            // measuring something this rule has already lengthened, and a full-span
            // flap that reads as covering 1.02 of the wing gets 2% longer, then reads
            // 1.04, and so on. Over a couple of minutes of somebody reading a
            // walkthrough panel, that doubles it.
            float stretch = Mathf.Sqrt(1f + slope * slope);

            // Seed the fraction against the stretch that was ALREADY applied, not
            // the one about to be. This runs during the very change that sweeps the
            // edge, so measuring the flap against the new stretch would back out a
            // fraction that preserves its current, unstretched length - and it would
            // never grow at all.
            float fraction = CoveredFraction(part, surface, wing, already);
            float hinge = span * fraction * stretch;
            if (hinge <= 1e-3f) return;

            // What the field says NOW, deliberately, and not what it held before this
            // frame. By the time this runs the span channel has already propagated the
            // wing's new LENGTH into this surface, but nothing has yet applied the
            // stretch that comes from the new sweep - so the current value is the
            // length after the span change and before the stretch, and subtracting it
            // leaves exactly the stretch-driven growth this slide is meant to correct.
            //
            // The pre-frame value would be the wrong baseline: it still carries the
            // span change, which Reanchor has already accounted for by putting the
            // surface back on its station. Taking it out here as well slides the
            // surface a second time, by half the span growth, outboard.
            float lengthBefore = Read(surface, "sharedBaseLength");
            Write(part, surface, "sharedBaseLength", hinge);

            // Slide it by half of whatever it grew, so the end it was anchored by stays
            // where it was put.
            //
            // A surface is turned about the station it starts at, which leaves its
            // middle short of where it belongs by the sagitta of that turn, and B9 then
            // rebuilds the longer part around its own origin - growing it at BOTH ends
            // rather than out toward the tip. The two together leave it hanging inboard
            // by half the growth: a quarter of a metre of extra hinge puts it an eighth
            // of a metre too far toward the root, every time the wing's chord or sweep
            // changes.
            // Not when the player set that length themselves. Anchoring an end is for
            // a surface whose WING changed underneath it; a flap somebody is resizing
            // by hand grows about its own middle, which is what B9 does and what they
            // are watching it do.
            float grew = ownLengthEdited ? 0f : hinge - lengthBefore;
            if (float.IsNaN(grew) || Mathf.Abs(grew) <= 1e-4f) return;

            host.SpanEnds(span, out Vector3 hostRoot, out _);
            Vector3 outboard = part.transform.TransformDirection(node.SpanAxis());
            if (Vector3.Dot(outboard, part.transform.position - hostRoot) < 0f) outboard = -outboard;

            part.transform.position += outboard * (grew / 2f);
            if (part.attachMode == AttachModes.SRF_ATTACH) part.attPos0 = part.transform.localPosition;
            EditorGizmo.PartMoved(part);

            if (DimensionSettings.Debug)
                UnityEngine.Debug.Log($"{DimensionSyncAddon.LogTag} hinge: {node.Label} {lengthBefore:F3} -> " +
                                      $"{hinge:F3}, sliding {grew / 2f:F3} m outboard to keep the end " +
                                      "it was anchored by");
        }

        /// <summary>How much of its wing each control surface runs along, 0 to 1.</summary>
        private static readonly Dictionary<Part, float> Covered = new Dictionary<Part, float>();

        /// <summary>
        /// The fraction of its wing's span a control surface runs along, from the
        /// two parts' own length fields.
        /// </summary>
        /// <param name="surface">The control surface's WingProcedural module.</param>
        /// <param name="wing">Its wing's WingProcedural module.</param>
        /// <param name="stretch">How much longer the hinge is than the span it covers.</param>
        /// <remarks>
        /// Divided out of the fields rather than measured off the geometry. Measuring
        /// meant measuring a part this rule had already lengthened, which fed its own
        /// output back into its input: a full-span flap reading as covering 1.02 of
        /// its wing got 2% longer, then read 1.04, and so on until it had doubled.
        /// One field over another cannot do that.
        /// </remarks>
        private static float DeriveFraction(PartModule surface, PartModule wing, float stretch)
        {
            float flap = Read(surface, "sharedBaseLength");
            float span = Read(wing, "sharedBaseLength");
            if (float.IsNaN(flap) || float.IsNaN(span) || span <= 1e-3f || stretch <= 1e-3f) return 1f;
            return Mathf.Clamp(flap / (span * stretch), 0.01f, 1f);
        }

        /// <summary>
        /// The fraction to hold a control surface at: learnt the first time its joint
        /// is seen, and relearnt whenever somebody else changes its length.
        /// </summary>
        private static float CoveredFraction(Part part, PartModule surface, PartModule wing, float stretch)
        {
            if (Covered.TryGetValue(part, out float known)) return known;

            float fraction = DeriveFraction(surface, wing, stretch);
            Covered[part] = fraction;
            return fraction;
        }

        /// <summary>
        /// Take note of a control surface whose length somebody else just changed.
        /// </summary>
        /// <param name="part">The control surface.</param>
        /// <remarks>
        /// The fraction is the player's to set, not ours to keep. Our own writes are
        /// absorbed by the addon's recache and never come back as edits, so a change
        /// that reaches here IS somebody else's - the player dragging a flap out to a
        /// quarter of the wing, or another mod doing it. From then on a quarter is
        /// what gets maintained, and lengthening the wing takes the flap to a quarter
        /// of the NEW span rather than snapping it back to what it was.
        /// </remarks>
        public static void NoteLengthEdited(Part part)
        {
            if (part == null) return;

            PartModule surface = FindWingModule(part);
            PartModule wing = FindWingModule(part.parent);
            if (surface == null || wing == null) return;
            if (!IsControlSurface(surface) || IsControlSurface(wing)) return;

            Covered[part] = DeriveFraction(surface, wing, EdgeStretchOf(wing, part));

            if (DimensionSettings.Debug)
                UnityEngine.Debug.Log($"{DimensionSyncAddon.LogTag} control surface now covers " +
                                      $"{Covered[part]:P0} of its wing, set by hand");
        }

        /// <summary>
        /// How much longer than the span it covers a control surface's hinge has to
        /// be to lie along one of a wing's edges.
        /// </summary>
        /// <param name="host">The wing.</param>
        /// <param name="attached">The part lying along one of its edges.</param>
        /// <returns>1 when the two are not a wing and a control surface, or the edge is straight.</returns>
        /// <remarks>
        /// Everything here is read off the WING. That is what makes it safe to apply
        /// on every propagation: the answer does not depend on the control surface's
        /// current length, so it cannot compound.
        /// </remarks>
        internal static float EdgeStretch(KspDimensionNode host, Part attached)
        {
            if (host?.Part == null || attached == null) return 1f;

            PartModule wing = FindWingModule(host.Part);
            PartModule surface = FindWingModule(attached);
            if (wing == null || surface == null) return 1f;
            if (IsControlSurface(wing) || !IsControlSurface(surface)) return 1f;

            return EdgeStretchOf(wing, attached);
        }

        /// <summary>
        /// How much longer than the span it covers a hinge has to be to lie along
        /// the wing edge the given part is sitting on.
        /// </summary>
        private static float EdgeStretchOf(PartModule wing, Part attached)
        {
            float span = Read(wing, "sharedBaseLength");
            if (float.IsNaN(span) || span <= 1e-3f) return 1f;

            bool trailing = IsOnTrailingEdge(attached, wing);
            float root = EdgeAt(wing, trailing, 0f);
            float tip = EdgeAt(wing, trailing, 1f);
            if (float.IsNaN(root) || float.IsNaN(tip)) return 1f;

            float slope = (tip - root) / span;
            return Mathf.Sqrt(1f + slope * slope);
        }

        /// <summary>How far each control surface has already been turned, in degrees.</summary>
        private static readonly Dictionary<Part, float> SweepApplied = new Dictionary<Part, float>();

        /// <summary>Each control surface's shear at the moment it was first seen.</summary>
        private static readonly Dictionary<Part, float> ShearBaseShear = new Dictionary<Part, float>();

        /// <summary>The wing edge slope that baseline shear belongs to.</summary>
        private static readonly Dictionary<Part, float> ShearBaseSlope = new Dictionary<Part, float>();

        /// <summary>
        /// Whether a control surface is still lying against the wing it is attached to.
        /// </summary>
        /// <remarks>
        /// Somebody who drags a flap off its wing has said something about where they
        /// want it. Every rule in here exists to keep a surface arranged ON its edge,
        /// and applying them to one that is no longer there rearranges a part its owner
        /// deliberately moved: pull a flap clear of the wing, change the wing's span,
        /// and it gets dragged back into a formation it was taken out of.
        ///
        /// Measured across the CHORD, which is the direction a surface is pulled off an
        /// edge in. Sliding one along the span is a different thing and stays handled.
        ///
        /// Answers true whenever it cannot tell. A rule that stops working because a
        /// measurement went missing is worse than one that goes on doing what it always
        /// did.
        /// </remarks>
        /// <summary>
        /// Whether a control surface still belongs to the wing it is attached to.
        /// </summary>
        /// <remarks>
        /// Sticky, and set from an EVENT rather than worked out afresh each pass. KSP
        /// announces when somebody has finished offsetting a part, and that is the only
        /// moment the question can honestly be asked: at any other time a surface off
        /// its edge is just as likely to be one this pass has not finished moving, and
        /// the two cannot be told apart from geometry.
        ///
        /// An earlier attempt measured it every frame instead. It could not tell those
        /// apart, and because the answer latches, one wrong reading meant the surface
        /// was never corrected again and drifted further with every later change.
        /// </remarks>
        internal static bool StillBelongsToItsHost(Part part, KspDimensionNode host)
        {
            if (part == null || host?.Part == null) return true;
            return !PlacedByPlayer.Contains(part);
        }

        /// <summary>Surfaces the player has deliberately moved off their wings.</summary>
        private static readonly HashSet<Part> PlacedByPlayer = new HashSet<Part>();

        /// <summary>Parts whose position the player has just changed, awaiting a look.</summary>
        private static readonly HashSet<Part> JustMovedByPlayer = new HashSet<Part>();

        /// <summary>The player has finished moving or turning this part.</summary>
        /// <remarks>
        /// Recorded rather than acted on: this arrives mid-event, and where the part has
        /// ended up is better measured from a settled frame than from this one.
        /// </remarks>
        public static void NoteMovedByPlayer(Part part)
        {
            if (part != null) JustMovedByPlayer.Add(part);
        }

        /// <summary>This part has been attached afresh, so nothing is held against it.</summary>
        public static void NoteReattached(Part part)
        {
            if (part == null) return;
            PlacedByPlayer.Remove(part);
            JustMovedByPlayer.Remove(part);
        }

        /// <summary>
        /// Look at the parts the player has just moved and decide which have been taken
        /// off their wings.
        /// </summary>
        /// <param name="nodes">Every part being tracked this frame.</param>
        /// <remarks>
        /// Only the ones somebody just moved, and only once each. Everything else keeps
        /// whatever was decided last time, so no surface is abandoned because of how
        /// things looked in the middle of a change.
        ///
        /// A part moved BACK against its wing clears its own flag here, so this is not a
        /// one-way door: put a flap back and the rules take it up again.
        /// </remarks>
        public static void NotePlayerPlacements(Dictionary<Part, KspDimensionNode> nodes)
        {
            if (JustMovedByPlayer.Count == 0) return;

            foreach (Part part in JustMovedByPlayer)
            {
                if (part == null) continue;

                PartModule surface = FindWingModule(part);
                PartModule wing = FindWingModule(part.parent);
                if (surface == null || wing == null) continue;
                if (!IsControlSurface(surface) || IsControlSurface(wing)) continue;
                if (!nodes.TryGetValue(part.parent, out KspDimensionNode host)) continue;
                nodes.TryGetValue(part, out KspDimensionNode node);

                bool against = LiesAgainstItsWing(part, host, surface, wing);
                if (against) PlacedByPlayer.Remove(part);
                else PlacedByPlayer.Add(part);

                if (DimensionSettings.Debug)
                    UnityEngine.Debug.Log($"{DimensionSyncAddon.LogTag} the player moved " +
                                          $"{node?.Label ?? part.name}: " +
                                          (against ? "still against its wing, so the rules keep it"
                                                   : "off its wing, so it stays where they put it"));
            }
            JustMovedByPlayer.Clear();
        }

        /// <summary>Whether a control surface is lying against the wing it is attached to.</summary>
        private static bool LiesAgainstItsWing(Part part, KspDimensionNode host,
                                               PartModule surface, PartModule wing)
        {
            float widthRoot = Read(surface, "sharedBaseWidthRoot");
            float widthTip = Read(surface, "sharedBaseWidthTip");
            if (float.IsNaN(widthRoot) || float.IsNaN(widthTip)) return true;

            float halfChord = (widthRoot + widthTip) / 4f;
            if (halfChord <= 1e-3f) return true;

            bool trailing = IsOnTrailingEdge(part, wing);
            if (!host.TryCoverage(part, out float from, out float to)) return true;

            float edge = EdgeAt(wing, trailing, (from + to) / 2f);
            if (float.IsNaN(edge)) return true;

            float wanted = trailing ? edge - halfChord : edge + halfChord;
            float actual = host.Part.transform.InverseTransformPoint(BodyCentre(part)).y;
            return Mathf.Abs(actual - wanted) <= Mathf.Abs(halfChord * 2f * DimensionSettings.FlushTolerance);
        }


        /// <summary>
        /// Whether this part's root and tip lie the opposite way round from its host's.
        /// </summary>
        /// <remarks>
        /// Two separate things can swap a control surface's ends, and either one on its
        /// own flips the sign of anything written in the part's own frame: the player
        /// can mount it end for end, and B9 builds it end for end when it considers the
        /// part mirrored. SpanAxis already carries the second, so asking it which way
        /// the part runs answers both at once - and when both are true they cancel,
        /// which is exactly right, because a mirrored PAIR is mounted reversed as well.
        /// </remarks>
        private static bool EndsSwapped(KspDimensionNode node, KspDimensionNode host)
        {
            if (node?.Part == null || host?.Part == null) return false;

            Vector3 ours = host.Part.transform.InverseTransformDirection(
                node.Part.transform.TransformDirection(node.SpanAxis()));
            return Vector3.Dot(ours, host.SpanAxis()) < 0f;
        }

        /// <summary>
        /// The angle, in degrees, that a surface is ALREADY turned through relative to
        /// its host's span.
        /// </summary>
        /// <remarks>
        /// Measured from the two parts' transforms, so it reports where the part
        /// actually lies rather than what anything believes about it. Used to seed the
        /// sweep bookkeeping the first time a surface is seen: a part B9 mounted along
        /// a swept edge is already turned, and treating it as square doubles the sweep.
        ///
        /// A surface a player deliberately mounted NOT matching its wing reads as
        /// whatever angle it really has, so the first correction brings it onto the
        /// edge rather than adding to its existing angle.
        /// </remarks>
        private static float MountedSlopeOf(KspDimensionNode node, KspDimensionNode host)
        {
            if (node?.Part == null || host?.Part == null) return 0f;

            Vector3 ours = host.Part.transform.InverseTransformDirection(
                node.Part.transform.TransformDirection(node.SpanAxis()));
            Vector3 theirs = host.SpanAxis();

            // In the wing's frame the span runs along one axis and the chord along
            // another, so the turn between them is the angle in that plane.
            float along = Vector3.Dot(ours, theirs);
            float across = Vector3.Dot(ours, ChordAxis);
            if (Mathf.Abs(along) <= 1e-6f && Mathf.Abs(across) <= 1e-6f) return 0f;

            float degrees = Mathf.Atan2(across, along) * Mathf.Rad2Deg;

            // A span axis is a line, not an arrow: pointing it the other way describes
            // the same part lying the same way. Both axes here carry a sign that says
            // which END is the root - and on a mirrored control surface that sign is
            // flipped - so the raw angle can come back near 180 degrees for a part
            // sitting almost square to its wing. Folding it into (-90, 90] asks the
            // question that was meant: how far round is this part turned, never mind
            // which of its ends is called the root.
            if (degrees > 90f) degrees -= 180f;
            else if (degrees <= -90f) degrees += 180f;
            return degrees;
        }

        /// <summary>
        /// The hinge stretch implied by the turn this surface has already been given.
        /// </summary>
        /// <remarks>
        /// One for a surface that has not been turned yet, which is what makes a
        /// freshly seen flap measure as covering exactly the fraction of its wing it
        /// looks like it covers.
        /// </remarks>
        private static float StretchAlreadyApplied(Part part, KspDimensionNode node = null,
                                                   KspDimensionNode host = null)
        {
            // Falls back to the angle the part is MEASURED at, not to "no angle at
            // all". This runs before the part has any sweep bookkeeping, which is
            // exactly when the covered fraction gets worked out and cached - and a
            // fraction divided by a stretch of 1 when the part is really carrying 1.18
            // comes out 18% too big and stays that way. The flap is then rebuilt 18%
            // too long after every later change, which reads as a length that will not
            // follow its wing rather than as a fraction that was wrong once.
            //
            // It showed up worst on a wing swept AND tapered, where the two edges carry
            // different stretches: the trailing surface was out by half a percent and
            // the leading one by eighteen, from the same line of code.
            if (!SweepApplied.TryGetValue(part, out float degrees))
            {
                if (node == null || host == null) return 1f;
                degrees = MountedSlopeOf(node, host);
            }
            float cosine = Mathf.Cos(degrees * Mathf.Deg2Rad);
            return Mathf.Abs(cosine) > 1e-3f ? 1f / Mathf.Abs(cosine) : 1f;
        }

        /// <summary>
        /// Drop everything remembered about parts that are no longer on the ship.
        /// </summary>
        /// <param name="nodes">Every part being tracked this frame.</param>
        /// <remarks>
        /// These dictionaries are keyed by Part and would otherwise keep every part
        /// of every ship opened this session alive for as long as the editor runs.
        /// </remarks>
        public static void Forget(Dictionary<Part, KspDimensionNode> nodes)
        {
            Prune(SweepApplied, nodes);
            Prune(ShearBaseShear, nodes);
            Prune(ShearBaseSlope, nodes);
            Prune(Covered, nodes);
            Prune(LastStation, nodes);
        }

        /// <summary>Remove entries whose part is gone.</summary>
        private static void Prune(Dictionary<Part, float> remembered,
                                  Dictionary<Part, KspDimensionNode> nodes)
        {
            if (remembered.Count <= nodes.Count) return;

            var stale = new List<Part>();
            foreach (KeyValuePair<Part, float> entry in remembered)
                if (entry.Key == null || !nodes.ContainsKey(entry.Key)) stale.Add(entry.Key);
            for (int i = 0; i < stale.Count; i++) remembered.Remove(stale[i]);
        }

        /// <summary>
        /// Where one of a wing's edges sits on its chord axis, at a point along the
        /// span.
        /// </summary>
        /// <param name="wing">The wing's WingProcedural module.</param>
        /// <param name="trailing">True for the trailing edge.</param>
        /// <param name="station">0 at the root, 1 at the tip.</param>
        /// <remarks>
        /// Where the wing's BODY ends, not where its edge strip does: a control
        /// surface sits against the body with the strip overlapping it, so the two
        /// stay one continuous surface as the control deflects.
        ///
        /// The chord straddles the part's origin, and B9 measures the root offset in
        /// the opposite direction from the tip one, so the midline runs from
        /// +offsetRoot to -offsetTip.
        /// </remarks>
        private static float EdgeAt(PartModule wing, bool trailing, float station)
        {
            float offsetRoot = Read(wing, "sharedBaseOffsetRoot");
            float offsetTip = Read(wing, "sharedBaseOffsetTip");
            float widthRoot = Read(wing, "sharedBaseWidthRoot");
            float widthTip = Read(wing, "sharedBaseWidthTip");
            if (float.IsNaN(widthRoot) || float.IsNaN(widthTip)) return float.NaN;
            if (float.IsNaN(offsetRoot)) offsetRoot = 0f;
            if (float.IsNaN(offsetTip)) offsetTip = 0f;

            float centre = Mathf.Lerp(offsetRoot, -offsetTip, station);
            float half = Mathf.Lerp(widthRoot, widthTip, station) / 2f;
            return trailing ? centre - half : centre + half;
        }

        /// <summary>
        /// Which of the wing's two edges a control surface sits on.
        /// </summary>
        /// <returns>True for the trailing edge.</returns>
        /// <remarks>
        /// Decided by where the part actually is rather than by anything it declares.
        /// Its BODY is what is asked about, not its origin: the attach node sits on
        /// the face against the wing, which for a leading-edge surface on a wing with
        /// no leading strip is on the wing's own midline.
        /// </remarks>
        private static bool IsOnTrailingEdge(Part part, PartModule wing)
        {
            Vector3 offset = wing.part.transform.InverseTransformPoint(BodyCentre(part));
            float here = Vector3.Dot(offset, ChordAxis);

            // Measured against the middle of the wing's chord AT THIS STATION, not
            // against the wing's origin.
            //
            // The origin only sits between the two edges while the planform is roughly
            // centred on it. Offset the tip far enough and the whole wing slides along
            // the chord axis, taking both edges to one side of the origin - and a flap
            // still sitting squarely on the trailing edge is then read as a LEADING
            // edge one. Everything downstream follows that: it is sheared to the wrong
            // edge's slope, turned about the wrong line and moved to the wrong place,
            // which is why one more notch of tip offset put the control surfaces
            // inside the wing.
            float span = Read(wing, "sharedBaseLength");
            float lead = float.NaN, trail = float.NaN;
            if (!float.IsNaN(span) && span > 1e-3f)
            {
                // The wing as it stood BEFORE this frame's change, because that is the
                // wing the flap is still positioned against. Its own move has not been
                // made yet when this is asked, so measuring it against the wing's new
                // shape compares a position from one moment with a planform from
                // another - and a flap sitting where the old trailing edge was can
                // land on the far side of the new mid-chord and be read as a leading
                // edge one. That is what put the surfaces inside the wing on the step
                // that REDUCED the sweep.
                float station = Mathf.Clamp01(Vector3.Dot(offset, SpanAxisOf(wing)) / span);
                lead = EdgeAtBefore(wing, trailing: false, station: station);
                trail = EdgeAtBefore(wing, trailing: true, station: station);
            }
            if (float.IsNaN(lead) || float.IsNaN(trail)) return here <= 0f;

            return here <= (lead + trail) / 2f;
        }

        /// <summary>
        /// The axis a wing's span runs along, in its own local space.
        /// </summary>
        /// <remarks>
        /// B9 builds every wing out along its local x, whichever way the part is turned
        /// afterwards, so this is a property of the module rather than of how the part
        /// has been placed.
        /// </remarks>
        private static Vector3 SpanAxisOf(PartModule wing)
        {
            return Vector3.right;
        }

        /// <summary>The middle of everything a part draws, in world space.</summary>
        /// <remarks>Falls back to the part's origin when it draws nothing.</remarks>
        private static Vector3 BodyCentre(Part part)
        {
            var box = new Bounds(part.transform.position, Vector3.zero);
            bool any = false;
            foreach (Renderer renderer in part.GetComponentsInChildren<Renderer>())
            {
                if (!renderer.enabled || renderer is ParticleSystemRenderer) continue;
                if (renderer.bounds.size.sqrMagnitude <= 1e-9f) continue;
                if (renderer.GetComponentInParent<Part>() != part) continue;
                if (!any) { box = renderer.bounds; any = true; }
                else box.Encapsulate(renderer.bounds);
            }
            return box.center;
        }

        // =====================================================================
        // Refitting a control surface the player resized
        // =====================================================================

        /// <summary>
        /// Bring a control surface back into agreement with its wing after somebody
        /// changed its own length.
        /// </summary>
        /// <param name="nodes">Every part being tracked this frame.</param>
        /// <param name="lengthEdited">Control surfaces whose own span somebody just changed.</param>
        /// <remarks>
        /// Every other rule here reacts to the WING changing. This one reacts to the
        /// flap changing: shortening an aileron leaves it covering a different
        /// stretch of the wing, and the wing is tapered, so the thickness it had at
        /// its old tip is not the thickness the wing has where its new tip lands. The
        /// wing has not moved, so nothing else notices.
        ///
        /// The inboard end stays where it is - the flap grows and shrinks from the
        /// end the player was not dragging - so only the far end's station changes.
        /// </remarks>
        public static void RefitAfterLengthEdit(Dictionary<Part, KspDimensionNode> nodes,
                                                HashSet<Part> lengthEdited)
        {
            if (!DimensionSettings.MatchControlSurfaceSweep || lengthEdited.Count == 0) return;
            Refit(nodes, lengthEdited, ownLengthEdited: true);
        }

        /// <summary>Where each control surface was last seen sitting on its wing.</summary>
        private static readonly Dictionary<Part, float> LastStation = new Dictionary<Part, float>();

        /// <summary>
        /// Bring a control surface back into agreement with its wing after somebody
        /// slid it along the wing.
        /// </summary>
        /// <param name="nodes">Every part being tracked this frame.</param>
        /// <remarks>
        /// Moving a flap outboard along a tapered wing changes nothing whatsoever
        /// about any field, on it or on the wing - so none of the change-driven rules
        /// here can see it happen, and the flap is left the thickness it was at the
        /// place it used to be. This watches where each surface sits instead of what
        /// it holds, which is the only way to notice.
        /// </remarks>
        public static void RefitMovedSurfaces(Dictionary<Part, KspDimensionNode> nodes)
        {
            if (!DimensionSettings.MatchControlSurfaceSweep) return;

            var moved = new HashSet<Part>();
            foreach (KeyValuePair<Part, KspDimensionNode> entry in nodes)
            {
                Part part = entry.Key;
                if (part == null || part.attachMode != AttachModes.SRF_ATTACH) continue;
                if (!nodes.TryGetValue(part.parent ?? part, out KspDimensionNode host)) continue;
                if (!host.TryCoverage(part, out float from, out _)) continue;

                if (!LastStation.TryGetValue(part, out float was))
                {
                    LastStation[part] = from;
                    continue;
                }

                // Measured against where the part was last REFITTED, not against
                // where it was a moment ago, and the baseline only moves when a refit
                // actually happens. Advancing it every time meant a part dragged
                // slowly never triggered anything: each step was under the threshold,
                // the reference crept along behind it, and the flap ended up far from
                // where it started having never been refitted once. Worse, by then
                // its thickness no longer matched the wing anywhere, so the ordinary
                // rules could not reach it either and it was orphaned for good.
                //
                // The threshold is now only wide enough to ignore floating-point
                // noise. Anything larger has to be justified by measuring how much
                // this figure moves on its own, which nobody has done - and a
                // too-wide threshold does real harm, as above, while a too-narrow one
                // only costs a recalculation that writes back the same numbers.
                if (Mathf.Abs(from - was) <= DimensionSettings.ChangeEpsilon) continue;

                LastStation[part] = from;
                moved.Add(part);
            }

            if (moved.Count > 0) Refit(nodes, moved);
        }

        /// <summary>Re-derive the thickness of each of these control surfaces from its wing.</summary>
        private static void Refit(Dictionary<Part, KspDimensionNode> nodes, HashSet<Part> surfaces,
                                  bool ownLengthEdited = false)
        {
            foreach (Part part in surfaces)
            {
                if (part == null || part.attachMode != AttachModes.SRF_ATTACH) continue;

                PartModule surface = FindWingModule(part);
                PartModule wing = FindWingModule(part.parent);
                if (surface == null || wing == null) continue;
                if (!IsControlSurface(surface) || IsControlSurface(wing)) continue;
                if (!nodes.TryGetValue(part.parent, out KspDimensionNode host)) continue;
                if (!nodes.TryGetValue(part, out KspDimensionNode node) || IsEndToEnd(part, node)) continue;
                if (!StillBelongsToItsHost(part, host)) continue;
                float wingRoot = Read(wing, "sharedBaseThicknessRoot");
                float wingTip = Read(wing, "sharedBaseThicknessTip");
                if (float.IsNaN(wingRoot) || float.IsNaN(wingTip)) continue;

                // Where it sat before, from the measurement taken at the last reconcile.
                if (!host.TryCoverage(part, out float from, out float previousTo)) { from = 0f; previousTo = 1f; }

                // Falls back to the stretch the SURFACE is carrying, not the one its
                // wing's edge has right now. The two agree while nothing is changing,
                // and this runs during a change - so on the one pass where it matters,
                // the wing's figure is the new one while the surface's length is still
                // the old one, and the fraction comes out wrong and is then cached.
                // The same pairing was what made a flap grow 18% too long in Conform.
                float covers = Covered.TryGetValue(part, out float known)
                    ? known
                    : DeriveFraction(surface, wing, StretchAlreadyApplied(part, node, host));

                float to;
                if (ownLengthEdited)
                {
                    // A part whose own length somebody just changed keeps its MIDDLE,
                    // because B9 builds the new mesh around the part's origin and the
                    // origin does not move - so both ends move by half the change.
                    // Reading the old inboard station as if it had stayed put puts the
                    // whole footprint half the change too far inboard, and the flap
                    // takes the thickness belonging to a stretch of wing it is no
                    // longer over.
                    float middle = (from + previousTo) / 2f;
                    from = Mathf.Clamp01(middle - covers / 2f);
                    to = Mathf.Clamp01(middle + covers / 2f);
                }
                else
                {
                    // A part that SLID keeps the end it was measured from.
                    to = Mathf.Clamp01(from + covers);
                }

                // WHICH END IS WHICH. A control surface does not have to be attached
                // the same way round as its wing: turn it end for end and its own
                // root is at the wing's TIP. Assuming otherwise gives every thickness
                // the value belonging to the opposite end, so thickening a wing's
                // root thins the flap where it meets that root - which is exactly
                // backwards, and looks like a sign error rather than a mix-up about
                // which end is being talked about.
                bool rootIsInboard = RootFacesTheHostsRoot(node, host);

                float atRoot = Mathf.Lerp(wingRoot, wingTip, rootIsInboard ? from : to);
                float atTip = Mathf.Lerp(wingRoot, wingTip, rootIsInboard ? to : from);

                if (DimensionSettings.Debug)
                    UnityEngine.Debug.Log($"{DimensionSyncAddon.LogTag} refit: control surface covers " +
                                          $"{from:F2} to {to:F2} of its wing, its own root at the " +
                                          $"{(rootIsInboard ? "inboard" : "OUTBOARD")} end, taking " +
                                          $"thickness {atRoot:F3} at its root and {atTip:F3} at its tip");

                Write(part, surface, "sharedBaseThicknessRoot", atRoot);
                Write(part, surface, "sharedBaseThicknessTip", atTip);
            }
        }

        // =====================================================================
        // Small helpers
        // =====================================================================

        /// <summary>
        /// How much of a B9 wing part's span lies behind its own origin, when that is
        /// known from what the part IS rather than from measuring it.
        /// </summary>
        /// <param name="part">The part to identify.</param>
        /// <returns>0 for a wing, 0.5 for a control surface, NaN for anything else.</returns>
        /// <remarks>
        /// Measuring this from renderer bounds is unsafe for a control surface,
        /// because the shear that pulls its ends back to streamwise displaces those
        /// bounds ALONG THE SPAN - which is the very axis being measured. A surface
        /// on a swept leading edge can carry enough shear to move its apparent centre
        /// by most of its own length, and everything that positions it then works
        /// from the wrong end of it.
        ///
        /// B9 builds both kinds to a fixed rule: a wing runs from its origin out to
        /// its tip, a control surface straddles its origin. Saying so is exact, and
        /// no amount of shear can make it wrong.
        /// </remarks>
        internal static float KnownSpanOrigin(Part part)
        {
            PartModule module = FindWingModule(part);
            if (module == null) return float.NaN;
            return IsControlSurface(module) ? 0.5f : 0f;
        }

        /// <summary>
        /// Which way along its own axis a B9 wing part runs, from its root to its tip.
        /// </summary>
        /// <param name="part">The part to identify.</param>
        /// <returns>Zero when the part is not one of B9's, so the caller measures.</returns>
        /// <remarks>
        /// A wing runs from its origin outward, so its root is at the near end of its
        /// own +X. A control surface straddles its origin, and which of its two ends
        /// carries sharedBaseWidthRoot is decided in B9's mesh code by the sign of a
        /// coordinate - and swapped again when the part is mirrored, so that a
        /// mirrored pair looks symmetric while holding identical field values.
        ///
        /// Getting this backwards does not look like an axis problem. It looks like a
        /// tapered wing handing its flap the wrong thickness at each end: both values
        /// correct, both on the wrong end.
        /// </remarks>
        internal static Vector3 KnownSpanAxis(Part part)
        {
            PartModule module = FindWingModule(part);
            if (module == null) return Vector3.zero;

            // MEASURED, not reasoned about. pwings_probe_which_end_is_root gives a
            // control surface a wide root and a narrow tip and asks the mesh which of
            // its ends came out wide: the answer is local -X. SpanEnds puts the root
            // at minus the axis, so the axis is +X - the same as a wing's, and the
            // same as the descriptors already declare.
            //
            // A wing is always laid out that way. A control surface is not: B9 builds
            // its mesh with root and tip exchanged - thickness, chord and offset alike
            // - whenever it considers the part mirrored, so on those the root sits at
            // +X and the axis is reversed. B9 swaps its own sliders and drag handles to
            // match, so this follows what the part actually looks like, which is the
            // only thing the player can see.
            //
            // In an ordinary mirrored pair the part is ALSO mounted with its span
            // reversed, and the two cancel. That is why symmetric pairs have always
            // come out right, and why this shows only on a lone control surface placed
            // on the far side of the craft in an editor that defaults to mirror
            // symmetry - which the SPH does and the VAB does not.
            //
            // An earlier version returned -X for every control surface and flipped
            // again on mirroring, which is wrong in both states. It appeared to fix one
            // saved craft, which is a good reminder that one craft agreeing with a
            // change is not evidence the change is right.
            return IsControlSurface(module) && IsMirrored(module)
                ? -Vector3.right
                : Vector3.right;
        }

        /// <summary>Whether B9 is building this part's mesh end-for-end.</summary>
        /// <remarks>
        /// Read by reflection rather than through Fields: unlike isCtrlSrf beside it,
        /// isMirrored carries no KSPField attribute, which is why B9 has to write it to
        /// the craft by hand under the name mirrorTexturing. The name undersells it -
        /// on a wing it only picks vertex colours, but on a control surface it swaps
        /// root and tip in the geometry.
        /// </remarks>
        private static bool IsMirrored(PartModule module)
        {
            System.Reflection.FieldInfo field = module.GetType().GetField(
                "isMirrored",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            return field != null && field.GetValue(module) is bool mirrored && mirrored;
        }

        /// <summary>The part's WingProcedural module, or null if it has none.</summary>
        private static PartModule FindWingModule(Part part)
        {
            if (part == null) return null;
            for (int i = 0; i < part.Modules.Count; i++)
            {
                PartModule module = part.Modules[i];
                if (module != null && module.GetType().Name == WingModule) return module;
            }
            return null;
        }

        /// <summary>Whether a WingProcedural module is configured as a control surface.</summary>
        private static bool IsControlSurface(PartModule module)
        {
            BaseField field = module.Fields["isCtrlSrf"];
            object value = field?.GetValue(module);
            return value is bool flag && flag;
        }

        /// <summary>The tracked node of whatever a part is surface-attached to.</summary>
        private static KspDimensionNode HostOf(Part part, Dictionary<Part, KspDimensionNode> nodes)
        {
            if (part.attachMode != AttachModes.SRF_ATTACH || part.parent == null) return null;
            return nodes.TryGetValue(part.parent, out KspDimensionNode host) ? host : null;
        }

        /// <summary>A node's span, from whichever of its slots carries that channel.</summary>
        private static float SpanOf(KspDimensionNode node)
        {
            for (int i = 0; i < node.SlotList.Count; i++)
            {
                IDimensionSlot slot = node.SlotList[i];
                if (slot.Descriptor.Channel == Channels.Span) return slot.Value;
            }
            return float.NaN;
        }

        /// <summary>Read a float field off a module, or NaN when it is not there.</summary>
        private static float Read(PartModule module, string name)
        {
            BaseField field = module.Fields[name];
            if (field == null) return float.NaN;
            return KspDimensionSlot.TryUnbox(field.GetValue(module), out float value) ? value : float.NaN;
        }

        /// <summary>
        /// B9's own ceilings for a control surface, which are far tighter than the
        /// ones it allows a wing.
        /// </summary>
        /// <remarks>
        /// B9 keeps one limit pair per field and picks the second half of it for a
        /// control surface - sharedBaseWidthRootLimits is (0.01, 40) for a wing but
        /// (0.01, 2) for a control surface. It only applies them when its own editor
        /// panel is open, so a value outside them survives happily until the player
        /// hovers the part and presses J, at which point it is clamped and the part
        /// visibly jumps. Staying inside them means we never set that up.
        /// </remarks>
        private static readonly Dictionary<string, Vector2> ControlSurfaceLimits =
            new Dictionary<string, Vector2>
            {
                { "sharedBaseLength", new Vector2(0f, 20f) },
                { "sharedBaseWidthRoot", new Vector2(0.01f, 2f) },
                { "sharedBaseWidthTip", new Vector2(0f, 2f) },
                { "sharedBaseOffsetRoot", new Vector2(-1.5f, 1.5f) },
                { "sharedBaseOffsetTip", new Vector2(-1.5f, 1.5f) },
                { "sharedBaseThicknessRoot", new Vector2(0.01f, 4f) },
                { "sharedBaseThicknessTip", new Vector2(0.01f, 4f) },
            };

        /// <summary>
        /// One of B9's own limits for a field on this part, or NaN if unreadable.
        /// </summary>
        /// <remarks>
        /// B9 keeps a Vector4 per field: the wing's min and max in x and y, the
        /// control surface's in z and w. Read rather than copied, because a copy is a
        /// second place for the number to live and B9's config can change it.
        /// </remarks>
        private static bool LimitsOf(PartModule module, string name, out float min, out float max)
        {
            min = max = float.NaN;
            if (module == null) return false;

            System.Reflection.FieldInfo field = module.GetType().GetField(
                name + "Limits",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.Static);
            if (!(field?.GetValue(null) is Vector4 limits)) return false;

            bool control = IsControlSurface(module);
            min = control ? limits.z : limits.x;
            max = control ? limits.w : limits.y;
            return true;
        }

        /// <summary>The narrowest tip B9 will let this part have.</summary>
        private static float MinimumChord(PartModule module)
        {
            return LimitsOf(module, "sharedBaseWidthTip", out float min, out _) && !float.IsNaN(min)
                ? Mathf.Max(min, 0.01f)
                : 0.01f;
        }

        /// <summary>The shortest span worth leaving a segment at.</summary>
        /// <remarks>
        /// Not B9's floor, which is zero: a segment shortened to nothing is a part the
        /// player can no longer see or grab. This is the point below which leaving it
        /// alone is the kinder answer.
        /// </remarks>
        private static float MinimumSpan(PartModule module)
        {
            return 0.05f;
        }

        /// <summary>
        /// Write a float field through the same part-action-window protocol the rest
        /// of the mod uses, so B9 rebuilds the mesh.
        /// </summary>
        private static void Write(Part part, PartModule module, string name, float value)
        {
            BaseField field = module.Fields[name];
            if (field == null) return;

            if (IsControlSurface(module) && ControlSurfaceLimits.TryGetValue(name, out Vector2 limit))
                value = Mathf.Clamp(value, limit.x, limit.y);
            if (KspDimensionSlot.TryUnbox(field.GetValue(module), out float current)
                && Mathf.Abs(current - value) <= 1e-5f) return;

            DimensionSyncAddon.SetFieldLikeUI(part, module, field, value);
        }
    }
}
