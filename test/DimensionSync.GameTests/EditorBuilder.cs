using System;
using System.Reflection;
using UnityEngine;

namespace DimensionSync.GameTests
{
    /// <summary>
    /// Builds stacks of real parts in the editor without going through the mouse.
    /// </summary>
    /// <remarks>
    /// The wiring mirrors what <c>EditorLogic.attachPart</c> and
    /// <c>ShipConstruct.LoadShip</c> do: parent/child links, both ends of the
    /// attach node pair, and <c>Part.onAttach</c>.
    /// </remarks>
    internal static class EditorBuilder
    {
        /// <summary>
        /// EditorLogic.rootPart is private and there is no setter, but the editor
        /// refuses to behave without it, so the harness reaches in.
        /// </summary>
        private static readonly FieldInfo RootPartField =
            typeof(EditorLogic).GetField("rootPart", BindingFlags.Instance | BindingFlags.NonPublic);

        /// <summary>Everything this class has created, so a scenario can be torn down cleanly.</summary>
        private static readonly System.Collections.Generic.List<Part> Spawned =
            new System.Collections.Generic.List<Part>();

        /// <summary>Whether a part this harness built has since been destroyed.</summary>
        /// <remarks>
        /// Deleting a fixture part mid-scenario is a reasonable thing to do while
        /// looking at one, and the scenario cannot carry on afterwards - it holds
        /// references to parts that are gone. Knowing that is the difference between
        /// saying so and showing a stack trace.
        /// </remarks>
        public static bool AnySpawnedPartMissing()
        {
            for (int i = 0; i < Spawned.Count; i++)
                if (Spawned[i] == null) return true;
            return false;
        }

        /// <summary>
        /// Put the editor's move and rotate gizmos back on the parts they are attached
        /// to, after this harness has moved the ship.
        /// </summary>
        /// <remarks>
        /// A gizmo sits where its part was when it latched on and does not follow. The
        /// mod keeps them in step when IT moves a part; this is for the harness lifting
        /// the whole ship clear of the floor, which the mod knows nothing about and
        /// which otherwise leaves the handles behind in mid-air.
        /// </remarks>
        public static void FollowGizmos()
        {
            foreach (string name in new[] { "EditorGizmos.GizmoOffset", "EditorGizmos.GizmoRotate" })
            {
                Type type = null;
                foreach (System.Reflection.Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    type = assembly.GetType(name);
                    if (type != null) break;
                }
                if (type == null) continue;

                const System.Reflection.BindingFlags flags =
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
                    | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static;

                foreach (UnityEngine.Object found in UnityEngine.Object.FindObjectsOfType(type))
                {
                    if (!(found is Component gizmo)) continue;
                    if (!(type.GetField("host", flags)?.GetValue(gizmo) is Transform host)) continue;
                    if (host == null) continue;
                    if (type.GetField("isDragging", flags)?.GetValue(gizmo) is bool dragging && dragging)
                        continue;

                    gizmo.transform.position = host.position;
                    if (type.GetField("coordSpace", flags)?.GetValue(null) is Space space
                        && space == Space.Self)
                        gizmo.transform.rotation = host.rotation;
                }
            }
        }

        /// <summary>Remove every part from the current ship.</summary>
        /// <summary>
        /// Load a craft the player saved, and adopt its parts so they are cleared up.
        /// </summary>
        /// <remarks>
        /// Every fixture in this suite is built part by part, which keeps scenarios
        /// independent of how KSP lays a craft out but also means none of them can
        /// reproduce a problem that only appears in something a PERSON built. Loading
        /// the real file is the only honest way to test one of those.
        ///
        /// The loaded parts are added to the spawned list on purpose. ClearShip only
        /// destroys what it knows about, so without this a craft's parts survive into
        /// the next scenario - visible, selectable, and quietly wrong.
        /// </remarks>
        /// <returns>True when the craft loaded and has parts.</returns>
        public static bool LoadCraft(string name)
        {
            string saves = System.IO.Path.Combine(KSPUtil.ApplicationRootPath, "saves");
            if (!System.IO.Directory.Exists(saves)) return false;

            string path = null;
            foreach (string file in System.IO.Directory.GetFiles(saves, "*.craft",
                                                                 System.IO.SearchOption.AllDirectories))
            {
                if (System.IO.Path.GetFileNameWithoutExtension(file)
                        .Equals(name, System.StringComparison.OrdinalIgnoreCase))
                {
                    path = file;
                    break;
                }
            }
            if (path == null) return false;

            EditorLogic.LoadShipFromFile(path);

            ShipConstruct ship = EditorLogic.fetch?.ship;
            if (ship == null || ship.parts.Count == 0) return false;

            _floorSettled = false;
            _reserved = 0f;
            for (int i = 0; i < ship.parts.Count; i++)
                if (ship.parts[i] != null && !Spawned.Contains(ship.parts[i]))
                    Spawned.Add(ship.parts[i]);
            return true;
        }

        public static void ClearShip()
        {
            _floorSettled = false;
            _reserved = 0f;

            // Backwards, because destroying a part takes its children with it and
            // parts were spawned parents-first.
            ShipConstruct ship = EditorLogic.fetch.ship;
            for (int i = Spawned.Count - 1; i >= 0; i--)
            {
                Part part = Spawned[i];
                if (part == null) continue;
                part.setParent();
                ship.Remove(part);
                UnityEngine.Object.DestroyImmediate(part.gameObject);
            }
            Spawned.Clear();
            ship.parts.Clear();
            RootPartField?.SetValue(EditorLogic.fetch, null);
            GameEvents.onEditorShipModified.Fire(ship);
        }

        /// <summary>
        /// Create a part but leave it out of the ship, so tests can dial in its
        /// starting dimensions before DimensionSync has anything to propagate to.
        /// </summary>
        public static Part Spawn(string partName)
        {
            return Instantiate(partName, Vector3.zero);
        }

        /// <summary>Make an already-spawned part the ship's root.</summary>
        public static void SetRoot(Part part)
        {
            if (part == null) return;
            RootPartField?.SetValue(EditorLogic.fetch, part);
            AddToShip(part);
            ReleaseEditorStartupLock();
            GameEvents.onEditorPartEvent.Fire(ConstructionEventType.PartCreated, part);
            GameEvents.onEditorShipModified.Fire(EditorLogic.fetch.ship);
        }

        /// <summary>Stack an already-spawned part on top of <paramref name="below"/>.</summary>
        /// <remarks>The new part becomes the child, so the stack grows upward from its root.</remarks>
        public static bool StackOnTop(Part below, Part part) => Stack(below, part, hostTop: true);

        /// <summary>Stack an already-spawned part underneath <paramref name="above"/>.</summary>
        /// <remarks>
        /// The new part is still the child, so this builds a stack whose root is at
        /// the TOP and whose children run downward - the mirror of
        /// <see cref="StackOnTop"/>. Which end of a stack is the root and which way a
        /// change travels along it are separate things, and this is what lets a test
        /// vary one without varying the other.
        /// </remarks>
        public static bool StackBelow(Part above, Part part) => Stack(above, part, hostTop: false);

        /// <summary>Join a part to one end of another with their axial attach nodes.</summary>
        /// <param name="host">The part being attached to, which becomes the parent.</param>
        /// <param name="part">The part being attached, which becomes the child.</param>
        /// <param name="hostTop">True to use the host's top node, false for its bottom.</param>
        private static bool Stack(Part host, Part part, bool hostTop)
        {
            if (host == null || part == null) return false;

            AttachNode theirNode = FindEndNode(host, top: hostTop, free: true);
            if (theirNode == null)
            {
                Harness.LogError($"{host.name} has no free {(hostTop ? "top" : "bottom")} node");
                return false;
            }

            // The two parts meet end to end, so the child offers the opposite end.
            AttachNode myNode = FindEndNode(part, top: !hostTop, free: true);
            if (myNode == null)
            {
                Harness.LogError($"{part.name} has no free {(hostTop ? "bottom" : "top")} node");
                return false;
            }

            // Line the two nodes up so the stack looks like a stack.
            part.transform.rotation = host.transform.rotation;
            Vector3 nodeWorld = host.transform.TransformPoint(theirNode.position);
            part.transform.position = nodeWorld - part.transform.TransformVector(myNode.position);

            part.setParent(host);
            part.transform.parent = host.transform;
            myNode.attachedPart = host;
            theirNode.attachedPart = part;
            part.attachMode = AttachModes.STACK;
            part.attPos0 = part.transform.localPosition;
            part.attRotation0 = part.transform.localRotation;
            part.isAttached = true;

            AddToShip(part);
            part.onAttach(host);
            // The editor leaves a held part's modules disabled and re-enables them on
            // attach; do the same or the part behaves as though it were still on the
            // cursor.
            for (int i = 0; i < part.Modules.Count; i++) part.Modules[i].enabled = true;

            GameEvents.onEditorPartEvent.Fire(ConstructionEventType.PartAttached, part);
            GameEvents.onEditorShipModified.Fire(EditorLogic.fetch.ship);
            return true;
        }

        /// <summary>
        /// Surface-attach an already-spawned part to <paramref name="host"/>, landing
        /// its surface node on <paramref name="hostLocalPoint"/> and facing it back
        /// toward the host.
        /// </summary>
        /// <param name="hostLocalPoint">
        /// Where on the host the joint should be, in the host's own space: the side
        /// of a tank, or the tip of the wing segment inboard of this one.
        /// </param>
        /// <remarks>
        /// The part is rotated so its surface node faces back toward the host, then
        /// translated until that node lands on the requested point. Getting this
        /// right matters because ProceduralParts works out where to push
        /// surface-attached children from the node's position in its own cylindrical
        /// coordinates, so a joint that is only approximately right sends the child
        /// somewhere unrelated as soon as the host is resized.
        ///
        /// Which way the node faces is taken from where it sits on the part rather
        /// than from its orientation vector. ProceduralParts keeps a surface node on
        /// the part's surface as the part is resized, but MoveNode only ever writes
        /// the node's position - the orientation is left at whatever the part config
        /// said. On a procedural tank the two end up contradicting each other: the
        /// node sits at (0, 0, +radius) while its orientation still reads (0, 0, -1).
        /// Trusting the orientation there attaches the booster a quarter turn out,
        /// and it then swings round the tank when the tank is widened.
        /// </remarks>
        public static bool SurfaceAttach(Part host, Part part, Vector3 hostLocalPoint)
        {
            // Inward, treating the host as a body of revolution about its own Y.
            // Points on that axis have no inward direction, so fall back to
            // something arbitrary but consistent.
            Vector3 outward = Vector3.ProjectOnPlane(hostLocalPoint, Vector3.up);
            Vector3 facing = outward.sqrMagnitude > 1e-6f ? -outward.normalized : Vector3.back;
            return SurfaceAttach(host, part, hostLocalPoint, facing);
        }

        /// <summary>
        /// Surface-attach with the direction back into the host given explicitly,
        /// for hosts that are not bodies of revolution.
        /// </summary>
        /// <param name="hostLocalFacing">
        /// Which way, in the host's own space, the child's surface node should point.
        /// A control surface on a wing's trailing edge faces back into the wing along
        /// the chord, which the cylindrical guess above cannot work out.
        /// </param>
        public static bool SurfaceAttach(Part host, Part part, Vector3 hostLocalPoint, Vector3 hostLocalFacing)
        {
            if (host == null || part == null) return false;

            AttachNode node = part.srfAttachNode;
            if (node == null)
            {
                Harness.LogError($"{part.name} has no surface attach node");
                return false;
            }

            Vector3 facing = hostLocalFacing.sqrMagnitude > 1e-6f
                ? hostLocalFacing.normalized
                : Vector3.back;

            // Where the node sits relative to the part's own centre is the direction
            // it faces; the orientation vector is only trustworthy for a node at the
            // origin, which is how a wing's root is defined.
            Vector3 nodeOffset = Vector3.ProjectOnPlane(node.position, Vector3.up);
            Vector3 nodeFacing = nodeOffset.sqrMagnitude > 1e-4f
                ? nodeOffset.normalized
                : (node.orientation.sqrMagnitude > 1e-6f ? node.orientation.normalized : Vector3.back);

            part.transform.rotation = host.transform.rotation * Quaternion.FromToRotation(nodeFacing, facing);

            Vector3 targetWorld = host.transform.TransformPoint(hostLocalPoint);
            Vector3 nodeWorld = part.transform.TransformPoint(node.position);
            part.transform.position += targetWorld - nodeWorld;

            return Fasten(host, part, node);
        }

        /// <summary>
        /// Record a surface attachment on both parts and tell the editor about it,
        /// once the part has already been put where it belongs.
        /// </summary>
        /// <returns>Always true, so callers can return it directly.</returns>
        private static bool Fasten(Part host, Part part, AttachNode node)
        {
            // ShipConstruct.LoadShip sets owner explicitly too; without it the mods
            // that reposition surface-attached children transform through the wrong
            // part and scatter them.
            node.owner = part;
            node.attachedPart = host;

            part.setParent(host);
            part.transform.parent = host.transform;
            part.attachMode = AttachModes.SRF_ATTACH;
            part.attPos0 = part.transform.localPosition;
            part.attRotation0 = part.transform.localRotation;
            part.isAttached = true;

            AddToShip(part);
            part.onAttach(host);
            // The editor leaves a held part's modules disabled and re-enables them on
            // attach; do the same or the part behaves as though it were still held.
            for (int i = 0; i < part.Modules.Count; i++) part.Modules[i].enabled = true;

            GameEvents.onEditorPartEvent.Fire(ConstructionEventType.PartAttached, part);
            GameEvents.onEditorShipModified.Fire(EditorLogic.fetch.ship);
            return true;
        }

        /// <summary>
        /// Surface-attach a part so it lies ALONGSIDE its host - a control surface
        /// on a wing's edge rather than a booster on a tank's side.
        /// </summary>
        /// <param name="host">The part being attached to.</param>
        /// <param name="part">The part being attached.</param>
        /// <param name="hostLocalContact">
        /// A point on the host's edge, in the host's own space, where the two should
        /// touch. Only its position across the joint matters; the placement along the
        /// joint comes from <paramref name="inboardStation"/>.
        /// </param>
        /// <param name="hostLocalFacing">Which way, in host space, is back into the host.</param>
        /// <param name="hostLocalAlong">Which way, in host space, the joint runs outboard.</param>
        /// <param name="inboardStation">
        /// How far along <paramref name="hostLocalAlong"/>, from the host's own
        /// origin, the attached part's inboard end should sit.
        /// </param>
        /// <remarks>
        /// The two orientations have to be pinned separately here. The plain
        /// <see cref="SurfaceAttach(Part, Part, Vector3, Vector3)"/> only lines the
        /// surface node up with the host's surface, which leaves the part free to
        /// spin about that node - fine for a booster, useless for a flap, which also
        /// has to run the same way as the wing.
        ///
        /// Placement is then done from the part's measured bounds rather than from
        /// its node, because a part's origin is not reliably at its own corner. A B9
        /// control surface is centred on its node along its span and hangs off it
        /// along its chord, so landing the node on the contact point puts half the
        /// flap inboard of where it was asked for and drops it below the edge.
        /// Measuring says where the part actually is and removes the guesswork.
        /// </remarks>
        public static bool AttachAlongEdge(Part host, Part part, Vector3 hostLocalContact,
                                           Vector3 hostLocalFacing, Vector3 hostLocalAlong,
                                           float inboardStation, bool reversed = false, bool upsideDown = false)
        {
            // A player can mount a control surface end for end, or flipped over, and
            // nothing stops them. Both are ordinary things to do and both change
            // which of the part's own ends is where - so a harness that can only
            // build one orientation cannot test what happens with the others.
            if (reversed) hostLocalAlong = -hostLocalAlong;
            if (upsideDown) hostLocalFacing = -hostLocalFacing;


            if (host == null || part == null) return false;

            AttachNode node = part.srfAttachNode;
            if (node == null)
            {
                Harness.LogError($"{part.name} has no surface attach node");
                return false;
            }

            // The caller's ALONG is kept exactly and the facing is squared up to it,
            // rather than the other way round. A joint direction that follows a sloping
            // edge carries a component in the facing direction - that slope IS the
            // tilt - so projecting the along onto the plane perpendicular to the facing
            // deletes precisely the thing the caller asked for, and the part comes out
            // square to a wing it was meant to lie along. The editor turns a control
            // surface to the edge it snaps to, so a square one is a shape no player
            // could produce, and every later measurement is taken against it.
            Vector3 along = hostLocalAlong.normalized;
            Vector3 facing = Vector3.ProjectOnPlane(hostLocalFacing, along).normalized;
            if (along.sqrMagnitude <= 1e-6f || facing.sqrMagnitude <= 1e-6f)
            {
                Harness.LogError("the joint direction and the facing direction are parallel");
                return false;
            }

            Vector3 nodeAxis = node.orientation.sqrMagnitude > 1e-6f
                ? node.orientation.normalized
                : Vector3.up;
            Vector3 spanAxis = LongestAxisPerpendicularTo(part, nodeAxis);

            // The measured axis is used as it comes. An earlier version negated it
            // for control surfaces, to match a mod-side convention that turned out to
            // be the wrong way round; pwings_probe_which_end_is_root settles it by
            // measurement instead.

            // Map the part's own two axes onto the host's two directions at once.
            // LookRotation(forward, up) takes +Z to forward and +Y to up, so building
            // it for each frame and composing gives the rotation between them.
            Quaternion fromPart = Quaternion.LookRotation(spanAxis, nodeAxis);
            Quaternion toHost = Quaternion.LookRotation(along, facing);
            part.transform.rotation = host.transform.rotation * toHost * Quaternion.Inverse(fromPart);

            // Land the node on the contact point first, then slide the part along
            // each of the three directions until its measured extent is where it
            // should be.
            Vector3 targetWorld = host.transform.TransformPoint(hostLocalContact);
            part.transform.position += targetWorld - part.transform.TransformPoint(node.position);

            Vector3 facingWorld = host.transform.TransformDirection(facing);
            Vector3 alongWorld = host.transform.TransformDirection(along);
            Vector3 acrossWorld = Vector3.Cross(facingWorld, alongWorld).normalized;

            Bounds localBox = LocalBounds(part);
            Vector3 contactWorld = host.transform.TransformPoint(hostLocalContact);

            // Chord: the part's trailing-most point along the facing direction is the
            // edge that has to touch the host.

            Extent(part, localBox, facingWorld, out _, out float partFar);
            part.transform.position += facingWorld * (Vector3.Dot(contactWorld, facingWorld) - partFar);

            // Span: measured along the HOST's own span axis, never along the joint
            // direction. On a sloping edge those are different lines, and a station is
            // a fraction of the WING's length - so measuring along the tilted joint
            // puts the part out by the whole taper times the part's reach, which on a
            // full-span flap is most of half a metre and leaves it hanging past the
            // tip.
            //
            // The middle is what gets placed, rather than an end. The part's reach
            // along the joint is its own length; how much of the WING that covers is
            // that length projected back onto the span axis, and its middle belongs
            // half of that from the station it starts at.
            Vector3 spanDir = Vector3.ProjectOnPlane(along, hostLocalFacing.normalized).normalized;
            Vector3 spanWorld = host.transform.TransformDirection(spanDir);

            // The part's own DECLARED length where it has one, not its bounding box. A
            // B9 surface's box is bigger than its span - the rounded edge strips stand
            // proud of it - and half of that surplus lands straight in the middle this
            // is about to place, which is a sixth of a metre out on a four metre flap.
            Extent(part, localBox, alongWorld, out float jointNear, out float jointFar);
            float jointLength = jointFar - jointNear;
            float declared = PartFields.Get(part, "WingProcedural", "sharedBaseLength");
            if (!float.IsNaN(declared) && declared > 1e-3f) jointLength = declared;
            float covered = jointLength * Vector3.Dot(spanDir, along);
            float wantedCentre = inboardStation + (reversed ? -covered : covered) / 2f;

            // Slid ALONG THE EDGE, by the distance that lands the middle on that
            // station. Sliding along the wing's span axis instead would be the shorter
            // path and the wrong one: the edge's chord position changes with span, so
            // a part moved straight along the span comes off the edge it was just set
            // against. Moving along the joint keeps it in contact and costs only a
            // longer move - one over the cosine of the edge's angle.
            Vector3 centreWorld = host.transform.TransformPoint(spanDir * wantedCentre);
            Extent(part, localBox, spanWorld, out float spanNear, out float spanFar);
            float alongSpan = Vector3.Dot(spanDir, along);
            if (Mathf.Abs(alongSpan) > 1e-6f)
            {
                float shortfall = Vector3.Dot(centreWorld, spanWorld) - (spanNear + spanFar) / 2f;
                part.transform.position += alongWorld * (shortfall / alongSpan);
            }

            // Thickness: centred on the host's own mid-plane.
            Extent(part, localBox, acrossWorld, out float acrossNear, out float acrossFar);
            part.transform.position += acrossWorld *
                (Vector3.Dot(contactWorld, acrossWorld) - (acrossNear + acrossFar) / 2f);

            return Fasten(host, part, node);
        }

        /// <summary>The centre of a part's rendered body, in world space.</summary>
        /// <remarks>
        /// For aiming at a part rather than at its origin, which for a wing sits on the
        /// join with whatever it is mounted to and is as likely to hit that instead.
        /// Renderer bounds are world-axis-aligned and so the wrong shape for measuring
        /// anything, but their CENTRE is not distorted by rotation, and the centre is
        /// all this needs.
        /// </remarks>
        public static Vector3 BodyCentreOf(Part part)
        {
            if (part == null) return Vector3.zero;

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
            return any ? box.center : part.transform.position;
        }

        /// <summary>
        /// Of the part's three local axes, the one perpendicular to
        /// <paramref name="exclude"/> that the part reaches furthest along.
        /// </summary>
        /// <remarks>
        /// A control surface is far longer than it is thick, so the longer of its two
        /// remaining axes is its span. Measured rather than declared, because which
        /// local axis B9 uses for what is not something the part config says.
        /// </remarks>
        private static Vector3 LongestAxisPerpendicularTo(Part part, Vector3 exclude)
        {
            Bounds box = LocalBounds(part);
            Vector3 best = Vector3.forward;
            float longest = -1f;

            foreach (Vector3 axis in new[] { Vector3.right, Vector3.up, Vector3.forward })
            {
                if (Mathf.Abs(Vector3.Dot(axis, exclude)) > 0.7f) continue;

                // The box is already in the part's own space, so a cardinal axis
                // reads straight off it.
                float reach = Mathf.Abs(Vector3.Dot(box.size, axis));
                if (reach <= longest) continue;
                longest = reach;
                best = axis;
            }
            return best;
        }

        /// <summary>
        /// How far a part reaches along a world direction, from its measured shape.
        /// </summary>
        /// <param name="part">The part to measure.</param>
        /// <param name="localBox">Its shape in its own space, from <see cref="LocalBounds"/>.</param>
        /// <param name="direction">A world-space direction.</param>
        /// <param name="near">The lower end of the part's shadow on that direction.</param>
        /// <param name="far">The upper end.</param>
        /// <remarks>
        /// Every corner of the box is transformed and projected, rather than the box
        /// being reduced to a centre and a reach. A part standing at an angle - which
        /// a wing on the side of a fuselage always is - has a local box whose axes do
        /// not line up with the direction being asked about, and the shortcut
        /// overstates its reach by however much of the other two axes leaks in.
        /// </remarks>
        private static void Extent(Part part, Bounds localBox, Vector3 direction,
                                   out float near, out float far)
        {
            near = float.PositiveInfinity;
            far = float.NegativeInfinity;

            Vector3 centre = localBox.center;
            Vector3 extents = localBox.extents;
            for (int corner = 0; corner < 8; corner++)
            {
                var offset = new Vector3(
                    (corner & 1) == 0 ? -extents.x : extents.x,
                    (corner & 2) == 0 ? -extents.y : extents.y,
                    (corner & 4) == 0 ? -extents.z : extents.z);
                float along = Vector3.Dot(part.transform.TransformPoint(centre + offset), direction);
                near = Mathf.Min(near, along);
                far = Mathf.Max(far, along);
            }
        }

        /// <summary>
        /// Which way along a part's own X axis its ROOT-dimension end lies, measured
        /// from the mesh rather than assumed.
        /// </summary>
        /// <param name="part">The part to measure.</param>
        /// <returns>
        /// +1 when the end carrying the larger chord is at positive local X, -1 when
        /// it is at negative X, 0 when the two ends cannot be told apart.
        /// </returns>
        /// <remarks>
        /// The caller sets the part's root and tip chords to obviously different
        /// numbers first; this then finds which end of the mesh got the wide one.
        /// That is the only way to answer the question without reading B9's mesh
        /// generator and reasoning about how its coordinates map onto the part's -
        /// which is exactly the reasoning that has been wrong twice.
        /// </remarks>
        public static int WideEndAlongLocalX(Part part)
        {
            if (part == null) return 0;

            Transform root = part.transform;
            var points = new System.Collections.Generic.List<Vector3>();

            foreach (MeshFilter filter in part.GetComponentsInChildren<MeshFilter>())
            {
                Renderer renderer = filter.GetComponent<Renderer>();
                if (renderer == null || !renderer.enabled) continue;
                if (filter.GetComponentInParent<Part>() != part) continue;

                Mesh mesh = filter.sharedMesh;
                if (mesh == null) continue;

                foreach (Vector3 vertex in mesh.vertices)
                    points.Add(root.InverseTransformPoint(filter.transform.TransformPoint(vertex)));
            }
            if (points.Count == 0) return 0;

            // The two ends are found from where the part actually REACHES, not by
            // splitting it at its own origin.
            //
            // Splitting at the origin only works for a part that straddles it. A B9
            // control surface does; a B9 WING does not - it is rooted at its origin and
            // runs one way from there, so one side of the split is empty and the two
            // ends can never be compared. That reported the wing's root as
            // unidentifiable no matter how differently its ends were shaped, which read
            // as a limitation of B9 rather than of this measurement.
            float lowest = float.PositiveInfinity, highest = float.NegativeInfinity;
            for (int i = 0; i < points.Count; i++)
            {
                lowest = Mathf.Min(lowest, points[i].x);
                highest = Mathf.Max(highest, points[i].x);
            }

            float reach = highest - lowest;
            if (reach <= 1e-3f) return 0;

            // A tenth off each end, which is enough of the taper to tell them apart and
            // little enough not to average the two together.
            float slice = reach * 0.1f;
            float lowMin = float.PositiveInfinity, lowMax = float.NegativeInfinity;
            float highMin = float.PositiveInfinity, highMax = float.NegativeInfinity;

            for (int i = 0; i < points.Count; i++)
            {
                Vector3 local = points[i];
                if (local.x <= lowest + slice)
                {
                    lowMin = Mathf.Min(lowMin, local.y);
                    lowMax = Mathf.Max(lowMax, local.y);
                }
                if (local.x >= highest - slice)
                {
                    highMin = Mathf.Min(highMin, local.y);
                    highMax = Mathf.Max(highMax, local.y);
                }
            }

            if (lowMax <= lowMin || highMax <= highMin) return 0;

            float lowSpan = lowMax - lowMin;
            float highSpan = highMax - highMin;
            if (Mathf.Abs(highSpan - lowSpan) < 0.1f) return 0;
            return highSpan > lowSpan ? 1 : -1;
        }

        /// <summary>Whether a part is a B9 procedural control surface.</summary>
        /// <remarks>
        /// The same question WingConformance.KnownSpanAxis asks in the mod, asked
        /// again here because the two live in different assemblies. If the rule for
        /// which end is a control surface's root ever changes, both have to change.
        /// </remarks>
        private static bool IsControlSurface(Part part)
        {
            for (int i = 0; i < part.Modules.Count; i++)
            {
                PartModule module = part.Modules[i];
                if (module == null || module.GetType().Name != "WingProcedural") continue;
                return module.Fields["isCtrlSrf"]?.GetValue(module) is bool flag && flag;
            }
            return false;
        }

        /// <summary>Everything a part draws, as one box in the part's own space.</summary>
        /// <remarks>
        /// Built from each mesh's own bounds rather than from Renderer.bounds, which
        /// is world-axis-aligned and therefore useless for a part that is not.
        /// Returns an empty box at the origin for a part that draws nothing, which
        /// keeps the arithmetic above well defined rather than producing infinities.
        /// </remarks>
        private static Bounds LocalBounds(Part part)
        {
            var box = new Bounds(Vector3.zero, Vector3.zero);
            Transform root = part.transform;
            bool any = false;

            foreach (MeshFilter filter in part.GetComponentsInChildren<MeshFilter>())
            {
                Renderer renderer = filter.GetComponent<Renderer>();
                if (renderer == null || !renderer.enabled) continue;

                // Attached parts are parented to this one's transform, so the search
                // above walks straight into them. A wing with another wing on its tip
                // would otherwise measure as twice its own length.
                if (filter.GetComponentInParent<Part>() != part) continue;

                Mesh mesh = filter.sharedMesh;
                if (mesh == null) continue;
                Bounds local = mesh.bounds;
                if (local.size.sqrMagnitude <= 1e-9f) continue;

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

                    if (!any) { box = new Bounds(point, Vector3.zero); any = true; }
                    else box.Encapsulate(point);
                }
            }
            return box;
        }

        /// <summary>
        /// How a part's own axes lie relative to whatever it is attached to.
        /// </summary>
        /// <remarks>
        /// The one thing that differs between the two halves of a mirrored pair, and
        /// the one thing no dimension field records.
        /// </remarks>
        public static string DescribeOrientation(Part part)
        {
            if (part?.parent == null) return "no parent";

            Transform parent = part.parent.transform;
            Vector3 span = parent.InverseTransformDirection(part.transform.right);
            Vector3 chord = parent.InverseTransformDirection(part.transform.up);
            Vector3 thick = parent.InverseTransformDirection(part.transform.forward);

            return $"span ({span.x:F2}, {span.y:F2}, {span.z:F2}) " +
                   $"chord ({chord.x:F2}, {chord.y:F2}, {chord.z:F2}) " +
                   $"thickness ({thick.x:F2}, {thick.y:F2}, {thick.z:F2}) | " +
                   $"span reversed {span.x < 0f}, upside down {thick.z < 0f}, " +
                   $"mirrored {part.isMirrored}";
        }


        /// <summary>
        /// Declare two parts to be mirror-symmetry counterparts of one another.
        /// </summary>
        /// <param name="one">One of the pair.</param>
        /// <param name="other">The other.</param>
        /// <remarks>
        /// The editor builds these lists as it places a part with symmetry switched
        /// on, and this harness attaches parts directly, so it has to say so itself.
        /// Only the counterpart lists and the method are set - that is what a mod
        /// looking at a part reads, and it is what decides whether a change made to
        /// one of them is expected to appear on the other.
        /// </remarks>
        public static void LinkSymmetry(Part one, Part other)
        {
            if (one == null || other == null || one == other) return;

            if (!one.symmetryCounterparts.Contains(other)) one.symmetryCounterparts.Add(other);
            if (!other.symmetryCounterparts.Contains(one)) other.symmetryCounterparts.Add(one);
            one.symMethod = other.symMethod = SymmetryMethod.Mirror;

            // Colours are handed out as parts attach, which is before this is known.
            // Counterparts are supposed to share one, so the answer has to be worked
            // out again now that there is something to share.
            Recolour.ApplyToShip();
        }

        /// <summary>
        /// Where a B9 procedural wing's leading or trailing edge sits, in the wing's
        /// own space, at a given point along its span.
        /// </summary>
        /// <param name="wing">The wing part.</param>
        /// <param name="trailing">True for the trailing edge, false for the leading edge.</param>
        /// <param name="station">How far along the span, 0 at the root and 1 at the tip.</param>
        /// <returns>The edge's position on the wing's chord axis, or NaN if it is not a wing.</returns>
        /// <remarks>
        /// This is where the wing's BODY ends, not where its leading or trailing edge
        /// strip ends. A control surface belongs against the body, with the strip
        /// overlapping it: that is what lets the two present one continuous surface
        /// as the control deflects, instead of a hinge line with a step in it.
        ///
        /// A B9 wing's chord is also CENTRED on its own origin rather than hung off
        /// it - at the root it runs from -width/2 to +width/2, shifted by the root
        /// offset - so the body's edge is half a chord from the origin, not a whole
        /// one.
        /// </remarks>
        public static float WingEdge(Part wing, bool trailing, float station = 0f)
        {
            float offsetRoot = PartFields.Get(wing, "WingProcedural", "sharedBaseOffsetRoot");
            float offsetTip = PartFields.Get(wing, "WingProcedural", "sharedBaseOffsetTip");
            float widthRoot = PartFields.Get(wing, "WingProcedural", "sharedBaseWidthRoot");
            float widthTip = PartFields.Get(wing, "WingProcedural", "sharedBaseWidthTip");
            if (float.IsNaN(widthRoot) || float.IsNaN(widthTip)) return float.NaN;
            if (float.IsNaN(offsetRoot)) offsetRoot = 0f;
            if (float.IsNaN(offsetTip)) offsetTip = 0f;

            // The chord's midline runs from +offsetRoot at the root to -offsetTip at
            // the tip; B9 measures the two ends from opposite directions.
            float centre = Mathf.Lerp(offsetRoot, -offsetTip, station);
            float half = Mathf.Lerp(widthRoot, widthTip, station) / 2f;

            return trailing ? centre - half : centre + half;
        }

        /// <summary>
        /// How far a control surface is from the wing edge it is supposed to be
        /// sitting on.
        /// </summary>
        /// <param name="wing">The wing carrying the surface.</param>
        /// <param name="surface">The control surface.</param>
        /// <param name="trailing">Which of the wing's edges it is on.</param>
        /// <param name="station">Where along the span to compare, 0 at the root.</param>
        /// <returns>
        /// Positive for a gap between the two, negative for an overlap, zero when
        /// they meet. NaN when the wing cannot be measured.
        /// </returns>
        /// <remarks>
        /// This is the check that catches a control surface mounted half a chord
        /// out. Reading it off the parts is worth the trouble because the failure it
        /// looks for is obvious on screen and invisible in the numbers: every
        /// dimension can be right while the part hangs in mid-air.
        /// </remarks>
        public static float EdgeGap(Part wing, Part surface, bool trailing, float station = 0f)
        {
            float edge = WingEdge(wing, trailing, station);
            float span = PartFields.Get(wing, "WingProcedural", "sharedBaseLength");
            if (float.IsNaN(edge) || float.IsNaN(span) || wing == null || surface == null) return float.NaN;

            // The control surface's eight corners, in the wing's own space, each kept
            // with the sign of the local offset that produced it.
            Bounds box = LocalBounds(surface);
            var corners = new Vector3[8];
            var localSign = new Vector3[8];
            Vector3 centre = box.center;
            Vector3 extents = box.extents;
            for (int corner = 0; corner < 8; corner++)
            {
                var sign = new Vector3(
                    (corner & 1) == 0 ? -1f : 1f,
                    (corner & 2) == 0 ? -1f : 1f,
                    (corner & 4) == 0 ? -1f : 1f);
                localSign[corner] = sign;
                corners[corner] = wing.transform.InverseTransformPoint(
                    surface.transform.TransformPoint(centre + Vector3.Scale(sign, extents)));
            }

            // The hinge face is chosen as a FACE of the part's own box, then looked at
            // in the wing's frame - not by taking the four corners that come out
            // furthest along the wing's chord.
            //
            // Those are the same four only while the part is square to the wing. Turn
            // it far enough and the four extremes stop being a face at all: two come
            // from each end, the "hinge" fitted through them is a diagonal, and its
            // slope extrapolated across the span reports metres of gap on a part that
            // is sitting flush. That is why the worst readings appeared on the biggest
            // sweeps and were far larger than the wing.
            // Which way the part's hinge face points is read off its ATTACH NODE, not
            // guessed from which of its axes currently lies closest to the wing's
            // chord. That guess ties whenever the part is turned 45 degrees - its span
            // and its chord are then equally aligned with the wing's chord - and the
            // tie can pick the span, which makes an END of the part into its "hinge"
            // and fits a line across it. The line comes out with the mirror of the
            // right slope: correct at one end of the span and out by twice the covered
            // length at the other, which is exactly what a 45 degree edge produced.
            //
            // The node is the direction the part was attached along, so it names the
            // face that meets the host whatever the part has been turned to since.
            Vector3 nodeDirection = surface.srfAttachNode != null
                                    && surface.srfAttachNode.orientation.sqrMagnitude > 1e-6f
                ? surface.srfAttachNode.orientation.normalized
                : Vector3.up;

            int chordAxis = 0;
            float closest = -1f;
            for (int axis = 0; axis < 3; axis++)
            {
                Vector3 local = axis == 0 ? Vector3.right : axis == 1 ? Vector3.up : Vector3.forward;
                float alignment = Mathf.Abs(Vector3.Dot(local, nodeDirection));
                if (alignment <= closest) continue;
                closest = alignment;
                chordAxis = axis;
            }

            // Of that axis's two faces, the one lying toward the wing.
            float positiveMean = 0f, negativeMean = 0f;
            int positiveCount = 0, negativeCount = 0;
            for (int corner = 0; corner < 8; corner++)
            {
                if (localSign[corner][chordAxis] > 0f) { positiveMean += corners[corner].y; positiveCount++; }
                else { negativeMean += corners[corner].y; negativeCount++; }
            }
            positiveMean /= Mathf.Max(1, positiveCount);
            negativeMean /= Mathf.Max(1, negativeCount);
            float wantedSign = (trailing == (positiveMean > negativeMean)) ? 1f : -1f;

            var face = new Vector3[4];
            int found = 0;
            for (int corner = 0; corner < 8 && found < 4; corner++)
                if (Mathf.Approximately(localSign[corner][chordAxis], wantedSign)) face[found++] = corners[corner];
            if (found != 4) return float.NaN;
            for (int i = 0; i < 4; i++) corners[i] = face[i];

            // Fit the hinge as a straight line across the span, so a turned surface
            // is compared against the wing at the same place at both ends rather than
            // by its overall extent, which cannot tell a turn from a shift.
            float sumX = 0f, sumY = 0f, sumXX = 0f, sumXY = 0f;
            for (int i = 0; i < 4; i++)
            {
                sumX += corners[i].x;
                sumY += corners[i].y;
                sumXX += corners[i].x * corners[i].x;
                sumXY += corners[i].x * corners[i].y;
            }
            float denominator = 4f * sumXX - sumX * sumX;
            float slope = Mathf.Abs(denominator) > 1e-6f ? (4f * sumXY - sumX * sumY) / denominator : 0f;
            float intercept = (sumY - slope * sumX) / 4f;

            float hinge = intercept + slope * (station * span);
            return trailing ? edge - hinge : hinge - edge;
        }

        /// <summary>
        /// A horizontal unit vector in <paramref name="host"/>'s own space at an
        /// angle to the editor camera's view, so something extending along it is seen
        /// neither flat on nor exactly end-on.
        /// </summary>
        /// <param name="host">The part whose space the answer is in.</param>
        /// <param name="degrees">
        /// How far round from broadside. 0 is fully across the view; positive leans
        /// the far end toward the camera, negative leans it away. -45 is the useful
        /// one for a wing: the span runs away from the viewer, so the ROOT is the end
        /// facing them - which is the end whose cross section changes when a root
        /// dimension is edited. Leaning the other way presents the tip, which is
        /// usually the end that is not changing.
        /// </param>
        public static Vector3 DirectionObliqueToCamera(Part host, float degrees = -45f)
        {
            Vector3 across = DirectionAcrossCamera(host);
            Vector3 toward = DirectionFacingCamera(host);
            float radians = degrees * Mathf.Deg2Rad;
            Vector3 oblique = across * Mathf.Cos(radians) + toward * Mathf.Sin(radians);
            return oblique.sqrMagnitude > 1e-6f ? oblique.normalized : across;
        }

        /// <summary>
        /// A horizontal unit vector in <paramref name="host"/>'s own space running
        /// across the editor camera's view, so something extending along it - a wing
        /// span - is seen broadside rather than end-on.
        /// </summary>
        public static Vector3 DirectionAcrossCamera(Part host)
        {
            Vector3 across = Vector3.Cross(Vector3.up, DirectionFacingCamera(host));
            return across.sqrMagnitude > 1e-6f ? across.normalized : Vector3.right;
        }

        /// <summary>
        /// A horizontal unit vector in <paramref name="host"/>'s own space pointing
        /// at the editor camera, so something attached there ends up on the near side
        /// of the part rather than hidden behind it.
        /// </summary>
        /// <remarks>Falls back to the host's +X when there is no camera to ask.</remarks>
        public static Vector3 DirectionFacingCamera(Part host)
        {
            Camera camera = Camera.main;
            if (host == null || camera == null) return Vector3.right;

            Vector3 toCamera = camera.transform.position - host.transform.position;
            Vector3 local = Vector3.ProjectOnPlane(host.transform.InverseTransformDirection(toCamera), Vector3.up);
            return local.sqrMagnitude > 1e-6f ? local.normalized : Vector3.right;
        }

        /// <summary>
        /// How far a part's surface is from its own axis, for picking a point to
        /// surface-attach something to.
        /// </summary>
        /// <remarks>
        /// A part's own surface node already sits on its surface - ProceduralParts
        /// keeps it there as the part is resized - so that is the cheapest honest
        /// answer. Renderer bounds are the fallback for parts without one.
        /// </remarks>
        public static float SurfaceRadius(Part part)
        {
            if (part == null) return 0f;

            AttachNode node = part.srfAttachNode;
            if (node != null)
            {
                float radius = Vector3.ProjectOnPlane(node.position, Vector3.up).magnitude;
                if (radius > 0.01f) return radius;
            }

            float widest = 0f;
            foreach (Renderer renderer in part.GetComponentsInChildren<Renderer>())
            {
                if (!renderer.enabled || renderer is ParticleSystemRenderer) continue;
                Vector3 extents = renderer.bounds.extents;
                widest = Mathf.Max(widest, Mathf.Max(extents.x, extents.z));
            }
            return widest > 0.01f ? widest : 0.5f;
        }

        /// <summary>
        /// Lift the ship clear of the editor floor and point the camera at it, so a
        /// guided run actually shows the parts being talked about.
        /// </summary>
        /// <remarks>
        /// Parts are spawned about their own centres, so a stack built from the
        /// origin has its bottom half buried. Called again after each operation
        /// because resizing a part changes how far down the stack reaches.
        /// Silently does nothing outside the VAB or before the camera exists.
        /// </remarks>
        public static void PresentShip(bool reframeCamera = true)
        {
            LiftAboveFloor();

            // The lift moves every part, including one somebody has a gizmo on, and a
            // gizmo does not follow its part on its own - so the handles would be left
            // hanging where the ship used to be.
            FollowGizmos();

            if (reframeCamera) FrameShip();
        }

        /// <summary>Raise the whole ship until nothing is below the editor floor.</summary>
        /// <remarks>
        /// Everything is parented to the root part, so moving the root moves the
        /// stack with it.
        /// </remarks>
        private static void LiftAboveFloor(float margin = 0.4f)
        {
            ShipConstruct ship = EditorLogic.fetch?.ship;
            if (ship == null || ship.parts.Count == 0) return;

            Part root = EditorLogic.RootPart ?? ship.parts[0];
            if (root == null) return;

            if (!TryGetShipBounds(out Bounds bounds)) return;

            // The first lift of a scenario clears the floor by a margin plus some
            // headroom; every later one only acts if something is actually through
            // the floor, which - given that headroom - it should never be.
            //
            // The point is that the ship stops MOVING. Lifting it by just the margin
            // each time means every resize shunts the whole stack upward, and a part
            // that jumps while you are watching to see whether it resized correctly
            // is worse than a part sitting a little high.
            //
            // Headroom is whatever a scenario has reserved for the part it is about
            // to change, and nothing more. The root part's height was the wrong
            // budget: growth downward comes from the part being EDITED, which is
            // usually not the root, and budgeting a tall root - the fairing fixture's
            // base - lifts the ship clean out of the camera's view.
            float lift = margin + (_floorSettled ? 0f : _reserved) - bounds.min.y;

            if (_floorSettled && lift <= 0.01f) return;
            if (lift > 0.01f) root.transform.position += Vector3.up * lift;
            _floorSettled = true;
        }

        /// <summary>
        /// Say which part a scenario is about to change, and by how much, so the ship
        /// can be given the room it will need before anything moves.
        /// </summary>
        /// <param name="edited">The part whose dimensions are about to change.</param>
        /// <param name="growth">How much bigger it is expected to get - 2 for a doubling.</param>
        /// <remarks>
        /// Only what the part reaches BELOW ITS OWN ORIGIN can grow toward the floor,
        /// and only if that part is what the ship is standing on. Reserving a part's
        /// whole height regardless was what threw the fairing fixture out of frame:
        /// its base is tall and grows a long way, but it grows upward, from a tank
        /// that is holding it clear of the floor already.
        ///
        /// Where a mod derives one dimension from another - ROLib setting a tank's
        /// length from its diameter - the growth figure is a budget rather than a
        /// prediction, so it is generous on purpose.
        /// </remarks>
        public static void WillEdit(Part edited, float growth = 2f)
        {
            if (edited == null || !TryGetShipBounds(out Bounds ship)) return;

            float bottom = BottomOf(edited);
            if (float.IsNaN(bottom)) return;

            // Something else is lower down, so this part growing cannot be what
            // reaches through the floor first.
            if (bottom > ship.min.y + 0.05f) return;

            float reach = Mathf.Max(edited.transform.position.y - bottom, 0f);
            ReserveHeadroom(reach * Mathf.Max(growth - 1f, 0f));
        }

        /// <summary>The lowest point of everything a part draws, ignoring anything attached to it.</summary>
        /// <returns>NaN when the part draws nothing measurable.</returns>
        private static float BottomOf(Part part)
        {
            float lowest = float.PositiveInfinity;
            foreach (Renderer renderer in part.GetComponentsInChildren<Renderer>())
            {
                if (!renderer.enabled || renderer is ParticleSystemRenderer) continue;
                if (renderer.GetComponentInParent<Part>() != part) continue;
                Bounds bounds = renderer.bounds;
                if (bounds.size.sqrMagnitude <= 1e-9f) continue;
                lowest = Mathf.Min(lowest, bounds.min.y);
            }
            return float.IsPositiveInfinity(lowest) ? float.NaN : lowest;
        }

        /// <summary>
        /// Ask for extra clearance under the ship before anything is resized.
        /// </summary>
        /// <param name="metres">How far a part is expected to move or grow downward.</param>
        /// <remarks>
        /// For the scenarios that knowingly push a part downward - sweeping a wing's
        /// tip moves the segment outboard of it along the chord, which on a wing
        /// mounted off the side of a fuselage is straight down. Reserving the room up
        /// front is what keeps the ship still during the step being watched.
        /// </remarks>
        public static void ReserveHeadroom(float metres)
        {
            if (metres <= _reserved) return;
            _reserved = metres;
            _floorSettled = false;
            LiftAboveFloor();
        }

        /// <summary>Extra clearance a scenario has asked for, in metres.</summary>
        private static float _reserved;

        /// <summary>False until this scenario's ship has been given its headroom.</summary>
        private static bool _floorSettled;

        /// <summary>
        /// Look straight down at a point, so nothing can be hidden behind anything.
        /// </summary>
        /// <remarks>
        /// For clicking a part that lies against another one. A control surface sits on
        /// its wing's edge, and from the side the wing is between it and the camera -
        /// the pointer finds the wing however carefully the aim is computed, and the
        /// part cannot be selected at all. From above there is nothing in the way.
        ///
        /// The pitch convention is not assumed, and just as well: setting camPitch to
        /// the controller's own maximum does NOT look down. Measured, the view comes
        /// out at (0.9, -0.3, 0.3) - mostly along the ship, tilted slightly down. The
        /// reframing still makes a surface on a wing's edge selectable where the old
        /// framing did not, so it earns its place, but it does so by moving the camera
        /// rather than by getting above anything. The name is aspirational; the log
        /// line reports what actually happened.
        /// </remarks>
        public static void LookDownAt(Vector3 world, float distance = 12f)
        {
            VABCamera camera = EditorDriver.fetch?.vabCamera;
            if (camera == null) return;

            camera.camHdg = 0f;
            camera.camPitch = camera.maxPitch;
            camera.PlaceCamera(world, distance);

            Camera eye = EditorCamera.Instance?.cam;
            Harness.Log($"CAMERA overhead at {world}, distance {distance:F1}, " +
                        $"pitch {camera.camPitch:F3} (max {camera.maxPitch:F3}), " +
                        $"looking {(eye == null ? "?" : eye.transform.forward.ToString())}");
        }

        /// <summary>Point the editor camera at whatever is currently on the ship.</summary>
        private static void FrameShip()
        {
            VABCamera camera = EditorDriver.fetch?.vabCamera;
            if (camera == null) return;
            if (!TryGetShipBounds(out Bounds bounds)) return;

            // Back to a known orientation first. camPitch and camHdg live on the
            // editor's camera controller and outlast the scenario that changed them, so
            // one scenario reframing the view to reach an awkward part leaves every
            // later one aiming through a camera it did not choose - which shows up as
            // synthetic input landing a hundred and more pixels from where it was
            // aimed, in scenarios that never touched the camera at all.
            camera.camPitch = camera.initialPitch;
            camera.camHdg = camera.initialHeading;

            // Aim a little above centre so the ship sits in the upper half of the
            // screen, above the walkthrough panel rather than behind it.
            float extent = Mathf.Max(bounds.size.magnitude, 2f);
            Vector3 focus = bounds.center + Vector3.down * (extent * 0.18f);
            camera.PlaceCamera(focus, Mathf.Clamp(extent * 1.7f, 9f, 34f));
        }

        /// <summary>
        /// World-space bounds of everything the ship draws.
        /// </summary>
        /// <remarks>
        /// Renderer bounds rather than part origins: a procedural tank's origin sits
        /// at its centre, so origins alone under-report a stack by a whole part.
        /// </remarks>
        private static bool TryGetShipBounds(out Bounds bounds)
        {
            bounds = default;
            ShipConstruct ship = EditorLogic.fetch?.ship;
            if (ship == null || ship.parts.Count == 0) return false;

            bool any = false;
            foreach (Part part in ship.parts)
            {
                if (part == null) continue;
                foreach (Renderer renderer in part.GetComponentsInChildren<Renderer>())
                {
                    if (!renderer.enabled || renderer is ParticleSystemRenderer) continue;
                    if (!any) { bounds = renderer.bounds; any = true; }
                    else bounds.Encapsulate(renderer.bounds);
                }
            }

            if (!any)
            {
                bounds = new Bounds(ship.parts[0].transform.position, Vector3.one);
                foreach (Part part in ship.parts)
                    if (part != null) bounds.Encapsulate(part.transform.position);
            }
            return true;
        }

        /// <summary>
        /// Drop the input lock KSP holds while the editor is waiting for its first
        /// part to be placed.
        /// </summary>
        /// <remarks>
        /// The editor enters "root part mode" with a lock covering nearly everything,
        /// camera controls included, and only lifts it when its own state machine
        /// sees a root part placed through the usual mouse flow. This harness builds
        /// ships by wiring parts up directly, so that never happens and the lock sits
        /// there for the whole session - which is why the camera cannot be moved to
        /// look at what a scenario just did.
        /// </remarks>
        public static void ReleaseEditorStartupLock()
        {
            // KSP takes both of these while the editor is starting and lifts them
            // when the player places a root part through the normal flow. This
            // harness puts a root part in directly, so nothing ever lifts them - and
            // between them they block picking a part up, the gizmo tools, and the
            // camera. Hovering still highlights, which is what makes it look like
            // nothing is wrong rather than like something is locked.
            InputLockManager.RemoveControlLock("EditorLogic_rootPartMode");
            InputLockManager.RemoveControlLock("EditorDriver_GameParameters");
            LeaveRootPartMode();
        }

        /// <summary>The name of the editor's current FSM state, for the log.</summary>
        public static string CurrentEditorState()
        {
            try
            {
                var machine = typeof(EditorLogic).GetField("fsm",
                    BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(EditorLogic.fetch) as KerbalFSM;
                return machine?.CurrentState?.name ?? "(unknown)";
            }
            catch (Exception)
            {
                return "(unreadable)";
            }
        }

        /// <summary>
        /// Push the editor's state machine out of "waiting for a root part".
        /// </summary>
        /// <remarks>
        /// Lifting the input lock is not enough on its own. The editor decides what a
        /// click MEANS from the state its FSM is in, and putting a root part in by
        /// hand leaves that FSM still waiting for one - so a click is read as "place
        /// the root part", nothing is being carried, and nothing happens. Hover
        /// highlighting is drawn by a different path, which is why the parts look
        /// live while refusing to be picked up.
        ///
        /// on_podSelect is the editor's own way out of that state, and its condition
        /// is simply that a root part exists, which by now one does. Everything here
        /// is private, so it is reached by reflection and gives up quietly.
        /// </remarks>
        private static void LeaveRootPartMode()
        {
            EditorLogic editor = EditorLogic.fetch;
            if (editor == null) return;

            try
            {
                const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
                var machine = typeof(EditorLogic).GetField("fsm", Hidden)?.GetValue(editor) as KerbalFSM;
                var podSelect = typeof(EditorLogic).GetField("on_podSelect", Hidden)?.GetValue(editor)
                                as KFSMEvent;
                if (machine == null || podSelect == null) return;

                string state = machine.CurrentState?.name ?? "(none)";
                if (state.IndexOf("pod", StringComparison.OrdinalIgnoreCase) < 0
                    && state.IndexOf("root", StringComparison.OrdinalIgnoreCase) < 0) return;

                machine.RunEvent(podSelect);
                Harness.Log($"editor was in FSM state '{state}', ran on_podSelect -> " +
                            $"'{machine.CurrentState?.name}'");
            }
            catch (Exception error)
            {
                Harness.Log($"could not move the editor out of root-part mode: {error.Message}");
            }
        }

        /// <summary>Put a part into the editor's ship, which is where DimensionSync looks for it.</summary>
        private static void AddToShip(Part part)
        {
            ShipConstruct ship = EditorLogic.fetch.ship;
            if (!ship.Contains(part)) ship.Add(part);
            part.ship = ship;

            // Colour it as it joins, not the next time the ship is presented. A part
            // attached after a PresentShip call would otherwise stay its stock colour
            // until the next one, which lands in the middle of a test and reads as
            // the part changing colour in response to whatever was just resized.
            Recolour.ApplyToShip();
        }

        /// <summary>
        /// Look a part up the way KSP stores it. PartLoader turns underscores in a
        /// part's cfg name into dots, so "B9_Aero_Wing_Procedural_TypeA" on disk is
        /// "B9.Aero.Wing.Procedural.TypeA" at runtime.
        /// </summary>
        public static AvailablePart FindPart(string partName)
        {
            if (string.IsNullOrEmpty(partName)) return null;
            return PartLoader.getPartInfoByName(partName)
                   ?? PartLoader.getPartInfoByName(partName.Replace('_', '.'));
        }

        /// <summary>
        /// Clone a part prefab and bring it up the way <c>EditorLogic.SpawnPart</c>
        /// does, short of handing it to the placement state machine.
        /// </summary>
        /// <remarks>
        /// The clone is left out of the ship so a scenario can set its dimensions
        /// before it has any neighbours to propagate to.
        /// </remarks>
        private static Part Instantiate(string partName, Vector3 position)
        {
            AvailablePart available = FindPart(partName);
            if (available == null)
            {
                Harness.LogError($"unknown part '{partName}'");
                return null;
            }

            Part part = UnityEngine.Object.Instantiate(available.partPrefab);
            part.gameObject.SetActive(true);
            part.name = available.name;
            part.partInfo = available;
            part.persistentId = FlightGlobals.CheckPartpersistentId(part.persistentId, part, false, true);
            part.transform.position = position;
            part.transform.rotation = Quaternion.identity;
            part.InitializeModules();
            // Layer 0, which is what the game puts a PLACED part on. Measured from craft
            // built by hand in the editor: every part and every collider on them reads
            // layer 0, while this used to set layer 1.
            //
            // Layer 1 is TransparentFX - where KSP keeps the part that is being carried,
            // not one that has been put down. A part left there looks to anything
            // layer-aware like a ghost still following the cursor, which is enough to
            // stop Unity delivering a hover to it however exactly it is aimed at, and is
            // the likeliest reason parts built here cannot be picked with the mouse.
            part.gameObject.SetLayerRecursive(0, true, 2097152);
            part.attPos0 = part.transform.localPosition;
            part.attRotation0 = part.transform.localRotation;
            part.isAttached = true;
            Spawned.Add(part);

            // A part with no modules means its mod's assembly did not load - almost
            // always an unmet KSPAssembly dependency - and every later assertion
            // would fail for reasons that have nothing to do with DimensionSync.
            if (part.Modules.Count == 0)
                Harness.LogError($"{partName} spawned with no PartModules; is its plugin assembly loaded?");

            return part;
        }

        /// <summary>
        /// Find the part's top or bottom stack node, optionally requiring that
        /// nothing is attached to it yet.
        /// </summary>
        public static AttachNode FindEndNode(Part part, bool top, bool free)
        {
            AttachNode best = null;
            for (int i = 0; i < part.attachNodes.Count; i++)
            {   // Nodes are unordered, so scan them all and keep the outermost match.
                AttachNode node = part.attachNodes[i];
                if (node.nodeType != AttachNode.NodeType.Stack) continue;
                if (free && node.attachedPart != null) continue;

                Vector3 orientation = node.orientation;
                if (orientation.sqrMagnitude <= 1e-6f) continue;
                float y = orientation.normalized.y;
                bool isTop = y >= 0.9f;
                bool isBottom = y <= -0.9f;
                if (top ? !isTop : !isBottom) continue;

                // Prefer the node furthest along the axis, which is the real end
                // of the part when several nodes share a direction.
                if (best == null ||
                    (top ? node.position.y > best.position.y : node.position.y < best.position.y))
                    best = node;
            }
            return best;
        }
    }
}
