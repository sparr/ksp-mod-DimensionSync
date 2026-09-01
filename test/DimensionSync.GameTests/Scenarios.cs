using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace DimensionSync.GameTests
{
    /// <summary>
    /// The integration scenarios. Each one assembles a real stack out of real
    /// parts, changes one diameter the way something else in the game would, and
    /// then checks what DimensionSync did to the neighbours.
    /// </summary>
    internal static class Scenarios
    {
        /// <summary>ProceduralParts' liquid fuel tank, the workhorse of these scenarios.</summary>
        private const string PPTank = "proceduralTankLiquid";

        /// <summary>ProceduralParts' structural element.</summary>
        private const string PPStructural = "proceduralStructural";

        /// <summary>ProceduralParts' stack decoupler.</summary>
        private const string PPDecoupler = "proceduralStackDecoupler";

        /// <summary>FL-T400, a fixed 1.25 m stock tank, used as an immovable neighbour.</summary>
        private const string StockTank = "fuelTank";

        /// <summary>A B9 procedural wing segment. PartLoader will spell it with dots.</summary>
        private const string PWing = "B9_Aero_Wing_Procedural_TypeA";

        /// <summary>
        /// A B9 edge-mounted control surface. Its config sets isCtrlSrf = true and
        /// its surface node faces +Y, so it mounts along a wing's chordwise edge
        /// rather than root-to-tip like a wing segment.
        /// </summary>
        private const string PWingCtrlSrf = "B9_Aero_Wing_Procedural_TypeB";

        /// <summary>The module B9 Procedural Wings keeps its geometry on.</summary>
        private const string PWingModule = "WingProcedural";

        /// <summary>The shape module a ProceduralParts tank starts out using.</summary>
        private const string PPShapeModule = "ProceduralShapeCylinder";

        /// <summary>ProceduralParts ships more than one cone shape; take whichever the part offers.</summary>
        private static readonly string[] PPConeModules = { "ProceduralShapeCone", "ProceduralShapeBezierCone" };

        /// <summary>Frames to let pass after a change so the poll and the rebuild can run.</summary>
        private const int Settle = 4;

        // Settle is only for the fixture's own spawn-and-wire steps, where nothing
        // has been asked to change shape yet. Anything following a dimension change
        // waits on context.Settled() instead, which waits in wall clock rather than
        // frames because that is how the mods being tested defer their work.

        /// <summary>
        /// Every scenario, in the order they run. Adding one here is all it takes
        /// for pytest to pick it up as a new case.
        /// </summary>
        public static IEnumerable<Scenario> All()
        {
            yield return New("pp_stack_propagates_upwards", PropagatesUpwards,
                "Three identical procedural tanks. Resizing the bottom one should carry the "
                + "new diameter all the way up the stack.");

            yield return New("pp_stack_propagates_downwards", PropagatesDownwards,
                "The same stack driven from the top instead, because propagation has no "
                + "preferred direction.");

            yield return New("pp_root_at_the_top_propagates_downwards", RootAtTheTopPropagatesDownwards,
                "The same three tanks, but built downward so the ROOT of the stack is "
                + "the top tank and its children hang below it. The top tank is changed "
                + "again, exactly as in the previous test - but this time it is the "
                + "parent rather than the leaf. Between the three tests, which end is "
                + "driven and which end is the parent vary independently, so neither "
                + "can be quietly carrying the other.");

            yield return New("pp_direct_field_write_is_detected", DirectFieldWriteIsDetected,
                "A bare field assignment with no KSP events at all - the way several mods "
                + "resize their own parts. The old event-based detection missed these "
                + "entirely. Expect the bottom tank to look wrong afterwards; that is the "
                + "point of the test and the next step explains it.");

            yield return New("pp_neighbour_mesh_is_rebuilt", NeighbourMeshIsRebuilt,
                "The neighbour's mesh has to actually rebuild, not just its number change. "
                + "This is the check that catches onFieldChanged being handed the wrong value.");

            yield return New("pp_near_miss_margin_none", NearMissDropsTheGap,
                "Two tanks that were meant to match but are 0.02 m apart. Anything "
                + "within the match tolerance - 1% by default - counts as the same "
                + "size and follows along. This is the default margin mode: the gap "
                + "is dropped, so 3.02 m lands on 6.00 m alongside its partner.");

            yield return New("pp_near_miss_margin_absolute", NearMissKeepsTheGap,
                "The same near miss with marginMode = absolute, which keeps the gap "
                + "as an amount: the 3.02 m tank keeps its 0.02 m and lands on 6.02 m.");

            yield return New("pp_near_miss_margin_proportional", NearMissKeepsTheRatio,
                "The same near miss with marginMode = proportional, which keeps the "
                + "gap as a ratio: the 3.02 m tank keeps being 0.67% larger and lands "
                + "on 6.04 m.");

            yield return New("pp_repeated_changes_reach_further", RepeatedChangesReachFurther,
                "A 1, 1, 2, 3 stack, with the bottom tank taken to 2 m, then 3 m, then "
                + "4 m. Three identical edits, each reaching one part further up than "
                + "the last - not because the rule changed, but because the previous "
                + "edit brought the next tank into line. Several changes in one "
                + "scenario on purpose: the sequence is what is being tested.");

            yield return New("pp_stops_at_a_different_size", StopsAtADifferentSize,
                "A stack with one odd-sized tank in it. The change should reach that tank and "
                + "stop, leaving it and everything above it alone.");

            yield return New("pp_cone_bottom_only_syncs_downwards", ConeBottomOnlySyncsDownwards,
                "A cone's bottom diameter describes only its lower end, so changing it must "
                + "not disturb what is stacked above the cone. Setting this one up takes an "
                + "extra step: the middle tank has to be switched to the Cone shape first, "
                + "which changes how it looks before any test has begun.");

            yield return New("pp_cone_blocks_when_far_end_differs", ConeBlocksWhenFarEndDiffers,
                "The same cone with a real taper. A change coming up from below should be "
                + "absorbed by the cone rather than carried past it. As above, the middle "
                + "tank is converted to a cone in a separate setup step first.");

            yield return New("pp_no_propagation_without_a_change", NoPropagationWithoutAChange,
                "Two mismatched tanks, left alone. Nothing should happen: the mod must not "
                + "decide by itself that the ship needs tidying up.");

            yield return New("pp_rigid_neighbour_stops_propagation", RigidNeighbourStopsPropagation,
                "A fixed-size stock tank in the middle of the stack. Its diameter is not "
                + "something we can change, so the run ends there.");

            yield return New("pp_surface_attached_part_is_left_alone", SurfaceAttachedPartIsLeftAlone,
                "A booster on the side of a tank is not part of the stack. The tank above "
                + "should follow; the booster should not. Expect the booster to slide "
                + "outward as the core widens - ProceduralParts moves its own "
                + "surface-attached children to keep them on the surface - but its own "
                + "diameter must not change.");

            yield return New("rolib_tank_stack_propagates", ROLibTankStackPropagates,
                "The base case again, but against ROLib, whose field name and change handling "
                + "are nothing like ProceduralParts'.");

            yield return New("mixed_pp_and_rolib_stack", MixedPPAndROLibStack,
                "A ProceduralParts tank under an RO-Tanks tank. The change has to cross "
                + "between two mods that share nothing but the attach node between them. "
                + "The RO tank will also grow longer, which is ROLib's own rule rather "
                + "than anything DimensionSync did.");

            yield return New("pp_booster_stack_syncs_within_itself", BoosterStackSyncsWithinItself,
                "A two-tank booster strapped to the side of a core. Resizing the lower "
                + "booster tank should carry up its own stack and stop there - the "
                + "core is joined to it by a surface attachment, not a stack node.");

            yield return New("pp_booster_sync_reaches_symmetry_counterparts", BoosterSyncReachesSymmetryCounterparts,
                "The same booster, mirrored to both sides. The tank the change reaches "
                + "on one side has a counterpart on the other that nothing can walk to "
                + "- it follows because every field this mod writes goes out to that "
                + "field's symmetry counterparts, exactly as a part action window edit "
                + "does.");

            yield return New("pf_fairing_base_only_syncs_downwards", FairingBaseOnlySyncsDownwards,
                "A Procedural Fairings base describes only its lower end, so a change coming "
                + "up the stack should resize the base and stop rather than resize the payload. "
                + "Expect the base to grow taller as well as wider and to push the payload up - "
                + "Procedural Fairings scales the whole base model from baseSize.");

            yield return New("pwings_tip_syncs_then_carries_when_colinear", WingEdgesBecomeColinearThenCarry,
                "The basic wing joint, and then the same edit again behaving "
                + "differently. Narrowing the inboard segment's tip widens or narrows "
                + "the root of the one outboard of it - the pairing B9's own "
                + "\"Inherit base\" button makes by hand. Because that first edit "
                + "leaves the two segments on one straight edge, the second one "
                + "carries on out to the tip instead of stopping.");

            yield return New("pwings_tip_offset_carries_the_next_wing", WingTipOffsetCarriesTheNextWing,
                "Sweeping one segment's tip should carry the segment outboard of it "
                + "along with it, rather than leaving a kink at the joint. B9 pairs "
                + "these equal and opposite - a root offset against the tip offset "
                + "inboard of it - so the number the neighbour lands on is negated.");

            yield return New("pwings_tip_offset_moves_a_mismatched_wing", WingTipOffsetMovesAMismatchedWing,
                "The same sweep, but with the outboard segment a different size from "
                + "the tip it hangs on. Whether two parts are the same size decides "
                + "whether a DIMENSION crosses the joint; it has nothing to do with "
                + "where the joint physically is. A segment attached to a tip that "
                + "moves has to move with it either way, or it is left hanging in "
                + "space.");

            yield return New("pwings_sweep_carries_through_a_run", WingSweepCarriesThroughARun,
                "Three rectangular segments in a row. Sweeping the first tip should "
                + "sweep all of them: each moves onto the tip in front of it AND has "
                + "its own tip swept to match, so the run ends up as one swept wing "
                + "rather than a kink at every joint.");

            yield return New("pwings_straight_edge_carries_through", WingStraightEdgeCarriesThrough,
                "Three segments forming one straight-edged triangle. No two chords "
                + "match, so nothing here would cross a joint on size - but the "
                + "leading and trailing edges run straight through all three, and "
                + "that is what a wing joint is really about. Narrowing one chord in "
                + "the middle should carry out to the tip and leave the edges straight.");

            yield return New("pwings_sweep_moves_but_does_not_reshape", WingSweepMovesButDoesNotReshape,
                "The third corner of this joint. The two chords match, so the outboard "
                + "segment is attached and must follow a swept tip - but they taper "
                + "differently, so no straight edge runs through the joint and there "
                + "is nothing to continue. It should move and keep its shape.");

            yield return New("pwings_taper_absorbs_the_change", WingTaperAbsorbsTheChange,
                "Three wing segments whose edges do NOT line up: each has a taper of "
                + "its own. With no straight edge running through the joint there is "
                + "nothing to continue, so the change should reach the second "
                + "segment's root and go no further.");

            yield return New("pwings_thickness_does_not_become_chord", WingThicknessDoesNotBecomeChord,
                "A wing whose chord and thickness happen to hold the same number. Changing "
                + "the thickness must not land on the chord.");

            yield return New("pwings_control_surface_span_follows", WingControlSurfaceSpanFollows,
                "A flap running the length of a wing should stay that length when the "
                + "wing is lengthened, and then everything should sit still. Span "
                + "only - the thickness joint is its own test. Seen broadside, since "
                + "this is about how far things reach.");

            yield return New("pwings_control_surface_thickness_follows", WingControlSurfaceThicknessFollows,
                "Control surfaces on a wing's leading and trailing edges. A control "
                + "surface lies ALONGSIDE the wing rather than end to end with it, so "
                + "root meets root and tip meets tip. Thickness and span have to cross "
                + "that joint - a flap must be as thick as the wing where they meet, "
                + "and as long as the wing it runs along.");

            yield return New("pwings_mirrored_control_surface_thickness",
                MirroredControlSurfaceThicknessFollows,
                "The same joint, on a control surface B9 considers MIRRORED. B9 builds "
                + "a mirrored control surface's mesh end for end, so the field it calls "
                + "the tip is the end that meets the wing's root. The change has to land "
                + "on the opposite fields from the unmirrored test to produce the same "
                + "shape on screen - so this should look identical to that test.");

            yield return New("pwings_mirrored_control_surface_refit",
                MirroredControlSurfaceRefits,
                "The same mirrored surface, shortened by hand on a TAPERED wing. "
                + "Shortening a flap makes the mod re-derive its thickness from the "
                + "span of wing it now covers, by a different route from the joint "
                + "test - so it is worth confirming that route agrees about which end "
                + "is which. The end meeting the thick wing root must come out the "
                + "thicker of the two.");

            yield return New("pwings_b9_panel_keeps_our_values", B9PanelKeepsOurValues,
                "Widen a wing until its control surfaces would need a chord wider than "
                + "B9 allows one, then run B9's own panel logic over them. B9 caps a "
                + "control surface at 2 m of chord where it lets a wing have 40, and it "
                + "only enforces that when its panel runs - so a value we leave behind "
                + "can sit there looking fine until the player presses J, at which point "
                + "the part changes size on its own. Nothing should move here.");

            yield return New("pwings_flipped_flap_shears_the_other_way",
                FlippedFlapShearsTheOtherWay,
                "Two flaps on one wing's trailing edge, one mounted the normal way up "
                + "and one turned over, then the wing is swept. Both should end up "
                + "lying along the same edge - which means the numbers written into "
                + "them have to be OPPOSITE, because a flap's offsets are measured in "
                + "its own frame and turning it over reverses that frame. Give them the "
                + "same sign and the flipped one comes out skewed twice as far, the "
                + "wrong way.");

            yield return New("probe_gizmo_after_the_mod_moves_a_part", ProbeGizmoStaleness,
                "A measurement, not a test: select a part with the move tool, let the "
                + "mod move it, and see which of the gizmo's remembered values go stale. "
                + "Reported by a player as axes that stop matching the part and a jump "
                + "on the next drag, and not visible from any value the mod writes.");

            yield return New("probe_what_the_editor_does_to_parts", ProbeEditorPartSetup,
                "A measurement of KSP, not a test of this mod: what the editor's OWN "
                + "part-spawning does that the harness does not. Layers and colliders "
                + "decide whether a part can be hovered or picked at all, and the "
                + "harness has been setting them by hand.");

            yield return New("pwings_dragged_handle_propagates", DraggedHandlePropagates,
                "The root chord changed by DRAGGING, the way a player does it: the "
                + "pointer really moves, B is really held down, and B9 reads the "
                + "movement a frame at a time. Every other scenario writes the field "
                + "instead, which skips the whole of B9's input handling - so this is "
                + "the only one that can tell whether a drag behaves like a typed "
                + "number. Skipped unless the run owns the display.");

            yield return New("pwings_span_runs_along_local_x", SpanRunsAlongLocalX,
                "A measurement of B9, not of this mod: which of a wing's own axes its "
                + "SPAN runs along. The conformance rules assume local x, and an "
                + "assumption that decides where every station on a wing lies is worth "
                + "holding to a measurement.");

            yield return New("pwings_demo_child_length_follows_parent_length",
                DemoChildLengthFollowsParentLength,
                "The preferred answer to the parent-length case: the child should keep "
                + "its parent's edge angles by SHORTENING, not by narrowing its tip. "
                + "Both keep the angles, so the collinearity test cannot tell them "
                + "apart - this one names which was wanted.");

            yield return New("pwings_demo_parent_length_carries_to_child",
                DemoParentLengthCarriesToChild,
                "A craft the player built and reported: symmetric wings, each with a "
                + "child wing whose edges are collinear with its parent's, flaps on "
                + "both. Shortening the PARENT's span has to keep those edges "
                + "collinear - by shortening the child to suit.");

            yield return New("pwings_demo_tip_offset_sequence", DemoTipOffsetSequence,
                "The same craft, with the parent's tip offset walked through the "
                + "sequence the player used: 0.5, 0, 2, -2, -4, -1. The last step is "
                + "the one that threw the control surfaces inside the wing.");

            yield return New("pwings_dragged_chord_reaches_flaps", DraggedChordReachesFlaps,
                "A wing carrying control surfaces, with its root chord changed by "
                + "DRAGGING rather than by writing the field. The surfaces have to "
                + "follow a change that arrived through B9's input handling, which is "
                + "the path a player actually uses and the one every other scenario "
                + "skips. Skipped unless the run owns the display.");

            yield return New("pwings_typed_tip_chord_reaches_outboard_flap",
                TypedTipChordReachesOutboardFlap,
                "The twin of the dragged outboard scenario with the change TYPED "
                + "instead. Its whole job is to say whether a second-order propagation "
                + "failure - inner tip to outer root, outer root to the flap on the "
                + "outer wing - depends on the change having arrived by drag.");

            yield return New("pwings_dragged_chord_reaches_outboard_wing",
                DraggedChordReachesOutboardWing,
                "The same, but the dragged change has two joints to cross: an inner "
                + "wing dragged at its root, an outer wing on its tip, and a control "
                + "surface on the OUTER wing. Skipped unless the run owns the display.");

            yield return New("pwings_outboard_wing_and_flaps_follow",
                OutboardWingAndFlapsFollow,
                "A wing, a second wing on its tip, and control surfaces on the OUTER "
                + "one. Thickening the inner wing's root has to cross two joints to "
                + "reach them: along the inner wing to the joint, across to the outer "
                + "wing, and out again to the flaps hanging off it. Nothing else here "
                + "tests a chain that long, and each link is a different kind of "
                + "joint.");

            yield return New("pwings_flap_the_player_moved_is_left_alone", FlapThePlayerMovedIsLeftAlone,
                "A control surface the PLAYER pulls off its wing, announced the way the "
                + "editor announces it, and then the wing is changed. Somebody who moves "
                + "a flap off an edge has said where they want it, so the rules that "
                + "arrange things along that edge should let go of it - and take it up "
                + "again if it is put back.");

            yield return New("pwings_flap_slid_along_the_wing_stays_put", FlapSlidAlongTheWingStaysPut,
                "A control surface the player slides ALONG its wing, and then the wing "
                + "is changed. It is still against the edge, so the rules should keep "
                + "looking after it - but where somebody put it along the span is their "
                + "decision, and the next edit should not walk it back.");

            yield return New("pwings_partial_span_flap_scales", PartialSpanFlapScalesWithTheWing,
                "The same half-span aileron, lengthening the wing instead. It should "
                + "become half the wing's new length and stay on the outboard half.");

            yield return New("pwings_aileron_length_edit_keeps_it_fitted", AileronLengthEditKeepsItFitted,
                "The one wing scenario driven from the control surface instead of the "
                + "wing. Shortening an aileron by hand leaves it covering less of a "
                + "tapered wing, so the thickness at its new tip is not the thickness "
                + "it had - and since the wing never changed, nothing that watches the "
                + "wing would notice.");

            yield return New("pwings_aileron_moved_along_the_wing_stays_fitted", AileronMovedAlongTheWingStaysFitted,
                "The same idea as the length edit, but harder to spot: the aileron is "
                + "SLID along the wing rather than resized. No field changes at all, "
                + "on it or on the wing, so this can only be noticed by watching where "
                + "the part sits - and on a tapered wing it is now the wrong thickness "
                + "for where it has ended up.");

            yield return New("pwings_aileron_moved_in_small_steps_stays_fitted", AileronMovedInSmallStepsStaysFitted,
                "The same move as the last one, crept rather than jumped - which is "
                + "how a drag actually arrives. Every step is smaller than the "
                + "movement threshold used to be, so a threshold measured from a "
                + "reference that creeps along behind the part would let the whole "
                + "journey through unnoticed.");

            yield return New("pwings_probe_manipulations", ProbeWingManipulations,
                "A sweep rather than a test. A wing with a short control surface on "
                + "the middle of its trailing edge is put through every kind of edit "
                + "in turn, and each step reports the gap between the two and whether "
                + "their thicknesses still agree. It only FAILS on the unambiguous "
                + "things - a flap coming detached, or a dimension going to NaN - so "
                + "the numbers are there to be read rather than judged.");

            yield return New("pwings_probe_swept", ProbeSweptWing,
                "The same sweep of edits on a wing that starts out swept back.");

            yield return New("pwings_probe_tapered", ProbeTaperedWing,
                "The same again on a wing that starts out tapered, 4 m at the root to "
                + "1 m at the tip.");

            yield return New("pwings_probe_swept_tapered_outboard", ProbeSweptTaperedTipSurfaces,
                "Swept AND tapered, with the surfaces out toward the tip - the "
                + "combination that produced the only drift found so far, and the one "
                + "that stopped reproducing when the fixture's numbers changed.");

            yield return New("pwings_probe_full_span", ProbeFullSpanSurfaces,
                "Surfaces running the whole span rather than part of it, which takes "
                + "a different path through the joint rules than a partial one does.");

            yield return New("pwings_control_surface_follows_a_moving_edge", ControlSurfaceFollowsAMovingEdge,
                "A short control surface on the middle of a wing's trailing edge. "
                + "Widening the wing's root pushes that edge back; narrowing its tip "
                + "pulls it forward. Either way the flap has to TRAVEL with the edge, "
                + "not merely skew to match its new angle - skewing alone leaves it "
                + "correctly angled and hanging off the wing.");

            yield return New("pwings_probe_which_end_is_root", ProbeWhichEndIsRoot,
                "A measurement of B9 rather than a test of this mod: a wing and a "
                + "control surface are each given a wide root and a narrow tip, and "
                + "the mesh is asked which of its ends came out wide. Everything that "
                + "pairs a control surface's ends with places along its wing rests on "
                + "that answer, and it has been guessed wrong twice.");

            yield return New("pwings_mirrored_wings_both_follow_thickness", MirroredWingsBothFollowThickness,
                "A mirror-symmetric pair of wings, each with a control surface. In "
                + "forty-odd scenarios there has never been a mirrored part anywhere "
                + "here, and a mirrored part's own axes point the opposite way to its "
                + "twin's - so anything deciding which end is which from those axes "
                + "gets one side of every aircraft ever built exactly backwards.");

            yield return New("pwings_reversed_control_surface_takes_the_right_ends", ReversedControlSurfaceTakesTheRightEnds,
                "A control surface mounted END FOR END, which nothing stops a player "
                + "doing and which no fixture here has ever built. Its own root is out "
                + "at the wing's tip, so thickening the wing's root has to thicken the "
                + "flap's TIP - the usual way round would thin it exactly where the "
                + "wing just got thicker.");

            yield return New("pwings_partial_span_flap_interpolates", PartialSpanFlapInterpolates,
                "An aileron covering only the outboard half of a wing. It should take "
                + "the wing's thickness at its OWN stations - half way along and at the "
                + "tip - rather than the wing's root thickness, and be half as long as "
                + "the wing.");

            yield return New("pwings_control_surface_chord_is_independent", WingControlSurfaceChordIsIndependent,
                "The other half of the same joint: a flap's chord is how far forward "
                + "it reaches, which has nothing to do with the wing's chord. It must "
                + "not cross, even when the two happen to match.");

        }

        /// <summary>Pair a name and its explanation with a body.</summary>
        private static Scenario New(string name, Func<TestContext, IEnumerator> body, string explain) =>
            new Scenario { Name = name, Body = body, Explain = explain };

        // =====================================================================
        // Helpers
        // =====================================================================

        /// <summary>
        /// Spawn a stack bottom-to-top, set each part's diameter while it is still
        /// detached, then attach them. Setting dimensions before attaching keeps
        /// the fixture itself from triggering the very propagation under test.
        /// </summary>
        /// <summary>
        /// A built fixture. Returned through an object rather than an out parameter
        /// because a coroutine cannot have one.
        /// </summary>
        private class Stack
        {
            /// <summary>The parts, in the order they were stacked.</summary>
            public Part[] Parts;

            /// <summary>False when the fixture could not be built, in which case the scenario stops.</summary>
            public bool Ok;
        }

        /// <summary>Build a stack of one repeated part with the given diameters.</summary>
        private static IEnumerator BuildStack(TestContext context, Stack stack,
                                              string partName, string moduleName, string fieldName,
                                              params float[] diameters)
        {
            yield return BuildStack(context, stack, partName, moduleName, fieldName, diameters, false);
        }

        /// <summary>
        /// Build a stack of one repeated part with the given diameters, choosing
        /// which end of it is the root.
        /// </summary>
        /// <param name="rootAtTop">
        /// True to make the topmost part the root and hang the rest below it, false
        /// for the usual bottom-up stack.
        /// </param>
        private static IEnumerator BuildStack(TestContext context, Stack stack,
                                              string partName, string moduleName, string fieldName,
                                              float[] diameters, bool rootAtTop)
        {
            var names = new string[diameters.Length];
            for (int i = 0; i < names.Length; i++) names[i] = partName;   // same part, N times
            yield return BuildStack(context, stack, names, moduleName, fieldName, diameters, rootAtTop);
        }

        /// <summary>
        /// Build a stack of the named parts bottom-to-top, dialling each one's
        /// diameter in before anything is attached. A NaN diameter leaves that
        /// part alone, which is how a fixed-size stock part joins the fixture.
        /// </summary>
        private static IEnumerator BuildStack(TestContext context, Stack stack,
                                              string[] partNames, string moduleName, string fieldName,
                                              float[] diameters, bool rootAtTop = false)
        {
            stack.Parts = new Part[partNames.Length];
            for (int i = 0; i < partNames.Length; i++)
            {
                Part part = EditorBuilder.Spawn(partNames[i]);
                if (part == null)
                {
                    context.Skip($"part '{partNames[i]}' is not installed");
                    yield break;
                }
                stack.Parts[i] = part;
            }

            // Let each part's modules start so their editor controls exist: a
            // procedural part wires onFieldChanged in OnStart, and OnStart runs off
            // the part's own coroutine a frame or two after it is activated.
            yield return context.Frames(6);

            for (int i = 0; i < stack.Parts.Length; i++)
            {
                if (float.IsNaN(diameters[i])) continue;
                PartFields.Set(stack.Parts[i], moduleName, fieldName, diameters[i], WriteMode.PartActionWindow);
            }

            yield return context.Frames(2);

            // Parts[] is always ordered bottom to top; which end of it is the root
            // and therefore which way the parent-child chain runs is what varies.
            if (rootAtTop)
            {
                int top = stack.Parts.Length - 1;
                EditorBuilder.SetRoot(stack.Parts[top]);
                for (int i = top - 1; i >= 0; i--)
                {
                    if (!EditorBuilder.StackBelow(stack.Parts[i + 1], stack.Parts[i]))
                    {
                        context.Result.Error($"could not stack {partNames[i]} under {partNames[i + 1]}");
                        yield break;
                    }
                }
            }
            else
            {
                EditorBuilder.SetRoot(stack.Parts[0]);
                for (int i = 1; i < stack.Parts.Length; i++)
                {
                    if (!EditorBuilder.StackOnTop(stack.Parts[i - 1], stack.Parts[i]))
                    {
                        context.Result.Error($"could not stack {partNames[i]} on {partNames[i - 1]}");
                        yield break;
                    }
                }
            }

            yield return context.Settled();

            // Confirm the fixture is what we asked for before testing anything. A
            // fixture that did not come out as intended is reported as an error
            // rather than a failure, since it says nothing about DimensionSync.
            for (int i = 0; i < stack.Parts.Length; i++)
            {
                if (float.IsNaN(diameters[i])) continue;
                float actual = PartFields.Get(stack.Parts[i], moduleName, fieldName);
                if (Mathf.Abs(actual - diameters[i]) > 0.002f)
                {
                    context.Result.Error(
                        $"fixture: part {i} started at {actual:F4} not {diameters[i]:F4}");
                    yield break;
                }
            }

            EditorBuilder.PresentShip();
            yield return context.Say("Fixture built.",
                                     DescribeStack(partNames, diameters)
                                     + "\n" + DescribeRoot(rootAtTop, stack.Parts.Length));
            stack.Ok = true;
        }

        /// <summary>Say which end of a stack is its root, counting the rest correctly.</summary>
        /// <param name="rootAtTop">True when the topmost part is the root.</param>
        /// <param name="parts">How many parts are in the stack.</param>
        private static string DescribeRoot(bool rootAtTop, int parts)
        {
            string end = rootAtTop ? "TOP" : "BOTTOM";
            string rest = parts > 2 ? "the others are its children" : "the other is its child";
            return $"The {end} tank is the root of this stack; {rest}.";
        }

        /// <summary>One line naming each part in a stack and the diameter it starts at.</summary>
        private static string DescribeStack(string[] partNames, float[] diameters)
        {
            var text = new StringBuilder("Bottom to top: ");
            for (int i = 0; i < partNames.Length; i++)
            {
                if (i > 0) text.Append(", ");
                text.Append(partNames[i]);
                text.Append(float.IsNaN(diameters[i]) ? " (fixed size)" : $" at {diameters[i]:F2} m");
            }
            return text.ToString();
        }

        // =====================================================================
        // ProceduralParts
        // =====================================================================

        /// <summary>
        /// Three equal procedural tanks: resizing the bottom one resizes the rest.
        /// </summary>
        private static IEnumerator PropagatesUpwards(TestContext context)
        {
            var stack = new Stack();
            yield return BuildStack(context, stack, PPTank, PPShapeModule, "diameter", 1.25f, 1.25f, 1.25f);
            if (!stack.Ok) yield break;

            EditorBuilder.WillEdit(stack.Parts[0]);

            yield return context.Say("Setting the bottom tank to 2.5 m, the way the part action window would.",
                                     "Both tanks above it should follow to 2.5 m.");

            PartFields.Set(stack.Parts[0], PPShapeModule, "diameter", 2.5f, WriteMode.PartActionWindow);
            yield return context.Settled();

            context.Check("middle diameter", PartFields.Get(stack.Parts[1], PPShapeModule, "diameter"), 2.5f);
            context.Check("top diameter", PartFields.Get(stack.Parts[2], PPShapeModule, "diameter"), 2.5f);
        }

        /// <summary>
        /// The same stack driven from the top, since propagation has no preferred
        /// direction.
        /// </summary>
        private static IEnumerator PropagatesDownwards(TestContext context)
        {
            var stack = new Stack();
            yield return BuildStack(context, stack, PPTank, PPShapeModule, "diameter", 1.25f, 1.25f, 1.25f);
            if (!stack.Ok) yield break;

            EditorBuilder.WillEdit(stack.Parts[2]);

            yield return context.Say("Setting the top tank to 0.75 m.",
                                     "Both tanks below it should follow to 0.75 m.");

            PartFields.Set(stack.Parts[2], PPShapeModule, "diameter", 0.75f, WriteMode.PartActionWindow);
            yield return context.Settled();

            context.Check("middle diameter", PartFields.Get(stack.Parts[1], PPShapeModule, "diameter"), 0.75f);
            context.Check("bottom diameter", PartFields.Get(stack.Parts[0], PPShapeModule, "diameter"), 0.75f);
        }

        /// <summary>
        /// The same change as <see cref="PropagatesDownwards"/> applied to the same
        /// tank, but with the stack's parentage turned round so that tank is the
        /// root rather than the leaf.
        /// </summary>
        /// <remarks>
        /// Taken with the two tests above this one, the three of them vary which end
        /// of the stack is driven and which end is the parent separately. Without
        /// this one, "changed the top tank" and "changed a leaf" would always be the
        /// same statement, and a rule that only ever walked from child to parent
        /// would pass both.
        /// </remarks>
        private static IEnumerator RootAtTheTopPropagatesDownwards(TestContext context)
        {
            var stack = new Stack();
            yield return BuildStack(context, stack, PPTank, PPShapeModule, "diameter",
                                    new[] { 1.25f, 1.25f, 1.25f }, rootAtTop: true);
            if (!stack.Ok) yield break;

            EditorBuilder.WillEdit(stack.Parts[2]);

            yield return context.Say("Setting the top tank to 0.75 m - the root of this stack.",
                                     "The same change as the previous test, on the same tank in the same "
                                     + "place. What differs is underneath: here the top tank is the parent "
                                     + "and the two below it are its children, so the change travels from "
                                     + "parent to child rather than child to parent. Both tanks below "
                                     + "should still follow to 0.75 m.");

            PartFields.Set(stack.Parts[2], PPShapeModule, "diameter", 0.75f, WriteMode.PartActionWindow);
            yield return context.Settled();

            context.CheckTrue("the top tank really is the root",
                              stack.Parts[2].parent == null && stack.Parts[1].parent == stack.Parts[2]);
            context.Check("middle diameter", PartFields.Get(stack.Parts[1], PPShapeModule, "diameter"), 0.75f);
            context.Check("bottom diameter", PartFields.Get(stack.Parts[0], PPShapeModule, "diameter"), 0.75f);
        }

        /// <summary>
        /// A bare field assignment, with no KSP events at all, still has to be
        /// noticed - that is how several mods resize their own parts.
        /// </summary>
        private static IEnumerator DirectFieldWriteIsDetected(TestContext context)
        {
            var stack = new Stack();
            yield return BuildStack(context, stack, PPTank, PPShapeModule, "diameter", 1.25f, 1.25f);
            if (!stack.Ok) yield break;

            yield return context.Say("Assigning the lower tank's field directly - no SetValue, no events at all.",
                                     "The upper tank should follow to 2 m, mesh and all. Nothing announces this "
                                     + "change, so the poll has to notice it.\n\n"
                                     + "The BOTTOM tank will show 2 m in its part action window but keep its old "
                                     + "shape until you click on it. That is correct. This test writes the field "
                                     + "more crudely than any real mod does - a real one calls its own rebuild "
                                     + "afterwards, the way ProceduralParts' SeekVolume calls "
                                     + "OnShapeDimensionChanged. DimensionSync deliberately leaves the part that "
                                     + "was edited alone: re-firing the callback there would make a well-behaved "
                                     + "mod rebuild twice, and ProceduralParts would move the attached parts "
                                     + "twice with it.");

            PartFields.Set(stack.Parts[0], PPShapeModule, "diameter", 2f, WriteMode.DirectAssignment);
            yield return context.Settled();

            context.Check("upper diameter", PartFields.Get(stack.Parts[1], PPShapeModule, "diameter"), 2f);
        }

        /// <summary>
        /// The neighbour's mesh must actually be rebuilt, not just its KSPField
        /// overwritten. ProceduralParts only rebuilds when onFieldChanged is
        /// handed the field's previous value.
        /// </summary>
        private static IEnumerator NeighbourMeshIsRebuilt(TestContext context)
        {
            var stack = new Stack();
            yield return BuildStack(context, stack, PPTank, PPShapeModule, "diameter", 1.25f, 1.25f);
            if (!stack.Ok) yield break;

            yield return context.Say("Setting the lower tank to 3 m.",
                                     "The upper tank should read 3 m and report a rebuilt mesh at 3 m. Before the fix the number changed but the part stayed 1.25 m wide, so watch the shape rather than the slider.");

            PartFields.Set(stack.Parts[0], PPShapeModule, "diameter", 3f, WriteMode.PartActionWindow);
            yield return context.Settled();

            context.Check("upper field", PartFields.Get(stack.Parts[1], PPShapeModule, "diameter"), 3f);
            context.Check("upper built mesh diameter", PartFields.ProceduralPartsBuiltDiameter(stack.Parts[1]), 3f);
        }

        /// <summary>
        /// A neighbour a fraction of a percent out is still the same size, so it
        /// follows. With the default margin mode it lands exactly on the new size.
        /// </summary>
        private static IEnumerator NearMissDropsTheGap(TestContext context) =>
            NearMiss(context, "none", 6.00f, "the gap is dropped, so the neighbour lands on 6.00 m exactly");

        /// <summary>The same near miss with the gap kept as an amount.</summary>
        private static IEnumerator NearMissKeepsTheGap(TestContext context) =>
            NearMiss(context, "absolute", 6.02f, "the 0.02 m gap is kept as an amount, so the neighbour lands on 6.02 m");

        /// <summary>The same near miss with the gap kept as a ratio.</summary>
        private static IEnumerator NearMissKeepsTheRatio(TestContext context) =>
            NearMiss(context, "proportional", 6.04f, "the gap is kept as a ratio, so the neighbour lands on 6.04 m");

        /// <summary>
        /// Two tanks 0.02 m apart, inside the match tolerance, driven from the
        /// smaller one under a given margin mode.
        /// </summary>
        /// <param name="mode">The margin mode to run under.</param>
        /// <param name="expected">Where the neighbour should end up.</param>
        /// <param name="says">How to describe that in the walkthrough.</param>
        /// <remarks>
        /// One manipulation and one check each, so each mode is its own scenario
        /// with its own fixture. Three modes in one scenario meant three fixtures
        /// and three checks behind a single result, which is harder to step through
        /// and harder to rerun.
        /// </remarks>
        private static IEnumerator NearMiss(TestContext context, string mode, float expected, string says)
        {
            var stack = new Stack();
            yield return BuildStack(context, stack, PPTank, PPShapeModule, "diameter", 3f, 3.02f);
            if (!stack.Ok) yield break;

            if (!PartFields.SetMarginMode(mode))
            {
                context.Skip("could not reach DimensionSync's margin setting");
                yield break;
            }

            EditorBuilder.WillEdit(stack.Parts[0]);

            yield return context.Say($"marginMode = {mode}: setting the lower tank to 6 m.",
                                     "The upper tank is 3.02 m against the lower one's 3.00 m - 0.67% out, "
                                     + "inside the 1% match tolerance - so it counts as the same size and "
                                     + $"follows.\n\nIn this mode {says}.");

            PartFields.Set(stack.Parts[0], PPShapeModule, "diameter", 6f, WriteMode.PartActionWindow);
            yield return context.Settled();

            context.Check("near-miss neighbour followed",
                          PartFields.Get(stack.Parts[1], PPShapeModule, "diameter"), expected);

            // Put the mod back the way it was found, so the scenarios after this one
            // are not quietly running under a mode this one chose.
            PartFields.SetMarginMode("none");
        }

        /// <summary>
        /// A stack with an odd-sized part in it. The change reaches that part and
        /// stops, leaving it and everything above it alone.
        /// </summary>
        private static IEnumerator StopsAtADifferentSize(TestContext context)
        {
            var stack = new Stack();
            yield return BuildStack(context, stack, PPTank, PPShapeModule, "diameter", 1.25f, 1.25f, 2.5f, 1.25f);
            if (!stack.Ok) yield break;

            yield return context.Say("Setting the bottom tank to 1.5 m.",
                                     "The second tank should follow. The 2.5 m tank, and the 1.25 m tank above it, should not move.");

            PartFields.Set(stack.Parts[0], PPShapeModule, "diameter", 1.5f, WriteMode.PartActionWindow);
            yield return context.Settled();

            context.Check("second part follows", PartFields.Get(stack.Parts[1], PPShapeModule, "diameter"), 1.5f);
            context.Check("odd-sized part unchanged", PartFields.Get(stack.Parts[2], PPShapeModule, "diameter"), 2.5f);
            context.Check("part beyond it unchanged", PartFields.Get(stack.Parts[3], PPShapeModule, "diameter"), 1.25f);
        }

        /// <summary>
        /// Turn a ProceduralParts tank into a cone of the given end diameters,
        /// without it flashing through a shape nobody asked for.
        /// </summary>
        /// <param name="part">The tank to convert.</param>
        /// <param name="bottom">The diameter its lower end should end up at.</param>
        /// <param name="top">The diameter its upper end should end up at.</param>
        /// <returns>The name of the cone module that ended up active, or null.</returns>
        /// <remarks>
        /// ProceduralParts keeps one module per shape and enables the chosen one, so
        /// the cone module already exists with its own saved diameters while the
        /// cylinder is showing. Switching first and setting afterwards means the part
        /// is briefly drawn as whatever cone those stale numbers describe - a visible
        /// flash of the wrong shape in the middle of a step that is supposed to be
        /// setting up quietly. Writing the numbers into the dormant module first
        /// means the shape it appears in is the shape it was asked for.
        /// </remarks>
        private static string ConvertToCone(Part part, float bottom, float top)
        {
            // Carry the length across as well as the diameters. Each shape module
            // keeps its own length, so a cone that has never been used comes with a
            // default one - and the part changing length shoves everything stacked
            // above it up or down, in a step that is only supposed to be changing its
            // profile.
            float length = PartFields.Get(part, PartFields.ProceduralPartsShapeModule(part), "length");

            foreach (string candidate in PPConeModules)
            {
                if (PartFields.Module(part, candidate) == null) continue;
                PartFields.Set(part, candidate, "bottomDiameter", bottom, WriteMode.DirectAssignment);
                PartFields.Set(part, candidate, "topDiameter", top, WriteMode.DirectAssignment);
                if (!float.IsNaN(length) && length > 0f)
                    PartFields.Set(part, candidate, "length", length, WriteMode.DirectAssignment);
            }

            return PartFields.SetProceduralPartsShape(part, "Cone") ? null : "failed";
        }

        /// <summary>
        /// Three changes in a row, each reaching further up the stack than the last
        /// as the tanks above come into line.
        /// </summary>
        /// <remarks>
        /// Deliberately several changes in one scenario, which the others avoid. The
        /// thing under test IS the sequence: each change is judged against what the
        /// edited part was immediately before it, so a tank that blocked one change
        /// carries the next. Split into three scenarios that would be three separate
        /// fixtures and the interaction between them - the only reason this exists -
        /// would go with them.
        /// </remarks>
        private static IEnumerator RepeatedChangesReachFurther(TestContext context)
        {
            var stack = new Stack();
            yield return BuildStack(context, stack, PPTank, PPShapeModule, "diameter",
                                    1f, 1f, 2f, 3f);
            if (!stack.Ok) yield break;

            EditorBuilder.WillEdit(stack.Parts[0], growth: 4f);

            yield return context.Say("Taking the bottom tank from 1 m to 2 m.",
                                     "The tank above it is also 1 m, so it follows. The 2 m tank above THAT "
                                     + "was never 1 m, so the run stops under it: 2, 2, 2, 3.");

            PartFields.Set(stack.Parts[0], PPShapeModule, "diameter", 2f, WriteMode.PartActionWindow);
            yield return context.Settled();
            yield return CheckStack(context, stack, "after the first change", 2f, 2f, 2f, 3f);

            yield return context.Say("Now the same tank from 2 m to 3 m.",
                                     "The third tank is 2 m now - it was left alone last time, but the tank "
                                     + "being edited has come up to meet it. So this change reaches one part "
                                     + "further than the last one did: 3, 3, 3, 3.");

            PartFields.Set(stack.Parts[0], PPShapeModule, "diameter", 3f, WriteMode.PartActionWindow);
            yield return context.Settled();
            yield return CheckStack(context, stack, "after the second change", 3f, 3f, 3f, 3f);

            yield return context.Say("And once more, from 3 m to 4 m.",
                                     "Everything is 3 m now, so this one runs the whole way to the top: "
                                     + "4, 4, 4, 4. Three identical edits, each reaching further than the "
                                     + "one before it, purely because of what the ones before it did.");

            PartFields.Set(stack.Parts[0], PPShapeModule, "diameter", 4f, WriteMode.PartActionWindow);
            yield return context.Settled();
            yield return CheckStack(context, stack, "after the third change", 4f, 4f, 4f, 4f);
        }

        /// <summary>Check every tank in a stack at once, naming which step it is.</summary>
        private static IEnumerator CheckStack(TestContext context, Stack stack,
                                              string when, params float[] expected)
        {
            EditorBuilder.PresentShip(reframeCamera: false);
            for (int i = 0; i < expected.Length && i < stack.Parts.Length; i++)
            {
                context.Check($"{when}: tank {i + 1}",
                              PartFields.Get(stack.Parts[i], PPShapeModule, "diameter"), expected[i]);
            }
            yield break;
        }

        /// <summary>
        /// A cone's bottom diameter describes only its lower end, so changing it
        /// must not disturb whatever is stacked above the cone.
        /// </summary>
        private static IEnumerator ConeBottomOnlySyncsDownwards(TestContext context)
        {
            var stack = new Stack();
            yield return BuildStack(context, stack,
                new[] { PPTank, PPTank, PPTank }, PPShapeModule, "diameter",
                new[] { 1.25f, 1.25f, 1.25f });
            if (!stack.Ok) yield break;

            yield return context.Say("Still setting up: switching the middle tank to ProceduralParts' Cone shape, "
                                     + "with both ends at 1.25 m.",
                                     "Expect the middle part to change on this step. Switching shape swaps in a "
                                     + "different ProceduralParts module with its own length, so the part will "
                                     + "visibly resize. It should go straight to a 1.25 m cylinder-shaped cone "
                                     + "without flashing through any other shape on the way.\n\n"
                                     + "Still setting up. More setup steps follow; the walkthrough will say "
                                     + "when the fixture is finished and the test itself begins.");

            if (ConvertToCone(stack.Parts[1], bottom: 1.25f, top: 1.25f) != null)
            {
                context.Skip("could not switch the middle part to a cone");
                yield break;
            }
            yield return context.Settled();

            string cone = ActiveConeModule(stack.Parts[1]);
            if (cone == null)
            {
                context.Skip($"no known cone shape module active: got {PartFields.ProceduralPartsShapeModule(stack.Parts[1])}");
                yield break;
            }
            EditorBuilder.PresentShip();

            EditorBuilder.WillEdit(stack.Parts[1]);

            yield return context.Say("Setting the cone's bottom diameter to 2 m.",
                                     "The tank below should follow. The cone's own top, and the tank above it, should not move.");

            PartFields.Set(stack.Parts[1], cone, "bottomDiameter", 2f, WriteMode.PartActionWindow);
            yield return context.Settled();

            context.Check("part below follows", PartFields.Get(stack.Parts[0], PPShapeModule, "diameter"), 2f);
            context.Check("cone top untouched", PartFields.Get(stack.Parts[1], cone, "topDiameter"), 1.25f);
            context.Check("part above untouched", PartFields.Get(stack.Parts[2], PPShapeModule, "diameter"), 1.25f);
        }

        /// <summary>
        /// With the cone's far end at a different size, a change coming in from
        /// below stops at the cone.
        /// </summary>
        private static IEnumerator ConeBlocksWhenFarEndDiffers(TestContext context)
        {
            var stack = new Stack();
            yield return BuildStack(context, stack,
                new[] { PPTank, PPTank, PPTank }, PPShapeModule, "diameter",
                new[] { 1.25f, 1.25f, 2.5f });
            if (!stack.Ok) yield break;

            yield return context.Say("Still setting up: switching the middle tank to a Cone and giving it a real "
                                     + "taper, 1.25 m at the bottom and 2.5 m at the top.",
                                     "Expect the middle part to change on this step - from a cylinder into an "
                                     + "actual cone, and to change length with it, because the Cone shape module "
                                     + "carries its own dimensions.\n\n"
                                     + "Still setting up. More setup steps follow; the walkthrough will say "
                                     + "when the fixture is finished and the test itself begins.");

            if (ConvertToCone(stack.Parts[1], bottom: 1.25f, top: 2.5f) != null)
            {
                context.Skip("could not switch the middle part to a cone");
                yield break;
            }
            yield return context.Settled();
            string cone = ActiveConeModule(stack.Parts[1]);
            if (cone == null)
            {
                context.Skip("no known cone shape module active");
                yield break;
            }
            EditorBuilder.PresentShip();

            yield return context.Say("Setting the bottom tank to 1.75 m.",
                                     "The cone's bottom should follow. Its 2.5 m top, and the 2.5 m tank above it, should not move.");

            PartFields.Set(stack.Parts[0], PPShapeModule, "diameter", 1.75f, WriteMode.PartActionWindow);
            yield return context.Settled();

            context.Check("cone bottom follows", PartFields.Get(stack.Parts[1], cone, "bottomDiameter"), 1.75f);
            context.Check("cone top unchanged", PartFields.Get(stack.Parts[1], cone, "topDiameter"), 2.5f);
            context.Check("part above unchanged", PartFields.Get(stack.Parts[2], PPShapeModule, "diameter"), 2.5f);
        }

        /// <summary>
        /// Two mismatched tanks left alone stay mismatched. Guards against the poll
        /// deciding by itself that the ship needs tidying up.
        /// </summary>
        private static IEnumerator NoPropagationWithoutAChange(TestContext context)
        {
            var stack = new Stack();
            yield return BuildStack(context, stack, PPTank, PPShapeModule, "diameter", 1.25f, 2.5f);
            if (!stack.Ok) yield break;

            yield return context.Say("Doing nothing at all for ten frames.",
                                     "Both tanks should still read what they started at. Nothing should be tidied up.");

            yield return context.Frames(10);

            context.Check("lower unchanged", PartFields.Get(stack.Parts[0], PPShapeModule, "diameter"), 1.25f);
            context.Check("upper unchanged", PartFields.Get(stack.Parts[1], PPShapeModule, "diameter"), 2.5f);
        }

        /// <summary>
        /// A fixed-size stock part in the middle of the stack ends the run, since
        /// its diameter is not something DimensionSync can change.
        /// </summary>
        /// <summary>
        /// A fixed-size stock tank in the middle of the stack ends the run, since its
        /// diameter is not something DimensionSync can change.
        /// </summary>
        private static IEnumerator RigidNeighbourStopsPropagation(TestContext context)
        {
            var stack = new Stack();
            yield return BuildStack(context, stack,
                new[] { PPTank, StockTank, PPTank }, PPShapeModule, "diameter",
                new[] { 1.25f, float.NaN, 1.25f });
            if (!stack.Ok) yield break;

            PartFields.Set(stack.Parts[0], PPShapeModule, "diameter", 2f, WriteMode.PartActionWindow);
            yield return context.Settled();

            context.Check("part beyond the stock tank unchanged",
                          PartFields.Get(stack.Parts[2], PPShapeModule, "diameter"), 1.25f);
        }

        /// <summary>
        /// A booster on the side of a tank is not part of the stack, so it keeps
        /// its own diameter.
        /// </summary>
        private static IEnumerator SurfaceAttachedPartIsLeftAlone(TestContext context)
        {
            Part core = EditorBuilder.Spawn(PPTank);
            Part above = EditorBuilder.Spawn(PPTank);
            Part booster = EditorBuilder.Spawn(PPTank);
            if (core == null || above == null || booster == null)
            {
                context.Skip("parts unavailable");
                yield break;
            }

            yield return context.Frames(6);
            foreach (Part part in new[] { core, above, booster })
                PartFields.Set(part, PPShapeModule, "diameter", 1.25f, WriteMode.PartActionWindow);

            yield return context.Frames(2);

            EditorBuilder.SetRoot(core);
            if (!EditorBuilder.StackOnTop(core, above))
            {
                context.Result.Error("could not build the fixture");
                yield break;
            }

            // Frame first, then hang the booster ACROSS the camera's view rather than
            // toward it. Toward the camera puts the booster between the viewer and
            // the stack it is supposed to be compared against; across puts it beside
            // the stack, where both are visible at once.
            EditorBuilder.PresentShip();
            Vector3 side = EditorBuilder.DirectionAcrossCamera(core) * EditorBuilder.SurfaceRadius(core);
            if (!EditorBuilder.SurfaceAttach(core, booster, side))
            {
                context.Result.Error("could not attach the booster");
                yield break;
            }
            yield return context.Settled();

            // Where the booster sits around the core's axis, so the check below can
            // tell "pushed outward", which is right, from "moved to another side",
            // which would mean the joint was set up wrongly.
            float bearingBefore = BearingAround(core, booster);
            float radiusBefore = RadiusAround(core, booster);

            yield return context.Say("Setting the core tank to 2 m.",
                                     "The tank stacked on top should follow. The booster on its side is not "
                                     + "part of the stack and should stay at 1.25 m.\n\n"
                                     + "The booster will slide outward as the core widens. That is "
                                     + "ProceduralParts keeping its own surface-attached children on the "
                                     + "surface, and it should stay on the same side of the tank throughout.");

            PartFields.Set(core, PPShapeModule, "diameter", 2f, WriteMode.PartActionWindow);
            yield return context.Settled();

            context.Check("stacked part follows", PartFields.Get(above, PPShapeModule, "diameter"), 2f);
            context.Check("surface-attached part unchanged",
                          PartFields.Get(booster, PPShapeModule, "diameter"), 1.25f);
            context.Check("booster stayed on the same side",
                          Mathf.DeltaAngle(bearingBefore, BearingAround(core, booster)), 0f, 5f);
            context.CheckTrue("booster moved outward with the surface",
                              RadiusAround(core, booster) > radiusBefore + 0.05f);
        }

        /// <summary>Bearing of <paramref name="part"/> around <paramref name="host"/>'s axis, in degrees.</summary>
        private static float BearingAround(Part host, Part part)
        {
            Vector3 local = host.transform.InverseTransformPoint(part.transform.position);
            return Mathf.Atan2(local.z, local.x) * Mathf.Rad2Deg;
        }

        /// <summary>Distance of <paramref name="part"/> from <paramref name="host"/>'s axis.</summary>
        private static float RadiusAround(Part host, Part part)
        {
            Vector3 local = host.transform.InverseTransformPoint(part.transform.position);
            return new Vector2(local.x, local.z).magnitude;
        }

        /// <summary>A core stack with one or two booster stacks on its flank.</summary>
        private class BoosterRig
        {
            /// <summary>The core stack, bottom to top.</summary>
            public Part[] Core;

            /// <summary>The first booster stack, bottom to top.</summary>
            public Part[] Booster;

            /// <summary>The mirrored booster stack, or null when there is only one.</summary>
            public Part[] Mirror;

            public bool Ok;
        }

        /// <summary>
        /// Build a core stack with a two-part booster stack strapped to its side,
        /// optionally mirrored to the opposite flank.
        /// </summary>
        /// <param name="mirrored">True to build a second booster stack as a symmetry counterpart.</param>
        private static IEnumerator BuildBoosterRig(TestContext context, BoosterRig rig, bool mirrored)
        {
            var core = new Stack();
            yield return BuildStack(context, core, PPTank, PPShapeModule, "diameter", 2.5f, 2.5f);
            if (!core.Ok) yield break;
            rig.Core = core.Parts;

            int stacks = mirrored ? 2 : 1;
            var built = new Part[stacks][];

            for (int side = 0; side < stacks; side++)
            {
                var boosters = new Part[2];
                for (int i = 0; i < boosters.Length; i++)
                {
                    boosters[i] = EditorBuilder.Spawn(PPTank);
                    if (boosters[i] == null)
                    {
                        context.Skip("could not spawn a booster");
                        yield break;
                    }
                }
                yield return context.Frames(6);

                foreach (Part part in boosters)
                    PartFields.Set(part, PPShapeModule, "diameter", 1.25f, WriteMode.PartActionWindow);
                yield return context.Frames(2);

                // Hang the stack off the core, then stack its second part on the
                // first so the booster is a run of its own rather than two separate
                // parts on the core's flank.
                EditorBuilder.PresentShip();
                Vector3 out_ = EditorBuilder.DirectionAcrossCamera(rig.Core[0]) * (side == 0 ? 1f : -1f);
                if (!EditorBuilder.SurfaceAttach(rig.Core[0], boosters[0],
                                                 out_ * EditorBuilder.SurfaceRadius(rig.Core[0]))
                    || !EditorBuilder.StackOnTop(boosters[0], boosters[1]))
                {
                    context.Result.Error("could not attach the booster stack");
                    yield break;
                }
                built[side] = boosters;
            }

            rig.Booster = built[0];
            if (mirrored)
            {
                rig.Mirror = built[1];
                for (int i = 0; i < rig.Booster.Length; i++)
                    EditorBuilder.LinkSymmetry(rig.Booster[i], rig.Mirror[i]);
            }

            yield return context.Settled();
            EditorBuilder.PresentShip();
            yield return context.Say("Fixture built.",
                                     "A 2.5 m core of two tanks, with a booster stack of two 1.25 m tanks on "
                                     + (mirrored ? "each side, the two linked as mirror symmetry counterparts."
                                                 : "one side."));
            rig.Ok = true;
        }

        /// <summary>
        /// A booster's own stack syncs within itself and leaves the core alone.
        /// </summary>
        private static IEnumerator BoosterStackSyncsWithinItself(TestContext context)
        {
            var rig = new BoosterRig();
            yield return BuildBoosterRig(context, rig, mirrored: false);
            if (!rig.Ok) yield break;

            EditorBuilder.WillEdit(rig.Booster[0]);

            yield return context.Say("Taking the LOWER booster tank from 1.25 m to 2 m.",
                                     "The booster above it is also 1.25 m, so it should follow to 2 m. The "
                                     + "core is 2.5 m and is joined to the booster by a surface attachment "
                                     + "rather than a stack node, so it should not move at all.");

            PartFields.Set(rig.Booster[0], PPShapeModule, "diameter", 2f, WriteMode.PartActionWindow);
            yield return context.Settled();
            EditorBuilder.PresentShip();

            context.Check("upper booster follows",
                          PartFields.Get(rig.Booster[1], PPShapeModule, "diameter"), 2f);
            context.Check("core bottom untouched",
                          PartFields.Get(rig.Core[0], PPShapeModule, "diameter"), 2.5f);
            context.Check("core top untouched",
                          PartFields.Get(rig.Core[1], PPShapeModule, "diameter"), 2.5f);
        }

        /// <summary>
        /// What a booster stack does, its mirror image does too.
        /// </summary>
        /// <remarks>
        /// The counterpart is not reached by walking the ship - it is on the other
        /// side of the craft, attached to the core independently. It follows because
        /// every field this mod writes goes out to that field's symmetry counterparts
        /// as well, the same way an edit made in the part action window does.
        /// </remarks>
        private static IEnumerator BoosterSyncReachesSymmetryCounterparts(TestContext context)
        {
            var rig = new BoosterRig();
            yield return BuildBoosterRig(context, rig, mirrored: true);
            if (!rig.Ok) yield break;

            EditorBuilder.WillEdit(rig.Booster[0]);

            yield return context.Say("Taking the LOWER booster tank on ONE side from 1.25 m to 2 m.",
                                     "The tank above it should follow, as before. So should the matching tank "
                                     + "on the far side: it is nowhere near the change and is not reachable by "
                                     + "walking the ship from it, but it is a symmetry counterpart of a part "
                                     + "that did change, and a craft with one fat booster and one thin one is "
                                     + "not what anybody asked for.");

            PartFields.Set(rig.Booster[0], PPShapeModule, "diameter", 2f, WriteMode.PartActionWindow);
            yield return context.Settled();
            EditorBuilder.PresentShip();

            context.Check("upper booster on the edited side follows",
                          PartFields.Get(rig.Booster[1], PPShapeModule, "diameter"), 2f);
            context.Check("lower booster on the mirrored side follows",
                          PartFields.Get(rig.Mirror[0], PPShapeModule, "diameter"), 2f);
            context.Check("upper booster on the mirrored side follows",
                          PartFields.Get(rig.Mirror[1], PPShapeModule, "diameter"), 2f);

            // Separately from the numbers: did the meshes actually rebuild? A field
            // can hold the right value on a part that still looks the old size, and
            // that is invisible to every check above.
            context.Check("edited booster's mesh rebuilt",
                          PartFields.ProceduralPartsBuiltDiameter(rig.Booster[0]), 2f);
            context.Check("mirrored lower booster's mesh rebuilt",
                          PartFields.ProceduralPartsBuiltDiameter(rig.Mirror[0]), 2f);
            context.Check("mirrored upper booster's mesh rebuilt",
                          PartFields.ProceduralPartsBuiltDiameter(rig.Mirror[1]), 2f);
            context.Check("core untouched",
                          PartFields.Get(rig.Core[0], PPShapeModule, "diameter"), 2.5f);
        }

        /// <summary>
        /// A Procedural Fairings base describes only its lower end with baseSize,
        /// so a change coming up the stack resizes the base but stops there
        /// instead of resizing the payload sitting on top of it.
        /// </summary>
        private static IEnumerator FairingBaseOnlySyncsDownwards(TestContext context)
        {
            string fairingBase = FirstInstalled("KzResizableFairingBase", "KzResizableFairingBaseRing");
            if (fairingBase == null)
            {
                context.Skip("no Procedural Fairings base installed");
                yield break;
            }

            Part lower = EditorBuilder.Spawn(PPTank);
            Part base_ = EditorBuilder.Spawn(fairingBase);
            Part payload = EditorBuilder.Spawn(PPTank);
            if (lower == null || base_ == null || payload == null)
            {
                context.Skip("parts unavailable");
                yield break;
            }

            yield return context.Frames(6);
            PartFields.Set(lower, PPShapeModule, "diameter", 1.25f, WriteMode.PartActionWindow);
            PartFields.Set(base_, "ProceduralFairingBase", "baseSize", 1.25f, WriteMode.PartActionWindow);
            PartFields.Set(payload, PPShapeModule, "diameter", 1.25f, WriteMode.PartActionWindow);
            yield return context.Frames(2);

            EditorBuilder.SetRoot(lower);
            if (!EditorBuilder.StackOnTop(lower, base_) || !EditorBuilder.StackOnTop(base_, payload))
            {
                context.Result.Error("could not build the fixture");
                yield break;
            }
            yield return context.Settled();

            // Procedural Fairings scales its whole base model from baseSize, so the
            // base is what grows here even though the tank is what is edited.
            EditorBuilder.WillEdit(base_, growth: 2f);

            yield return context.Say("Setting the tank under the fairing base to 2.5 m.",
                                     "The fairing base should widen to match. The payload on its top node should "
                                     + "keep its own 1.25 m diameter.\n\n"
                                     + "The base will also get much TALLER and shove the payload a long way up. "
                                     + "That is Procedural Fairings, not us: UpdatePartProperties sets the whole "
                                     + "model's localScale from baseSize, so a wider base is a proportionally "
                                     + "taller one, and UpdateNodes then moves the top node to "
                                     + "originalPosition * scale with pushAttachments on. Dragging the Base Size "
                                     + "slider by hand does exactly the same. DimensionSync only writes baseSize.");

            PartFields.Set(lower, PPShapeModule, "diameter", 2.5f, WriteMode.PartActionWindow);
            yield return context.Settled();

            context.Check("fairing base follows", PartFields.Get(base_, "ProceduralFairingBase", "baseSize"), 2.5f);
            context.Check("payload above the base unchanged",
                          PartFields.Get(payload, PPShapeModule, "diameter"), 1.25f);
        }

        // =====================================================================
        // B9 Procedural Wings
        // =====================================================================

        /// <summary>
        /// Build a run of wing segments, each surface-attached to the previous
        /// one's tip, with the given root/tip chords set before attachment.
        /// </summary>
        private static IEnumerator BuildWingRun(TestContext context, Stack stack, params float[][] chords)
        {
            yield return BuildWingRun(context, stack, false, 0f, chords);
        }

        /// <summary>
        /// Build a run of wing segments, choosing whether the span is laid across
        /// the camera's view or pointed at it.
        /// </summary>
        /// <param name="oblique">
        /// True to mount the run halfway between broadside and end-on, so the wing's
        /// surface and its root cross section are both in view. That is the view to
        /// use when the test is about thickness, which cannot be seen broadside.
        /// </param>
        /// <param name="obliqueDegrees">
        /// Which way to lean when <paramref name="oblique"/> is set. Negative turns
        /// the run away from the camera so its ROOT end faces the viewer; positive
        /// turns it toward, presenting the TIP. Point it at whichever end the test
        /// changes.
        /// </param>
        private static IEnumerator BuildWingRun(TestContext context, Stack stack,
                                                bool oblique, float obliqueDegrees, params float[][] chords)
        {
            if (EditorBuilder.FindPart(PWing) == null)
            {
                context.Skip("B9 Procedural Wings is not installed");
                yield break;
            }

            stack.Parts = new Part[chords.Length];
            for (int i = 0; i < chords.Length; i++)
            {   // Spawn them all first; they are configured and attached below.
                stack.Parts[i] = EditorBuilder.Spawn(PWing);
                if (stack.Parts[i] == null)
                {
                    context.Skip("could not spawn a procedural wing");
                    yield break;
                }
            }

            yield return context.Frames(8);

            // Set every dimension while the segments are still detached, so building
            // the fixture cannot trigger the propagation under test. A four-entry row
            // also sets thickness; a two-entry row leaves it at the part default.
            for (int i = 0; i < stack.Parts.Length; i++)
            {
                PartFields.Set(stack.Parts[i], PWingModule, "sharedBaseWidthRoot", chords[i][0], WriteMode.DirectAssignment);
                PartFields.Set(stack.Parts[i], PWingModule, "sharedBaseWidthTip", chords[i][1], WriteMode.DirectAssignment);
                if (chords[i].Length > 2)
                {
                    PartFields.Set(stack.Parts[i], PWingModule, "sharedBaseThicknessRoot", chords[i][2], WriteMode.DirectAssignment);
                    PartFields.Set(stack.Parts[i], PWingModule, "sharedBaseThicknessTip", chords[i][3], WriteMode.DirectAssignment);
                }
            }

            yield return context.Frames(4);

            // A wing needs something to hang off, so root the run on a tank.
            Part fuselage = EditorBuilder.Spawn(PPTank);
            if (fuselage == null)
            {
                context.Skip("parts unavailable");
                yield break;
            }
            yield return context.Frames(4);
            EditorBuilder.SetRoot(fuselage);

            // Point the span at the camera or across it, whichever shows what the
            // test is about, so nobody has to move the camera to see it.
            EditorBuilder.PresentShip();
            Vector3 outward = oblique
                ? EditorBuilder.DirectionObliqueToCamera(fuselage, obliqueDegrees)
                : EditorBuilder.DirectionAcrossCamera(fuselage);
            Vector3 rootMount = outward * EditorBuilder.SurfaceRadius(fuselage);
            if (!EditorBuilder.SurfaceAttach(fuselage, stack.Parts[0], rootMount))
            {
                context.Result.Error("could not attach the first wing to the fuselage");
                yield break;
            }

            // Each segment hangs off the previous one's tip, which is how a player
            // builds a multi-segment wing. A B9 wing's span runs along its own +X
            // from a root at the origin, so the tip is sharedBaseLength out.
            for (int i = 1; i < stack.Parts.Length; i++)
            {
                float span = PartFields.Get(stack.Parts[i - 1], PWingModule, "sharedBaseLength");
                if (float.IsNaN(span) || span <= 0f) span = 4f;

                if (!EditorBuilder.SurfaceAttach(stack.Parts[i - 1], stack.Parts[i],
                                                 new Vector3(span, 0f, 0f)))
                {
                    context.Result.Error($"could not attach wing {i} to wing {i - 1}");
                    yield break;
                }
            }

            yield return context.Settled();

            // Confirm the fixture before testing anything: an unexpected starting
            // chord would make the assertions below meaningless.
            for (int i = 0; i < stack.Parts.Length; i++)
            {
                float root = PartFields.Get(stack.Parts[i], PWingModule, "sharedBaseWidthRoot");
                float tip = PartFields.Get(stack.Parts[i], PWingModule, "sharedBaseWidthTip");
                if (Mathf.Abs(root - chords[i][0]) > 0.002f || Mathf.Abs(tip - chords[i][1]) > 0.002f)
                {
                    context.Result.Error(
                        $"fixture: wing {i} started at {root:F3}/{tip:F3} not {chords[i][0]:F3}/{chords[i][1]:F3}");
                    yield break;
                }
            }

            var described = new StringBuilder("Root to tip: ");
            for (int i = 0; i < chords.Length; i++)
            {
                if (i > 0) described.Append(", ");
                described.Append($"segment {i + 1} chord {chords[i][0]:F2} m to {chords[i][1]:F2} m");
            }
            EditorBuilder.PresentShip();
            yield return context.Say("Fixture built.", described.ToString());
            stack.Ok = true;
        }

        /// <summary>
        /// Sweeping a segment's tip carries the segment outboard of it with it, so
        /// the joint stays straight instead of developing a kink.
        /// </summary>
        private static IEnumerator WingTipOffsetCarriesTheNextWing(TestContext context)
        {
            var stack = new Stack();
            yield return BuildWingRun(context, stack,
                new[] { 2f, 2f },
                new[] { 2f, 2f });
            if (!stack.Ok) yield break;

            // Both segments start unswept, so the pairing under test is the only
            // thing that can move the outboard one.
            foreach (Part part in stack.Parts)
            {
                PartFields.Set(part, PWingModule, "sharedBaseOffsetRoot", 0f, WriteMode.DirectAssignment);
                PartFields.Set(part, PWingModule, "sharedBaseOffsetTip", 0f, WriteMode.DirectAssignment);
            }
            yield return context.Settled();

            // The outboard segment is about to move a metre along the chord, which
            // here is straight down. Book the room now so the ship holds still while
            // the step being watched actually happens.
            EditorBuilder.ReserveHeadroom(1.5f);

            yield return context.Say("Sweeping the inboard segment's tip back by 1 m.",
                                     "Two things should happen. The outboard segment should MOVE back by 1 m "
                                     + "to stay on the tip it is bolted to, and its own TIP should sweep back "
                                     + "by 1 m as well, so the two segments carry on as one swept wing rather "
                                     + "than one swept segment with a straight one stuck on the end.\n\n"
                                     + "What must not change is the outboard segment's ROOT offset. B9's own "
                                     + "\"inherit base\" button closes this joint by shearing the neighbour's "
                                     + "root to match, which changes a planform the player chose. Moving the "
                                     + "part does the same job without touching its shape.");

            Vector3 before = stack.Parts[1].transform.localPosition;
            PartFields.Set(stack.Parts[0], PWingModule, "sharedBaseOffsetTip", 1f, WriteMode.DirectAssignment);
            yield return context.Settled();
            EditorBuilder.PresentShip();

            context.Check("outboard root offset left alone",
                          PartFields.Get(stack.Parts[1], PWingModule, "sharedBaseOffsetRoot"), 0f);
            context.Check("outboard tip offset continues the sweep",
                          PartFields.Get(stack.Parts[1], PWingModule, "sharedBaseOffsetTip"), 1f);
            context.Check("outboard chord left alone",
                          PartFields.Get(stack.Parts[1], PWingModule, "sharedBaseWidthTip"), 2f);

            // The tip midline moves to -offsetTip on the wing's chord axis, so the
            // segment hanging off it has to move the same way.
            Vector3 after = stack.Parts[1].transform.localPosition;
            context.Check("outboard segment moved along the chord", after.y - before.y, -1f, tolerance: 0.05f);
            context.Check("outboard segment stayed on the tip", after.x - before.x, 0f, tolerance: 0.05f);
        }

        /// <summary>
        /// A swept tip carries the segment attached to it even when the two are
        /// nothing like the same size.
        /// </summary>
        /// <remarks>
        /// Size matching gates whether a VALUE crosses a joint. Where the joint
        /// physically sits is a separate question, and the answer does not depend on
        /// the two parts agreeing about anything: if the tip moves, whatever is
        /// bolted to it moves. Getting these two confused would leave a mismatched
        /// segment floating away from the wing it is attached to.
        /// </remarks>
        private static IEnumerator WingTipOffsetMovesAMismatchedWing(TestContext context)
        {
            var stack = new Stack();
            // 2 m tip against a 3 m root: half as much again, nowhere near a match.
            yield return BuildWingRun(context, stack,
                new[] { 2f, 2f },
                new[] { 3f, 3f });
            if (!stack.Ok) yield break;

            foreach (Part part in stack.Parts)
            {
                PartFields.Set(part, PWingModule, "sharedBaseOffsetRoot", 0f, WriteMode.DirectAssignment);
                PartFields.Set(part, PWingModule, "sharedBaseOffsetTip", 0f, WriteMode.DirectAssignment);
            }
            yield return context.Settled();

            EditorBuilder.ReserveHeadroom(1.5f);

            Vector3 before = stack.Parts[1].transform.localPosition;
            yield return context.Say("Sweeping the inboard segment's tip back by 1 m.",
                                     "The outboard segment's root is 3 m against the inboard tip's 2 m, so no "
                                     + "chord or thickness should cross this joint at all. The segment should "
                                     + "still MOVE the full metre, because the tip it is bolted to moved.");

            PartFields.Set(stack.Parts[0], PWingModule, "sharedBaseOffsetTip", 1f, WriteMode.DirectAssignment);
            yield return context.Settled();
            EditorBuilder.PresentShip();

            Vector3 after = stack.Parts[1].transform.localPosition;
            context.Check("mismatched segment moved along the chord", after.y - before.y, -1f, tolerance: 0.05f);
            context.Check("mismatched segment stayed on the tip", after.x - before.x, 0f, tolerance: 0.05f);
            context.Check("outboard root chord left alone",
                          PartFields.Get(stack.Parts[1], PWingModule, "sharedBaseWidthRoot"), 3f);
            context.Check("outboard root offset left alone",
                          PartFields.Get(stack.Parts[1], PWingModule, "sharedBaseOffsetRoot"), 0f);
        }

        /// <summary>
        /// Sweeping the first tip of a run of three sweeps the whole run.
        /// </summary>
        /// <remarks>
        /// The companion to the chord version below. Sweeping bends the line the
        /// edges run along just as narrowing does, so it has to travel the same way -
        /// but with a wrinkle: every segment MOVES to stay on the tip in front of it
        /// AND has its own tip swept, and neither on its own is enough. Moving alone
        /// leaves each segment unswept and the wing kinked at every joint; sweeping
        /// alone leaves them swept but detached.
        /// </remarks>
        private static IEnumerator WingSweepCarriesThroughARun(TestContext context)
        {
            // Three plain rectangular segments in a row: one straight edge, no sweep
            // anywhere, so everything that happens is the rule under test.
            var stack = new Stack();
            yield return BuildWingRun(context, stack,
                new[] { 2f, 2f },
                new[] { 2f, 2f },
                new[] { 2f, 2f });
            if (!stack.Ok) yield break;

            foreach (Part part in stack.Parts)
            {
                PartFields.Set(part, PWingModule, "sharedBaseOffsetRoot", 0f, WriteMode.DirectAssignment);
                PartFields.Set(part, PWingModule, "sharedBaseOffsetTip", 0f, WriteMode.DirectAssignment);
            }
            yield return context.Settled();

            EditorBuilder.ReserveHeadroom(2.5f);

            Vector3 secondBefore = stack.Parts[1].transform.localPosition;
            Vector3 thirdBefore = stack.Parts[2].transform.localPosition;

            yield return context.Say("Sweeping the FIRST segment's tip back by 1 m.",
                                     "All three should end up as one swept wing. The second and third "
                                     + "segments should each sweep their own tip back by 1 m, and each should "
                                     + "move back a metre to stay on the tip in front of it. Chords should not "
                                     + "change anywhere - this is a sweep, not a taper.");

            PartFields.Set(stack.Parts[0], PWingModule, "sharedBaseOffsetTip", 1f, WriteMode.DirectAssignment);
            yield return context.Settled();
            EditorBuilder.PresentShip();

            context.Check("second segment tip sweeps",
                          PartFields.Get(stack.Parts[1], PWingModule, "sharedBaseOffsetTip"), 1f);
            context.Check("third segment tip sweeps",
                          PartFields.Get(stack.Parts[2], PWingModule, "sharedBaseOffsetTip"), 1f);
            context.Check("second segment root offset left alone",
                          PartFields.Get(stack.Parts[1], PWingModule, "sharedBaseOffsetRoot"), 0f);
            context.Check("chords unchanged throughout",
                          PartFields.Get(stack.Parts[2], PWingModule, "sharedBaseWidthTip"), 2f);

            // Each segment sits a metre further back than the one before it, so the
            // second moves one metre and the third two.
            context.Check("second segment moved onto the swept tip",
                          stack.Parts[1].transform.localPosition.y - secondBefore.y, -1f, tolerance: 0.05f);
            context.Check("third segment moved onto the swept tip",
                          stack.Parts[2].transform.localPosition.y - thirdBefore.y, -1f, tolerance: 0.05f);
        }

        /// <summary>
        /// A run of segments whose edges form one straight line keeps that line when
        /// a chord in the middle of it changes.
        /// </summary>
        /// <remarks>
        /// The companion to the taper test below. There, no two segments share an
        /// edge slope, so a change stops at the first joint. Here every segment is on
        /// the same line, and stopping would leave a kink in an edge that was
        /// straight - so the change carries on out to the tip instead, and the shape
        /// the segments make together survives.
        /// </remarks>
        private static IEnumerator WingStraightEdgeCarriesThrough(TestContext context)
        {
            // Three segments on one line: each chord loses a metre over its 4 m span,
            // so every leading and trailing edge runs at the same slope.
            var stack = new Stack();
            yield return BuildWingRun(context, stack,
                new[] { 6f, 5f },
                new[] { 5f, 4f },
                new[] { 4f, 3f });
            if (!stack.Ok) yield break;

            yield return context.Say("Narrowing the first segment's tip chord to 4.5 m.",
                                     "No two of these chords are the same, so nothing here matches by size. "
                                     + "What they do share is a straight leading edge and a straight trailing "
                                     + "edge running the length of all three.\n\n"
                                     + "Narrowing that one chord steepens the taper of the whole thing, and "
                                     + "the change should carry all the way out to keep the edges straight at "
                                     + "the new angle: the second segment to 4.5 m and 3.0 m, the third to "
                                     + "3.0 m and 1.5 m. Still a triangle, just a narrower one.");

            PartFields.Set(stack.Parts[0], PWingModule, "sharedBaseWidthTip", 4.5f, WriteMode.DirectAssignment);
            yield return context.Settled();
            EditorBuilder.PresentShip();

            context.Check("second segment root follows",
                          PartFields.Get(stack.Parts[1], PWingModule, "sharedBaseWidthRoot"), 4.5f);
            context.Check("second segment tip keeps the edge straight",
                          PartFields.Get(stack.Parts[1], PWingModule, "sharedBaseWidthTip"), 3f);
            context.Check("third segment root follows",
                          PartFields.Get(stack.Parts[2], PWingModule, "sharedBaseWidthRoot"), 3f);
            context.Check("third segment tip keeps the edge straight",
                          PartFields.Get(stack.Parts[2], PWingModule, "sharedBaseWidthTip"), 1.5f);
        }

        /// <summary>
        /// Sweeping a tip whose neighbour is the same size but not in line with it
        /// moves the neighbour and leaves its shape alone.
        /// </summary>
        /// <remarks>
        /// The third corner of this joint's behaviour. Where the edges run straight
        /// through, the sweep continues outward; where the two chords do not even
        /// meet, nothing crosses at all. This is the case in between: the chords
        /// match, so the two are properly joined and the neighbour has to follow the
        /// tip - but they arrive at an angle, so there is no single line to continue
        /// and the neighbour's own planform is none of our business.
        /// </remarks>
        private static IEnumerator WingSweepMovesButDoesNotReshape(TestContext context)
        {
            // The chords meet at 2 m, but the outboard segment tapers twice as hard,
            // so the edges cross at an angle rather than running through.
            var stack = new Stack();
            yield return BuildWingRun(context, stack,
                new[] { 3f, 2f },
                new[] { 2f, 0.5f });
            if (!stack.Ok) yield break;

            foreach (Part part in stack.Parts)
            {
                PartFields.Set(part, PWingModule, "sharedBaseOffsetRoot", 0f, WriteMode.DirectAssignment);
                PartFields.Set(part, PWingModule, "sharedBaseOffsetTip", 0f, WriteMode.DirectAssignment);
            }
            yield return context.Settled();

            EditorBuilder.ReserveHeadroom(1.5f);
            Vector3 before = stack.Parts[1].transform.localPosition;

            yield return context.Say("Sweeping the inboard segment's tip back by 1 m.",
                                     "The chords meet at 2 m, so the outboard segment is genuinely attached "
                                     + "and must move back the full metre with the tip it sits on. But the two "
                                     + "taper at different rates, so their edges meet at an angle - there is no "
                                     + "straight line running through this joint for a sweep to continue "
                                     + "along. Its own tip offset and both its chords should be untouched.");

            PartFields.Set(stack.Parts[0], PWingModule, "sharedBaseOffsetTip", 1f, WriteMode.DirectAssignment);
            yield return context.Settled();
            EditorBuilder.PresentShip();

            context.Check("outboard segment moved onto the swept tip",
                          stack.Parts[1].transform.localPosition.y - before.y, -1f, tolerance: 0.05f);
            context.Check("outboard tip offset not swept",
                          PartFields.Get(stack.Parts[1], PWingModule, "sharedBaseOffsetTip"), 0f);
            context.Check("outboard root offset left alone",
                          PartFields.Get(stack.Parts[1], PWingModule, "sharedBaseOffsetRoot"), 0f);
            context.Check("outboard root chord left alone",
                          PartFields.Get(stack.Parts[1], PWingModule, "sharedBaseWidthRoot"), 2f);
            context.Check("outboard tip chord left alone",
                          PartFields.Get(stack.Parts[1], PWingModule, "sharedBaseWidthTip"), 0.5f);
        }

        /// <summary>
        /// The basic tip-to-root pairing, and then the same edit again travelling
        /// further because the first one left the edges in line.
        /// </summary>
        /// <remarks>
        /// Two changes in one scenario on purpose. Whether a change carries through a
        /// wing joint depends on the shape the previous change left behind, and that
        /// dependency is the whole point here - the same edit does two different
        /// things depending on what happened before it.
        ///
        /// The first step is exactly what a separate "tip syncs to the next wing's
        /// root" scenario used to do, down to the fixture and the numbers, so that
        /// one has been folded in here rather than run twice.
        /// </remarks>
        private static IEnumerator WingEdgesBecomeColinearThenCarry(TestContext context)
        {
            // 4 to 3, then 3 to 1: the outboard segment tapers twice as hard, so the
            // edges meet at an angle.
            var stack = new Stack();
            yield return BuildWingRun(context, stack,
                new[] { 4f, 3f },
                new[] { 3f, 1f });
            if (!stack.Ok) yield break;

            yield return context.Say("Narrowing the inboard tip from 3 m to 2.5 m.",
                                     "The basic pairing first: the outboard segment's ROOT follows the "
                                     + "inboard segment's TIP, which is what B9's own \"Inherit base\" button "
                                     + "does by hand. It should land on 2.5 m, and the outboard tip should "
                                     + "stay at 1 m - the two segments taper at different rates, so there is "
                                     + "no single straight edge through this joint for the change to continue "
                                     + "along.\n\n"
                                     + "But look at what that leaves behind. 4 to 2.5 over four metres and "
                                     + "2.5 to 1 over four metres are the same slope - the joint has just "
                                     + "become a straight edge.");

            PartFields.Set(stack.Parts[0], PWingModule, "sharedBaseWidthTip", 2.5f, WriteMode.DirectAssignment);
            yield return context.Settled();
            EditorBuilder.PresentShip();

            context.Check("first change: outboard root follows the inboard tip",
                          PartFields.Get(stack.Parts[1], PWingModule, "sharedBaseWidthRoot"), 2.5f);
            context.Check("first change: outboard tip stays put",
                          PartFields.Get(stack.Parts[1], PWingModule, "sharedBaseWidthTip"), 1f);

            yield return context.Say("Now widening the same tip from 2.5 m back out to 3 m.",
                                     "The identical kind of edit, on the same field, going the other way - and "
                                     + "it should travel differently, because the joint it is crossing is a "
                                     + "straight edge now. The outboard root should follow to 3 m AND its tip "
                                     + "should go to 2 m, keeping the edge straight, instead of staying at 1 m.");

            PartFields.Set(stack.Parts[0], PWingModule, "sharedBaseWidthTip", 3f, WriteMode.DirectAssignment);
            yield return context.Settled();
            EditorBuilder.PresentShip();

            context.Check("second change: outboard root follows",
                          PartFields.Get(stack.Parts[1], PWingModule, "sharedBaseWidthRoot"), 3f);
            context.Check("second change: outboard tip carries the edge",
                          PartFields.Get(stack.Parts[1], PWingModule, "sharedBaseWidthTip"), 2f);
            context.Check("second change: no sweep introduced",
                          PartFields.Get(stack.Parts[1], PWingModule, "sharedBaseOffsetTip"), 0f);
        }

        /// <summary>A tapered segment absorbs the change instead of passing it on.</summary>
        private static IEnumerator WingTaperAbsorbsTheChange(TestContext context)
        {
            var stack = new Stack();
            yield return BuildWingRun(context, stack,
                new[] { 4f, 3f },
                new[] { 3f, 1f },
                new[] { 1f, 0.5f });
            if (!stack.Ok) yield break;

            yield return context.Say("Narrowing the first segment's tip chord to 2.5 m.",
                                     "The second segment's root should follow. Its 1 m tip, and the third segment beyond it, should not move.");

            PartFields.Set(stack.Parts[0], PWingModule, "sharedBaseWidthTip", 2.5f, WriteMode.DirectAssignment);
            yield return context.Settled();

            context.Check("second wing root follows",
                          PartFields.Get(stack.Parts[1], PWingModule, "sharedBaseWidthRoot"), 2.5f);
            context.Check("second wing tip unchanged",
                          PartFields.Get(stack.Parts[1], PWingModule, "sharedBaseWidthTip"), 1f);
            context.Check("third wing root unchanged",
                          PartFields.Get(stack.Parts[2], PWingModule, "sharedBaseWidthRoot"), 1f);
        }

        /// <summary>
        /// Chord and thickness are separate channels, so a thickness change must
        /// not land on a chord that happens to hold the same number.
        /// </summary>
        private static IEnumerator WingThicknessDoesNotBecomeChord(TestContext context)
        {
            var stack = new Stack();
            // Obliquely, not end-on: thickness cannot be seen broadside, but pointed
            // straight at the camera the wing is a sliver and its span is impossible
            // to read. Leaning TOWARD the camera, because what changes here is the
            // first segment's TIP - lean the other way and the view is of the root,
            // which is the end that stays put.
            yield return BuildWingRun(context, stack, oblique: true, obliqueDegrees: 45f,
                new[] { 1f, 1f, 1f, 1f },
                new[] { 1f, 1f, 1f, 1f });
            if (!stack.Ok) yield break;

            yield return context.Say("Halving the first segment's tip thickness to 0.5 m.",
                                     "Every chord here is also 1 m. The outboard root thickness should follow; the outboard root chord must not.");

            PartFields.Set(stack.Parts[0], PWingModule, "sharedBaseThicknessTip", 0.5f, WriteMode.DirectAssignment);
            yield return context.Settled();

            context.Check("outboard root thickness follows",
                          PartFields.Get(stack.Parts[1], PWingModule, "sharedBaseThicknessRoot"), 0.5f);
            context.Check("outboard root chord untouched",
                          PartFields.Get(stack.Parts[1], PWingModule, "sharedBaseWidthRoot"), 1f);
        }

        /// <summary>
        /// Build a wing on a fuselage with a control surface on each edge.
        /// </summary>
        /// <remarks>
        /// The wing's local axes: span along X, chord along -Y, thickness along Z.
        /// The trailing edge therefore sits at -(chord + trailing edge width) and the
        /// leading edge just past 0, both facing back into the wing along Y.
        /// </remarks>
        private class WingRig
        {
            public Part Fuselage;
            public Part Wing;
            public Part Trailing;
            public Part Leading;
            public bool Ok;
        }

        /// <summary>Assemble the wing-and-control-surfaces fixture.</summary>
        /// <remarks>
        /// The control surfaces are given a chord of their own rather than the
        /// wing's. B9 holds control surfaces to a much tighter limit than wings -
        /// sharedBaseWidthRootLimits is (0.01, 40) for a wing but (0.01, 2) for a
        /// control surface - and only enforces it when its own editor panel runs. A
        /// 3 m flap therefore looks fine until somebody hovers it and presses J, at
        /// which point B9 clamps it to 2 m and the root visibly shrinks.
        /// </remarks>
        /// <param name="oblique">
        /// True to mount the wing halfway between broadside and end-on, which shows
        /// its root cross section - the view for a THICKNESS test. False mounts it
        /// broadside, which is the view for a test about length, where what matters
        /// is how far the parts reach rather than what shape their ends are.
        /// </param>
        private static IEnumerator BuildWingRig(TestContext context, WingRig rig,
                                                float chord, float thicknessRoot, float thicknessTip,
                                                float span = 4f, bool oblique = true)
        {
            // B9's own ceiling for a control surface's chord.
            const float controlSurfaceChordLimit = 2f;
            float surfaceChord = Mathf.Min(chord, controlSurfaceChordLimit);

            if (EditorBuilder.FindPart(PWing) == null || EditorBuilder.FindPart(PWingCtrlSrf) == null)
            {
                context.Skip("B9 Procedural Wings is not installed");
                yield break;
            }

            rig.Fuselage = EditorBuilder.Spawn(PPTank);
            rig.Wing = EditorBuilder.Spawn(PWing);
            rig.Trailing = EditorBuilder.Spawn(PWingCtrlSrf);
            rig.Leading = EditorBuilder.Spawn(PWingCtrlSrf);
            if (rig.Fuselage == null || rig.Wing == null || rig.Trailing == null || rig.Leading == null)
            {
                context.Skip("parts unavailable");
                yield break;
            }

            yield return context.Frames(8);

            PartFields.Set(rig.Fuselage, PPShapeModule, "diameter", 1.25f, WriteMode.PartActionWindow);
            foreach (Part part in new[] { rig.Wing, rig.Trailing, rig.Leading })
            {
                float own = part == rig.Wing ? chord : surfaceChord;
                PartFields.Set(part, PWingModule, "sharedBaseWidthRoot", own, WriteMode.DirectAssignment);
                PartFields.Set(part, PWingModule, "sharedBaseWidthTip", own, WriteMode.DirectAssignment);
                PartFields.Set(part, PWingModule, "sharedBaseThicknessRoot", thicknessRoot, WriteMode.DirectAssignment);
                PartFields.Set(part, PWingModule, "sharedBaseThicknessTip", thicknessTip, WriteMode.DirectAssignment);
                // A control surface defaults to a shorter span than a wing. Match
                // them up, since a flap only counts as running along this wing if it
                // is already the same length as it.
                PartFields.Set(part, PWingModule, "sharedBaseLength", span, WriteMode.DirectAssignment);
            }
            yield return context.Frames(4);

            EditorBuilder.SetRoot(rig.Fuselage);
            EditorBuilder.PresentShip();
            Vector3 mount = (oblique
                                ? EditorBuilder.DirectionObliqueToCamera(rig.Fuselage)
                                : EditorBuilder.DirectionAcrossCamera(rig.Fuselage))
                            * EditorBuilder.SurfaceRadius(rig.Fuselage);
            if (!EditorBuilder.SurfaceAttach(rig.Fuselage, rig.Wing, mount))
            {
                context.Result.Error("could not attach the wing to the fuselage");
                yield break;
            }

            // Measured off the wing rather than worked out from its chord: a B9
            // wing's chord straddles its origin, so anything mounted on an edge has
            // to be told where that edge actually is.
            float trailingEdge = EditorBuilder.WingEdge(rig.Wing, trailing: true);
            float leadingEdge = EditorBuilder.WingEdge(rig.Wing, trailing: false);
            if (float.IsNaN(trailingEdge) || float.IsNaN(leadingEdge))
            {
                context.Result.Error("could not find the wing's edges");
                yield break;
            }

            // AttachAlongEdge rather than SurfaceAttach: a control surface has to run
            // the same way as the wing as well as sit against it, and it is neither
            // rooted nor centred at its own origin, so where it ends up is worked out
            // from its measured size rather than from its attach node.
            bool attached =
                EditorBuilder.AttachAlongEdge(rig.Wing, rig.Trailing, new Vector3(0f, trailingEdge, 0f),
                                              Vector3.up, Vector3.right, inboardStation: 0f) &&
                EditorBuilder.AttachAlongEdge(rig.Wing, rig.Leading, new Vector3(0f, leadingEdge, 0f),
                                              Vector3.down, Vector3.right, inboardStation: 0f);
            if (!attached)
            {
                context.Result.Error("could not attach the control surfaces to the wing");
                yield break;
            }

            yield return context.Settled();
            EditorBuilder.PresentShip();

            // A control surface can carry every right number and still be hanging in
            // mid-air, so check it is actually against the wing before testing
            // anything about its dimensions.
            float trailingGap = EditorBuilder.EdgeGap(rig.Wing, rig.Trailing, trailing: true);
            float leadingGap = EditorBuilder.EdgeGap(rig.Wing, rig.Leading, trailing: false);
            if (Mathf.Abs(trailingGap) > 0.15f || Mathf.Abs(leadingGap) > 0.15f)
            {
                context.Result.Error(
                    $"fixture: control surfaces are not on the wing's edges " +
                    $"(trailing off by {trailingGap:F3} m, leading off by {leadingGap:F3} m)");
                yield break;
            }

            yield return context.Say("Fixture built.",
                                     $"A {chord:F1} m wing on the fuselage, {thicknessRoot:F2} m thick at the root "
                                     + $"and {thicknessTip:F2} m at the tip, spanning {span:F1} m, with a control "
                                     + "surface on its trailing edge and another on its leading edge, both the same.");
            rig.Ok = true;
        }

        /// <summary>
        /// Thickness and span cross the side-by-side joint, root to root and tip to
        /// tip, so a flap stays flush with the wing carrying it.
        /// </summary>
        private static IEnumerator WingControlSurfaceThicknessFollows(TestContext context)
        {
            var rig = new WingRig();
            yield return BuildWingRig(context, rig, chord: 3f, thicknessRoot: 0.5f, thicknessTip: 0.5f);
            if (!rig.Ok) yield break;

            yield return context.Say("Thickening the wing's ROOT to 0.9 m.",
                                     "Both control surfaces should thicken to 0.9 m at their roots and stay at "
                                     + "0.5 m at their tips. Root meets root across this joint, so the change has "
                                     + "no business reaching the far end.");

            PartFields.Set(rig.Wing, PWingModule, "sharedBaseThicknessRoot", 0.9f, WriteMode.DirectAssignment);
            yield return context.Settled();
            EditorBuilder.PresentShip();

            context.Check("trailing surface root thickness follows",
                          PartFields.Get(rig.Trailing, PWingModule, "sharedBaseThicknessRoot"), 0.9f);
            context.Check("leading surface root thickness follows",
                          PartFields.Get(rig.Leading, PWingModule, "sharedBaseThicknessRoot"), 0.9f);
            context.Check("trailing surface tip thickness unchanged",
                          PartFields.Get(rig.Trailing, PWingModule, "sharedBaseThicknessTip"), 0.5f);
            context.Check("leading surface tip thickness unchanged",
                          PartFields.Get(rig.Leading, PWingModule, "sharedBaseThicknessTip"), 0.5f);
        }

        /// <summary>
        /// A flap mounted upside down is sheared the opposite way from one that is not.
        /// </summary>
        /// <remarks>
        /// Every other fixture in this suite mounts its control surfaces the same way
        /// up, so none of them can tell a rule that handles orientation from one that
        /// ignores it. This was found on craft a player built by hand, on a wing with
        /// one flap turned over, and it had gone unnoticed for as long as it had
        /// because nothing here could produce that arrangement.
        ///
        /// Asserts opposite signs and equal magnitudes rather than exact numbers: what
        /// is being tested is that orientation is taken into account at all, and the
        /// precise slope belongs to the sweep tests.
        /// </remarks>
        private static IEnumerator FlippedFlapShearsTheOtherWay(TestContext context)
        {
            var rig = new WingRig();
            yield return BuildWingRig(context, rig, chord: 3f, thicknessRoot: 0.5f, thicknessTip: 0.5f);
            if (!rig.Ok) yield break;

            // The rig's leading surface is re-purposed as a second trailing one, mounted
            // end for end, so both lie on the same edge and any difference between them
            // is down to how they are turned.
            //
            // reversed, not upsideDown: turning a flap about the craft's roll axis is a
            // half turn about the WING's chord, which reverses the part's span and its
            // thickness and leaves its chord alone. That is what reversed builds, and
            // what a player who flips a flap actually gets - the two go together in
            // every craft this was found on. upsideDown negates the facing instead,
            // which is a different arrangement and not the one under test here.
            float trailingEdge = EditorBuilder.WingEdge(rig.Wing, trailing: true);
            if (float.IsNaN(trailingEdge)) { context.Result.Error("no trailing edge"); yield break; }

            if (!EditorBuilder.AttachAlongEdge(rig.Wing, rig.Leading, new Vector3(0f, trailingEdge, 0f),
                                               Vector3.up, Vector3.right, inboardStation: 0.5f,
                                               reversed: true))
            {
                context.Result.Error("could not mount the second flap upside down");
                yield break;
            }
            yield return context.Settled();

            yield return context.Say("Two flaps on the trailing edge, the outboard one turned over. "
                                     + "Sweeping the wing by setting its tip offset to 1 m.",
                                     "Both flaps should finish lying along the swept edge, looking the "
                                     + "same as each other.\n\n"
                                     + "Their offset NUMBERS have to come out opposite to achieve that, "
                                     + "because turning a flap over reverses the frame those numbers are "
                                     + "measured in. Equal signs here would mean the flipped one is skewed "
                                     + "twice as far, the wrong way.");

            PartFields.Set(rig.Wing, PWingModule, "sharedBaseOffsetTip", 1f, WriteMode.DirectAssignment);
            yield return context.Settled();
            EditorBuilder.PresentShip();

            float upright = PartFields.Get(rig.Trailing, PWingModule, "sharedBaseOffsetRoot");
            float flipped = PartFields.Get(rig.Leading, PWingModule, "sharedBaseOffsetRoot");

            context.CheckTrue($"the upright flap was sheared at all (got {upright:F3})",
                              Mathf.Abs(upright) > 1e-3f);
            context.CheckTrue($"the flipped flap was sheared at all (got {flipped:F3})",
                              Mathf.Abs(flipped) > 1e-3f);
            context.CheckTrue($"the two were sheared OPPOSITE ways ({upright:F3} vs {flipped:F3})",
                              upright * flipped < 0f);
            context.Check("and by the same amount", Mathf.Abs(flipped), Mathf.Abs(upright), 1e-3f);
        }

        /// <summary>
        /// Measure what the editor's move gizmo remembers before and after the mod
        /// moves the part it is attached to.
        /// </summary>
        /// <remarks>
        /// An earlier attempt at fixing this reasoned from the gizmo's field NAMES and
        /// got it wrong twice over - it forced the coordinate system to absolute and
        /// did not cure the jump. This measures instead: what the values are before,
        /// what they are after, and therefore which of them nobody updated.
        /// </remarks>
        private static IEnumerator ProbeGizmoStaleness(TestContext context)
        {
            if (!SyntheticInput.Available)
            {
                context.Skip(SyntheticInput.Unavailable);
                yield break;
            }

            var rig = new WingRig();
            yield return BuildWingRig(context, rig, chord: 3f, thicknessRoot: 0.5f, thicknessTip: 0.5f,
                                      oblique: false);
            if (!rig.Ok) yield break;

            rig.Fuselage.transform.position += Vector3.up * 10f;
            EditorBuilder.PresentShip();

            // Wait for the camera, then find a point that really lands on the flap.
            Vector3 probe = EditorBuilder.BodyCentreOf(rig.Trailing);
            var lastSeen = new Vector2(float.NaN, float.NaN);
            for (int settle = 0; settle < 300; settle++)
            {
                if (SyntheticInput.ScreenPoint(probe, out int px, out int py))
                {
                    var now = new Vector2(px, py);
                    if ((now - lastSeen).sqrMagnitude <= 1f) break;
                    lastSeen = now;
                }
                yield return context.Frames(5);
            }

            if (!SyntheticInput.ScreenPoint(probe, out int x, out int y)
                || SyntheticInput.PartAt(x, y) != rig.Trailing)
            {
                context.Skip("could not find a screen position that the flap itself is under");
                yield break;
            }

            // Refuse to click near the window edges, where the editor keeps its own
            // interface. A click aimed at a part but landing on the application launcher
            // presses whatever button is there - which is how the stock Messages window
            // came to be stuck open over the save and load buttons.
            int margin = Mathf.RoundToInt(Screen.height * 0.15f);
            if (x < margin || x > Screen.width - margin || y < margin || y > Screen.height - margin)
            {
                context.Skip($"the only aim found ({x}, {y}) is too near the window edge to click safely");
                yield break;
            }

            SyntheticInput.FocusOwnWindow();
            SyntheticInput.Press("2");              // the offset tool
            yield return context.Frames(10);
            SyntheticInput.MoveTo(x, y);
            yield return context.Frames(10);

            // Checked against the GAME's idea of the pointer before clicking. A click
            // sent to a screen position when the window is not at the screen origin
            // lands somewhere else entirely, selects nothing, and leaves this scenario
            // reporting on a gizmo that was never summoned - which reads exactly like a
            // gizmo that failed to appear.
            if (!SyntheticInput.PointerAgrees(x, y, out string where))
            {
                context.Skip($"the pointer did not land where it was aimed - {where}");
                yield break;
            }
            Harness.Log($"GIZMO pointer verified against Unity: {where}");

            SyntheticInput.Click();
            yield return context.Frames(20);

            // Said out loud, because everything downstream is a description of this
            // gizmo and a description of nothing looks much the same in a log.
            Harness.Log($"GIZMO selection: EditorLogic.selectedPart is " +
                        $"{(EditorLogic.SelectedPart == null ? "null" : EditorLogic.SelectedPart.name)}, " +
                        $"offset gizmos in the scene "
                        + $"{UnityEngine.Object.FindObjectsOfType(typeof(EditorGizmos.GizmoOffset)).Length}");
            Harness.Log($"GIZMO after selecting: {DescribeGizmo(rig.Trailing)}");

            yield return context.Say("The flap is selected with the move tool. Now widening the wing's "
                                     + "root chord, which makes the mod move the flap.",
                                     "Whatever the gizmo remembers about where the flap is should move "
                                     + "with it. Anything that does not is what goes stale.");

            PartFields.Set(rig.Wing, PWingModule, "sharedBaseWidthRoot", 5f, WriteMode.DirectAssignment);
            yield return context.Settled();

            Harness.Log($"GIZMO after the mod moved it: {DescribeGizmo(rig.Trailing)}");
            // What the editor announces when a part is moved, and what the offset gizmo
            // itself offers to tell anyone who asks.
            var kinds = new List<string>();
            foreach (object value in System.Enum.GetValues(typeof(ConstructionEventType)))
                kinds.Add($"{value}");
            Harness.Log($"EVENT ConstructionEventType: {string.Join(", ", kinds.ToArray())}");

            var hooks = new List<string>();
            foreach (System.Reflection.FieldInfo field in typeof(GameEvents).GetFields(
                         System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
            {
                string name = field.Name;
                if (name.IndexOf("Editor", System.StringComparison.OrdinalIgnoreCase) < 0
                    && name.IndexOf("Part", System.StringComparison.OrdinalIgnoreCase) < 0) continue;
                hooks.Add(name);
            }
            hooks.Sort();
            Harness.Log($"EVENT GameEvents: {string.Join(", ", hooks.ToArray())}");

            System.Type offset = null;
            foreach (System.Reflection.Assembly assembly in System.AppDomain.CurrentDomain.GetAssemblies())
            {
                offset = assembly.GetType("EditorGizmos.GizmoOffset");
                if (offset != null) break;
            }
            if (offset != null)
            {
                var callbacks = new List<string>();
                foreach (System.Reflection.FieldInfo field in offset.GetFields(
                             System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
                             | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static))
                    if (field.FieldType.Name.StartsWith("Callback"))
                        callbacks.Add($"{field.FieldType.Name} {field.Name}");
                Harness.Log($"EVENT GizmoOffset callbacks: {string.Join(", ", callbacks.ToArray())}");
            }

            context.CheckTrue("this scenario only reports; it does not assert", true);
        }

        /// <summary>Every value a move gizmo holds about the part it is attached to.</summary>
        private static string DescribeGizmo(Part part)
        {
            System.Type type = null;
            foreach (System.Reflection.Assembly assembly in System.AppDomain.CurrentDomain.GetAssemblies())
            {
                type = assembly.GetType("EditorGizmos.GizmoOffset");
                if (type != null) break;
            }
            if (type == null) return "GizmoOffset not found";

            UnityEngine.Object[] found = UnityEngine.Object.FindObjectsOfType(type);
            if (found == null || found.Length == 0)
                return $"no gizmo exists (part is at {part.transform.position})";

            const System.Reflection.BindingFlags flags =
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static;

            var text = new System.Text.StringBuilder();
            foreach (UnityEngine.Object item in found)
            {
                if (!(item is Component gizmo)) continue;
                text.Append($"part at {part.transform.position} | gizmo at {gizmo.transform.position} | ");
                foreach (string name in new[] { "host", "rotOffset", "trfPos0", "offset0", "offset",
                                                "coordSpace", "isDragging" })
                {
                    System.Reflection.FieldInfo field = type.GetField(name, flags);
                    object value = field == null ? "absent"
                        : field.GetValue(field.IsStatic ? null : gizmo);
                    if (name == "host" && value is Transform host)
                        value = $"{host.name} (this part: {host == part.transform})";
                    text.Append($"{name}={value} ");
                }
            }
            return text.ToString();
        }

        /// <summary>
        /// Report how the editor sets a part up, against how the harness does.
        /// </summary>        /// <summary>
        /// Report how the editor sets a part up, against how the harness does.
        /// </summary>
        /// <remarks>
        /// The harness instantiates a prefab and puts the result on layer 1 by hand.
        /// Layer 1 is TransparentFX - what KSP uses for the part being CARRIED, not for
        /// one that has been placed - so every part this suite builds looks to anything
        /// layer-aware like a ghost that is still following the cursor. That is enough
        /// to stop a hover reaching it, and possibly enough to stop it being picked.
        ///
        /// This finds out what the editor's own route produces instead, so the harness
        /// can do the same rather than approximate it.
        /// </remarks>
        private static IEnumerator ProbeEditorPartSetup(TestContext context)
        {
            Part ours = EditorBuilder.Spawn(PPTank);
            if (ours == null) { context.Skip("no part to inspect"); yield break; }
            yield return context.Frames(8);

            Harness.Log($"SETUP harness part '{ours.name}': layer {ours.gameObject.layer}, " +
                        $"colliders {DescribeColliders(ours)}");
            Harness.Log($"SETUP harness part state: {DescribePartState(ours)}");

            // The same part, made the way the editor makes one.
            AvailablePart available = EditorBuilder.FindPart(PPTank);
            Part theirs = null;

            // SpawnPart hands the new part to the editor as the one being CARRIED
            // rather than returning it - which is the point: that is how a part a player
            // has just picked up begins, and the state the harness has never reproduced.
            // Which of the editor's references ends up holding it is found by looking
            // for what appeared, rather than assumed.
            var before = new HashSet<Part>(UnityEngine.Object.FindObjectsOfType<Part>());
            if (available != null && EditorLogic.fetch != null)
            {
                try
                {
                    EditorLogic.fetch.SpawnPart(available);
                }
                catch (System.Exception error) { Harness.Log($"SETUP SpawnPart threw: {error.Message}"); }
            }
            yield return context.Frames(30);

            foreach (Part candidate in UnityEngine.Object.FindObjectsOfType<Part>())
                if (!before.Contains(candidate)) { theirs = candidate; break; }

            Harness.Log($"SETUP after SpawnPart: SelectedPart " +
                        $"{(EditorLogic.SelectedPart == null ? "null" : EditorLogic.SelectedPart.name)}, " +
                        $"fetch.selPart-like references -> new part " +
                        $"{(theirs == null ? "none appeared" : theirs.name)}, " +
                        $"ship holds {EditorLogic.fetch?.ship?.parts?.Count}");

            if (theirs != null)
            {
                Harness.Log($"SETUP editor part '{theirs.name}': layer {theirs.gameObject.layer}, " +
                            $"colliders {DescribeColliders(theirs)}");
                Harness.Log($"SETUP editor part state: {DescribePartState(theirs)}");
            }
            else
            {
                Harness.Log("SETUP the editor would not spawn one");
            }

            // What the editor offers for doing this properly.
            var methods = new List<string>();
            foreach (System.Reflection.MethodInfo method in typeof(EditorLogic).GetMethods(
                         System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
                         | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static))
            {
                string name = method.Name;
                if (name.IndexOf("Spawn", System.StringComparison.OrdinalIgnoreCase) < 0
                    && name.IndexOf("Attach", System.StringComparison.OrdinalIgnoreCase) < 0
                    && name.IndexOf("Pick", System.StringComparison.OrdinalIgnoreCase) < 0) continue;

                var arguments = new List<string>();
                foreach (System.Reflection.ParameterInfo parameter in method.GetParameters())
                    arguments.Add(parameter.ParameterType.Name);
                methods.Add($"{name}({string.Join(",", arguments.ToArray())})");
            }
            methods.Sort();
            Harness.Log($"SETUP EditorLogic offers: {string.Join(" | ", methods.ToArray())}");

            // And what Part itself offers for collider and layer setup.
            var partMethods = new List<string>();
            foreach (System.Reflection.MethodInfo method in typeof(Part).GetMethods(
                         System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
                         | System.Reflection.BindingFlags.Instance))
            {
                string name = method.Name;
                if (name.IndexOf("Collider", System.StringComparison.OrdinalIgnoreCase) < 0
                    && name.IndexOf("Layer", System.StringComparison.OrdinalIgnoreCase) < 0) continue;
                partMethods.Add(name);
            }
            partMethods.Sort();
            Harness.Log($"SETUP Part offers: {string.Join(" | ", partMethods.ToArray())}");

            // What the editor exposes for keeping its move/rotate gizmo in step with a
            // part that something else has moved.
            var gizmo = new List<string>();
            foreach (System.Reflection.Assembly assembly in System.AppDomain.CurrentDomain.GetAssemblies())
            {
                System.Type[] types;
                try { types = assembly.GetTypes(); }
                catch (System.Exception) { continue; }

                foreach (System.Type type in types)
                {
                    if (type.Name.IndexOf("Gizmo", System.StringComparison.OrdinalIgnoreCase) < 0) continue;
                    var members = new List<string>();
                    foreach (System.Reflection.MemberInfo member in type.GetMembers(
                                 System.Reflection.BindingFlags.Public
                                 | System.Reflection.BindingFlags.Instance
                                 | System.Reflection.BindingFlags.Static))
                    {
                        string name = member.Name;
                        if (name.IndexOf("Update", System.StringComparison.OrdinalIgnoreCase) < 0
                            && name.IndexOf("Attach", System.StringComparison.OrdinalIgnoreCase) < 0
                            && name.IndexOf("Instance", System.StringComparison.OrdinalIgnoreCase) < 0
                            && name.IndexOf("Refresh", System.StringComparison.OrdinalIgnoreCase) < 0
                            && name.IndexOf("Set", System.StringComparison.OrdinalIgnoreCase) < 0) continue;
                        members.Add(name);
                    }
                    if (members.Count > 0) gizmo.Add($"{type.FullName}: {string.Join(",", members.ToArray())}");
                }
            }
            // Take the editor's part away again. ClearShip only knows about parts this
            // harness spawned, so one obtained any other way outlives the scenario that
            // made it - sitting in the floor through every test that follows, and being
            // built into their fixtures.
            if (theirs != null)
            {
                theirs.setParent();
                EditorLogic.fetch?.ship?.Remove(theirs);
                UnityEngine.Object.DestroyImmediate(theirs.gameObject);
                Harness.Log("SETUP removed the editor-spawned part again");
            }

            gizmo.Sort();
            foreach (string line in gizmo) Harness.Log($"SETUP gizmo {line}");

            // Everything on the two gizmos that matter, with signatures, so the refresh
            // can be written against what is actually there.
            foreach (string wanted in new[] { "EditorGizmos.GizmoOffset", "EditorGizmos.GizmoRotate" })
            {
                System.Type type = null;
                foreach (System.Reflection.Assembly assembly in System.AppDomain.CurrentDomain.GetAssemblies())
                {
                    type = assembly.GetType(wanted);
                    if (type != null) break;
                }
                if (type == null) { Harness.Log($"SETUP {wanted} not found"); continue; }

                foreach (System.Reflection.MemberInfo member in type.GetMembers(
                             System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
                             | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static))
                {
                    if (member is System.Reflection.MethodInfo method)
                    {
                        if (method.IsSpecialName) continue;
                        var arguments = new List<string>();
                        foreach (System.Reflection.ParameterInfo parameter in method.GetParameters())
                            arguments.Add($"{parameter.ParameterType.Name} {parameter.Name}");
                        Harness.Log($"SETUP {wanted}.{method.Name}({string.Join(", ", arguments.ToArray())})"
                                    + $" static {method.IsStatic}");
                    }
                    else if (member is System.Reflection.FieldInfo field)
                    {
                        Harness.Log($"SETUP {wanted} field {field.FieldType.Name} {field.Name} " +
                                    $"static {field.IsStatic}");
                    }
                }
            }

            context.CheckTrue("this scenario only reports; it does not assert", true);
        }

        /// <summary>The part state worth comparing between the two ways of making one.</summary>
        /// <remarks>
        /// Modules and attach nodes are what is left once layers and colliders agree.
        /// A part missing an initialised module behaves correctly right up until
        /// something asks that module a question, and a part with the wrong attach nodes
        /// joins to things in the wrong places - both of which would look like mod bugs.
        /// </remarks>
        private static string DescribePartState(Part part)
        {
            var modules = new List<string>();
            foreach (PartModule module in part.Modules) modules.Add(module.GetType().Name);
            modules.Sort();

            var nodes = new List<string>();
            if (part.attachNodes != null)
                foreach (AttachNode node in part.attachNodes)
                    nodes.Add($"{node.id}@{node.position}");
            nodes.Sort();

            return $"isAttached {part.isAttached}, attachMode {part.attachMode}, " +
                   $"parent {(part.parent == null ? "none" : part.parent.name)}, " +
                   $"srfAttachNode {(part.srfAttachNode == null ? "none" : part.srfAttachNode.id)}, " +
                   $"modules {part.Modules.Count} [{string.Join(",", modules.ToArray())}], " +
                   $"nodes {nodes.Count} [{string.Join(",", nodes.ToArray())}]";
        }

        /// <summary>Every collider on a part, with the things that decide picking.</summary>
        private static string DescribeColliders(Part part)
        {
            var text = new List<string>();
            foreach (Collider collider in part.GetComponentsInChildren<Collider>(true))
                text.Add($"{collider.name}(layer {collider.gameObject.layer}, " +
                         $"trigger {collider.isTrigger}, enabled {collider.enabled}, " +
                         $"onRoot {collider.gameObject == part.gameObject})");
            return text.Count == 0 ? "none" : string.Join(", ", text.ToArray());
        }

        /// <summary>
        /// A root chord changed by dragging B9's handle, not by writing the field.
        /// </summary>        /// <summary>
        /// A root chord changed by dragging B9's handle, not by writing the field.
        /// </summary>
        /// <remarks>
        /// The pointer is moved for real and B is held for real, because B9 reads
        /// Input.mousePosition and Input.GetKey directly and neither can be set from
        /// inside the process. It takes a DELTA each frame, so the drag is made in
        /// steps with a frame between them - one jump to the far end would be read as a
        /// single enormous movement, or missed entirely.
        ///
        /// Asserts the direction and that the surfaces followed, not the exact size:
        /// how many metres a drag is worth depends on B9's sensitivity and the camera
        /// distance, and pinning that would make this a test of B9's arithmetic rather
        /// than of whether the mod hears a drag at all.
        /// </remarks>
        private static IEnumerator DraggedHandlePropagates(TestContext context)
        {
            if (!SyntheticInput.Available)
            {
                context.Skip(SyntheticInput.Unavailable);
                yield break;
            }
            if (EditorBuilder.FindPart(PWing) == null)
            {
                context.Skip("B9 Procedural Wings is not installed");
                yield break;
            }

            // A wing on a fuselage and NOTHING else. B9 arms a drag from OnMouseOver,
            // and Unity hands that to whichever collider it picks - which, with a
            // control surface running the wing's whole edge, was the surface rather
            // than the wing however the aim was computed. Aiming past a neighbour is a
            // problem worth avoiding rather than solving: what this scenario is for is
            // whether a DRAGGED change reaches the mod at all, and that needs one part.
            Part fuselage = EditorBuilder.Spawn(PPTank);
            Part wing = EditorBuilder.Spawn(PWing);
            if (fuselage == null || wing == null) { context.Skip("parts unavailable"); yield break; }
            yield return context.Frames(8);

            EditorBuilder.SetRoot(fuselage);
            PartFields.Set(fuselage, PPShapeModule, "diameter", 1.25f, WriteMode.PartActionWindow);
            yield return context.Settled();

            PartFields.Set(wing, PWingModule, "sharedBaseWidthRoot", 3f, WriteMode.DirectAssignment);
            PartFields.Set(wing, PWingModule, "sharedBaseWidthTip", 3f, WriteMode.DirectAssignment);
            PartFields.Set(wing, PWingModule, "sharedBaseLength", 4f, WriteMode.DirectAssignment);
            if (!EditorBuilder.SurfaceAttach(fuselage, wing, new Vector3(0.625f, 0f, 0f)))
            {
                context.Result.Error("could not attach the wing");
                yield break;
            }
            // Lifted clear of the floor before the camera is framed. Without it the wing
            // sits below the bottom of the window - the projection lands at a NEGATIVE
            // screen row, which reads as "not on screen" and looks like a camera or
            // coordinate problem rather than a part that is simply too low to see.
            // Lifted bodily, not by reserving headroom - that keeps a growing part
            // clear of the floor and does not move a craft already standing on it.
            // The wing has to end up somewhere the pointer can reach: too low and it
            // projects to a negative screen row, and only a little higher it lands on
            // the editor's own interface along the bottom of the window, where the
            // hover is taken by the UI and never reaches the part at all.
            fuselage.transform.position += Vector3.up * 10f;
            yield return context.Settled();
            EditorBuilder.PresentShip();

            float chordBefore = PartFields.Get(wing, PWingModule, "sharedBaseWidthRoot");
            float spanBefore = PartFields.Get(wing, PWingModule, "sharedBaseLength");

            ScreenNotes.Show();
            ScreenNotes.Say("Waiting for the camera to finish moving. It reframes onto the craft "
                            + "over about a second, so anything aimed at before it settles is aimed "
                            + "at where the wing USED to be.");

            // Wait for the view to stop moving before working out where anything is.
            //
            // PresentShip reframes the camera and the camera travels to its new framing
            // rather than arriving there. A point projected during that is projected
            // through a camera that is still moving, and by the time the pointer gets
            // there the wing has slid out from under it - while the raycast, the aim and
            // the geometry all agree, because every one of them was computed before the
            // move finished.
            Vector3 probe = wing.transform.TransformPoint(new Vector3(spanBefore * 0.5f, 0f, 0f));
            var lastSeen = new Vector2(float.NaN, float.NaN);
            for (int settle = 0; settle < 300; settle++)
            {
                if (SyntheticInput.ScreenPoint(probe, out int px, out int py))
                {
                    var now = new Vector2(px, py);
                    if ((now - lastSeen).sqrMagnitude <= 1f) break;
                    lastSeen = now;
                }
                yield return context.Frames(5);
            }
            Harness.Log($"DRAG camera settled, wing centre now projects to {lastSeen}");

            ScreenNotes.Say("Camera has stopped. Looking for a point on the wing the pointer can "
                            + "reach - not off screen, and not on the editor's own interface.");

            float wingSpan = spanBefore;
            int x = 0, y = 0;
            bool aimed = false;
            foreach (float station in new[] { 0.5f, 0.35f, 0.65f, 0.25f, 0.75f })
            {
                Vector3 candidate = wing.transform.TransformPoint(new Vector3(wingSpan * station, 0f, 0f));
                if (!SyntheticInput.ScreenPoint(candidate, out int cx, out int cy))
                {
                    // Logged, not skipped silently. A candidate that never reaches the
                    // screen at all and one that lands on the wrong part are different
                    // problems, and without this they look identical from the report.
                    Harness.Log($"DRAG aim at station {station:F2}: {candidate} is not on screen - " +
                                SyntheticInput.Project(candidate));
                    continue;
                }

                // Well clear of the window's edges. KSP's editor puts its own interface
                // along the top and bottom, and a pointer over that is over a UI element
                // rather than the part behind it - the hover never reaches the collider
                // and the drag silently never arms, while every geometric check agrees
                // the aim was right.
                int margin = Mathf.RoundToInt(Screen.height * 0.15f);
                if (cy < margin || cy > Screen.height - margin)
                {
                    Harness.Log($"DRAG aim at station {station:F2} -> ({cx}, {cy}) is too near the " +
                                "window edge, where the editor's own interface is");
                    continue;
                }

                Part hit = SyntheticInput.PartAt(cx, cy);
                Harness.Log($"DRAG aim at station {station:F2} -> ({cx}, {cy}) hits " +
                            $"{(hit == null ? "nothing" : hit.name)}");
                if (hit != wing) continue;

                x = cx; y = cy; aimed = true;
                break;
            }
            if (!aimed)
            {
                context.Skip("no screen position was found that the wing itself is under");
                yield break;
            }

            yield return context.Say($"Dragging the wing's root chord, holding B, from ({x}, {y}).",
                                     "The pointer moves for real, four pixels at a time with a frame "
                                     + "between each, and B9 reads the movement one frame at a time - the "
                                     + "same thing that happens when somebody does this by hand.\n\n"
                                     + "Every other scenario writes the field directly, which skips B9's "
                                     + "input handling entirely. This is the only one that can tell whether "
                                     + "a drag reaches the mod the way a typed number does.");

            SyntheticInput.FocusOwnWindow();
            SyntheticInput.MoveTo(x, y);
            yield return context.Frames(6);

            // Pauses so a screen capture can catch each moment. Without them the whole
            // drag is over in well under a second and any recording of it is a blur of
            // one frame per stage, which is no use to somebody trying to see what the
            // editor is actually doing.
            ScreenNotes.Target = new Vector2(x, Screen.height - y);
            ScreenNotes.Say($"Pointer moved onto the wing at ({x}, {y}). Green square is where the "
                            + "harness aimed; red cross is where the pointer actually is. A raycast "
                            + "from this point reports it hits the WING.");
            yield return context.HoldStill(4f, new TestContext.Ref());
            Harness.Log($"DRAG cameras: {SyntheticInput.Cameras()}; screen {Screen.width}x{Screen.height}; " +
                        $"pointer at {SyntheticInput.PointerLocation()}");

            // Where the collider actually lives. Unity delivers OnMouseOver to the
            // GameObject carrying the collider and NOT to its parents, so a module on
            // the part root never hears a hover on a collider hanging off a child mesh.
            // That is invisible to every geometric check: the aim, the raycast and the
            // part lookup all agree, because they all walk UP to the part.
            Harness.Log($"DRAG wing colliders: {DescribeColliders(wing)}");
            Harness.Log($"DRAG queriesHitTriggers {Physics.queriesHitTriggers}; " +
                        $"camera eventMask {SyntheticInput.PickingCamera?.eventMask}; " +
                        $"module on GO '{ModuleObject(wing)}', part root GO '{wing.gameObject.name}'");

            // Delivery separated from arming. If OnMouseOver is invoked directly and
            // the drag DOES arm, then everything downstream works and the only thing
            // missing is Unity handing the hover to this object - which is a different
            // problem from B9 refusing the input.
            // The pointer is where the GAME thinks it is, not merely where X was told
            // to put it. Those differ whenever the window is not at the screen origin,
            // and the difference is invisible to every other check the harness makes:
            // the projection, the raycast and the part lookup all work in the window's
            // coordinates, so they agree with each other while the pointer sits
            // somewhere else entirely. Asking Unity is the only question with an
            // independent answer.
            if (!SyntheticInput.PointerAgrees(x, y, out string where))
            {
                context.Result.Error($"the pointer did not land where it was aimed - {where}");
                yield break;
            }
            Harness.Log($"DRAG pointer verified against Unity: {where}");

            // Listeners on the part root and on every collider under it. Unity delivers
            // OnMouseOver by SendMessage to an object picked from the collider it hit,
            // and if it delivers to NOTHING then no aiming or timing could have helped -
            // which is worth saying outright rather than leaving as "B9 ignored it".
            Harness.Log($"DRAG probes armed: {HoverProbe.Watch(wing)}");
            SyntheticInput.MoveTo(x + 2, y);
            yield return context.Frames(4);
            SyntheticInput.MoveTo(x, y);
            yield return context.Frames(20);
            Harness.Log($"DRAG probes after hovering: {HoverProbe.Report(wing)}");
            if (HoverProbe.TotalOvers(wing) == 0)
            {
                context.Skip("Unity delivered no hover to the wing or to any collider under "
                             + "it, so nothing could have armed a drag. The pointer is "
                             + $"verified in the game's own coordinates ({where}), so this is "
                             + "Unity's mouse dispatch, not the aim.");
                yield break;
            }

            // Pressed until it takes. B9 arms only when GetKeyDown lands in the same
            // frame as OnMouseOver for this part, which is a one-frame coincidence - and
            // losing that race looks exactly like the mod ignoring the drag. The pointer
            // is nudged a pixel and back between attempts so the hover is re-delivered
            // rather than sitting in whatever state missed it.
            //
            // With the aim verified this takes on the first attempt; the retries are
            // kept for the race, which is real, not for the aim, which is now checked.
            const int attempts = 6;
            for (int attempt = 1; attempt <= attempts; attempt++)
            {
                SyntheticInput.KeyDown("b");
                yield return context.Frames(3);
                if (B9State(wing) != 0) break;

                SyntheticInput.KeyUp("b");
                SyntheticInput.MoveTo(x + 1, y);
                yield return context.Frames(2);
                SyntheticInput.MoveTo(x, y);
                yield return context.Frames(2);
                Harness.Log($"DRAG arm attempt {attempt} did not take, trying again");
            }
            // Checked again with the pointer already there. If the view moved between
            // choosing the point and arriving at it, this is where it shows up - rather
            // than as B9 mysteriously ignoring a drag.
            Harness.Log($"DRAG everything on the ray at ({x}, {y}): {SyntheticInput.Everything(x, y)}");
            Harness.Log($"DRAG ship has {EditorLogic.fetch?.ship?.parts?.Count} parts");
            Part under = SyntheticInput.PartAt(x, y);
            Harness.Log($"DRAG re-check with the pointer in place: ({x}, {y}) is over " +
                        $"{(under == null ? "nothing" : under.name)}");
            if (under != wing)
            {
                context.Skip($"the view moved after the aim was chosen - ({x}, {y}) is now over " +
                             $"{(under == null ? "nothing" : under.name)} rather than the wing");
                yield break;
            }

            ScreenNotes.Say($"Holding B - B9's root-chord key. B9 arms a drag from OnMouseOver on "
                            + $"the frame this key goes down. Its state is now {B9State(wing)}: "
                            + "zero means it did NOT arm, which is the problem. A control surface "
                            + "arms from this same path, so the keystroke is arriving.");
            yield return context.HoldStill(5f, new TestContext.Ref());
            Harness.Log($"DRAG after key down, B9 state {B9State(wing)} (0 means it never armed), " +
                        $"B9 isAttached {B9Flag(wing, "isAttached")}, " +
                        $"pointerOverUI {(UnityEngine.EventSystems.EventSystem.current != null && UnityEngine.EventSystems.EventSystem.current.IsPointerOverGameObject())}, " +
                        $"openPAWs {UIPartActionController.Instance?.windows?.Count}, " +
                        $"locks [{InputLockManager.lockStack.Count}: " +
                        $"{string.Join(", ", new List<string>(InputLockManager.lockStack.Keys).ToArray())}], " +
                        $"uiEditMode {B9Static(wing, "uiEditMode")}, " +
                        $"uiEditModeTimeout {B9Static(wing, "uiEditModeTimeout")}, " +
                        $"part.parent {(wing.parent == null ? "null" : wing.parent.name)}");

            // Smooth: twenty small steps with a frame between each, so every one is a
            // movement B9 can see rather than a jump it reads once or misses.
            const int steps = 20;
            const int pixelsPerStep = 4;
            ScreenNotes.Say("Dragging: 20 steps of 4 pixels, upward, with B held. B9 reads a mouse "
                            + "delta each frame. If it had armed, the wing's root chord would be "
                            + "growing as this happens.");
            for (int step = 1; step <= steps; step++)
            {
                SyntheticInput.MoveTo(x, y - step * pixelsPerStep);
                // Slower than one frame a step while somebody is watching, so the
                // movement is visible rather than instantaneous.
                yield return context.Frames(6);
            }
            ScreenNotes.Say($"Drag finished. Root chord {chordBefore:F3} -> "
                            + $"{PartFields.Get(wing, PWingModule, "sharedBaseWidthRoot"):F3}. "
                            + "Unchanged means B9 never saw the drag at all.");
            yield return context.HoldStill(5f, new TestContext.Ref());

            // If B9 never armed, the input did not reach it and this scenario has not
            // found out anything about the mod. That is a skip, not a failure: a failure
            // here would read as "the mod ignores dragged changes", which is a claim this
            // has no evidence for.
            //
            // Rare now that the pointer is checked against Unity's own idea of where it
            // is. This used to fire every run, and the reason was neither B9 nor the
            // aim: the pointer was being sent to a SCREEN position while every check
            // was computed in WINDOW coordinates, so it sat 128px away over nothing
            // while the projection, the raycast and the part lookup all agreed.
            if (B9State(wing) == 0)
            {
                SyntheticInput.KeyUp("b");
                context.Skip("B9 never armed a drag on the wing, although the pointer is verified "
                             + "against Unity's own position and a hover was delivered.");
                yield break;
            }

            SyntheticInput.KeyUp("b");
            yield return context.Settled();
            EditorBuilder.PresentShip();

            float chordAfter = PartFields.Get(wing, PWingModule, "sharedBaseWidthRoot");
            float spanAfter = PartFields.Get(wing, PWingModule, "sharedBaseLength");
            Harness.Log($"DRAG root chord {chordBefore:F3} -> {chordAfter:F3}, " +
                        $"span {spanBefore:F3} -> {spanAfter:F3}");

            context.CheckTrue($"the drag changed the wing's root chord "
                              + $"({chordBefore:F3} -> {chordAfter:F3})",
                              Mathf.Abs(chordAfter - chordBefore) > 0.01f);
            context.Check("and left its span alone", spanAfter, spanBefore, 1e-3f);
            context.CheckTrue("the wing is still attached", wing.parent == fuselage);
        }

        /// <summary>
        /// B9 builds a wing's span along its own local x.
        /// </summary>
        /// <remarks>
        /// The conformance rules take this for granted everywhere they turn a position
        /// on a wing into a station along it, and getting it wrong would not fail
        /// loudly - it would put every station in the wrong place while every value
        /// still looked plausible. It was assumed from how the rest of the code reads
        /// rather than measured, which is exactly the kind of thing this suite exists
        /// to settle.
        ///
        /// Measured by changing the span and watching which local axis the geometry
        /// actually grows along, rather than by reading a field: a field only says what
        /// B9 was told, and the question is what B9 did.
        /// </remarks>
        private static IEnumerator SpanRunsAlongLocalX(TestContext context)
        {
            if (EditorBuilder.FindPart(PWing) == null)
            {
                context.Skip("B9 Procedural Wings is not installed");
                yield break;
            }

            Part wing = EditorBuilder.Spawn(PWing);
            if (wing == null) { context.Skip("parts unavailable"); yield break; }
            yield return context.Frames(8);

            EditorBuilder.SetRoot(wing);
            PartFields.Set(wing, PWingModule, "sharedBaseWidthRoot", 2f, WriteMode.DirectAssignment);
            PartFields.Set(wing, PWingModule, "sharedBaseWidthTip", 2f, WriteMode.DirectAssignment);
            PartFields.Set(wing, PWingModule, "sharedBaseLength", 2f, WriteMode.DirectAssignment);
            yield return context.Settled();

            Vector3 shortSides = LocalExtentOf(wing);
            PartFields.Set(wing, PWingModule, "sharedBaseLength", 6f, WriteMode.DirectAssignment);
            yield return context.Settled();
            Vector3 longSides = LocalExtentOf(wing);

            Vector3 grew = longSides - shortSides;
            Harness.Log($"SPANAXIS extent at span 2 {shortSides}, at span 6 {longSides}, " +
                        $"grew by {grew}");

            // Four metres of extra span has to appear on one axis and nowhere else.
            context.Check("the span grew along local x", grew.x, 4f, 0.3f);
            context.CheckTrue($"and not along local y (grew {grew.y:F3})", Mathf.Abs(grew.y) < 0.3f);
            context.CheckTrue($"nor along local z (grew {grew.z:F3})", Mathf.Abs(grew.z) < 0.3f);
        }

        /// <summary>How far a part's drawn geometry reaches along each of its own axes.</summary>
        private static Vector3 LocalExtentOf(Part part)
        {
            var box = new Bounds(Vector3.zero, Vector3.zero);
            bool any = false;
            foreach (Renderer renderer in part.GetComponentsInChildren<Renderer>())
            {
                if (!renderer.enabled || renderer is ParticleSystemRenderer) continue;
                MeshFilter filter = renderer.GetComponent<MeshFilter>();
                if (filter?.sharedMesh == null) continue;

                // Every vertex carried back into the PART's own frame, because a child
                // mesh can be turned relative to it and world-space bounds would answer
                // a question about the editor's axes rather than the wing's.
                foreach (Vector3 vertex in filter.sharedMesh.vertices)
                {
                    Vector3 local = part.transform.InverseTransformPoint(
                        renderer.transform.TransformPoint(vertex));
                    if (!any) { box = new Bounds(local, Vector3.zero); any = true; }
                    else box.Encapsulate(local);
                }
            }
            return any ? box.size : Vector3.zero;
        }

        /// <summary>
        /// A child wing keeps its parent's edge angles by shortening, not by narrowing.
        /// </summary>
        /// <remarks>
        /// Both answers hold the angles, so pwings_demo_parent_length_carries_to_child
        /// passes either way. This one records which of them was actually wanted: the
        /// child's own planform is the player's, and reshaping its tip to hold an angle
        /// changes a wing they did not touch.
        /// </remarks>
        private static IEnumerator DemoChildLengthFollowsParentLength(TestContext context)
        {
            var rig = new DemoRig();
            yield return BuildDemoRig(context, rig);
            if (!rig.Ok) yield break;

            float childSpanBefore = PartFields.Get(rig.Child, PWingModule, "sharedBaseLength");
            float childTipBefore = PartFields.Get(rig.Child, PWingModule, "sharedBaseWidthTip");
            float childOffsetBefore = PartFields.Get(rig.Child, PWingModule, "sharedBaseOffsetTip");

            yield return context.Say("Shortening the parent wing from 4 m to 3 m.",
                                     "The child has to keep the parent's edge angles. The way it "
                                     + "should do that is by getting shorter, leaving the planform "
                                     + "the player drew for it otherwise alone.");

            PartFields.Set(rig.Parent, PWingModule, "sharedBaseLength", 3f, WriteMode.DirectAssignment);
            yield return context.Settled();
            EditorBuilder.PresentShip();

            float childSpanAfter = PartFields.Get(rig.Child, PWingModule, "sharedBaseLength");
            float childTipAfter = PartFields.Get(rig.Child, PWingModule, "sharedBaseWidthTip");
            float childOffsetAfter = PartFields.Get(rig.Child, PWingModule, "sharedBaseOffsetTip");
            Harness.Log($"DEMOPREF child span {childSpanBefore:F3} -> {childSpanAfter:F3}, " +
                        $"tip {childTipBefore:F3} -> {childTipAfter:F3}, " +
                        $"tipOffset {childOffsetBefore:F3} -> {childOffsetAfter:F3}");

            context.CheckTrue($"the child was shortened ({childSpanBefore:F3} -> {childSpanAfter:F3})",
                              Mathf.Abs(childSpanAfter - childSpanBefore) > 0.01f);
            context.Check("and kept the tip chord the player gave it",
                          childTipAfter, childTipBefore, 0.01f);
            context.Check("and kept its tip offset", childOffsetAfter, childOffsetBefore, 0.01f);
        }

        /// <summary>The wings and flaps of the player's demo craft, sorted out by role.</summary>
        private class DemoRig
        {
            public Part Parent;
            public Part Child;
            public Part ParentFlap;
            public Part ChildFlap;
            public bool Ok;
        }

        /// <summary>
        /// Load the player's demo craft and work out which part is playing which role.
        /// </summary>
        /// <remarks>
        /// By structure rather than by name or index: a wing whose own parent is a
        /// wing is the child, and the flap on each is whichever control surface hangs
        /// off it. Indices would work today and break the moment the craft is resaved.
        /// </remarks>
        private static IEnumerator BuildDemoRig(TestContext context, DemoRig rig)
        {
            if (EditorBuilder.FindPart(PWing) == null)
            {
                context.Skip("B9 Procedural Wings is not installed");
                yield break;
            }
            if (!EditorBuilder.LoadCraft("DimensionSync Demo 1 Before"))
            {
                context.Skip("the craft 'DimensionSync Demo 1 Before' is not in this save");
                yield break;
            }
            yield return context.Frames(120);
            yield return context.Settled();

            ShipConstruct ship = EditorLogic.fetch?.ship;
            if (ship == null) { context.Result.Error("the craft did not load"); yield break; }

            // One side only. The pair are mirror images and testing both says the same
            // thing twice, but symmetry still has to be present for the edit to behave
            // the way it does for the player.
            foreach (Part part in ship.parts)
            {
                PartModule module = FindWing(part);
                if (module == null) continue;
                bool control = IsControl(module);
                PartModule hostModule = part.parent == null ? null : FindWing(part.parent);

                if (!control && hostModule != null && !IsControl(hostModule))
                {
                    if (rig.Child == null) { rig.Child = part; rig.Parent = part.parent; }
                }
            }
            if (rig.Child == null || rig.Parent == null)
            {
                context.Result.Error("could not find a parent/child wing pair in the craft");
                yield break;
            }

            foreach (Part part in ship.parts)
            {
                PartModule module = FindWing(part);
                if (module == null || !IsControl(module)) continue;
                if (part.parent == rig.Parent && rig.ParentFlap == null) rig.ParentFlap = part;
                if (part.parent == rig.Child && rig.ChildFlap == null) rig.ChildFlap = part;
            }

            Harness.Log($"DEMO parent {Describe(rig.Parent)}");
            Harness.Log($"DEMO child  {Describe(rig.Child)}");
            Harness.Log($"DEMO parent flap {(rig.ParentFlap == null ? "none" : Describe(rig.ParentFlap))}");
            Harness.Log($"DEMO child flap  {(rig.ChildFlap == null ? "none" : Describe(rig.ChildFlap))}");
            rig.Ok = true;
        }

        /// <summary>A wing's dimensions in one line, for reading a craft's state.</summary>
        private static string Describe(Part part)
        {
            return $"{part.name}: span {PartFields.Get(part, PWingModule, "sharedBaseLength"):F3}, "
                   + $"root {PartFields.Get(part, PWingModule, "sharedBaseWidthRoot"):F3}, "
                   + $"tip {PartFields.Get(part, PWingModule, "sharedBaseWidthTip"):F3}, "
                   + $"offRoot {PartFields.Get(part, PWingModule, "sharedBaseOffsetRoot"):F3}, "
                   + $"offTip {PartFields.Get(part, PWingModule, "sharedBaseOffsetTip"):F3}";
        }

        /// <summary>The WingProcedural module on a part, or null.</summary>
        private static PartModule FindWing(Part part)
        {
            if (part == null) return null;
            for (int i = 0; i < part.Modules.Count; i++)
                if (part.Modules[i] != null && part.Modules[i].GetType().Name == PWingModule)
                    return part.Modules[i];
            return null;
        }

        /// <summary>Whether a WingProcedural module is a control surface.</summary>
        private static bool IsControl(PartModule module)
        {
            System.Reflection.FieldInfo field = module.GetType().GetField(
                "isCtrlSrf",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.Instance);
            return field?.GetValue(module) is bool value && value;
        }

        /// <summary>
        /// Shortening a parent wing keeps its child's edges collinear with it.
        /// </summary>
        private static IEnumerator DemoParentLengthCarriesToChild(TestContext context)
        {
            var rig = new DemoRig();
            yield return BuildDemoRig(context, rig);
            if (!rig.Ok) yield break;

            float leadBefore = EdgeAngleOf(rig.Parent, trailing: false);
            float childLeadBefore = EdgeAngleOf(rig.Child, trailing: false);
            Harness.Log($"DEMOLEN before: parent lead {leadBefore:F3} deg, " +
                        $"child lead {childLeadBefore:F3} deg");

            yield return context.Say("Shortening the parent wing from 4 m to 3 m.",
                                     "Its edges get steeper. The child's edges are collinear with "
                                     + "them, so the child has to follow - by shortening to suit.");

            PartFields.Set(rig.Parent, PWingModule, "sharedBaseLength", 3f, WriteMode.DirectAssignment);
            yield return context.Settled();
            EditorBuilder.PresentShip();

            float parentLead = EdgeAngleOf(rig.Parent, trailing: false);
            float childLead = EdgeAngleOf(rig.Child, trailing: false);
            float parentTrail = EdgeAngleOf(rig.Parent, trailing: true);
            float childTrail = EdgeAngleOf(rig.Child, trailing: true);
            Harness.Log($"DEMOLEN after: parent lead {parentLead:F3} trail {parentTrail:F3}, " +
                        $"child lead {childLead:F3} trail {childTrail:F3}");
            Harness.Log($"DEMOLEN parent {Describe(rig.Parent)}");
            Harness.Log($"DEMOLEN child  {Describe(rig.Child)}");

            context.Check("the child's leading edge still runs at the parent's angle",
                          childLead, parentLead, 0.5f);
            context.Check("and its trailing edge too", childTrail, parentTrail, 0.5f);
        }

        /// <summary>The angle one of a wing's edges makes, in degrees.</summary>
        private static float EdgeAngleOf(Part wing, bool trailing)
        {
            float span = PartFields.Get(wing, PWingModule, "sharedBaseLength");
            if (float.IsNaN(span) || span <= 1e-3f) return float.NaN;

            float root = PartFields.Get(wing, PWingModule, "sharedBaseWidthRoot");
            float tip = PartFields.Get(wing, PWingModule, "sharedBaseWidthTip");
            float offRoot = PartFields.Get(wing, PWingModule, "sharedBaseOffsetRoot");
            float offTip = PartFields.Get(wing, PWingModule, "sharedBaseOffsetTip");
            if (float.IsNaN(offRoot)) offRoot = 0f;
            if (float.IsNaN(offTip)) offTip = 0f;

            // B9 measures a tip offset from the opposite direction to a root one, which
            // is why the tip term is negated here and everywhere else in this suite.
            float edgeRoot = trailing ? -root / 2f - offRoot : root / 2f - offRoot;
            float edgeTip = trailing ? -tip / 2f - offTip : tip / 2f - offTip;
            return Mathf.Atan2(edgeTip - edgeRoot, span) * Mathf.Rad2Deg;
        }

        /// <summary>
        /// The tip-offset sequence the player walked, ending on the step that broke.
        /// </summary>
        private static IEnumerator DemoTipOffsetSequence(TestContext context)
        {
            var rig = new DemoRig();
            yield return BuildDemoRig(context, rig);
            if (!rig.Ok) yield break;

            var steps = new[] { 0f, 2f, -2f, -4f, -1f };
            foreach (float offset in steps)
            {
                yield return context.Say($"Parent tip offset -> {offset:F1}.",
                                         "The flaps should stay on the edges they are hinged to "
                                         + "at every step, not only at the end.");

                PartFields.Set(rig.Parent, PWingModule, "sharedBaseOffsetTip", offset,
                               WriteMode.DirectAssignment);
                yield return context.Settled();
                EditorBuilder.PresentShip();

                float parentGap = rig.ParentFlap == null ? 0f
                    : EditorBuilder.EdgeGap(rig.Parent, rig.ParentFlap, trailing: true);
                float childGap = rig.ChildFlap == null ? 0f
                    : EditorBuilder.EdgeGap(rig.Child, rig.ChildFlap, trailing: true);
                // Every control surface on the craft, identified. The mod's own logs
                // name them all "Procedural control surface", so a repeated correction
                // and a mirrored pair being corrected once each look identical there.
                var seen = new List<string>();
                foreach (Part part in EditorLogic.fetch.ship.parts)
                {
                    PartModule module = FindWing(part);
                    if (module == null || !IsControl(module)) continue;
                    seen.Add($"#{part.GetInstanceID()} " +
                             $"offRoot {PartFields.Get(part, PWingModule, "sharedBaseOffsetRoot"):F4} " +
                             $"len {PartFields.Get(part, PWingModule, "sharedBaseLength"):F3}");
                }
                Harness.Log($"DEMOOFF offset {offset:F1}: parent gap {parentGap:F3}, " +
                            $"child gap {childGap:F3}");
                Harness.Log($"DEMOOFF   parent {Describe(rig.Parent)} " +
                            $"trailAngle {EdgeAngleOf(rig.Parent, trailing: true):F2}");
                Harness.Log($"DEMOOFF   child  {Describe(rig.Child)} " +
                            $"trailAngle {EdgeAngleOf(rig.Child, trailing: true):F2}");
                Harness.Log($"DEMOOFF flaps: {string.Join(" | ", seen.ToArray())}");

                context.CheckTrue($"at tip offset {offset:F1} the parent's flap is still on its edge "
                                  + $"(off by {parentGap:F3} m)", Mathf.Abs(parentGap) <= 0.2f);
                context.CheckTrue($"at tip offset {offset:F1} the child's flap is still on its edge "
                                  + $"(off by {childGap:F3} m)", Mathf.Abs(childGap) <= 0.2f);
            }
        }

        /// <summary>
        /// Control surfaces follow a root chord that was DRAGGED, not typed.
        /// </summary>
        /// <remarks>
        /// The bare-wing drag scenario proves a drag reaches B9. This one asks the
        /// question that matters to the mod: whether a change arriving that way is
        /// propagated like any other. The mod watches fields rather than input, so it
        /// should not care - but "should not care" is the kind of claim that wants a
        /// test, because the drag writes continuously over many frames rather than once,
        /// which is a different shape of change from a typed number.
        /// </remarks>
        private static IEnumerator DraggedChordReachesFlaps(TestContext context)
        {
            if (!SyntheticInput.Available) { context.Skip(SyntheticInput.Unavailable); yield break; }

            var rig = new WingRig();
            yield return BuildWingRig(context, rig, chord: 2f, thicknessRoot: 0.4f,
                                      thicknessTip: 0.4f, span: 6f, oblique: false);
            if (!rig.Ok) yield break;

            // Lifted clear of the floor so the wing is somewhere the pointer can reach.
            rig.Fuselage.transform.position += Vector3.up * 10f;
            yield return context.Settled();
            EditorBuilder.PresentShip();

            float chordBefore = PartFields.Get(rig.Wing, PWingModule, "sharedBaseWidthRoot");
            float trailingBefore = PartFields.Get(rig.Trailing, PWingModule, "sharedBaseWidthRoot");
            Vector3 trailingWas = rig.Trailing.transform.position;

            yield return context.Say("Dragging the wing's root chord with B held.",
                                     "The control surfaces on both edges should stay against the wing "
                                     + "and take the new sweep, exactly as they do when the same field "
                                     + "is typed.");

            var outcome = new DragOutcome();
            yield return DragHandle(context, rig.Wing, "b", outcome, upwards: 80);
            if (!outcome.Armed) { context.Skip(outcome.Why); yield break; }

            yield return context.Settled();
            EditorBuilder.PresentShip();

            float chordAfter = PartFields.Get(rig.Wing, PWingModule, "sharedBaseWidthRoot");
            float trailingGap = EditorBuilder.EdgeGap(rig.Wing, rig.Trailing, trailing: true);
            float leadingGap = EditorBuilder.EdgeGap(rig.Wing, rig.Leading, trailing: false);
            Harness.Log($"DRAGFLAPS chord {chordBefore:F3} -> {chordAfter:F3}, " +
                        $"trailing gap {trailingGap:F3}, leading gap {leadingGap:F3}, " +
                        $"trailing chord {trailingBefore:F3} -> " +
                        $"{PartFields.Get(rig.Trailing, PWingModule, "sharedBaseWidthRoot"):F3}");

            context.CheckTrue($"the drag changed the wing's root chord "
                              + $"({chordBefore:F3} -> {chordAfter:F3})",
                              Mathf.Abs(chordAfter - chordBefore) > 0.01f);
            // Movement asserted before flushness. A surface that never moved is flush
            // with a wing that did move only if the wing's edge did not go anywhere -
            // so without this the check passes precisely when nothing worked.
            float trailingMoved = (rig.Trailing.transform.position - trailingWas).magnitude;
            context.CheckTrue($"the trailing surface actually moved ({trailingMoved:F3} m)",
                              trailingMoved > 0.01f);
            context.CheckTrue($"the trailing surface is still against the wing "
                              + $"(off by {trailingGap:F3} m)", Mathf.Abs(trailingGap) <= 0.15f);
            context.CheckTrue($"the leading surface is still against the wing "
                              + $"(off by {leadingGap:F3} m)", Mathf.Abs(leadingGap) <= 0.15f);
            context.CheckTrue("both surfaces are still attached to the wing",
                              rig.Trailing.parent == rig.Wing && rig.Leading.parent == rig.Wing);
        }

        /// <summary>
        /// The same two-joint chain, with the tip chord TYPED rather than dragged.
        /// </summary>
        /// <remarks>
        /// Exists to answer one question and no other: whether a flap that fails to
        /// follow its wing depends on how the change arrived. If this fails too then
        /// the drag is innocent and the gap is in propagation itself; if it passes,
        /// something about a change written continuously over many frames is the cause.
        /// </remarks>
        private static IEnumerator TypedTipChordReachesOutboardFlap(TestContext context)
        {
            var rig = new OutboardRig();
            yield return BuildOutboardRig(context, rig);
            if (!rig.Ok) yield break;

            float innerBefore = PartFields.Get(rig.Inner, PWingModule, "sharedBaseWidthTip");
            Vector3 flapBefore = rig.Trailing.transform.position;

            yield return context.Say("Typing the INNER wing's tip chord down to 0.7 m.",
                                     "The same change the dragged scenario makes, by the path every "
                                     + "other scenario uses.");

            PartFields.Set(rig.Inner, PWingModule, "sharedBaseWidthTip", 0.7f,
                           WriteMode.DirectAssignment);
            yield return context.Settled();
            EditorBuilder.PresentShip();

            float innerAfter = PartFields.Get(rig.Inner, PWingModule, "sharedBaseWidthTip");
            float outerAfter = PartFields.Get(rig.Outer, PWingModule, "sharedBaseWidthRoot");
            float gap = EditorBuilder.EdgeGap(rig.Outer, rig.Trailing, trailing: true);
            float flapMoved = (rig.Trailing.transform.position - flapBefore).magnitude;
            Harness.Log($"TYPEDOUTBOARD inner tip {innerBefore:F3} -> {innerAfter:F3}, " +
                        $"outer root {outerAfter:F3}, " +
                        $"outer tip {PartFields.Get(rig.Outer, PWingModule, "sharedBaseWidthTip"):F3}, " +
                        $"flap gap {gap:F3}, " +
                        $"flap travel [{TravelOf(rig.Outer, flapBefore, rig.Trailing.transform.position)}], " +
                        $"seat [{SeatOf(rig.Outer, rig.Trailing)}]");

            context.Check("the outer wing's root took the inner wing's new tip chord",
                          outerAfter, innerAfter, 0.05f);
            context.CheckTrue($"the flap on the outer wing actually moved ({flapMoved:F3} m)",
                              flapMoved > 0.01f);
            context.CheckTrue($"and is still against its edge (off by {gap:F3} m)",
                              Mathf.Abs(gap) <= 0.15f);

            // The same invariant the dragged twin is held to. A change typed in one go
            // and the same change dragged over twenty frames have to leave the same
            // craft, and the way that used to fail was one route keeping the edges
            // collinear while the other declined and left a kink.
            float outerSpan = PartFields.Get(rig.Outer, PWingModule, "sharedBaseLength");
            Harness.Log($"TYPEDOUTBOARD taper inner {TaperRateOf(rig.Inner):F4} " +
                        $"outer {TaperRateOf(rig.Outer):F4}, outer span {outerSpan:F3}");
            context.Check("the outer wing's edges stayed collinear with the inner wing's",
                          TaperRateOf(rig.Outer), TaperRateOf(rig.Inner), 0.01f);
            context.CheckTrue($"and it was shortened to hold them ({outerSpan:F3} m)",
                              outerSpan < 3.9f);
        }

        /// <summary>
        /// A dragged root chord crosses a span joint and reaches a flap on the outer wing.
        /// </summary>
        /// <summary>
        /// How fast a wing's chord closes along its span, in metres per metre.
        /// </summary>
        /// <remarks>
        /// The number that says whether two segments are collinear: same taper rate
        /// and a shared chord at the joint means one straight edge runs through both.
        /// Comparing tip chords instead would call a shortened segment a broken one,
        /// which is exactly the case worth getting right.
        /// </remarks>
        private static float TaperRateOf(Part wing)
        {
            float root = PartFields.Get(wing, PWingModule, "sharedBaseWidthRoot");
            float tip = PartFields.Get(wing, PWingModule, "sharedBaseWidthTip");
            float span = PartFields.Get(wing, PWingModule, "sharedBaseLength");
            return span > 1e-3f ? (root - tip) / span : float.NaN;
        }

        /// <summary>
        /// Where a part sits on its wing, position and orientation together.
        /// </summary>
        /// <remarks>
        /// For comparing two routes to the same craft. A drag and a typed number that
        /// end with the same field values should also end with the part in the same
        /// place and at the same angle; a distance travelled cannot show that, because
        /// two different journeys reach the same place with different odometers.
        /// </remarks>
        private static string SeatOf(Part wing, Part part)
        {
            Vector3 local = wing.transform.InverseTransformPoint(part.transform.position);
            Vector3 angles = (Quaternion.Inverse(wing.transform.rotation)
                              * part.transform.rotation).eulerAngles;
            return $"at (span {local.x:F3}, chord {local.y:F3}, normal {local.z:F3}) "
                   + $"angles ({angles.x:F2}, {angles.y:F2}, {angles.z:F2})";
        }

        /// <summary>
        /// How far a part moved, split into along-the-span and across-the-chord.
        /// </summary>
        /// <remarks>
        /// A single distance cannot tell a flap that followed its wing's edge from one
        /// that also slid along the wing, and the two have completely different causes.
        /// EdgeGap only sees the across-the-chord part, so a spanwise slide leaves it
        /// reading zero however far the part has crept.
        /// </remarks>
        private static string TravelOf(Part wing, Vector3 was, Vector3 now)
        {
            Vector3 delta = now - was;
            Vector3 local = wing.transform.InverseTransformDirection(delta);
            // y is the CHORD direction, not "up" in any useful sense: the mod moves a
            // surface along its wing's local up axis to follow an edge (ChordAxis).
            return $"span {local.x:F3}, chord {local.y:F3}, normal {local.z:F3}, "
                   + $"total {delta.magnitude:F3}";
        }

        /// <summary>An inner wing, an outer wing on its tip, and a flap on the outer one.</summary>
        private class OutboardRig
        {
            public Part Fuselage;
            public Part Inner;
            public Part Outer;
            public Part Trailing;
            public bool Ok;
        }

        /// <summary>
        /// Assemble the two-wing chain with a control surface on the OUTER wing.
        /// </summary>
        /// <remarks>
        /// Shared so that the dragged and typed versions of this test are the same
        /// craft. Two hand-built copies would drift, and a difference between them
        /// would then be indistinguishable from the difference being measured.
        /// </remarks>
        private static IEnumerator BuildOutboardRig(TestContext context, OutboardRig rig)
        {
            if (EditorBuilder.FindPart(PWing) == null || EditorBuilder.FindPart(PWingCtrlSrf) == null)
            {
                context.Skip("B9 Procedural Wings is not installed");
                yield break;
            }

            Part fuselage = EditorBuilder.Spawn(PPTank);
            Part inner = EditorBuilder.Spawn(PWing);
            Part outer = EditorBuilder.Spawn(PWing);
            Part trailing = EditorBuilder.Spawn(PWingCtrlSrf);
            if (fuselage == null || inner == null || outer == null || trailing == null)
            {
                context.Skip("parts unavailable");
                yield break;
            }
            yield return context.Frames(8);

            EditorBuilder.SetRoot(fuselage);
            PartFields.Set(fuselage, PPShapeModule, "diameter", 1.25f, WriteMode.PartActionWindow);
            yield return context.Settled();

            foreach (Part wing in new[] { inner, outer })
            {
                PartFields.Set(wing, PWingModule, "sharedBaseWidthRoot", 2f, WriteMode.DirectAssignment);
                PartFields.Set(wing, PWingModule, "sharedBaseWidthTip", 2f, WriteMode.DirectAssignment);
                PartFields.Set(wing, PWingModule, "sharedBaseLength", 4f, WriteMode.DirectAssignment);
                PartFields.Set(wing, PWingModule, "sharedBaseThicknessRoot", 0.4f, WriteMode.DirectAssignment);
                PartFields.Set(wing, PWingModule, "sharedBaseThicknessTip", 0.4f, WriteMode.DirectAssignment);
            }
            PartFields.Set(trailing, PWingModule, "sharedBaseWidthRoot", 0.8f, WriteMode.DirectAssignment);
            PartFields.Set(trailing, PWingModule, "sharedBaseWidthTip", 0.8f, WriteMode.DirectAssignment);
            PartFields.Set(trailing, PWingModule, "sharedBaseLength", 4f, WriteMode.DirectAssignment);
            PartFields.Set(trailing, PWingModule, "sharedBaseThicknessRoot", 0.4f, WriteMode.DirectAssignment);
            PartFields.Set(trailing, PWingModule, "sharedBaseThicknessTip", 0.4f, WriteMode.DirectAssignment);
            yield return context.Settled();

            if (!EditorBuilder.SurfaceAttach(fuselage, inner, new Vector3(0.625f, 0f, 0f)))
            {
                context.Result.Error("could not attach the inner wing");
                yield break;
            }
            yield return context.Settled();

            if (!EditorBuilder.StackBelow(outer, inner) && !EditorBuilder.SurfaceAttach(
                    inner, outer, new Vector3(4f, 0f, 0f)))
            {
                context.Result.Error("could not attach the outer wing to the inner wing's tip");
                yield break;
            }
            yield return context.Settled();

            float outerEdge = EditorBuilder.WingEdge(outer, trailing: true);
            if (float.IsNaN(outerEdge)
                || !EditorBuilder.AttachAlongEdge(outer, trailing, new Vector3(0f, outerEdge, 0f),
                                                  Vector3.up, Vector3.right, inboardStation: 0f))
            {
                context.Result.Error("could not attach the control surface to the outer wing");
                yield break;
            }

            // Clear of the floor, so a scenario that needs to aim at it can.
            fuselage.transform.position += Vector3.up * 10f;
            yield return context.Settled();
            EditorBuilder.PresentShip();

            float gap = EditorBuilder.EdgeGap(outer, trailing, trailing: true);
            if (Mathf.Abs(gap) > 0.15f)
            {
                context.Result.Error($"fixture: the flap is not on the outer wing's edge "
                                     + $"(off by {gap:F3} m)");
                yield break;
            }

            rig.Fuselage = fuselage; rig.Inner = inner; rig.Outer = outer; rig.Trailing = trailing;
            rig.Ok = inner.parent == fuselage && outer.parent == inner && trailing.parent == outer;
            if (!rig.Ok) context.Result.Error("fixture: the chain did not assemble");
        }

        /// <summary>
        /// A dragged TIP chord crosses a span joint and reaches a flap on the outer wing.
        /// </summary>
        private static IEnumerator DraggedChordReachesOutboardWing(TestContext context)
        {
            if (!SyntheticInput.Available) { context.Skip(SyntheticInput.Unavailable); yield break; }

            var rig = new OutboardRig();
            yield return BuildOutboardRig(context, rig);
            if (!rig.Ok) yield break;

            Part inner = rig.Inner, outer = rig.Outer, trailing = rig.Trailing;

            float innerBefore = PartFields.Get(inner, PWingModule, "sharedBaseWidthTip");
            float outerBefore = PartFields.Get(outer, PWingModule, "sharedBaseWidthRoot");
            Vector3 flapBefore = trailing.transform.position;

            // The TIP chord, not the root. Dragging the root changes the end of the
            // inner wing that faces the fuselage, which the outer wing is not joined to
            // - so nothing crosses either joint, the flap has no reason to move, and
            // "the flap is still flush" passes because nothing happened. The tip is the
            // end the outer wing is attached to, so this is the drag that has to travel.
            yield return context.Say("Dragging the INNER wing's TIP chord with T held.",
                                     "It has two joints to cross: to the outer wing's root, which "
                                     + "is joined to the inner wing's tip, and on to the control "
                                     + "surface hanging off the outer wing.");

            var outcome = new DragOutcome();
            yield return DragHandle(context, inner, "t", outcome, upwards: 80);
            if (!outcome.Armed) { context.Skip(outcome.Why); yield break; }

            yield return context.Settled();
            EditorBuilder.PresentShip();

            float innerAfter = PartFields.Get(inner, PWingModule, "sharedBaseWidthTip");
            float outerAfter = PartFields.Get(outer, PWingModule, "sharedBaseWidthRoot");
            float gap = EditorBuilder.EdgeGap(outer, trailing, trailing: true);
            float flapMoved = (trailing.transform.position - flapBefore).magnitude;
            Harness.Log($"DRAGOUTBOARD inner tip {innerBefore:F3} -> {innerAfter:F3}, " +
                        $"outer root {outerBefore:F3} -> {outerAfter:F3}, " +
                        $"outer tip {PartFields.Get(outer, PWingModule, "sharedBaseWidthTip"):F3}, " +
                        $"flap gap {gap:F3}, flap travel [{TravelOf(outer, flapBefore, trailing.transform.position)}], " +
                        $"seat [{SeatOf(outer, trailing)}]");

            context.CheckTrue($"the drag changed the inner wing's tip chord "
                              + $"({innerBefore:F3} -> {innerAfter:F3})",
                              Mathf.Abs(innerAfter - innerBefore) > 0.01f);

            // The point of the scenario. Without this it passes whenever nothing
            // happened, because a flap that never moved is trivially still flush.
            context.Check("the outer wing's root took the inner wing's new tip chord",
                          outerAfter, innerAfter, 0.05f);
            context.CheckTrue($"the flap on the outer wing actually moved ({flapMoved:F3} m)",
                              flapMoved > 0.01f);
            context.CheckTrue($"and is still against its edge (off by {gap:F3} m)",
                              Mathf.Abs(gap) <= 0.15f);
            // The edges stay collinear across the joint. Where the taper runs out
            // inside the outer wing, that is held by SHORTENING it rather than by
            // flattening its tip - so the test is the taper rate, not the tip chord.
            float outerSpan = PartFields.Get(outer, PWingModule, "sharedBaseLength");
            Harness.Log($"DRAGOUTBOARD taper inner {TaperRateOf(inner):F4} " +
                        $"outer {TaperRateOf(outer):F4}, outer span {outerSpan:F3}");
            context.Check("the outer wing's edges stayed collinear with the inner wing's",
                          TaperRateOf(outer), TaperRateOf(inner), 0.01f);
            context.CheckTrue($"and it was shortened to hold them ({outerSpan:F3} m)",
                              outerSpan < 3.9f);

            context.CheckTrue("the chain is still assembled",
                              outer.parent == inner && trailing.parent == outer);
        }

        /// <summary>What a synthetic drag managed, and why if it managed nothing.</summary>
        private class DragOutcome
        {
            /// <summary>True only if B9 actually took the drag.</summary>
            public bool Armed;

            /// <summary>Why it did not, for a skip message that names the real reason.</summary>
            public string Why = "not attempted";
        }

        /// <summary>
        /// Drag one of B9's handles on a part, with the pointer really moving.
        /// </summary>
        /// <remarks>
        /// One implementation for every scenario that drags, because the coordinates
        /// here are the delicate part and two copies would drift. The pointer is placed
        /// in the game WINDOW's coordinates and then checked against Unity's own
        /// Input.mousePosition: those are the only two frames of reference that matter,
        /// and a mismatch between them is silent everywhere else, since the projection,
        /// the raycast and the part lookup are all computed in the same frame and so
        /// agree with each other whatever the pointer is really doing.
        ///
        /// Skips rather than fails whenever the drag could not be delivered. A scenario
        /// that never got its input in has found out nothing about the mod, and saying
        /// otherwise would read as "the mod ignores dragged changes".
        /// </remarks>
        /// <param name="key">B9's key for the dimension: b root chord, t tip chord, g translate.</param>
        /// <param name="upwards">Pixels to travel; negative drags down the screen.</param>
        private static IEnumerator DragHandle(TestContext context, Part part, string key,
                                              DragOutcome outcome, int upwards = 80)
        {
            outcome.Armed = false;

            if (!SyntheticInput.Available) { outcome.Why = SyntheticInput.Unavailable; yield break; }

            float span = PartFields.Get(part, PWingModule, "sharedBaseLength");

            // The camera is left to STOP before anything is projected through it. It
            // travels to a new framing rather than arriving at one, and a point worked
            // out mid-flight describes where the part was, not where it will be.
            Vector3 probe = part.transform.TransformPoint(new Vector3(span * 0.5f, 0f, 0f));
            var lastSeen = new Vector2(float.NaN, float.NaN);
            for (int settle = 0; settle < 300; settle++)
            {
                if (SyntheticInput.ScreenPoint(probe, out int px, out int py))
                {
                    var now = new Vector2(px, py);
                    if ((now - lastSeen).sqrMagnitude <= 1f) break;
                    lastSeen = now;
                }
                yield return context.Frames(5);
            }

            // Several stations along the span, because the first one can be behind a
            // neighbour - on a wing carrying control surfaces the obvious middle of the
            // wing is often the surface's.
            int x = 0, y = 0;
            bool aimed = false;
            int margin = Mathf.RoundToInt(Screen.height * 0.15f);
            foreach (float station in new[] { 0.5f, 0.35f, 0.65f, 0.25f, 0.75f, 0.45f, 0.55f })
            {
                Vector3 candidate = part.transform.TransformPoint(new Vector3(span * station, 0f, 0f));
                if (!SyntheticInput.ScreenPoint(candidate, out int cx, out int cy)) continue;
                if (cy < margin || cy > Screen.height - margin) continue;
                if (SyntheticInput.PartAt(cx, cy) != part) continue;
                x = cx; y = cy; aimed = true;
                break;
            }
            if (!aimed)
            {
                outcome.Why = $"no point along {part.name} was reachable and unobstructed";
                yield break;
            }

            SyntheticInput.FocusOwnWindow();
            SyntheticInput.MoveTo(x, y);
            yield return context.Frames(6);

            if (!SyntheticInput.PointerAgrees(x, y, out string where))
            {
                outcome.Why = $"the pointer did not land where it was aimed - {where}";
                yield break;
            }

            // A hover has to actually be delivered before a drag can arm. Without this
            // the failure reads as B9 refusing the input, when what happened is that
            // Unity never spoke to the part at all.
            HoverProbe.Watch(part);
            SyntheticInput.MoveTo(x + 2, y);
            yield return context.Frames(3);
            SyntheticInput.MoveTo(x, y);
            yield return context.Frames(12);
            if (HoverProbe.TotalOvers(part) == 0)
            {
                outcome.Why = $"Unity delivered no hover to {part.name} ({where})";
                yield break;
            }

            // B9 arms only when the key goes down on a frame it is also being hovered,
            // which is a one-frame coincidence rather than a state - so it is retried.
            for (int attempt = 1; attempt <= 6 && B9State(part) == 0; attempt++)
            {
                SyntheticInput.KeyDown(key);
                yield return context.Frames(3);
                if (B9State(part) != 0) break;

                SyntheticInput.KeyUp(key);
                SyntheticInput.MoveTo(x + 1, y);
                yield return context.Frames(2);
                SyntheticInput.MoveTo(x, y);
                yield return context.Frames(2);
            }
            if (B9State(part) == 0)
            {
                SyntheticInput.KeyUp(key);
                outcome.Why = $"B9 never armed a drag on {part.name}, although the pointer is "
                              + $"verified and {HoverProbe.TotalOvers(part)} hovers were delivered";
                yield break;
            }

            // Smooth: small steps with frames between, because B9 takes a DELTA each
            // frame. One jump to the far end is read as a single enormous movement or
            // missed altogether, and neither is what a person does.
            const int steps = 20;
            int perStep = Mathf.Max(1, Mathf.Abs(upwards) / steps) * (upwards < 0 ? -1 : 1);
            for (int step = 1; step <= steps; step++)
            {
                SyntheticInput.MoveTo(x, y - step * perStep);
                yield return context.Frames(3);
            }

            SyntheticInput.KeyUp(key);
            yield return context.Settled();
            outcome.Armed = true;
            outcome.Why = $"dragged {part.name} by {steps * perStep}px with {key} held";
            Harness.Log($"DRAG {outcome.Why}");
        }

        /// <summary>The name of the GameObject the WingProcedural module sits on.</summary>
        private static string ModuleObject(Part part)
        {
            for (int i = 0; i < part.Modules.Count; i++)
            {
                PartModule module = part.Modules[i];
                if (module != null && module.GetType().Name == PWingModule)
                    return module.gameObject.name;
            }
            return "no module";
        }

        /// <summary>
        /// Calls B9's OnMouseOver directly, the way Unity would if the hover reached it.
        /// </summary>
        /// <remarks>
        /// Only a diagnostic. It answers one question the outside of the process cannot:
        /// whether a drag fails because B9 declined it or because the message never
        /// arrived. Those look identical from a log that only records the result.
        /// </remarks>
        private static void InvokeMouseOver(Part part)
        {
            for (int i = 0; i < part.Modules.Count; i++)
            {
                PartModule module = part.Modules[i];
                if (module == null || module.GetType().Name != PWingModule) continue;
                System.Reflection.MethodInfo method = module.GetType().GetMethod(
                    "OnMouseOver",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public
                    | System.Reflection.BindingFlags.Instance);
                if (method == null) { Harness.Log("DRAG no OnMouseOver method found"); return; }
                method.Invoke(module, null);
                return;
            }
        }

        /// <summary>A STATIC field on the WingProcedural type, or "unreadable".</summary>
        /// <remarks>
        /// Some of B9's gate-keeping is static - shared by every wing in the game rather
        /// than held per part. A scenario that leaves one of those set stops the next
        /// one from working for reasons that have nothing to do with the parts in front
        /// of it, which is exactly the shape of a test that passes alone and fails in a
        /// suite.
        /// </remarks>
        private static string B9Static(Part part, string name)
        {
            for (int i = 0; i < part.Modules.Count; i++)
            {
                PartModule module = part.Modules[i];
                if (module == null || module.GetType().Name != PWingModule) continue;
                System.Reflection.FieldInfo field = module.GetType().GetField(
                    name,
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
                    | System.Reflection.BindingFlags.Static);
                if (field != null) return $"{field.GetValue(null)}";
            }
            return "unreadable";
        }

        /// <summary>A bool field on a part's WingProcedural module, or null if unreadable.</summary>
        /// <remarks>
        /// PartFields.Get is for floats and hands back NaN for anything else, which
        /// answers no question at all - a diagnostic that cannot fail to look broken.
        /// </remarks>
        private static string B9Flag(Part part, string name)
        {
            for (int i = 0; i < part.Modules.Count; i++)
            {
                PartModule module = part.Modules[i];
                if (module == null || module.GetType().Name != PWingModule) continue;
                System.Reflection.FieldInfo field = module.GetType().GetField(
                    name,
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
                    | System.Reflection.BindingFlags.Instance);
                if (field != null) return $"{field.GetValue(module)}";
            }
            return "unreadable";
        }

        /// <summary>B9's internal drag state for a wing, or -1 if it cannot be read.</summary>
        /// <remarks>
        /// Zero means B9 is not in a drag. Reading it is the difference between "the
        /// mod ignored the drag" and "the drag never started", which look identical
        /// from the outside and want completely different investigations.
        /// </remarks>
        private static int B9State(Part part)
        {
            for (int i = 0; i < part.Modules.Count; i++)
            {
                PartModule module = part.Modules[i];
                if (module == null || module.GetType().Name != PWingModule) continue;
                System.Reflection.FieldInfo field = module.GetType().GetField(
                    "state",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (field != null && field.GetValue(module) is int value) return value;
            }
            return -1;
        }

        /// <summary>
        /// A flap slid along its wing keeps the station the player gave it.
        /// </summary>
        /// <remarks>
        /// Different from pulling one OFF the wing: this surface is still against the
        /// edge, so it should go on being swept and thickened with the wing. What it
        /// should not do is jump back to where it used to be along the span, which is
        /// the part the player just changed.
        ///
        /// Reported from a walkthrough: move a flap outward, change the wing's chord,
        /// and before the flap is turned to the new sweep it is walked back to its old
        /// place along the wing.
        /// </remarks>
        private static IEnumerator FlapSlidAlongTheWingStaysPut(TestContext context)
        {
            var rig = new WingRig();
            yield return BuildWingRig(context, rig, chord: 3f, thicknessRoot: 0.5f, thicknessTip: 0.5f);
            if (!rig.Ok) yield break;

            // Along the span only, staying against the edge - which is what makes this
            // different from the surface being taken off the wing.
            float slide = 1f;
            rig.Trailing.transform.position += rig.Wing.transform.right * slide;
            rig.Trailing.attPos0 = rig.Trailing.transform.localPosition;
            GameEvents.onEditorPartEvent.Fire(ConstructionEventType.PartOffset, rig.Trailing);
            yield return context.Settled();
            EditorBuilder.PresentShip();

            float slidTo = rig.Wing.transform.InverseTransformPoint(rig.Trailing.transform.position).x;

            yield return context.Say($"The player has slid the trailing surface {slide} m outward "
                                     + "along the wing. Now widening the wing's root chord to 5 m.",
                                     "The surface should follow the wing's new shape - it is still "
                                     + "against the edge.\n\n"
                                     + "What it should NOT do is go back to where it was along the span. "
                                     + "That is the one thing somebody just changed on purpose.");

            PartFields.Set(rig.Wing, PWingModule, "sharedBaseWidthRoot", 5f, WriteMode.DirectAssignment);
            yield return context.Settled();
            EditorBuilder.PresentShip();

            float ended = rig.Wing.transform.InverseTransformPoint(rig.Trailing.transform.position).x;
            Harness.Log($"SLID along the wing: {slidTo:F3} -> {ended:F3} (moved {ended - slidTo:+0.000;-0.000})");

            context.Check("the surface kept the place along the wing the player gave it",
                          ended, slidTo, 0.05f);
            context.CheckTrue("and is still attached", rig.Trailing.parent == rig.Wing);
        }

        /// <summary>
        /// A flap the player moved off its wing is not rearranged by later changes, and        /// <summary>
        /// A flap the player moved off its wing is not rearranged by later changes, and
        /// is taken up again once they put it back.
        /// </summary>
        /// <remarks>
        /// The move is announced with PartOffset, which is what the editor fires when
        /// somebody finishes dragging a part with the offset gizmo. That event is the
        /// whole mechanism: whether a surface is off its wing cannot be told from
        /// geometry while anything is moving, because one this mod is part way through
        /// repositioning looks identical - but "did somebody just move this?" has an
        /// answer.
        ///
        /// Both halves are checked. A rule that simply stopped touching any surface it
        /// had ever seen move would pass the first half and strand the flap for good.
        /// </remarks>
        private static IEnumerator FlapThePlayerMovedIsLeftAlone(TestContext context)
        {
            var rig = new WingRig();
            yield return BuildWingRig(context, rig, chord: 3f, thicknessRoot: 0.5f, thicknessTip: 0.5f);
            if (!rig.Ok) yield break;

            Vector3 home = rig.Trailing.transform.position;
            float shift = 1.5f;

            rig.Trailing.transform.position -= rig.Wing.transform.up * shift;
            rig.Trailing.attPos0 = rig.Trailing.transform.localPosition;
            GameEvents.onEditorPartEvent.Fire(ConstructionEventType.PartOffset, rig.Trailing);
            yield return context.Settled();
            EditorBuilder.PresentShip();

            Vector3 movedTo = rig.Wing.transform.InverseTransformPoint(rig.Trailing.transform.position);
            float spanBefore = PartFields.Get(rig.Trailing, PWingModule, "sharedBaseLength");

            yield return context.Say($"The player has pulled the trailing surface {shift} m off the "
                                     + "wing. Now lengthening the wing's span to 6 m.",
                                     "The leading surface is untouched and should follow the wing as "
                                     + "usual.\n\n"
                                     + "The trailing one should not move or change. It was taken off the "
                                     + "edge these rules arrange things along, and putting it back would "
                                     + "undo what somebody just did.");

            PartFields.Set(rig.Wing, PWingModule, "sharedBaseLength", 6f, WriteMode.DirectAssignment);
            yield return context.Settled();
            EditorBuilder.PresentShip();

            Vector3 after = rig.Wing.transform.InverseTransformPoint(rig.Trailing.transform.position);
            Harness.Log($"PLAYERMOVED left alone: {movedTo} -> {after}, span {spanBefore:F3} -> " +
                        $"{PartFields.Get(rig.Trailing, PWingModule, "sharedBaseLength"):F3}");

            context.Check("the surface the player moved stayed where they put it (chord)",
                          after.y, movedTo.y, 0.02f);
            context.Check("the surface the player moved stayed where they put it (span)",
                          after.x, movedTo.x, 0.02f);
            context.Check("and kept its own span", PartFields.Get(rig.Trailing, PWingModule,
                          "sharedBaseLength"), spanBefore, 1e-3f);
            context.Check("while the untouched surface followed the wing",
                          PartFields.Get(rig.Leading, PWingModule, "sharedBaseLength"), 6f, 0.3f);

            yield return context.Say("Now the player puts it back against the wing.",
                                     "Once it is on the edge again the rules should take it up as "
                                     + "before - this is not a one-way door.");

            rig.Trailing.transform.position = home;
            rig.Trailing.attPos0 = rig.Trailing.transform.localPosition;
            GameEvents.onEditorPartEvent.Fire(ConstructionEventType.PartOffset, rig.Trailing);
            yield return context.Settled();

            PartFields.Set(rig.Wing, PWingModule, "sharedBaseLength", 4f, WriteMode.DirectAssignment);
            yield return context.Settled();
            EditorBuilder.PresentShip();

            float taken = PartFields.Get(rig.Trailing, PWingModule, "sharedBaseLength");
            Harness.Log($"PLAYERMOVED taken up again: span {taken:F3} (the wing is 4)");
            context.Check("the surface follows its wing again once it is put back", taken, 4f, 0.3f);
        }

        /// <summary>
        /// A change on an inner wing reaches control surfaces on the wing beyond it.
        /// </summary>        /// <summary>
        /// A change on an inner wing reaches control surfaces on the wing beyond it.
        /// </summary>        /// <summary>
        /// A change on an inner wing reaches control surfaces on the wing beyond it.
        /// </summary>
        /// <remarks>
        /// Three parts in a line, joined two different ways: the outer wing meets the
        /// inner one end to end, and the flaps lie alongside the outer one. Everything
        /// else here tests one joint at a time, so a rule that works on each kind
        /// separately and loses the value in between would pass all of them.
        /// </remarks>
        private static IEnumerator OutboardWingAndFlapsFollow(TestContext context)
        {
            if (EditorBuilder.FindPart(PWing) == null || EditorBuilder.FindPart(PWingCtrlSrf) == null)
            {
                context.Skip("B9 Procedural Wings is not installed");
                yield break;
            }

            Part fuselage = EditorBuilder.Spawn(PPTank);
            Part inner = EditorBuilder.Spawn(PWing);
            Part outer = EditorBuilder.Spawn(PWing);
            Part trailing = EditorBuilder.Spawn(PWingCtrlSrf);
            if (fuselage == null || inner == null || outer == null || trailing == null)
            {
                context.Skip("parts unavailable");
                yield break;
            }
            yield return context.Frames(8);

            EditorBuilder.SetRoot(fuselage);
            PartFields.Set(fuselage, PPShapeModule, "diameter", 1.25f, WriteMode.PartActionWindow);
            yield return context.Settled();

            foreach (Part wing in new[] { inner, outer })
            {
                PartFields.Set(wing, PWingModule, "sharedBaseWidthRoot", 2f, WriteMode.DirectAssignment);
                PartFields.Set(wing, PWingModule, "sharedBaseWidthTip", 2f, WriteMode.DirectAssignment);
                PartFields.Set(wing, PWingModule, "sharedBaseLength", 3f, WriteMode.DirectAssignment);
                PartFields.Set(wing, PWingModule, "sharedBaseThicknessRoot", 0.4f, WriteMode.DirectAssignment);
                PartFields.Set(wing, PWingModule, "sharedBaseThicknessTip", 0.4f, WriteMode.DirectAssignment);
            }
            PartFields.Set(trailing, PWingModule, "sharedBaseWidthRoot", 0.8f, WriteMode.DirectAssignment);
            PartFields.Set(trailing, PWingModule, "sharedBaseWidthTip", 0.8f, WriteMode.DirectAssignment);
            PartFields.Set(trailing, PWingModule, "sharedBaseLength", 3f, WriteMode.DirectAssignment);
            PartFields.Set(trailing, PWingModule, "sharedBaseThicknessRoot", 0.4f, WriteMode.DirectAssignment);
            PartFields.Set(trailing, PWingModule, "sharedBaseThicknessTip", 0.4f, WriteMode.DirectAssignment);
            yield return context.Settled();

            if (!EditorBuilder.SurfaceAttach(fuselage, inner, new Vector3(0.625f, 0f, 0f)))
            {
                context.Result.Error("could not attach the inner wing");
                yield break;
            }
            yield return context.Settled();

            // The outer wing goes on the inner one's TIP - end to end, a span joint -
            // and the flap alongside the outer wing, which is an edge joint. Two kinds
            // of joint in one chain is the point of the scenario.
            if (!EditorBuilder.StackBelow(outer, inner) && !EditorBuilder.SurfaceAttach(
                    inner, outer, new Vector3(3f, 0f, 0f)))
            {
                context.Result.Error("could not attach the outer wing to the inner wing's tip");
                yield break;
            }
            yield return context.Settled();

            float outerEdge = EditorBuilder.WingEdge(outer, trailing: true);
            if (float.IsNaN(outerEdge)
                || !EditorBuilder.AttachAlongEdge(outer, trailing, new Vector3(0f, outerEdge, 0f),
                                                  Vector3.up, Vector3.right, inboardStation: 0f))
            {
                context.Result.Error("could not attach the control surface to the outer wing");
                yield break;
            }
            yield return context.Settled();
            EditorBuilder.PresentShip();

            context.CheckTrue("the chain is assembled",
                              inner.parent == fuselage && outer.parent == inner
                              && trailing.parent == outer);

            yield return context.Say("Thickening the INNER wing's TIP to 0.9 m.",
                                     "It has two joints to cross. The inner wing's tip should follow its "
                                     + "root, the outer wing should take that thickness at its own root, "
                                     + "and the control surface hanging off the OUTER wing should end up "
                                     + "as thick as the wing it is against.\n\n"
                                     + "Nothing should come adrift on the way.");

            // The TIP, because that is the end the outer wing is joined to. Thickening
            // the inner wing's ROOT changes nothing at the joint - root and tip are
            // separate channels - so the chain would never be crossed and every
            // assertion below would compare an unchanged number against itself and
            // pass. A test that can only pass is worse than no test.
            PartFields.Set(inner, PWingModule, "sharedBaseThicknessTip", 0.9f, WriteMode.DirectAssignment);
            yield return context.Settled();
            EditorBuilder.PresentShip();

            float innerTip = PartFields.Get(inner, PWingModule, "sharedBaseThicknessTip");
            float outerRoot = PartFields.Get(outer, PWingModule, "sharedBaseThicknessRoot");
            float flapRoot = PartFields.Get(trailing, PWingModule, "sharedBaseThicknessRoot");
            Harness.Log($"CHAIN inner root 0.900, inner tip {innerTip:F3}, " +
                        $"outer root {outerRoot:F3}, flap root {flapRoot:F3}");

            context.Check("the inner wing's tip took the change", innerTip, 0.9f, 1e-3f);
            context.CheckTrue($"the change actually crossed the joints (started at 0.400, "
                              + $"outer root {outerRoot:F3}, flap root {flapRoot:F3})",
                              Mathf.Abs(outerRoot - 0.4f) > 1e-3f && Mathf.Abs(flapRoot - 0.4f) > 1e-3f);
            context.Check("the outer wing's root matches the inner wing's tip", outerRoot, innerTip, 1e-3f);
            context.Check("the control surface matches the outer wing where they meet",
                          flapRoot, outerRoot, 1e-3f);
            context.CheckTrue("nothing came adrift",
                              inner.parent == fuselage && outer.parent == inner
                              && trailing.parent == outer);
        }

        /// <summary>
        /// B9's own panel accepts the values the mod leaves on a control surface.
        /// </summary>        /// <summary>
        /// B9's own panel accepts the values the mod leaves on a control surface.
        /// </summary>        /// <summary>
        /// B9's own panel accepts the values the mod leaves on a control surface.
        /// </summary>
        /// <remarks>
        /// Every other scenario writes fields either directly or through the part
        /// action window, and neither route runs B9's own limit checking - so all of
        /// them would pass while leaving a part B9 will silently resize the moment its
        /// panel opens. This is the only test that asks B9 what it thinks.
        /// </remarks>
        private static IEnumerator B9PanelKeepsOurValues(TestContext context)
        {
            var rig = new WingRig();
            yield return BuildWingRig(context, rig, chord: 3f, thicknessRoot: 0.5f, thicknessTip: 0.5f);
            if (!rig.Ok) yield break;

            yield return context.Say("Widening the wing's root chord to 6 m.",
                                     "Wide enough that a control surface following it would want more "
                                     + "chord than B9 permits one to have.");

            PartFields.Set(rig.Wing, PWingModule, "sharedBaseWidthRoot", 6f, WriteMode.DirectAssignment);
            yield return context.Settled();
            EditorBuilder.PresentShip();

            string[] watched =
            {
                "sharedBaseWidthRoot", "sharedBaseWidthTip",
                "sharedBaseThicknessRoot", "sharedBaseThicknessTip",
                "sharedBaseOffsetRoot", "sharedBaseOffsetTip",
                "sharedBaseLength",
            };

            var before = new Dictionary<string, float>();
            foreach (Part surface in new[] { rig.Trailing, rig.Leading })
                foreach (string name in watched)
                    before[surface.GetInstanceID() + name] = PartFields.Get(surface, PWingModule, name);

            yield return context.Say("Now opening B9's own panel logic on both surfaces.",
                                     "This is what pressing J does. If anything changes shape as a "
                                     + "result, the value it was holding was one B9 was never going "
                                     + "to keep.");

            foreach (Part surface in new[] { rig.Trailing, rig.Leading })
                PartFields.OpenB9Panel(surface, PWingModule);
            yield return context.Settled();
            EditorBuilder.PresentShip();

            foreach (Part surface in new[] { rig.Trailing, rig.Leading })
            {
                string which = surface == rig.Trailing ? "trailing" : "leading";
                foreach (string name in watched)
                {
                    float was = before[surface.GetInstanceID() + name];
                    float now = PartFields.Get(surface, PWingModule, name);
                    context.Check($"{which} {name} survives B9's panel", now, was, 1e-3f);
                }
            }
        }

        /// <summary>
        /// The wing-to-flap thickness joint, on a flap B9 considers mirrored.
        /// </summary>        /// <summary>
        /// The wing-to-flap thickness joint, on a flap B9 considers mirrored.
        /// </summary>
        /// <remarks>
        /// This is the case a programmatic fixture cannot reach on its own. B9 sets
        /// isMirrored only in UpdateOnEditorAttach, only under mirror symmetry, and only
        /// for a part on the far side of the root - so it needs a person building in the
        /// SPH, which defaults to mirror symmetry where the VAB defaults to radial. The
        /// flag is set here by hand instead, because what it changes is worth a test:
        /// B9 swaps root and tip in the GEOMETRY of a mirrored control surface, so its
        /// fields and its shape disagree, and a rule that reads only the fields puts
        /// every change on the wrong end.
        ///
        /// In an ordinary mirrored PAIR the part is also mounted with its span reversed
        /// and the two cancel, which is why pairs have always come out right and why
        /// this went unnoticed for so long.
        /// </remarks>
        private static IEnumerator MirroredControlSurfaceThicknessFollows(TestContext context)
        {
            var rig = new WingRig();
            yield return BuildWingRig(context, rig, chord: 3f, thicknessRoot: 0.5f, thicknessTip: 0.5f);
            if (!rig.Ok) yield break;

            SetMirrored(rig.Trailing, true);
            SetMirrored(rig.Leading, true);
            MarkShipModified();

            yield return context.Say("Both control surfaces are now marked mirrored. Thickening the "
                                     + "wing's ROOT to 0.9 m.",
                                     "On screen this should look exactly like the unmirrored thickness "
                                     + "test: each surface thickens at the end that meets the wing's root "
                                     + "and stays at 0.5 m at the far end.\n\n"
                                     + "The NUMBERS have to be the other way round to get there. A "
                                     + "mirrored control surface is built end for end, so the end meeting "
                                     + "the wing root is the one B9 calls the tip.");

            PartFields.Set(rig.Wing, PWingModule, "sharedBaseThicknessRoot", 0.9f, WriteMode.DirectAssignment);
            yield return context.Settled();
            EditorBuilder.PresentShip();

            context.CheckTrue("B9 still considers both surfaces mirrored",
                              GetMirrored(rig.Trailing) && GetMirrored(rig.Leading));

            context.Check("trailing surface thickens at the end meeting the wing root",
                          PartFields.Get(rig.Trailing, PWingModule, "sharedBaseThicknessTip"), 0.9f);
            context.Check("leading surface thickens at the end meeting the wing root",
                          PartFields.Get(rig.Leading, PWingModule, "sharedBaseThicknessTip"), 0.9f);
            context.Check("trailing surface far end unchanged",
                          PartFields.Get(rig.Trailing, PWingModule, "sharedBaseThicknessRoot"), 0.5f);
            context.Check("leading surface far end unchanged",
                          PartFields.Get(rig.Leading, PWingModule, "sharedBaseThicknessRoot"), 0.5f);
        }

        /// <summary>
        /// A mirrored flap re-derives its thickness from the right end of its wing.
        /// </summary>
        /// <remarks>
        /// Reaches Refit rather than the ordinary channel propagation: editing the
        /// flap's OWN length makes the mod work out afresh which stretch of wing it
        /// now covers and what thickness belongs at each of its ends. That path decides
        /// which end is which through RootFacesTheHostsRoot, which is a different piece
        /// of code from the one the joint test exercises even though both rest on the
        /// same span axis. Worth its own test for exactly that reason.
        ///
        /// Deliberately asserts the ORDER of the two ends rather than exact numbers:
        /// what is being tested is which end gets the thick value, and a test that also
        /// pinned the coverage arithmetic would fail for two unrelated reasons at once.
        /// </remarks>
        private static IEnumerator MirroredControlSurfaceRefits(TestContext context)
        {
            var rig = new WingRig();
            yield return BuildWingRig(context, rig, chord: 3f, thicknessRoot: 0.9f, thicknessTip: 0.3f);
            if (!rig.Ok) yield break;

            SetMirrored(rig.Trailing, true);
            SetMirrored(rig.Leading, true);
            MarkShipModified();

            yield return context.Say("Both control surfaces are marked mirrored, and the wing tapers "
                                     + "from 0.9 m at the root to 0.3 m at the tip. Shortening each "
                                     + "surface to 2 m.",
                                     "Each surface now covers only the inboard half of its wing, so it "
                                     + "should end up thick where it meets the wing's root and thinner at "
                                     + "the far end.\n\n"
                                     + "Because these are mirrored, the field holding the THICK value has "
                                     + "to be the tip one. On screen the thick end should still be the "
                                     + "inboard end.");

            PartFields.Set(rig.Trailing, PWingModule, "sharedBaseLength", 2f, WriteMode.DirectAssignment);
            PartFields.Set(rig.Leading, PWingModule, "sharedBaseLength", 2f, WriteMode.DirectAssignment);
            yield return context.Settled();
            EditorBuilder.PresentShip();

            float trailingInboard = PartFields.Get(rig.Trailing, PWingModule, "sharedBaseThicknessTip");
            float trailingOutboard = PartFields.Get(rig.Trailing, PWingModule, "sharedBaseThicknessRoot");
            float leadingInboard = PartFields.Get(rig.Leading, PWingModule, "sharedBaseThicknessTip");
            float leadingOutboard = PartFields.Get(rig.Leading, PWingModule, "sharedBaseThicknessRoot");

            context.CheckTrue("B9 still considers both surfaces mirrored",
                              GetMirrored(rig.Trailing) && GetMirrored(rig.Leading));

            context.CheckTrue($"trailing surface thicker where it meets the wing root "
                              + $"({trailingInboard:F3} vs {trailingOutboard:F3})",
                              trailingInboard > trailingOutboard);
            context.CheckTrue($"leading surface thicker where it meets the wing root "
                              + $"({leadingInboard:F3} vs {leadingOutboard:F3})",
                              leadingInboard > leadingOutboard);
            context.CheckTrue("trailing surface thickness stays within the wing's range",
                              trailingInboard <= 0.9f + 1e-3f && trailingOutboard >= 0.3f - 1e-3f);
            context.CheckTrue("leading surface thickness stays within the wing's range",
                              leadingInboard <= 0.9f + 1e-3f && leadingOutboard >= 0.3f - 1e-3f);
        }

        /// <summary>Tell the mod the ship changed, so it measures the joints again.</summary>
        /// <remarks>
        /// The mod measures joint geometry only when the ship is modified, which in
        /// play is the same moment B9 works out isMirrored - both happen on attach. A
        /// test that sets the flag by hand changes nothing structural, so without this
        /// the mod goes on using coverage it measured before the flag existed, and the
        /// test would be reporting a staleness it created itself rather than anything
        /// the game can do.
        /// </remarks>
        private static void MarkShipModified()
        {
            if (EditorLogic.fetch?.ship != null)
                GameEvents.onEditorShipModified.Fire(EditorLogic.fetch.ship);
        }

        /// <summary>Whether B9 currently considers this part mirrored.</summary>
        /// <remarks>
        /// Read back rather than assumed: B9 recomputes isMirrored in
        /// UpdateOnEditorAttach, so anything that re-attaches the part during a test
        /// silently clears what SetMirrored put there, and every assertion after that
        /// would be testing the ordinary case under a misleading name.
        /// </remarks>
        private static bool GetMirrored(Part part)
        {
            for (int i = 0; i < part.Modules.Count; i++)
            {
                PartModule module = part.Modules[i];
                if (module == null || module.GetType().Name != PWingModule) continue;
                System.Reflection.FieldInfo field = module.GetType().GetField(
                    "isMirrored",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                if (field != null && field.GetValue(module) is bool value) return value;
            }
            return false;
        }

        /// <summary>Set B9's isMirrored on a part, as attaching it in the SPH would.</summary>
        /// <remarks>
        /// Plain reflection rather than PartFields: unlike isCtrlSrf beside it,
        /// isMirrored carries no KSPField attribute, which is why B9 writes it to the
        /// craft by hand under the name mirrorTexturing.
        /// </remarks>
        private static void SetMirrored(Part part, bool value)
        {
            for (int i = 0; i < part.Modules.Count; i++)
            {
                PartModule module = part.Modules[i];
                if (module == null || module.GetType().Name != PWingModule) continue;
                System.Reflection.FieldInfo field = module.GetType().GetField(
                    "isMirrored",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                if (field != null) field.SetValue(module, value);
            }
        }

        /// <summary>
        /// A flap running the length of a wing stays that length when the wing is
        /// lengthened.
        /// </summary>
        private static IEnumerator WingControlSurfaceSpanFollows(TestContext context)
        {
            // Broadside. This is a test about length, so what matters is how far the
            // parts reach - which is exactly what an oblique view foreshortens.
            var rig = new WingRig();
            yield return BuildWingRig(context, rig, chord: 3f, thicknessRoot: 0.5f, thicknessTip: 0.5f,
                                      oblique: false);
            if (!rig.Ok) yield break;

            float leadingEdgeWidth = PartFields.Get(rig.Wing, PWingModule, "sharedEdgeWidthLeadingRoot");
            float trailingEdgeWidth = PartFields.Get(rig.Wing, PWingModule, "sharedEdgeWidthTrailingRoot");

            yield return context.Say("Lengthening the wing's span to 6 m.",
                                     "Both control surfaces should lengthen to 6 m.\n\n"
                                     + $"The wing's edge strips are {leadingEdgeWidth:F2} m leading and "
                                     + $"{trailingEdgeWidth:F2} m trailing going in, and should read the same "
                                     + "afterwards - they are their own channel and have nothing to do with "
                                     + "span. They sit UNDER the control surfaces rather than butting against "
                                     + "them, so watch the numbers rather than the silhouette.");

            PartFields.Set(rig.Wing, PWingModule, "sharedBaseLength", 6f, WriteMode.DirectAssignment);
            yield return context.Settled();
            EditorBuilder.PresentShip();

            context.Check("trailing surface span follows",
                          PartFields.Get(rig.Trailing, PWingModule, "sharedBaseLength"), 6f);
            context.Check("leading surface span follows",
                          PartFields.Get(rig.Leading, PWingModule, "sharedBaseLength"), 6f);
            context.CheckTrue("control surfaces still attached",
                              rig.Trailing.parent == rig.Wing && rig.Leading.parent == rig.Wing);
            context.Check("wing leading edge width untouched",
                          PartFields.Get(rig.Wing, PWingModule, "sharedEdgeWidthLeadingRoot"), leadingEdgeWidth);
            context.Check("wing trailing edge width untouched",
                          PartFields.Get(rig.Wing, PWingModule, "sharedEdgeWidthTrailingRoot"), trailingEdgeWidth);

            // Still the one change, watched for longer. The rules that keep a control
            // surface on its wing run on every change, and one that worked from its
            // own last answer rather than from the wing would creep a little each
            // pass - invisible for a moment, and the parts have doubled by the time a
            // panel has been read. It has to be watched idle to be seen at all.
            yield return context.Say("Now leaving it alone for a few seconds.",
                                     "Nothing should move.");

            var drift = new TestContext.Ref();
            yield return context.HoldStill(3f, drift);

            context.Check("nothing moved while idle", drift.Value, 0f, tolerance: 0.05f);
            context.Check("wing span still 6 m after sitting idle",
                          PartFields.Get(rig.Wing, PWingModule, "sharedBaseLength"), 6f);
            context.Check("trailing surface span still 6 m after sitting idle",
                          PartFields.Get(rig.Trailing, PWingModule, "sharedBaseLength"), 6f);
        }

        /// <summary>
        /// Shortening an aileron by hand leaves it covering less of the wing, and it
        /// has to take the wing's thickness where it now ends.
        /// </summary>
        /// <remarks>
        /// The only wing scenario driven from the control surface rather than the
        /// wing. Nothing about the wing changes here, so none of the rules that watch
        /// the wing fire at all - and yet the flap is now in the wrong place on a
        /// tapered surface. What it covered before is the player's business; what
        /// thickness it is where it sits is not.
        /// </remarks>
        private static IEnumerator AileronLengthEditKeepsItFitted(TestContext context)
        {
            var rig = new AileronRig();
            yield return BuildAileronRig(context, rig);
            if (!rig.Ok) yield break;

            // The MIDDLE, not an end: B9 rebuilds a resized flap around its origin, so
            // that is the point which should still be where it was afterwards.
            float middleBefore = rig.Wing.transform.InverseTransformPoint(
                rig.Aileron.transform.position).x;

            yield return context.Say("Halving the AILERON's own length, from 4 m to 2 m.",
                                     "This is the player resizing the flap, not the wing - the wing does not "
                                     + "change at all.\n\n"
                                     + "It covered the outboard half of an 8 m wing, so its root sat at the "
                                     + "4 m mark and its tip at the 8 m one. At 2 m it reaches from 4 m to "
                                     + "6 m instead. The wing tapers from 0.6 m thick to 0.2 m, so the "
                                     + "B9 rebuilds a shortened flap around its own origin, so it loses a "
                                     + "metre off EACH end and now reaches from the 5 m mark to the 7 m one. "
                                     + "The wing tapers from 0.6 m thick to 0.2 m, so the aileron should come "
                                     + "out 0.35 m at its root and 0.25 m at its tip - the thicknesses at the "
                                     + "5 m and 7 m marks.\n\n"
                                     + "Its middle should not move. The mod deliberately does not reposition a "
                                     + "part whose own length the player edited: anchoring is for holding a "
                                     + "part's place when its WING changes underneath it, and hijacking a "
                                     + "direct edit means a flap could only ever grow outboard.");

            PartFields.Set(rig.Aileron, PWingModule, "sharedBaseLength", 2f, WriteMode.DirectAssignment);
            yield return context.Settled();
            EditorBuilder.PresentShip();

            context.Check("aileron kept the length it was given",
                          PartFields.Get(rig.Aileron, PWingModule, "sharedBaseLength"), 2f);
            context.Check("aileron root takes the wing's thickness where it now starts",
                          PartFields.Get(rig.Aileron, PWingModule, "sharedBaseThicknessRoot"), 0.35f, 0.02f);
            context.Check("aileron tip takes the wing's thickness where it now ends",
                          PartFields.Get(rig.Aileron, PWingModule, "sharedBaseThicknessTip"), 0.25f, 0.02f);
            context.Check("aileron middle stayed put",
                          rig.Wing.transform.InverseTransformPoint(rig.Aileron.transform.position).x,
                          middleBefore, tolerance: 0.3f);
            context.CheckTrue("aileron still attached", rig.Aileron.parent == rig.Wing);
        }

        /// <summary>
        /// Sliding an aileron along its wing leaves it fitted, taking the wing's
        /// thickness wherever it has ended up.
        /// </summary>
        /// <remarks>
        /// The companion to the length edit, and a harder one to notice: moving a
        /// part changes no field at all - not on the flap, not on the wing - so
        /// nothing driven by field changes can see it happen. The flap simply ends up
        /// the thickness it was at the place it used to be. A move along the wing's
        /// own axis keeps it touching, so it should stay matched at both ends.
        /// </remarks>
        private static IEnumerator AileronMovedAlongTheWingStaysFitted(TestContext context)
        {
            var rig = new AileronRig();
            yield return BuildAileronRig(context, rig);
            if (!rig.Ok) yield break;

            yield return context.Say("Sliding the aileron 2 m INBOARD along the wing.",
                                     "Nothing is being resized here at all - the aileron is simply moved, and "
                                     + "along the wing's own axis so it stays touching.\n\n"
                                     + "It covered 4 m to 8 m of an 8 m wing; now it covers 2 m to 6 m. The "
                                     + "wing tapers from 0.6 m thick at the root to 0.2 m at the tip, so the "
                                     + "aileron should go from 0.4/0.2 to 0.5 m at its root and 0.3 m at its "
                                     + "tip. No field changed to tell anybody that, so this has to be noticed "
                                     + "by watching where the part sits.");

            // Straight along the wing's span, which is its local X.
            Vector3 along = rig.Wing.transform.TransformDirection(Vector3.right);
            rig.Aileron.transform.position -= along * 2f;
            rig.Aileron.attPos0 = rig.Aileron.transform.localPosition;
            GameEvents.onEditorShipModified.Fire(EditorLogic.fetch.ship);

            yield return context.Settled();
            EditorBuilder.PresentShip();

            context.Check("aileron root takes the wing's thickness where it now starts",
                          PartFields.Get(rig.Aileron, PWingModule, "sharedBaseThicknessRoot"), 0.5f);
            context.Check("aileron tip takes the wing's thickness where it now ends",
                          PartFields.Get(rig.Aileron, PWingModule, "sharedBaseThicknessTip"), 0.3f);
            context.Check("aileron length unchanged",
                          PartFields.Get(rig.Aileron, PWingModule, "sharedBaseLength"), 4f);
            context.CheckTrue("aileron still attached", rig.Aileron.parent == rig.Wing);
        }

        /// <summary>
        /// The same move made in many small steps arrives at the same place.
        /// </summary>
        /// <remarks>
        /// A player drags a part; they do not teleport it. Each frame of that drag is
        /// a tiny move, and a rule that only reacts to moves bigger than some
        /// threshold sees a long series of steps that are each too small and never
        /// fires at all - while the part travels just as far as it would have in one
        /// jump. That is worse than not having the rule, because by the end the flap
        /// matches the wing nowhere and the ordinary thickness rules cannot reach it
        /// either.
        ///
        /// Every step here is well under one percent of the wing's span, which is
        /// what the threshold used to be.
        /// </remarks>
        private static IEnumerator AileronMovedInSmallStepsStaysFitted(TestContext context)
        {
            var rig = new AileronRig();
            yield return BuildAileronRig(context, rig);
            if (!rig.Ok) yield break;

            const int steps = 12;
            const float each = 0.05f;   // 0.625% of an 8 m wing, per step

            yield return context.Say($"Sliding the aileron inboard in {steps} steps of {each:F2} m.",
                                     $"The same kind of move as the previous test, but crept rather than "
                                     + "jumped - which is how a drag actually arrives.\n\n"
                                     + $"It ends up {steps * each:F1} m inboard, covering 3.4 m to 7.4 m of the "
                                     + "8 m wing instead of 4 m to 8 m, so it should finish 0.43 m thick at its "
                                     + "root and 0.23 m at its tip. Every single step is smaller than the "
                                     + "movement threshold used to be, so if the threshold is measured from a "
                                     + "reference that creeps along behind the part, none of them counts and "
                                     + "nothing happens at all.");

            Vector3 along = rig.Wing.transform.TransformDirection(Vector3.right);
            for (int i = 0; i < steps; i++)
            {
                rig.Aileron.transform.position -= along * each;
                rig.Aileron.attPos0 = rig.Aileron.transform.localPosition;
                GameEvents.onEditorShipModified.Fire(EditorLogic.fetch.ship);
                yield return context.Frames(4);
            }

            yield return context.Settled();
            EditorBuilder.PresentShip();

            context.Check("aileron root matches the wing where it now starts",
                          PartFields.Get(rig.Aileron, PWingModule, "sharedBaseThicknessRoot"), 0.43f);
            context.Check("aileron tip matches the wing where it now ends",
                          PartFields.Get(rig.Aileron, PWingModule, "sharedBaseThicknessTip"), 0.23f);
            context.Check("aileron length unchanged",
                          PartFields.Get(rig.Aileron, PWingModule, "sharedBaseLength"), 4f);
        }

        /// <summary>
        /// Put a wing and its control surface through a wide range of edits, and
        /// report how well they still fit after each one.
        /// </summary>
        /// <remarks>
        /// A sweep rather than a test. Every other wing scenario checks one thing
        /// that is known to matter; this one runs through everything a player can
        /// change and writes down what happened, so gaps nobody has thought of yet
        /// turn up on their own. The only failures it reports are the ones that are
        /// unambiguous - a flap that has come detached, or one whose numbers have
        /// gone to NaN.
        /// </remarks>
        private static IEnumerator ProbeWingManipulations(TestContext context) =>
            ProbeWings(context, "straight", rootChord: 2f, tipChord: 2f, tipOffset: 0f,
                       inboard: 1f, length: 1f);

        /// <summary>The same sweep on a wing that is already swept back.</summary>
        private static IEnumerator ProbeSweptWing(TestContext context) =>
            ProbeWings(context, "swept", rootChord: 2f, tipChord: 2f, tipOffset: 1f,
                       inboard: 1f, length: 1f);

        /// <summary>The same sweep on a wing that already tapers.</summary>
        private static IEnumerator ProbeTaperedWing(TestContext context) =>
            ProbeWings(context, "tapered", rootChord: 4f, tipChord: 1f, tipOffset: 0f,
                       inboard: 1f, length: 1f);

        /// <summary>
        /// Swept and tapered at once, with the surfaces out at the tip.
        /// </summary>
        /// <remarks>
        /// The combination that produced the only drift found so far, and the one
        /// that stopped reproducing when the fixture's numbers changed. A surface
        /// well out along a swept, tapered wing carries the most shear and sits
        /// furthest from the root everything is measured against, so if geometry
        /// matters anywhere it should matter here.
        /// </remarks>
        private static IEnumerator ProbeSweptTaperedTipSurfaces(TestContext context) =>
            ProbeWings(context, "swept+tapered, outboard", rootChord: 4f, tipChord: 1f, tipOffset: 1f,
                       inboard: 2f, length: 2f);

        /// <summary>Surfaces running the whole span rather than part of it.</summary>
        private static IEnumerator ProbeFullSpanSurfaces(TestContext context) =>
            ProbeWings(context, "full span", rootChord: 2f, tipChord: 1f, tipOffset: 0f,
                       inboard: 0f, length: 4f);

        /// <summary>
        /// Put a wing and its control surfaces through a wide range of edits, and
        /// report how well they still fit after each one.
        /// </summary>
        /// <param name="name">What this starting geometry is called, for the log.</param>
        /// <param name="rootChord">The wing's chord at its root.</param>
        /// <param name="tipChord">The wing's chord at its tip.</param>
        /// <param name="tipOffset">How far the wing is swept to begin with.</param>
        /// <param name="inboard">Where the surfaces start along the span, in metres.</param>
        /// <param name="length">How long the surfaces are.</param>
        /// <remarks>
        /// A sweep rather than a test. Every other wing scenario checks one thing
        /// that is known to matter; these run through everything a player can change
        /// and write down what happened, so gaps nobody has thought of yet turn up on
        /// their own.
        ///
        /// Several starting geometries because the one drift found so far turned out
        /// to depend on the fixture: it vanished when the numbers changed, with no
        /// code change, which means a single starting shape proves very little.
        /// </remarks>
        private static IEnumerator ProbeWings(TestContext context, string name,
                                              float rootChord, float tipChord, float tipOffset,
                                              float inboard, float length)
        {
            if (EditorBuilder.FindPart(PWing) == null || EditorBuilder.FindPart(PWingCtrlSrf) == null)
            {
                context.Skip("B9 Procedural Wings is not installed");
                yield break;
            }

            Part fuselage = EditorBuilder.Spawn(PPTank);
            Part wing = EditorBuilder.Spawn(PWing);
            Part flap = EditorBuilder.Spawn(PWingCtrlSrf);
            Part slat = EditorBuilder.Spawn(PWingCtrlSrf);
            if (fuselage == null || wing == null || flap == null || slat == null)
            {
                context.Skip("parts unavailable");
                yield break;
            }
            yield return context.Frames(8);

            const float span = 4f;
            float from = inboard / span;
            float to = (inboard + length) / span;

            PartFields.Set(fuselage, PPShapeModule, "diameter", 1.25f, WriteMode.PartActionWindow);
            foreach (Part part in new[] { wing, flap, slat })
            {
                PartFields.Set(part, PWingModule, "sharedBaseThicknessRoot", 0.5f, WriteMode.DirectAssignment);
                PartFields.Set(part, PWingModule, "sharedBaseThicknessTip", 0.5f, WriteMode.DirectAssignment);
            }

            // The surfaces keep a chord of their own: B9 caps a control surface at
            // 2 m, so they cannot simply copy a wide wing.
            foreach (Part part in new[] { flap, slat })
            {
                PartFields.Set(part, PWingModule, "sharedBaseWidthRoot", Mathf.Min(rootChord, 2f),
                               WriteMode.DirectAssignment);
                PartFields.Set(part, PWingModule, "sharedBaseWidthTip", Mathf.Min(rootChord, 2f),
                               WriteMode.DirectAssignment);
                PartFields.Set(part, PWingModule, "sharedBaseLength", length, WriteMode.DirectAssignment);
            }

            PartFields.Set(wing, PWingModule, "sharedBaseWidthRoot", rootChord, WriteMode.DirectAssignment);
            PartFields.Set(wing, PWingModule, "sharedBaseWidthTip", tipChord, WriteMode.DirectAssignment);
            PartFields.Set(wing, PWingModule, "sharedBaseOffsetTip", tipOffset, WriteMode.DirectAssignment);
            PartFields.Set(wing, PWingModule, "sharedBaseLength", span, WriteMode.DirectAssignment);

            // The WING's edge strips off, and only the wing's, so the outline you see
            // is its body. Zeroing a surface's strip collapses the mesh rather than
            // removing it and leaves a sliver hanging in space.
            foreach (string edge in new[] { "sharedEdgeWidthLeading", "sharedEdgeWidthTrailing" })
            {
                PartFields.Set(wing, PWingModule, edge + "Root", 0f, WriteMode.DirectAssignment);
                PartFields.Set(wing, PWingModule, edge + "Tip", 0f, WriteMode.DirectAssignment);
            }
            yield return context.Frames(4);

            EditorBuilder.SetRoot(fuselage);
            EditorBuilder.PresentShip();
            if (!EditorBuilder.SurfaceAttach(fuselage, wing,
                    EditorBuilder.DirectionAcrossCamera(fuselage) * EditorBuilder.SurfaceRadius(fuselage)))
            {
                context.Result.Error("could not attach the wing");
                yield break;
            }

            // One on each edge. Every sign error so far has shown up on exactly one of
            // them, so probing only the trailing edge misses half of what there is.
            float back = EditorBuilder.WingEdge(wing, trailing: true, station: from);
            float front = EditorBuilder.WingEdge(wing, trailing: false, station: from);
            if (float.IsNaN(back) || float.IsNaN(front))
            {
                context.Result.Error("could not find the wing's edges");
                yield break;
            }

            // Lay each surface ALONG its edge, not along the span. On a swept wing
            // those are different directions, and mounting square to the span builds
            // a fixture no player would produce: surfaces visibly out of line with
            // the wing before a single test step has run. The mod then pulls them
            // into place on the first edit, which looks like the mod doing something
            // wrong when it is the fixture that was wrong.
            //
            // The shear goes on for the same reason - it is what squares a turned
            // surface's ends back up with the wing's, and B9 will not work it out on
            // its own.
            float backSlope = (EditorBuilder.WingEdge(wing, true, 1f) - EditorBuilder.WingEdge(wing, true, 0f)) / span;
            float frontSlope = (EditorBuilder.WingEdge(wing, false, 1f) - EditorBuilder.WingEdge(wing, false, 0f)) / span;

            PartFields.Set(flap, PWingModule, "sharedBaseOffsetRoot", -backSlope, WriteMode.DirectAssignment);
            PartFields.Set(flap, PWingModule, "sharedBaseOffsetTip", -backSlope, WriteMode.DirectAssignment);
            PartFields.Set(slat, PWingModule, "sharedBaseOffsetRoot", frontSlope, WriteMode.DirectAssignment);
            PartFields.Set(slat, PWingModule, "sharedBaseOffsetTip", frontSlope, WriteMode.DirectAssignment);

            // Along the edge, and long enough to span it: a hinge lying on a sloped
            // edge is the hypotenuse of the span it covers.
            var backAlong = new Vector3(1f, backSlope, 0f).normalized;
            var frontAlong = new Vector3(1f, frontSlope, 0f).normalized;
            PartFields.Set(flap, PWingModule, "sharedBaseLength",
                           length * Mathf.Sqrt(1f + backSlope * backSlope), WriteMode.DirectAssignment);
            PartFields.Set(slat, PWingModule, "sharedBaseLength",
                           length * Mathf.Sqrt(1f + frontSlope * frontSlope), WriteMode.DirectAssignment);
            yield return context.Frames(4);

            if (!EditorBuilder.AttachAlongEdge(wing, flap, new Vector3(from * span, back, 0f),
                                               Vector3.up, backAlong, inboardStation: inboard)
                || !EditorBuilder.AttachAlongEdge(wing, slat, new Vector3(from * span, front, 0f),
                                                 Vector3.down, frontAlong, inboardStation: inboard))
            {
                context.Result.Error("could not attach the control surfaces");
                yield break;
            }

            yield return context.Settled();
            EditorBuilder.PresentShip();
            yield return context.Say("Fixture built.",
                                     $"Starting geometry: {name}. A 4 m wing, chord {rootChord:F0} m at the "
                                     + $"root and {tipChord:F0} m at the tip, swept {tipOffset:F0} m, with a "
                                     + $"{length:F0} m control surface on each edge running from the "
                                     + $"{inboard:F0} m mark.\n\n"
                                     + "The wing's edge strips are off, so the outline you see IS its body. "
                                     + "Every step below changes one thing and reports the gap between wing "
                                     + "and surface at each end, and whether their thicknesses still agree.");

            ProbeFit(context, wing, flap, true, from, to, $"[{name}] trailing: as built");
            ProbeFit(context, wing, slat, false, from, to, $"[{name}] leading:  as built");

            var edits = new[]
            {
                new { Field = "sharedBaseWidthRoot",     Value = 4f,   Label = "root chord -> 4" },
                new { Field = "sharedBaseWidthRoot",     Value = 1f,   Label = "root chord -> 1" },
                new { Field = "sharedBaseWidthTip",      Value = 4f,   Label = "tip chord -> 4" },
                new { Field = "sharedBaseWidthTip",      Value = 1f,   Label = "tip chord -> 1" },
                new { Field = "sharedBaseOffsetTip",     Value = 2f,   Label = "sweep -> 2" },
                new { Field = "sharedBaseOffsetTip",     Value = -2f,  Label = "sweep -> -2" },
                new { Field = "sharedBaseOffsetRoot",    Value = 1f,   Label = "root offset -> 1" },
                new { Field = "sharedBaseOffsetRoot",    Value = 0f,   Label = "root offset -> 0" },
                new { Field = "sharedBaseThicknessRoot", Value = 1f,   Label = "root thickness -> 1" },
                new { Field = "sharedBaseLength",        Value = 8f,   Label = "span -> 8" },
                new { Field = "sharedBaseLength",        Value = 2f,   Label = "span -> 2" },
                new { Field = "sharedBaseLength",        Value = 4f,   Label = "span -> 4" },
            };

            foreach (var edit in edits)
            {
                yield return context.Say($"[{name}] wing: {edit.Label}.",
                                         "Watch whether the control surfaces stay against the wing's edges, "
                                         + "and whether they stay as thick as the wing where they meet it.");

                PartFields.Set(wing, PWingModule, edit.Field, edit.Value, WriteMode.DirectAssignment);
                yield return context.Settled();
                EditorBuilder.PresentShip();

                float gapBack = ProbeFit(context, wing, flap, true, from, to,
                                         $"[{name}] trailing: {edit.Label}");
                float gapFront = ProbeFit(context, wing, slat, false, from, to,
                                          $"[{name}] leading:  {edit.Label}");

                context.CheckTrue($"{edit.Label}: still attached",
                                  flap.parent == wing && slat.parent == wing);

                // Generous, because this is a position measured off meshes rather
                // than a field read: it is here to catch a surface coming away from
                // its wing, not to police the last centimetre.
                context.Check($"{edit.Label}: trailing surface still on the wing", gapBack, 0f, 0.15f);
                context.Check($"{edit.Label}: leading surface still on the wing", gapFront, 0f, 0.15f);
            }
        }

        /// <summary>
        /// Measure how well a control surface still fits its wing, and log it.
        /// </summary>
        /// <param name="label">What was just done to the wing.</param>
        /// <remarks>
        /// Reports rather than asserts. This exists to sweep a wide range of
        /// manipulations and see which ones leave the flap somewhere it should not
        /// be, without deciding in advance which of them are supposed to.
        /// </remarks>
        /// <returns>The larger of the two gaps, so the caller can assert on it.</returns>
        private static float ProbeFit(TestContext context, Part wing, Part flap,
                                      bool trailing, float from, float to, string label)
        {
            float gapIn = EditorBuilder.EdgeGap(wing, flap, trailing, from);
            float gapOut = EditorBuilder.EdgeGap(wing, flap, trailing, to);

            float wingRoot = PartFields.Get(wing, PWingModule, "sharedBaseThicknessRoot");
            float wingTip = PartFields.Get(wing, PWingModule, "sharedBaseThicknessTip");
            float wantIn = Mathf.Lerp(wingRoot, wingTip, from);
            float wantOut = Mathf.Lerp(wingRoot, wingTip, to);
            float haveIn = PartFields.Get(flap, PWingModule, "sharedBaseThicknessRoot");
            float haveOut = PartFields.Get(flap, PWingModule, "sharedBaseThicknessTip");

            float wingSpan = PartFields.Get(wing, PWingModule, "sharedBaseLength");
            float flapSpan = PartFields.Get(flap, PWingModule, "sharedBaseLength");

            // Where the surface actually sits along the wing, so a step that slides
            // it can be told apart from one that leaves a gap where it stands.
            float here = float.NaN;
            float wingLen = PartFields.Get(wing, PWingModule, "sharedBaseLength");
            if (!float.IsNaN(wingLen) && wingLen > 1e-3f)
                here = wing.transform.InverseTransformPoint(flap.transform.position).x / wingLen;

            Harness.Log($"PROBE {label,-38} station {here,6:F3}   " +
                        $"shear {PartFields.Get(flap, PWingModule, "sharedBaseOffsetTip"),7:F3}   " +
                        $"gap {gapIn,7:F3}/{gapOut,7:F3}   " +
                        $"thickness {haveIn,6:F3}/{haveOut,6:F3} want {wantIn,6:F3}/{wantOut,6:F3}   " +
                        $"span {flapSpan,6:F3} of {wingSpan,6:F3}   " +
                        $"attached {flap.parent == wing}");

            context.UI?.Note($"{label}: gap {gapIn:F2}/{gapOut:F2}, thickness " +
                             $"{haveIn:F2}/{haveOut:F2} (want {wantIn:F2}/{wantOut:F2})");

            if (float.IsNaN(gapIn) || float.IsNaN(gapOut)) return float.NaN;
            return Mathf.Abs(gapIn) > Mathf.Abs(gapOut) ? gapIn : gapOut;
        }

        /// <summary>
        /// A short control surface part way along a wing stays ON the wing's edge
        /// when that edge moves fore or aft.
        /// </summary>
        /// <remarks>
        /// Widening a wing's root pushes its trailing edge back; narrowing its tip
        /// pulls the edge forward. Either way the edge the flap is hinged to has
        /// moved, and the flap has to travel with it.
        ///
        /// This is not the same thing as following the edge's SLOPE, which is what
        /// the sweep rules do. A wing whose chords change together keeps the same
        /// slope throughout - there is no angle to turn through - and a rule that
        /// only ever turns the part leaves it correctly skewed and sitting in mid-air
        /// a little way off the wing.
        /// </remarks>
        private static IEnumerator ControlSurfaceFollowsAMovingEdge(TestContext context)
        {
            if (EditorBuilder.FindPart(PWing) == null || EditorBuilder.FindPart(PWingCtrlSrf) == null)
            {
                context.Skip("B9 Procedural Wings is not installed");
                yield break;
            }

            Part fuselage = EditorBuilder.Spawn(PPTank);
            Part wing = EditorBuilder.Spawn(PWing);
            Part flap = EditorBuilder.Spawn(PWingCtrlSrf);
            if (fuselage == null || wing == null || flap == null)
            {
                context.Skip("parts unavailable");
                yield break;
            }
            yield return context.Frames(8);

            const float span = 4f;
            const float chord = 2f;
            const float flapSpan = 1f;
            const float station = 0.375f;   // the central quarter, 1.5 m to 2.5 m

            PartFields.Set(fuselage, PPShapeModule, "diameter", 1.25f, WriteMode.PartActionWindow);
            foreach (Part part in new[] { wing, flap })
            {
                PartFields.Set(part, PWingModule, "sharedBaseWidthRoot", chord, WriteMode.DirectAssignment);
                PartFields.Set(part, PWingModule, "sharedBaseWidthTip", chord, WriteMode.DirectAssignment);
                PartFields.Set(part, PWingModule, "sharedBaseThicknessRoot", 0.24f, WriteMode.DirectAssignment);
                PartFields.Set(part, PWingModule, "sharedBaseThicknessTip", 0.24f, WriteMode.DirectAssignment);
            }
            PartFields.Set(wing, PWingModule, "sharedBaseLength", span, WriteMode.DirectAssignment);
            PartFields.Set(flap, PWingModule, "sharedBaseLength", flapSpan, WriteMode.DirectAssignment);
            yield return context.Frames(4);

            EditorBuilder.SetRoot(fuselage);
            EditorBuilder.PresentShip();
            Vector3 mount = EditorBuilder.DirectionAcrossCamera(fuselage) * EditorBuilder.SurfaceRadius(fuselage);
            if (!EditorBuilder.SurfaceAttach(fuselage, wing, mount))
            {
                context.Result.Error("could not attach the wing");
                yield break;
            }

            float edge = EditorBuilder.WingEdge(wing, trailing: true, station: station);
            if (float.IsNaN(edge)
                || !EditorBuilder.AttachAlongEdge(wing, flap, new Vector3(station * span, edge, 0f),
                                                  Vector3.up, Vector3.right,
                                                  inboardStation: station * span))
            {
                context.Result.Error("could not attach the control surface");
                yield break;
            }

            yield return context.Settled();
            EditorBuilder.PresentShip();
            yield return context.Say("Fixture built.",
                                     $"A {span:F0} m wing, {chord:F0} m chord throughout, with a "
                                     + $"{flapSpan:F0} m control surface on the middle of its trailing edge - "
                                     + "the central quarter of the span, flush with the wing's surfaces.");

            yield return context.Say("Widening the wing's ROOT chord to 3 m.",
                                     "The trailing edge moves BACK, and further back at the root than at the "
                                     + "tip, so it also picks up a slope. The flap should do both things: skew "
                                     + "to match the new slope AND move back to stay on the edge. Skewing "
                                     + "without moving leaves it hanging behind the wing.");

            PartFields.Set(wing, PWingModule, "sharedBaseWidthRoot", 3f, WriteMode.DirectAssignment);
            yield return context.Settled();
            EditorBuilder.PresentShip();

            context.Check("flap still on the edge at its inboard end",
                          EditorBuilder.EdgeGap(wing, flap, trailing: true, station: station),
                          0f, tolerance: 0.12f);
            context.Check("flap still on the edge at its outboard end",
                          EditorBuilder.EdgeGap(wing, flap, trailing: true, station: station + flapSpan / span),
                          0f, tolerance: 0.12f);

            yield return context.Say("Now narrowing the wing's TIP chord to 1 m.",
                                     "The trailing edge swings the other way, forward at the tip. The flap "
                                     + "should follow it forward.");

            PartFields.Set(wing, PWingModule, "sharedBaseWidthTip", 1f, WriteMode.DirectAssignment);
            yield return context.Settled();
            EditorBuilder.PresentShip();

            context.Check("flap followed the edge forward at its inboard end",
                          EditorBuilder.EdgeGap(wing, flap, trailing: true, station: station),
                          0f, tolerance: 0.12f);
            context.Check("flap followed the edge forward at its outboard end",
                          EditorBuilder.EdgeGap(wing, flap, trailing: true, station: station + flapSpan / span),
                          0f, tolerance: 0.12f);
            context.Check("flap kept its own chord",
                          PartFields.Get(flap, PWingModule, "sharedBaseWidthRoot"), chord);
        }

        /// <summary>
        /// Measure which end of a B9 part its ROOT dimensions actually draw at.
        /// </summary>
        /// <remarks>
        /// Not a test of this mod - a measurement of B9, which everything else here
        /// depends on and which has been guessed wrong twice. Each part is given a
        /// wide root and a narrow tip, and then the mesh is asked which of its two
        /// ends came out wide.
        ///
        /// It reports rather than asserts, because the point is to find out what the
        /// answer IS. Writing down an expected answer would be recording the guess
        /// again.
        /// </remarks>
        private static IEnumerator ProbeWhichEndIsRoot(TestContext context)
        {
            if (EditorBuilder.FindPart(PWing) == null || EditorBuilder.FindPart(PWingCtrlSrf) == null)
            {
                context.Skip("B9 Procedural Wings is not installed");
                yield break;
            }

            Part fuselage = EditorBuilder.Spawn(PPTank);
            Part wing = EditorBuilder.Spawn(PWing);
            Part flap = EditorBuilder.Spawn(PWingCtrlSrf);
            if (fuselage == null || wing == null || flap == null)
            {
                context.Skip("parts unavailable");
                yield break;
            }
            yield return context.Frames(8);

            EditorBuilder.SetRoot(fuselage);
            PartFields.Set(fuselage, PPShapeModule, "diameter", 1.25f, WriteMode.PartActionWindow);
            yield return context.Settled();

            // BOTH are attached, and each to the FUSELAGE rather than one to the other.
            //
            // Attached because B9 does not rebuild a wing's mesh until it is on
            // something: left floating, the part keeps the symmetric mesh it was
            // spawned with, its two ends really are the same width, and the question
            // this scenario asks cannot be answered. It also left the parts sitting in
            // the floor, which is what it looked like from the outside.
            //
            // Each to the fuselage because a control surface mounted on the WING is one
            // this mod then conforms to that wing - which would overwrite the very
            // dimensions being measured. On opposite sides, they are both built and
            // neither is anybody's control surface.
            float side = PartFields.Get(fuselage, PPShapeModule, "diameter") / 2f;
            if (!EditorBuilder.SurfaceAttach(fuselage, wing, new Vector3(side, 0f, 0f))
                || !EditorBuilder.SurfaceAttach(fuselage, flap, new Vector3(-side, 0f, 0f)))
            {
                context.Result.Error("could not attach the parts to the fuselage");
                yield break;
            }
            yield return context.Settled();

            // A wide root and a narrow tip on both, so the two ends cannot be
            // confused for one another. Set AFTER attaching, so B9 builds the mesh
            // from them rather than from the defaults.
            foreach (Part part in new[] { wing, flap })
            {
                PartFields.Set(part, PWingModule, "sharedBaseWidthRoot", 2f, WriteMode.DirectAssignment);
                PartFields.Set(part, PWingModule, "sharedBaseWidthTip", 0.4f, WriteMode.DirectAssignment);
                PartFields.Set(part, PWingModule, "sharedBaseLength", 4f, WriteMode.DirectAssignment);
                PartFields.Set(part, PWingModule, "sharedBaseThicknessRoot", 0.5f, WriteMode.DirectAssignment);
                PartFields.Set(part, PWingModule, "sharedBaseThicknessTip", 0.5f, WriteMode.DirectAssignment);
            }

            yield return context.Settled();
            yield return context.Settled();
            EditorBuilder.PresentShip();

            int wingEnd = EditorBuilder.WideEndAlongLocalX(wing);
            int flapEnd = EditorBuilder.WideEndAlongLocalX(flap);

            Harness.Log($"ROOTEND wing: wide (root) end at local X {Describe(wingEnd)}, " +
                        $"isMirrored {wing.isMirrored}");
            Harness.Log($"ROOTEND control surface: wide (root) end at local X {Describe(flapEnd)}, " +
                        $"isMirrored {flap.isMirrored}");

            yield return context.Say("Which end is the root?",
                                     $"A wing and a control surface, each 2 m at the root and 0.4 m at the "
                                     + "tip, so the two ends are unmistakable.\n\n"
                                     + $"Wing: root end at local X {Describe(wingEnd)}.\n"
                                     + $"Control surface: root end at local X {Describe(flapEnd)}.\n\n"
                                     + "This is a measurement of B9, not a test of this mod. Everything that "
                                     + "pairs a control surface's ends with places along its wing depends on "
                                     + "the answer, and it has been guessed wrong twice.");

            context.CheckTrue("the wing's root end could be identified", wingEnd != 0);
            context.CheckTrue("the control surface's root end could be identified", flapEnd != 0);
        }

        /// <summary>Put a measured end into words.</summary>
        private static string Describe(int end) =>
            end > 0 ? "POSITIVE (+X)" : end < 0 ? "NEGATIVE (-X)" : "indistinguishable";

        /// <summary>
        /// A mirror-symmetric pair of wings, each with a control surface: both sides
        /// have to follow a thickness change the same way.
        /// </summary>
        /// <remarks>
        /// The hole this fills is larger than the bug it was written for. Every other
        /// wing fixture here builds ONE wing on one side of a fuselage, so in
        /// forty-odd scenarios there has never been a mirrored part anywhere - and a
        /// mirrored part's own axes point the opposite way to its twin's. Anything
        /// that works out which end is which from those axes gets one side of every
        /// aircraft ever built exactly backwards, and nothing here could see it.
        ///
        /// It is also why this looked like a Spaceplane Hangar problem. Mirroring
        /// works identically in both editors; the SPH is simply where a person
        /// naturally builds a symmetric pair of wings.
        /// </remarks>
        private static IEnumerator MirroredWingsBothFollowThickness(TestContext context)
        {
            if (EditorBuilder.FindPart(PWing) == null || EditorBuilder.FindPart(PWingCtrlSrf) == null)
            {
                context.Skip("B9 Procedural Wings is not installed");
                yield break;
            }

            Part fuselage = EditorBuilder.Spawn(PPTank);
            var wings = new Part[2];
            var flaps = new Part[2];
            for (int i = 0; i < 2; i++)
            {
                wings[i] = EditorBuilder.Spawn(PWing);
                flaps[i] = EditorBuilder.Spawn(PWingCtrlSrf);
            }
            if (fuselage == null || wings[0] == null || wings[1] == null
                || flaps[0] == null || flaps[1] == null)
            {
                context.Skip("parts unavailable");
                yield break;
            }
            yield return context.Frames(8);

            const float span = 4f;
            PartFields.Set(fuselage, PPShapeModule, "diameter", 1.25f, WriteMode.PartActionWindow);
            for (int i = 0; i < 2; i++)
            {
                foreach (Part part in new[] { wings[i], flaps[i] })
                {
                    PartFields.Set(part, PWingModule, "sharedBaseWidthRoot", 2f, WriteMode.DirectAssignment);
                    PartFields.Set(part, PWingModule, "sharedBaseWidthTip", 2f, WriteMode.DirectAssignment);
                    PartFields.Set(part, PWingModule, "sharedBaseThicknessRoot", 0.5f, WriteMode.DirectAssignment);
                    PartFields.Set(part, PWingModule, "sharedBaseThicknessTip", 0.5f, WriteMode.DirectAssignment);
                    PartFields.Set(part, PWingModule, "sharedBaseLength", span, WriteMode.DirectAssignment);
                }
            }
            yield return context.Frames(4);

            EditorBuilder.SetRoot(fuselage);
            EditorBuilder.PresentShip();

            // One wing out each side, which is what makes the pair a mirror.
            Vector3 across = EditorBuilder.DirectionAcrossCamera(fuselage);
            float radius = EditorBuilder.SurfaceRadius(fuselage);
            for (int i = 0; i < 2; i++)
            {
                float side = i == 0 ? 1f : -1f;
                if (!EditorBuilder.SurfaceAttach(fuselage, wings[i], across * side * radius))
                {
                    context.Result.Error($"could not attach wing {i}");
                    yield break;
                }

                // Both flaps mounted the same way round relative to THEIR OWN wing,
                // which is what a real mirrored pair contains. KSP's symmetry here is
                // a 180-degree rotation, not a reflection - every part in a craft
                // built this way carries mir = 1,1,1 - and rotating one wing onto the
                // other maps root to root. So the mirrored flap's root is still at
                // its wing's root, and both halves agree about which end is which.
                //
                // An earlier version of this built the far flap turned end for end,
                // on the assumption that a mirror swaps its ends. It does not, and
                // that fixture reproduced a failure in a configuration nothing builds.
                float back = EditorBuilder.WingEdge(wings[i], trailing: true, station: 0f);
                if (float.IsNaN(back)
                    || !EditorBuilder.AttachAlongEdge(wings[i], flaps[i], new Vector3(0f, back, 0f),
                                                      Vector3.up, Vector3.right, inboardStation: 0f))
                {
                    context.Result.Error($"could not attach control surface {i}");
                    yield break;
                }
            }

            EditorBuilder.LinkSymmetry(wings[0], wings[1]);
            EditorBuilder.LinkSymmetry(flaps[0], flaps[1]);

            yield return context.Settled();
            EditorBuilder.PresentShip();

            // Report how each part is actually oriented relative to its parent. This
            // is the thing that differs between the two sides, and it is invisible
            // from the dimensions alone.
            for (int i = 0; i < 2; i++)
            {
                Harness.Log($"MIRROR wing {i}: {EditorBuilder.DescribeOrientation(wings[i])}");
                Harness.Log($"MIRROR flap {i}: {EditorBuilder.DescribeOrientation(flaps[i])}");
            }

            yield return context.Say("Fixture built.",
                                     "A fuselage with a 4 m wing out each side, mirror-symmetry counterparts "
                                     + "of one another, and a full-length control surface on each wing's "
                                     + "trailing edge.");

            yield return context.Say("Thickening ONE wing's root to 1 m.",
                                     "Both wings should end up 1 m thick at the root - they are symmetry "
                                     + "counterparts - and so should both control surfaces, at the end that "
                                     + "meets the root.\n\n"
                                     + "The mirrored side is the one to watch. Its parts' own axes point the "
                                     + "opposite way to its twin's in world terms - but the symmetry is a "
                                     + "180-degree rotation rather than a reflection, so root still meets "
                                     + "root and both sides should behave identically.");

            PartFields.Set(wings[0], PWingModule, "sharedBaseThicknessRoot", 1f, WriteMode.DirectAssignment);
            yield return context.Settled();
            EditorBuilder.PresentShip();

            for (int i = 0; i < 2; i++)
            {
                context.Check($"wing {i} root thickened",
                              PartFields.Get(wings[i], PWingModule, "sharedBaseThicknessRoot"), 1f);
                context.Check($"flap {i} thickened where it meets the wing's root",
                              PartFields.Get(flaps[i], PWingModule, "sharedBaseThicknessRoot"), 1f);
                context.Check($"flap {i} unchanged where it meets the wing's tip",
                              PartFields.Get(flaps[i], PWingModule, "sharedBaseThicknessTip"), 0.5f);
            }
        }

        /// <summary>
        /// A control surface mounted end for end still takes the wing's thickness at
        /// the right ends.
        /// </summary>
        /// <remarks>
        /// Nothing stops a player attaching a control surface the other way round,
        /// and until now nothing in this suite ever did - every fixture built them
        /// the same way, so "its root" and "the end nearest the fuselage" were the
        /// same thing in every test and the difference between them was never tested.
        /// Turn one end for end and its own root is at the WING'S TIP, so thickening
        /// the wing's root should thicken the flap's TIP.
        /// </remarks>
        private static IEnumerator ReversedControlSurfaceTakesTheRightEnds(TestContext context)
        {
            if (EditorBuilder.FindPart(PWing) == null || EditorBuilder.FindPart(PWingCtrlSrf) == null)
            {
                context.Skip("B9 Procedural Wings is not installed");
                yield break;
            }

            Part fuselage = EditorBuilder.Spawn(PPTank);
            Part wing = EditorBuilder.Spawn(PWing);
            Part flap = EditorBuilder.Spawn(PWingCtrlSrf);
            if (fuselage == null || wing == null || flap == null)
            {
                context.Skip("parts unavailable");
                yield break;
            }
            yield return context.Frames(8);

            const float span = 4f;

            PartFields.Set(fuselage, PPShapeModule, "diameter", 1.25f, WriteMode.PartActionWindow);
            foreach (Part part in new[] { wing, flap })
            {
                PartFields.Set(part, PWingModule, "sharedBaseWidthRoot", 2f, WriteMode.DirectAssignment);
                PartFields.Set(part, PWingModule, "sharedBaseWidthTip", 2f, WriteMode.DirectAssignment);
                PartFields.Set(part, PWingModule, "sharedBaseThicknessRoot", 0.5f, WriteMode.DirectAssignment);
                PartFields.Set(part, PWingModule, "sharedBaseThicknessTip", 0.5f, WriteMode.DirectAssignment);
            }
            PartFields.Set(wing, PWingModule, "sharedBaseLength", span, WriteMode.DirectAssignment);
            PartFields.Set(flap, PWingModule, "sharedBaseLength", span, WriteMode.DirectAssignment);
            yield return context.Frames(4);

            EditorBuilder.SetRoot(fuselage);
            EditorBuilder.PresentShip();
            if (!EditorBuilder.SurfaceAttach(fuselage, wing,
                    EditorBuilder.DirectionAcrossCamera(fuselage) * EditorBuilder.SurfaceRadius(fuselage)))
            {
                context.Result.Error("could not attach the wing");
                yield break;
            }

            float back = EditorBuilder.WingEdge(wing, trailing: true, station: 0f);
            if (float.IsNaN(back)
                || !EditorBuilder.AttachAlongEdge(wing, flap, new Vector3(0f, back, 0f),
                                                  Vector3.up, Vector3.right,
                                                  inboardStation: 0f, reversed: true))
            {
                context.Result.Error("could not attach the control surface");
                yield break;
            }

            yield return context.Settled();
            EditorBuilder.PresentShip();
            yield return context.Say("Fixture built.",
                                     "A 4 m wing with a full-length flap on its trailing edge - mounted END "
                                     + "FOR END, so the flap's own root is out at the wing's tip and its tip "
                                     + "is in at the fuselage.");

            yield return context.Say("Thickening the wing's ROOT to 1 m.",
                                     "The flap has to end up 1 m thick where it meets the wing's root - and "
                                     + "that is the flap's TIP, because it is mounted backwards. Its own root "
                                     + "is out at the wing's tip and should stay at 0.5 m.\n\n"
                                     + "Getting this the usual way round would thin the flap exactly where "
                                     + "the wing just got thicker.");

            PartFields.Set(wing, PWingModule, "sharedBaseThicknessRoot", 1f, WriteMode.DirectAssignment);
            yield return context.Settled();
            EditorBuilder.PresentShip();

            context.Check("flap is thick where the wing's root is",
                          PartFields.Get(flap, PWingModule, "sharedBaseThicknessTip"), 1f);
            context.Check("flap is unchanged where the wing's tip is",
                          PartFields.Get(flap, PWingModule, "sharedBaseThicknessRoot"), 0.5f);
        }

        /// <summary>
        /// A flap over the outboard half of a wing conforms to the wing at its own
        /// stations rather than copying the wing's root values.
        /// </summary>
        private static IEnumerator PartialSpanFlapInterpolates(TestContext context)
        {
            var rig = new AileronRig();
            yield return BuildAileronRig(context, rig);
            if (!rig.Ok) yield break;

            yield return context.Say("Thickening the wing's ROOT to 1.0 m.",
                                     "The aileron's root sits half way along the wing, so it should end up half "
                                     + "way between the wing's new 1.0 m root and its unchanged 0.2 m tip - that "
                                     + "is 0.6 m, not 1.0 m. Its own tip is at the wing's tip and should not "
                                     + "move at all.");

            PartFields.Set(rig.Wing, PWingModule, "sharedBaseThicknessRoot", 1f, WriteMode.DirectAssignment);
            yield return context.Settled();
            EditorBuilder.PresentShip();

            context.Check("aileron root takes the wing's mid-span thickness",
                          PartFields.Get(rig.Aileron, PWingModule, "sharedBaseThicknessRoot"), 0.6f);
            context.Check("aileron tip unchanged at the wing's tip",
                          PartFields.Get(rig.Aileron, PWingModule, "sharedBaseThicknessTip"), 0.2f);
        }

        /// <summary>
        /// A flap over half a wing becomes half of the wing's new length, and stays
        /// on the half it was on.
        /// </summary>
        private static IEnumerator PartialSpanFlapScalesWithTheWing(TestContext context)
        {
            var rig = new AileronRig();
            yield return BuildAileronRig(context, rig);
            if (!rig.Ok) yield break;

            yield return context.Say("Lengthening the wing to 12 m.",
                                     "Two things should happen. The aileron covers half the wing, so it should "
                                     + "become 6 m rather than 12 m. It should also still be on the OUTBOARD "
                                     + "half - B9 grows a control surface from its middle, so left alone it "
                                     + "would put half its extra length back toward the fuselage.");

            PartFields.Set(rig.Wing, PWingModule, "sharedBaseLength", 12f, WriteMode.DirectAssignment);
            yield return context.Settled();
            EditorBuilder.PresentShip();

            context.Check("aileron span is half the wing's",
                          PartFields.Get(rig.Aileron, PWingModule, "sharedBaseLength"), 6f);

            // The aileron is centred on its own origin along its span, so covering
            // the wing from 6 m to 12 m puts that origin at the 9 m mark. Loose
            // tolerance because this is a position rather than a field: it is here to
            // catch the aileron sliding a whole 3 m inboard.
            context.Check("aileron still on the outboard half",
                          rig.Wing.transform.InverseTransformPoint(rig.Aileron.transform.position).x,
                          9f, tolerance: 0.4f);
        }

        /// <summary>A wing and an aileron over the outboard half of it.</summary>
        private class AileronRig
        {
            public Part Fuselage;
            public Part Wing;
            public Part Aileron;
            public bool Ok;
        }

        /// <summary>Assemble the partial-span aileron fixture.</summary>
        private static IEnumerator BuildAileronRig(TestContext context, AileronRig rig)
        {
            if (EditorBuilder.FindPart(PWing) == null || EditorBuilder.FindPart(PWingCtrlSrf) == null)
            {
                context.Skip("B9 Procedural Wings is not installed");
                yield break;
            }

            rig.Fuselage = null; Part fuselage = EditorBuilder.Spawn(PPTank);
            Part wing = EditorBuilder.Spawn(PWing);
            Part aileron = EditorBuilder.Spawn(PWingCtrlSrf);
            if (fuselage == null || wing == null || aileron == null)
            {
                context.Skip("parts unavailable");
                yield break;
            }

            yield return context.Frames(8);

            const float wingSpan = 8f;
            const float rootThickness = 0.6f;
            const float tipThickness = 0.2f;
            const float midThickness = 0.5f * (rootThickness + tipThickness);   // 0.4 at half span

            PartFields.Set(fuselage, PPShapeModule, "diameter", 1.25f, WriteMode.PartActionWindow);

            PartFields.Set(wing, PWingModule, "sharedBaseWidthRoot", 3f, WriteMode.DirectAssignment);
            PartFields.Set(wing, PWingModule, "sharedBaseWidthTip", 3f, WriteMode.DirectAssignment);
            PartFields.Set(wing, PWingModule, "sharedBaseThicknessRoot", rootThickness, WriteMode.DirectAssignment);
            PartFields.Set(wing, PWingModule, "sharedBaseThicknessTip", tipThickness, WriteMode.DirectAssignment);
            PartFields.Set(wing, PWingModule, "sharedBaseLength", wingSpan, WriteMode.DirectAssignment);

            // Already conforming: half as long, and matching the wing where it sits.
            PartFields.Set(aileron, PWingModule, "sharedBaseWidthRoot", 1f, WriteMode.DirectAssignment);
            PartFields.Set(aileron, PWingModule, "sharedBaseWidthTip", 1f, WriteMode.DirectAssignment);
            PartFields.Set(aileron, PWingModule, "sharedBaseThicknessRoot", midThickness, WriteMode.DirectAssignment);
            PartFields.Set(aileron, PWingModule, "sharedBaseThicknessTip", tipThickness, WriteMode.DirectAssignment);
            PartFields.Set(aileron, PWingModule, "sharedBaseLength", wingSpan * 0.5f, WriteMode.DirectAssignment);
            yield return context.Frames(4);

            EditorBuilder.SetRoot(fuselage);
            EditorBuilder.PresentShip();
            Vector3 mount = EditorBuilder.DirectionAcrossCamera(fuselage) * EditorBuilder.SurfaceRadius(fuselage);
            if (!EditorBuilder.SurfaceAttach(fuselage, wing, mount))
            {
                context.Result.Error("could not attach the wing to the fuselage");
                yield break;
            }

            // On the trailing edge, starting half way out along the wing's span and
            // running to its tip.
            float trailingEdge = EditorBuilder.WingEdge(wing, trailing: true, station: 0.5f);
            if (float.IsNaN(trailingEdge))
            {
                context.Result.Error("could not find the wing's trailing edge");
                yield break;
            }
            if (!EditorBuilder.AttachAlongEdge(wing, aileron,
                                               new Vector3(wingSpan * 0.5f, trailingEdge, 0f),
                                               Vector3.up, Vector3.right,
                                               inboardStation: wingSpan * 0.5f))
            {
                context.Result.Error("could not attach the aileron to the wing");
                yield break;
            }

            yield return context.Settled();
            EditorBuilder.PresentShip();
            yield return context.Say("Fixture built.",
                                     $"An {wingSpan:F0} m wing tapering {rootThickness:F1} m at the root to "
                                     + $"{tipThickness:F1} m at the tip, with a {wingSpan * 0.5f:F0} m aileron on "
                                     + "the outboard half of its trailing edge. The aileron already conforms: "
                                     + $"{midThickness:F1} m at its root, where the wing is half way along, and "
                                     + $"{tipThickness:F1} m at its tip.");


            rig.Fuselage = fuselage;
            rig.Wing = wing;
            rig.Aileron = aileron;
            rig.Ok = true;
        }

        /// <summary>
        /// Chord names end-to-end joints only, so it must not cross onto a control
        /// surface even when both parts happen to have the same chord.
        /// </summary>
        private static IEnumerator WingControlSurfaceChordIsIndependent(TestContext context)
        {
            // Two metres, not three: B9 caps a control surface's chord at 2 m, so a
            // 3 m wing no longer shares a value with its flaps and the "same number
            // must not cross" premise of this test quietly evaporates.
            var rig = new WingRig();
            yield return BuildWingRig(context, rig, chord: 2f, thicknessRoot: 0.5f, thicknessTip: 0.5f);
            if (!rig.Ok) yield break;

            yield return context.Say("Narrowing the wing's tip chord to 1 m.",
                                     "Everything here is 2 m in chord - the wing and both control surfaces - so "
                                     + "a rule that went by value alone would drag the control surfaces along "
                                     + "with it. They should keep their own 2 m.");

            PartFields.Set(rig.Wing, PWingModule, "sharedBaseWidthTip", 1f, WriteMode.DirectAssignment);
            yield return context.Settled();
            EditorBuilder.PresentShip();

            context.Check("wing tip chord changed",
                          PartFields.Get(rig.Wing, PWingModule, "sharedBaseWidthTip"), 1f);
            context.Check("trailing surface chord untouched",
                          PartFields.Get(rig.Trailing, PWingModule, "sharedBaseWidthTip"), 2f);
            context.Check("leading surface chord untouched",
                          PartFields.Get(rig.Leading, PWingModule, "sharedBaseWidthTip"), 2f);

            yield return context.Say("The control surfaces should have followed the wing's new edges.",
                                     "Narrowing the tip chord swept both of the wing's edges. A control "
                                     + "surface's chord has nothing to do with the wing's, but the LINE it "
                                     + "sits on does, so each one should have turned to lie along its edge "
                                     + "and moved to stay against it. Its offset changes with them - B9 "
                                     + "stores that as sweep per metre of span, which is what shears the "
                                     + "ends of a turned surface back to streamwise. Changing the offset "
                                     + "without turning the part just skews it.");

            // The edges are swept now, so both ends have to be checked: a surface
            // that only turned about the wrong point, or did not turn at all, still
            // meets the wing at one end.
            foreach (var each in new[] { (rig.Trailing, true, "trailing"), (rig.Leading, false, "leading") })
            {
                context.Check($"{each.Item3} surface meets the wing at the root",
                              EditorBuilder.EdgeGap(rig.Wing, each.Item1, each.Item2, station: 0f),
                              0f, tolerance: 0.15f);
                context.Check($"{each.Item3} surface meets the wing at the tip",
                              EditorBuilder.EdgeGap(rig.Wing, each.Item1, each.Item2, station: 1f),
                              0f, tolerance: 0.15f);
            }

            // A hinge lying along a swept edge is the hypotenuse of the span it
            // covers, so it has to be longer than the wing or it stops short of the
            // tip. Worked out from the chords rather than written down, so narrowing
            // the fixture later cannot leave a stale number here.
            float wingSpan = PartFields.Get(rig.Wing, PWingModule, "sharedBaseLength");
            float edgeDrop = Mathf.Abs(EditorBuilder.WingEdge(rig.Wing, trailing: true, station: 1f)
                                       - EditorBuilder.WingEdge(rig.Wing, trailing: true, station: 0f));
            float expectedHinge = wingSpan * Mathf.Sqrt(1f + Mathf.Pow(edgeDrop / wingSpan, 2f));
            context.Check("trailing surface reaches the whole span",
                          PartFields.Get(rig.Trailing, PWingModule, "sharedBaseLength"),
                          expectedHinge, tolerance: 0.05f);
            context.Check("leading surface reaches the whole span",
                          PartFields.Get(rig.Leading, PWingModule, "sharedBaseLength"),
                          expectedHinge, tolerance: 0.05f);
        }

        // =====================================================================
        // ROLib
        // =====================================================================

        /// <summary>
        /// The same base case against ROLib, whose currentDiameter field and change
        /// handling are nothing like ProceduralParts'.
        /// </summary>
        private static IEnumerator ROLibTankStackPropagates(TestContext context)
        {
            string roTank = FirstInstalled("ROT-GenericTank", "ROT-BoosterTank", "ROT-AtlasTank");
            if (roTank == null)
            {
                context.Skip("no ROTanks part installed");
                yield break;
            }

            var stack = new Stack();
            yield return BuildStack(context, stack, roTank, "ModuleROTank", "currentDiameter", 2f, 2f);
            if (!stack.Ok) yield break;

            // ROLib derives a tank's length from its diameter, so this one grows
            // downward as well as outward. Budget for it before anything moves.
            EditorBuilder.WillEdit(stack.Parts[0], growth: 2f);

            yield return context.Say("Setting the lower RO tank to 3 m.",
                                     "The RO tank above should follow to 3 m.\n\n"
                                     + "Both tanks will get LONGER as well as wider. That is ROLib, not us: "
                                     + "this part has lengthWidth = true, so its OnDiameterChanged calls "
                                     + "ValidateLength, which raises the minimum length as the domed ends grow "
                                     + "with the diameter. The same thing happens if you drag the Diameter "
                                     + "slider by hand.");

            PartFields.Set(stack.Parts[0], "ModuleROTank", "currentDiameter", 3f, WriteMode.PartActionWindow);
            yield return context.Settled();

            context.Check("upper RO tank diameter",
                          PartFields.Get(stack.Parts[1], "ModuleROTank", "currentDiameter"), 3f);
        }

        /// <summary>
        /// A ProceduralParts tank under an RO-Tanks tank: the change has to cross
        /// between two mods that share nothing but the attach node between them.
        /// </summary>
        private static IEnumerator MixedPPAndROLibStack(TestContext context)
        {
            string roTank = FirstInstalled("ROT-GenericTank", "ROT-BoosterTank", "ROT-AtlasTank");
            if (roTank == null)
            {
                context.Skip("no ROTanks part installed");
                yield break;
            }

            var stack = new Stack();
            stack.Parts = new Part[2];
            stack.Parts[0] = EditorBuilder.Spawn(PPTank);
            stack.Parts[1] = EditorBuilder.Spawn(roTank);
            if (stack.Parts[0] == null || stack.Parts[1] == null)
            {
                context.Skip("parts unavailable");
                yield break;
            }

            yield return context.Frames(6);
            PartFields.Set(stack.Parts[0], PPShapeModule, "diameter", 2f, WriteMode.PartActionWindow);
            PartFields.Set(stack.Parts[1], "ModuleROTank", "currentDiameter", 2f, WriteMode.PartActionWindow);
            yield return context.Frames(2);

            EditorBuilder.SetRoot(stack.Parts[0]);
            if (!EditorBuilder.StackOnTop(stack.Parts[0], stack.Parts[1]))
            {
                context.Result.Error("could not stack the RO tank on the procedural tank");
                yield break;
            }
            yield return context.Settled();

            EditorBuilder.WillEdit(stack.Parts[0], growth: 2f);

            yield return context.Say("Setting the ProceduralParts tank to 3 m.",
                                     "The RO-Tanks part on top should follow, across the boundary between two "
                                     + "unrelated mods.\n\n"
                                     + "Watch the RO tank get longer too. ROLib ties length to diameter on this "
                                     + "part, so widening it raises its minimum length. DimensionSync only ever "
                                     + "writes the diameter; the length is ROLib's own doing.");

            PartFields.Set(stack.Parts[0], PPShapeModule, "diameter", 3f, WriteMode.PartActionWindow);
            yield return context.Settled();

            context.Check("RO tank followed the procedural tank",
                          PartFields.Get(stack.Parts[1], "ModuleROTank", "currentDiameter"), 3f);
        }

        /// <summary>
        /// Which cone shape module the part currently has selected. ProceduralParts
        /// ships more than one and the choice depends on the part and its upgrades.
        /// </summary>
        private static string ActiveConeModule(Part part)
        {
            foreach (string name in PPConeModules)
                if (PartFields.ActiveModule(part, name) != null) return name;
            return null;
        }

        /// <summary>
        /// The first of these parts that this install actually has, or null - which
        /// lets a scenario skip cleanly rather than fail on a missing mod.
        /// </summary>
        private static string FirstInstalled(params string[] partNames)
        {
            foreach (string name in partNames)
                if (EditorBuilder.FindPart(name) != null) return name;
            return null;
        }
    }
}
