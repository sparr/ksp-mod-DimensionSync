using Xunit;

namespace DimensionSync.Tests
{
    /// <summary>
    /// The propagation rules, exercised against an in-memory part graph. Nothing
    /// here needs KSP, Unity or a running game, so it covers the decisions the
    /// integration tests are too slow and too coarse to pin down individually.
    /// </summary>
    public class PropagatorTests
    {
        /// <summary>A propagator with the shipped defaults: 1e-4 tolerance, rigid parts stop the walk.</summary>
        private static DimensionPropagator NewPropagator() => new DimensionPropagator();

        /// <summary>
        /// Apply the change to the origin slot itself and then propagate, in that
        /// order, because that is what the addon sees: by the time it notices a
        /// field has moved, the field already holds the new value.
        /// </summary>
        /// <returns>How many neighbouring fields the walk updated.</returns>
        private static int Change(DimensionPropagator propagator, FakeNode origin, string field, float newValue)
        {
            FakeSlot slot = origin.Slot(field);
            float oldValue = slot.Value;
            slot.Raw = slot.Descriptor.ToFieldUnits(newValue);
            return propagator.Propagate(origin, slot.Descriptor, oldValue, newValue);
        }

        /// <summary>
        /// The base case: a uniform stack takes the new size all the way along.
        /// </summary>
        [Fact]
        public void PropagatesAlongAStackOfEquallySizedParts()
        {
            FakeNode a = Fake.Cylinder("a", 1.25f);
            FakeNode b = Fake.Cylinder("b", 1.25f);
            FakeNode c = Fake.Cylinder("c", 1.25f);
            Fake.Stack(a, b);
            Fake.Stack(b, c);

            int changed = Change(NewPropagator(), a, "diameter", 2.5f);

            Assert.Equal(2, changed);
            Assert.Equal(2.5f, b.Slot("diameter").Value, 4);
            Assert.Equal(2.5f, c.Slot("diameter").Value, 4);
        }

        /// <summary>
        /// Propagation has no preferred direction; a part in the middle feeds both ways.
        /// </summary>
        [Fact]
        public void PropagatesDownwardsAsWellAsUpwards()
        {
            FakeNode bottom = Fake.Cylinder("bottom", 1.25f);
            FakeNode middle = Fake.Cylinder("middle", 1.25f);
            FakeNode top = Fake.Cylinder("top", 1.25f);
            Fake.Stack(bottom, middle);
            Fake.Stack(middle, top);

            Change(NewPropagator(), middle, "diameter", 3f);

            Assert.Equal(3f, bottom.Slot("diameter").Value, 4);
            Assert.Equal(3f, top.Slot("diameter").Value, 4);
        }

        /// <summary>
        /// The stopping rule. An odd-sized part keeps its size and shields everything
        /// beyond it, which is what stops one slider from resizing a whole rocket.
        /// </summary>
        [Fact]
        public void StopsAtAPartOfADifferentSize()
        {
            FakeNode a = Fake.Cylinder("a", 1.25f);
            FakeNode b = Fake.Cylinder("b", 1.25f);
            FakeNode wrongSize = Fake.Cylinder("wrongSize", 2.5f);
            FakeNode beyond = Fake.Cylinder("beyond", 1.25f);
            Fake.Stack(a, b);
            Fake.Stack(b, wrongSize);
            Fake.Stack(wrongSize, beyond);

            Change(NewPropagator(), a, "diameter", 1.5f);

            Assert.Equal(1.5f, b.Slot("diameter").Value, 4);
            Assert.Equal(2.5f, wrongSize.Slot("diameter").Value, 4);
            Assert.Equal(1.25f, beyond.Slot("diameter").Value, 4);
        }

        /// <summary>
        /// A field that describes one end of a part only feeds the joint at that end,
        /// even when the other end happens to hold the same number.
        /// </summary>
        [Fact]
        public void ChangingATopDiameterOnlyPropagatesUpwards()
        {
            FakeNode below = Fake.Cylinder("below", 1.25f);
            FakeNode cone = Fake.Cone("cone", bottom: 1.25f, top: 1.25f);
            FakeNode above = Fake.Cylinder("above", 1.25f);
            Fake.Stack(below, cone);
            Fake.Stack(cone, above);

            Change(NewPropagator(), cone, "topDiameter", 2f);

            Assert.Equal(2f, above.Slot("diameter").Value, 4);
            Assert.Equal(1.25f, below.Slot("diameter").Value, 4);
            Assert.Equal(1.25f, cone.Slot("bottomDiameter").Value, 4);
        }

        /// <summary>
        /// The mirror of the previous case, for the lower end.
        /// </summary>
        [Fact]
        public void ChangingABottomDiameterOnlyPropagatesDownwards()
        {
            FakeNode below = Fake.Cylinder("below", 1.25f);
            FakeNode cone = Fake.Cone("cone", bottom: 1.25f, top: 1.25f);
            FakeNode above = Fake.Cylinder("above", 1.25f);
            Fake.Stack(below, cone);
            Fake.Stack(cone, above);

            Change(NewPropagator(), cone, "bottomDiameter", 2f);

            Assert.Equal(2f, below.Slot("diameter").Value, 4);
            Assert.Equal(1.25f, above.Slot("diameter").Value, 4);
        }

        /// <summary>
        /// A cone with equal ends is part of a uniform run, so the change carries
        /// through it rather than stopping there.
        /// </summary>
        [Fact]
        public void PassesThroughAConeWhoseFarEndIsTheSameSize()
        {
            // A cone whose ends are both 1.25 is, for sizing purposes, part of a
            // 1.25 stack, so the change carries on past it.
            FakeNode a = Fake.Cylinder("a", 1.25f);
            FakeNode cone = Fake.Cone("cone", bottom: 1.25f, top: 1.25f);
            FakeNode c = Fake.Cylinder("c", 1.25f);
            Fake.Stack(a, cone);
            Fake.Stack(cone, c);

            Change(NewPropagator(), a, "diameter", 2f);

            Assert.Equal(2f, cone.Slot("bottomDiameter").Value, 4);
            Assert.Equal(2f, cone.Slot("topDiameter").Value, 4);
            Assert.Equal(2f, c.Slot("diameter").Value, 4);
        }

        /// <summary>
        /// The same cone with a real taper absorbs the change instead: its near end
        /// follows, its far end and everything past it do not.
        /// </summary>
        [Fact]
        public void StopsAtAConeWhoseFarEndIsADifferentSize()
        {
            FakeNode a = Fake.Cylinder("a", 1.25f);
            FakeNode cone = Fake.Cone("cone", bottom: 1.25f, top: 2.5f);
            FakeNode c = Fake.Cylinder("c", 2.5f);
            Fake.Stack(a, cone);
            Fake.Stack(cone, c);

            Change(NewPropagator(), a, "diameter", 2f);

            Assert.Equal(2f, cone.Slot("bottomDiameter").Value, 4);
            Assert.Equal(2.5f, cone.Slot("topDiameter").Value, 4);
            Assert.Equal(2.5f, c.Slot("diameter").Value, 4);
        }

        /// <summary>
        /// A neighbour that stores a radius still ends up the right size, and the
        /// number written to the field is the radius rather than the diameter.
        /// </summary>
        [Fact]
        public void RadiusFieldsAreConvertedToAndFromDiameters()
        {
            FakeNode a = Fake.Cylinder("a", 1.25f);
            var b = new FakeNode("b");
            b.Add(Fake.Radius, 1.25f);          // stored as radius 0.625
            Fake.Stack(a, b);

            Assert.Equal(0.625f, b.Slot("radius").Raw, 4);

            Change(NewPropagator(), a, "diameter", 3f);

            Assert.Equal(3f, b.Slot("radius").Value, 4);
            Assert.Equal(1.5f, b.Slot("radius").Raw, 4);
        }

        /// <summary>
        /// The same conversion in the other direction: a radius field as the origin
        /// of the change.
        /// </summary>
        [Fact]
        public void AChangedRadiusPropagatesAsADiameter()
        {
            var a = new FakeNode("a");
            a.Add(Fake.Radius, 1.25f);
            FakeNode b = Fake.Cylinder("b", 1.25f);
            Fake.Stack(a, b);

            FakeSlot slot = a.Slot("radius");
            slot.Raw = 1f;                       // radius 1 -> diameter 2
            NewPropagator().Propagate(a, slot.Descriptor, 1.25f, 2f);

            Assert.Equal(2f, b.Slot("diameter").Value, 4);
        }

        /// <summary>
        /// A hollow part's bore and its outside are separate channels, so resizing
        /// one leaves the other alone.
        /// </summary>
        [Fact]
        public void InnerAndOuterDimensionsDoNotCrossOver()
        {
            var a = new FakeNode("a");
            a.Add(Fake.OuterDiameter, 2f);
            a.Add(Fake.InnerDiameter, 1f);
            var b = new FakeNode("b");
            b.Add(Fake.OuterDiameter, 2f);
            b.Add(Fake.InnerDiameter, 1f);
            Fake.Stack(a, b);

            Change(NewPropagator(), a, "innerDiameter", 1.5f);

            Assert.Equal(1.5f, b.Slot("innerDiameter").Value, 4);
            Assert.Equal(2f, b.Slot("outerDiameter").Value, 4);
        }

        /// <summary>
        /// A neighbour built to fit a bore follows that bore.
        /// </summary>
        /// <remarks>
        /// This asserted the opposite until hollow parts were supported, on the
        /// reasoning that a bore and an outside are different channels and a shared
        /// number between them is a coincidence. It is not: a plug sized to the tank
        /// above it was built to that size deliberately, and a part following the
        /// size it was built to is the whole premise of every other channel here.
        /// </remarks>
        [Fact]
        public void AnOutsideBuiltToABoreFollowsThatBore()
        {
            var a = new FakeNode("a");
            a.Add(Fake.OuterDiameter, 2f);
            a.Add(Fake.InnerDiameter, 1f);
            var b = new FakeNode("b");
            b.Add(Fake.OuterDiameter, 1f);      // a plug sized to a's bore
            Fake.Stack(a, b);

            Change(NewPropagator(), a, "innerDiameter", 0.5f);

            Assert.Equal(0.5f, b.Slot("outerDiameter").Value, 4);
        }

        /// <summary>
        /// Letting the two channels match does not let a part's bore write its own
        /// outside. Nothing forbids it: the walk writes only fields that still hold
        /// the pre-change value, and a hollow part's two diameters are never equal.
        /// Coupling them is a separate rule with its own setting.
        /// </summary>
        [Fact]
        public void ABoreChangeLeavesItsOwnPartsOutsideAlone()
        {
            var a = new FakeNode("a");
            a.Add(Fake.OuterDiameter, 2f);
            a.Add(Fake.InnerDiameter, 1f);

            Change(NewPropagator(), a, "innerDiameter", 0.5f);

            Assert.Equal(2f, a.Slot("outerDiameter").Value, 4);
        }

        /// <summary>
        /// A field the player could not have changed themselves is neither written
        /// nor propagated through - the in-game case is a fixed-size stock part.
        /// </summary>
        [Fact]
        public void SkipsFieldsThatAreNotAdjustable()
        {
            FakeNode a = Fake.Cylinder("a", 1.25f);
            FakeNode b = Fake.Cylinder("b", 1.25f);
            FakeNode c = Fake.Cylinder("c", 1.25f);
            Fake.Stack(a, b);
            Fake.Stack(b, c);
            b.Slot("diameter").Adjustable = false;   // e.g. a fixed-size stock part

            Change(NewPropagator(), a, "diameter", 2f);

            Assert.Equal(1.25f, b.Slot("diameter").Value, 4);
            Assert.Equal(1.25f, c.Slot("diameter").Value, 4);
        }

        /// <summary>
        /// With the option on, a part with nothing on this channel is transparent
        /// rather than a wall.
        /// </summary>
        [Fact]
        public void PassThroughRigidPartsCarriesOnPastAPartWithNoDimensions()
        {
            FakeNode a = Fake.Cylinder("a", 1.25f);
            var decoupler = new FakeNode("decoupler");     // no dimension fields at all
            FakeNode c = Fake.Cylinder("c", 1.25f);
            Fake.Stack(a, decoupler);
            Fake.Stack(decoupler, c);

            var propagator = NewPropagator();
            propagator.PassThroughRigidParts = true;
            Change(propagator, a, "diameter", 2f);

            Assert.Equal(2f, c.Slot("diameter").Value, 4);
        }

        /// <summary>
        /// With the option off, which is the default, that same part ends the run.
        /// </summary>
        [Fact]
        public void RigidPartsStopPropagationByDefault()
        {
            FakeNode a = Fake.Cylinder("a", 1.25f);
            var decoupler = new FakeNode("decoupler");
            FakeNode c = Fake.Cylinder("c", 1.25f);
            Fake.Stack(a, decoupler);
            Fake.Stack(decoupler, c);

            Change(NewPropagator(), a, "diameter", 2f);

            Assert.Equal(1.25f, c.Slot("diameter").Value, 4);
        }

        /// <summary>
        /// Pass-through only applies to parts with no dimension at all. A part that
        /// has one and disagrees still stops the walk, however the option is set.
        /// </summary>
        [Fact]
        public void PassThroughStillStopsAtAPartWithADifferentAdjustableDiameter()
        {
            FakeNode a = Fake.Cylinder("a", 1.25f);
            FakeNode wrongSize = Fake.Cylinder("wrongSize", 2.5f);
            FakeNode c = Fake.Cylinder("c", 1.25f);
            Fake.Stack(a, wrongSize);
            Fake.Stack(wrongSize, c);

            var propagator = NewPropagator();
            propagator.PassThroughRigidParts = true;
            Change(propagator, a, "diameter", 2f);

            Assert.Equal(1.25f, c.Slot("diameter").Value, 4);
        }

        /// <summary>
        /// When a field cannot take the value asked for, it is now a different size
        /// than the rest of the run, so the walk ends there instead of carrying a
        /// value nothing actually holds.
        /// </summary>
        [Fact]
        public void ClampedValuesStopPropagation()
        {
            FakeNode a = Fake.Cylinder("a", 1.25f);
            FakeNode b = Fake.Cylinder("b", 1.25f);
            FakeNode c = Fake.Cylinder("c", 1.25f);
            Fake.Stack(a, b);
            Fake.Stack(b, c);
            b.Slot("diameter").Max = 2f;

            Change(NewPropagator(), a, "diameter", 4f);

            Assert.Equal(2f, b.Slot("diameter").Value, 4);
            Assert.Equal(1.25f, c.Slot("diameter").Value, 4);
        }

        /// <summary>
        /// The visited set terminates a cycle, and terminates it without writing to
        /// the same field twice.
        /// </summary>
        [Fact]
        public void DoesNotRevisitPartsOrLoopForever()
        {
            FakeNode a = Fake.Cylinder("a", 1.25f);
            FakeNode b = Fake.Cylinder("b", 1.25f);
            Fake.Stack(a, b);
            // Add a second, bogus link back to a to make sure a cycle terminates.
            b.LinkList.Add(new AxialLink(a, PartEnd.Top, PartEnd.Bottom));

            int changed = Change(NewPropagator(), a, "diameter", 2f);

            Assert.Equal(1, changed);
            Assert.Equal(1, b.Slot("diameter").Writes);
        }

        /// <summary>
        /// A part with no joint of the right kind is simply unreachable; a booster
        /// on the side of a tank keeps its own size.
        /// </summary>
        [Fact]
        public void RadiallyAttachedPartsAreNotReachedBecauseTheyHaveNoAxialLink()
        {
            FakeNode a = Fake.Cylinder("a", 1.25f);
            FakeNode booster = Fake.Cylinder("booster", 1.25f);
            // No Fake.Stack call: a surface-attached part contributes no AxialLink.

            Change(NewPropagator(), a, "diameter", 2f);

            Assert.Equal(1.25f, booster.Slot("diameter").Value, 4);
        }

        /// <summary>
        /// A change that is not a change writes nothing, which keeps the poll from
        /// churning when a mod rewrites a field with the value it already had.
        /// </summary>
        [Fact]
        public void NoOpChangeDoesNothing()
        {
            FakeNode a = Fake.Cylinder("a", 1.25f);
            FakeNode b = Fake.Cylinder("b", 1.25f);
            Fake.Stack(a, b);

            int changed = NewPropagator().Propagate(a, Fake.Diameter, 1.25f, 1.25f);

            Assert.Equal(0, changed);
            Assert.Equal(0, b.Slot("diameter").Writes);
        }

        /// <summary>
        /// A part with two neighbours on one end - a multi-node adapter - feeds both.
        /// </summary>
        [Fact]
        public void BranchingStacksAreAllFollowed()
        {
            // One part with two children stacked on its top node - a multi-node
            // adapter, for instance.
            FakeNode root = Fake.Cylinder("root", 1.25f);
            FakeNode left = Fake.Cylinder("left", 1.25f);
            FakeNode right = Fake.Cylinder("right", 1.25f);
            Fake.Stack(root, left);
            Fake.Stack(root, right);

            Change(NewPropagator(), root, "diameter", 2f);

            Assert.Equal(2f, left.Slot("diameter").Value, 4);
            Assert.Equal(2f, right.Slot("diameter").Value, 4);
        }
        // =====================================================================
        // Channels and link kinds
        // =====================================================================

        /// <summary>
        /// The wing case: widening a tip widens the root of the segment outboard of
        /// it, which is what B9 Procedural Wings' own "Inherit base" button does by
        /// hand.
        /// </summary>
        [Fact]
        public void WingChordSyncsFromOneSegmentsTipToTheNextSegmentsRoot()
        {
            FakeNode inboard = Fake.Wing("inboard", widthRoot: 4f, widthTip: 3f);
            FakeNode outboard = Fake.Wing("outboard", widthRoot: 3f, widthTip: 2f);
            Fake.Span(inboard, outboard);

            Change(NewPropagator(), inboard, "widthTip", 2.5f);

            Assert.Equal(2.5f, outboard.Slot("widthRoot").Value, 4);
            Assert.Equal(2f, outboard.Slot("widthTip").Value, 4);
        }

        /// <summary>
        /// Chord and thickness are separate channels even when they hold the same
        /// number, which they routinely do on a thin wing measured in metres.
        /// </summary>
        [Fact]
        public void WingThicknessAndChordDoNotCrossOver()
        {
            // Both channels happen to hold the same number here.
            FakeNode inboard = Fake.Wing("inboard", widthRoot: 1f, widthTip: 1f, thicknessRoot: 1f, thicknessTip: 1f);
            FakeNode outboard = Fake.Wing("outboard", widthRoot: 1f, widthTip: 1f, thicknessRoot: 1f, thicknessTip: 1f);
            Fake.Span(inboard, outboard);

            Change(NewPropagator(), inboard, "thicknessTip", 0.5f);

            Assert.Equal(0.5f, outboard.Slot("thicknessRoot").Value, 4);
            Assert.Equal(1f, outboard.Slot("widthRoot").Value, 4);
        }

        /// <summary>
        /// The stack pass-through rule applied to wings: a constant-chord run takes
        /// the change along its whole length.
        /// </summary>
        [Fact]
        public void AConstantChordWingRunCarriesTheChangeAllTheWayOut()
        {
            FakeNode a = Fake.Wing("a", 3f, 3f);
            FakeNode b = Fake.Wing("b", 3f, 3f);
            FakeNode c = Fake.Wing("c", 3f, 3f);
            Fake.Span(a, b);
            Fake.Span(b, c);

            Change(NewPropagator(), a, "widthTip", 2f);

            Assert.Equal(2f, b.Slot("widthRoot").Value, 4);
            Assert.Equal(2f, b.Slot("widthTip").Value, 4);
            Assert.Equal(2f, c.Slot("widthRoot").Value, 4);
        }

        /// <summary>
        /// And a tapered segment ends it, exactly as a tapered cone does in a stack.
        /// </summary>
        [Fact]
        public void ATaperedWingSegmentAbsorbsTheChange()
        {
            FakeNode a = Fake.Wing("a", 4f, 3f);
            FakeNode b = Fake.Wing("b", 3f, 1f);
            FakeNode c = Fake.Wing("c", 1f, 0.5f);
            Fake.Span(a, b);
            Fake.Span(b, c);

            Change(NewPropagator(), a, "widthTip", 2.5f);

            Assert.Equal(2.5f, b.Slot("widthRoot").Value, 4);
            Assert.Equal(1f, b.Slot("widthTip").Value, 4);
            Assert.Equal(1f, c.Slot("widthRoot").Value, 4);
        }

        /// <summary>
        /// Link kinds are enforced: a wing dimension will not cross an attach-node
        /// joint even between two parts that both have wing fields.
        /// </summary>
        [Fact]
        public void WingDimensionsDoNotTravelOverStackJoints()
        {
            FakeNode wing = Fake.Wing("wing", 3f, 3f);
            FakeNode other = Fake.Wing("other", 3f, 3f);
            Fake.Stack(wing, other);          // stacked, not spanned

            int changed = Change(NewPropagator(), wing, "widthTip", 2f);

            Assert.Equal(0, changed);
            Assert.Equal(3f, other.Slot("widthRoot").Value, 4);
        }

        /// <summary>
        /// The converse, and the one that matters in practice: a tank's diameter
        /// must not travel onto whatever is surface-attached to its side.
        /// </summary>
        [Fact]
        public void StackDiametersDoNotTravelOverSpanJoints()
        {
            FakeNode tank = Fake.Cylinder("tank", 1.25f);
            FakeNode booster = Fake.Cylinder("booster", 1.25f);
            Fake.Span(tank, booster);         // surface-attached, not stacked

            int changed = Change(NewPropagator(), tank, "diameter", 2f);

            Assert.Equal(0, changed);
            Assert.Equal(1.25f, booster.Slot("diameter").Value, 4);
        }

        /// <summary>
        /// A mirrored channel - a wing's sweep offset - matches the negation of its
        /// neighbour's value rather than the value itself.
        /// </summary>
        [Fact]
        public void MirroredChannelsPairAsEqualAndOpposite()
        {
            var inboard = new FakeNode("inboard");
            inboard.Add(Fake.OffsetRoot, 0f);
            inboard.Add(Fake.OffsetTip, 0.5f);
            var outboard = new FakeNode("outboard");
            outboard.Add(Fake.OffsetRoot, -0.5f);
            outboard.Add(Fake.OffsetTip, 0.25f);
            Fake.Span(inboard, outboard);

            Change(NewPropagator(), inboard, "offsetTip", 1.5f);

            Assert.Equal(-1.5f, outboard.Slot("offsetRoot").Value, 4);
        }

        /// <summary>
        /// A mirrored dimension describes a joint, not a part, so it never continues
        /// past the part it lands on even when the far end would have matched.
        /// </summary>
        [Fact]
        public void MirroredChannelsDoNotCarryThroughAPart()
        {
            // outboard's own tip offset happens to equal the incoming value, but a
            // mirrored dimension describes a joint, not a part, so it stops here.
            var inboard = new FakeNode("inboard");
            inboard.Add(Fake.OffsetTip, 0.5f);
            var outboard = new FakeNode("outboard");
            outboard.Add(Fake.OffsetRoot, -0.5f);
            outboard.Add(Fake.OffsetTip, -0.5f);
            var beyond = new FakeNode("beyond");
            beyond.Add(Fake.OffsetRoot, 0.5f);
            Fake.Span(inboard, outboard);
            Fake.Span(outboard, beyond);

            Change(NewPropagator(), inboard, "offsetTip", 1f);

            Assert.Equal(-1f, outboard.Slot("offsetRoot").Value, 4);
            Assert.Equal(-0.5f, outboard.Slot("offsetTip").Value, 4);
            Assert.Equal(0.5f, beyond.Slot("offsetRoot").Value, 4);
        }

        /// <summary>
        /// A tank with a wing on its side takes part in two independent runs at once,
        /// and a change to one does not leak into the other.
        /// </summary>
        [Fact]
        public void AWingAndAStackShareAPartWithoutInterfering()
        {
            // A procedural tank with a wing on its side: the two runs are separate.
            FakeNode tank = Fake.Cylinder("tank", 3f);
            FakeNode upperTank = Fake.Cylinder("upperTank", 3f);
            FakeNode wing = Fake.Wing("wing", 3f, 3f);
            Fake.Stack(tank, upperTank);
            Fake.Span(tank, wing);

            Change(NewPropagator(), tank, "diameter", 2f);

            Assert.Equal(2f, upperTank.Slot("diameter").Value, 4);
            Assert.Equal(3f, wing.Slot("widthRoot").Value, 4);
        }
        // =====================================================================
        // Side-by-side joints: a control surface lying along a wing
        // =====================================================================

        /// <summary>
        /// The point of the whole side-by-side joint: a control surface has to be as
        /// thick as the wing where the two meet, so a thickness change carries across.
        /// </summary>
        [Fact]
        public void ThicknessCarriesSidewaysOnToAControlSurface()
        {
            FakeNode wing = Fake.Wing("wing", widthRoot: 3f, widthTip: 2f, thicknessRoot: 0.5f, thicknessTip: 0.3f);
            FakeNode flap = Fake.Wing("flap", widthRoot: 1f, widthTip: 1f, thicknessRoot: 0.5f, thicknessTip: 0.3f);
            Fake.Edge(wing, flap);

            Change(NewPropagator(), wing, "thicknessRoot", 0.8f);

            Assert.Equal(0.8f, flap.Slot("thicknessRoot").Value, 4);
            Assert.Equal(0.3f, flap.Slot("thicknessTip").Value, 4);
        }

        /// <summary>
        /// Root meets root and tip meets tip across a side-by-side joint, so a tip
        /// change must land on the tip rather than on the root.
        /// </summary>
        [Fact]
        public void SideBySideJointsPairRootWithRootAndTipWithTip()
        {
            FakeNode wing = Fake.Wing("wing", 3f, 2f, thicknessRoot: 0.5f, thicknessTip: 0.5f);
            FakeNode flap = Fake.Wing("flap", 1f, 1f, thicknessRoot: 0.5f, thicknessTip: 0.5f);
            Fake.Edge(wing, flap);

            Change(NewPropagator(), wing, "thicknessTip", 0.2f);

            Assert.Equal(0.2f, flap.Slot("thicknessTip").Value, 4);
            Assert.Equal(0.5f, flap.Slot("thicknessRoot").Value, 4);
        }

        /// <summary>
        /// A flap's chord is how far forward it reaches, which has nothing to do with
        /// the chord of the wing carrying it. Chord names only end-to-end joints, so
        /// it cannot cross - even though both parts here have the same chord.
        /// </summary>
        [Fact]
        public void ChordDoesNotCarrySidewaysOnToAControlSurface()
        {
            FakeNode wing = Fake.Wing("wing", widthRoot: 3f, widthTip: 3f);
            FakeNode flap = Fake.Wing("flap", widthRoot: 3f, widthTip: 3f);
            Fake.Edge(wing, flap);

            int changed = Change(NewPropagator(), wing, "widthRoot", 1.5f);

            Assert.Equal(0, changed);
            Assert.Equal(3f, flap.Slot("widthRoot").Value, 4);
        }

        /// <summary>
        /// A control surface running the length of a wing should stay that length.
        /// </summary>
        [Fact]
        public void SpanCarriesSidewaysOnToAControlSurface()
        {
            FakeNode wing = Fake.SpanningWing("wing", 3f, 2f, 0.5f, 0.3f, span: 4f);
            FakeNode flap = Fake.SpanningWing("flap", 1f, 1f, 0.5f, 0.3f, span: 4f);
            Fake.Edge(wing, flap);

            Change(NewPropagator(), wing, "spanLength", 6f);

            Assert.Equal(6f, flap.Slot("spanLength").Value, 4);
        }

        /// <summary>
        /// Span is a side-by-side dimension only: consecutive wing segments have
        /// lengths of their own and must not be dragged along by their neighbour.
        /// </summary>
        [Fact]
        public void SpanDoesNotCarryEndToEndBetweenWingSegments()
        {
            FakeNode inboard = Fake.SpanningWing("inboard", 4f, 3f, 0.5f, 0.4f, span: 4f);
            FakeNode outboard = Fake.SpanningWing("outboard", 3f, 2f, 0.4f, 0.3f, span: 4f);
            Fake.Span(inboard, outboard);

            int changed = Change(NewPropagator(), inboard, "spanLength", 6f);

            Assert.Equal(0, changed);
            Assert.Equal(4f, outboard.Slot("spanLength").Value, 4);
        }

        /// <summary>
        /// Thickness names both kinds of joint, so one change reaches the next
        /// segment outboard and the control surface alongside in the same walk.
        /// </summary>
        [Fact]
        public void ThicknessReachesBothTheNextSegmentAndTheControlSurface()
        {
            FakeNode inboard = Fake.Wing("inboard", 4f, 3f, thicknessRoot: 0.5f, thicknessTip: 0.4f);
            FakeNode outboard = Fake.Wing("outboard", 3f, 2f, thicknessRoot: 0.4f, thicknessTip: 0.3f);
            FakeNode flap = Fake.Wing("flap", 1f, 1f, thicknessRoot: 0.5f, thicknessTip: 0.4f);
            Fake.Span(inboard, outboard);
            Fake.Edge(inboard, flap);

            Change(NewPropagator(), inboard, "thicknessTip", 0.25f);

            Assert.Equal(0.25f, outboard.Slot("thicknessRoot").Value, 4);
            Assert.Equal(0.25f, flap.Slot("thicknessTip").Value, 4);
        }

        /// <summary>
        /// A control surface mounted the other way round has its ends swapped, and the
        /// joint has to reflect that or a tip change lands on the wrong end.
        /// </summary>
        [Fact]
        public void AReversedControlSurfaceHasItsEndsSwapped()
        {
            FakeNode wing = Fake.Wing("wing", 3f, 2f, thicknessRoot: 0.5f, thicknessTip: 0.5f);
            FakeNode flap = Fake.Wing("flap", 1f, 1f, thicknessRoot: 0.5f, thicknessTip: 0.5f);
            // Reversed: our root meets its tip.
            wing.LinkList.Add(new AxialLink(flap, PartEnd.Bottom, PartEnd.Top, LinkKind.Edge));
            flap.LinkList.Add(new AxialLink(wing, PartEnd.Top, PartEnd.Bottom, LinkKind.Edge));

            Change(NewPropagator(), wing, "thicknessRoot", 0.9f);

            Assert.Equal(0.9f, flap.Slot("thicknessTip").Value, 4);
            Assert.Equal(0.5f, flap.Slot("thicknessRoot").Value, 4);
        }
        // =====================================================================
        // Partial covers: a flap over only part of a wing
        // =====================================================================

        /// <summary>
        /// A flap over the outboard half of a wing has to match the wing half way
        /// along, not at its root. Root 0.6 and tip 0.2 put the midpoint at 0.4, so
        /// thickening the root to 1.0 puts the midpoint at 0.6.
        /// </summary>
        [Fact]
        public void APartialFlapTakesTheWingsThicknessAtItsOwnStation()
        {
            FakeNode wing = Fake.Wing("wing", 3f, 2f, thicknessRoot: 0.6f, thicknessTip: 0.2f);
            FakeNode flap = Fake.Wing("flap", 1f, 1f, thicknessRoot: 0.4f, thicknessTip: 0.2f);
            Fake.PartialEdge(wing, flap, from: 0.5f, to: 1f);

            Change(NewPropagator(), wing, "thicknessRoot", 1f);

            Assert.Equal(0.6f, flap.Slot("thicknessRoot").Value, 4);   // midpoint of 1.0 and 0.2
            Assert.Equal(0.2f, flap.Slot("thicknessTip").Value, 4);    // the wing's tip, unchanged
        }

        /// <summary>
        /// The same joint driven from the wing's tip: the flap's root sits half way
        /// along, so it moves by half of what the tip moved.
        /// </summary>
        [Fact]
        public void APartialFlapFollowsAChangeAtTheWingsFarEnd()
        {
            FakeNode wing = Fake.Wing("wing", 3f, 2f, thicknessRoot: 0.6f, thicknessTip: 0.2f);
            FakeNode flap = Fake.Wing("flap", 1f, 1f, thicknessRoot: 0.4f, thicknessTip: 0.2f);
            Fake.PartialEdge(wing, flap, from: 0.5f, to: 1f);

            Change(NewPropagator(), wing, "thicknessTip", 0.4f);

            Assert.Equal(0.5f, flap.Slot("thicknessRoot").Value, 4);   // midpoint of 0.6 and 0.4
            Assert.Equal(0.4f, flap.Slot("thicknessTip").Value, 4);
        }

        /// <summary>
        /// An inboard flap covering the first quarter: its tip sits at 0.25, so it
        /// takes a quarter of the way between the wing's new root and its tip.
        /// </summary>
        [Fact]
        public void AnInboardFlapTakesItsOwnStationsToo()
        {
            FakeNode wing = Fake.Wing("wing", 3f, 2f, thicknessRoot: 0.8f, thicknessTip: 0.4f);
            FakeNode flap = Fake.Wing("flap", 1f, 1f, thicknessRoot: 0.8f, thicknessTip: 0.7f);
            Fake.PartialEdge(wing, flap, from: 0f, to: 0.25f);

            Change(NewPropagator(), wing, "thicknessRoot", 0.4f);

            Assert.Equal(0.4f, flap.Slot("thicknessRoot").Value, 4);   // at station 0
            Assert.Equal(0.4f, flap.Slot("thicknessTip").Value, 4);    // quarter of 0.4 to 0.4
        }

        /// <summary>
        /// A flap over half a wing should be half as long as it, so span scales by
        /// the fraction covered rather than being copied.
        /// </summary>
        [Fact]
        public void SpanScalesByHowMuchOfTheWingIsCovered()
        {
            FakeNode wing = Fake.SpanningWing("wing", 3f, 2f, 0.5f, 0.3f, span: 8f);
            FakeNode flap = Fake.SpanningWing("flap", 1f, 1f, 0.4f, 0.3f, span: 4f);
            Fake.PartialEdge(wing, flap, from: 0.5f, to: 1f);

            Change(NewPropagator(), wing, "spanLength", 10f);

            Assert.Equal(5f, flap.Slot("spanLength").Value, 4);
        }

        /// <summary>
        /// A flap that is not already conforming is left alone, the same rule that
        /// stops a stack at the first part of a different size.
        /// </summary>
        [Fact]
        public void APartialFlapThatDoesNotConformIsLeftAlone()
        {
            FakeNode wing = Fake.Wing("wing", 3f, 2f, thicknessRoot: 0.6f, thicknessTip: 0.2f);
            // Its root should be 0.4 to conform at station 0.5; it is not.
            FakeNode flap = Fake.Wing("flap", 1f, 1f, thicknessRoot: 0.9f, thicknessTip: 0.2f);
            Fake.PartialEdge(wing, flap, from: 0.5f, to: 1f);

            Change(NewPropagator(), wing, "thicknessRoot", 1f);

            Assert.Equal(0.9f, flap.Slot("thicknessRoot").Value, 4);
        }
        // =====================================================================
        // Near misses: parts that were meant to match but do not quite
        // =====================================================================

        /// <summary>
        /// Parts that were meant to match are rarely identical to the last decimal.
        /// A neighbour a fraction of a percent out still counts as the same size.
        /// </summary>
        [Fact]
        public void ANeighbourJustInsideTheToleranceStillFollows()
        {
            FakeNode a = Fake.Cylinder("a", 3f);
            FakeNode b = Fake.Cylinder("b", 3.02f);       // 0.67% out, inside the 1% default
            Fake.Stack(a, b);

            Change(NewPropagator(), a, "diameter", 6f);

            Assert.Equal(6f, b.Slot("diameter").Value, 4);
        }

        /// <summary>And one outside it is still a different size, so the run stops.</summary>
        [Fact]
        public void ANeighbourOutsideTheToleranceStillStopsTheRun()
        {
            FakeNode a = Fake.Cylinder("a", 3f);
            FakeNode b = Fake.Cylinder("b", 3.2f);        // 6.25% out
            Fake.Stack(a, b);

            int changed = Change(NewPropagator(), a, "diameter", 6f);

            Assert.Equal(0, changed);
            Assert.Equal(3.2f, b.Slot("diameter").Value, 4);
        }

        /// <summary>The tolerance is a fraction, so it scales with the parts involved.</summary>
        [Fact]
        public void ToleranceIsRelativeToTheSizeInvolved()
        {
            var propagator = NewPropagator();

            Assert.True(propagator.Same(40f, 40.3f));      // 0.75% of a big fairing
            Assert.False(propagator.Same(0.6f, 0.62f));    // 3.2% of a small probe core
        }

        /// <summary>
        /// The default closes the gap: a neighbour that was 0.02 out ends up exactly
        /// on the new size.
        /// </summary>
        [Fact]
        public void MarginNoneBringsANearMissAllTheWayToTheNewSize()
        {
            FakeNode a = Fake.Cylinder("a", 3f);
            FakeNode b = Fake.Cylinder("b", 3.02f);
            Fake.Stack(a, b);

            var propagator = NewPropagator();
            propagator.Margin = MarginMode.None;
            Change(propagator, a, "diameter", 6f);

            Assert.Equal(6f, b.Slot("diameter").Value, 4);
        }

        /// <summary>Absolute keeps the gap as a fixed amount: 3.02 against 3.00 to 6.00 lands at 6.02.</summary>
        [Fact]
        public void MarginAbsoluteKeepsTheGapAsAFixedAmount()
        {
            FakeNode a = Fake.Cylinder("a", 3f);
            FakeNode b = Fake.Cylinder("b", 3.02f);
            Fake.Stack(a, b);

            var propagator = NewPropagator();
            propagator.Margin = MarginMode.Absolute;
            Change(propagator, a, "diameter", 6f);

            Assert.Equal(6.02f, b.Slot("diameter").Value, 4);
        }

        /// <summary>Proportional keeps the gap as a ratio: 3.02 against 3.00 to 6.00 lands at 6.04.</summary>
        [Fact]
        public void MarginProportionalKeepsTheGapAsARatio()
        {
            FakeNode a = Fake.Cylinder("a", 3f);
            FakeNode b = Fake.Cylinder("b", 3.02f);
            Fake.Stack(a, b);

            var propagator = NewPropagator();
            propagator.Margin = MarginMode.Proportional;
            Change(propagator, a, "diameter", 6f);

            Assert.Equal(6.04f, b.Slot("diameter").Value, 4);
        }

        /// <summary>
        /// Every part in the run measures its own gap against the part that actually
        /// changed, not against whichever neighbour was adjusted just before it.
        /// </summary>
        [Fact]
        public void EachPartKeepsItsOwnMarginRatherThanCompounding()
        {
            FakeNode a = Fake.Cylinder("a", 3f);
            FakeNode b = Fake.Cylinder("b", 3.02f);
            FakeNode c = Fake.Cylinder("c", 2.99f);
            Fake.Stack(a, b);
            Fake.Stack(b, c);

            var propagator = NewPropagator();
            propagator.Margin = MarginMode.Absolute;
            Change(propagator, a, "diameter", 6f);

            Assert.Equal(6.02f, b.Slot("diameter").Value, 4);
            Assert.Equal(5.99f, c.Slot("diameter").Value, 4);
        }

        /// <summary>
        /// A margin mode must not disturb parts that already matched exactly, which is
        /// the overwhelmingly common case.
        /// </summary>
        [Fact]
        public void AnExactMatchIsUnaffectedByTheMarginMode()
        {
            FakeNode a = Fake.Cylinder("a", 1.25f);
            FakeNode b = Fake.Cylinder("b", 1.25f);
            Fake.Stack(a, b);

            var propagator = NewPropagator();
            propagator.Margin = MarginMode.Proportional;
            Change(propagator, a, "diameter", 2.5f);

            Assert.Equal(2.5f, b.Slot("diameter").Value, 4);
        }

        /// <summary>
        /// Detection is far tighter than matching: a millimetre of slider movement has
        /// to register as a change even though a millimetre is well inside the 1%
        /// that counts as the same size.
        /// </summary>
        [Fact]
        public void ASmallChangeStillCountsAsAChange()
        {
            FakeNode a = Fake.Cylinder("a", 1.25f);
            FakeNode b = Fake.Cylinder("b", 1.25f);
            Fake.Stack(a, b);

            var propagator = NewPropagator();
            Assert.True(propagator.Changed(1.25f, 1.251f));
            Assert.True(propagator.Same(1.25f, 1.251f));

            Change(propagator, a, "diameter", 1.251f);

            Assert.Equal(1.251f, b.Slot("diameter").Value, 4);
        }
    }
}
