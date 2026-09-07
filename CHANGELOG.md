# Changelog

All notable changes to DimensionSync are recorded here. This project follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and
[semantic versioning](https://semver.org/).

## [Unreleased]

## [0.2.0]

Hollow parts, and the fixes from a long test session against real craft.

### Added

- Hollow parts: a part's bore and its outside diameter can follow one another,
  under `hollowCoupling` - `hard` (the default; the bore never moves and a change
  that will not fit around it stops short), `soft` (the bore gives up exactly the
  room the outside needs and no more), `proportional` (the bore stays the same
  fraction of the outside) or `constant` (the wall keeps its thickness). A hollow
  cone keeps each end's pair separate.
- A bore and an outside diameter now match each other across a joint, so a stack
  can be built to either: a plug sized to the tank above it follows when that
  tank's bore moves. Within a single part they still cannot set one another,
  because the walk only writes fields that still hold the pre-change value and a
  part's two diameters are never equal.
- Nested parts: a part slid inside another's bore keeps its clearance when either
  of them is resized, under the same four `hollowCoupling` modes. It reads from
  either end - move the bore and the part inside follows, resize the part and the
  bore makes room. Nesting is recognised from any of the four end-plane pairings
  being flush, so a part turned end for end about its joint or slid until its far
  face lines up with its host's far face still counts; the two need neither a
  shared axis nor matching diameters.
- Other mods are asked what they changed, rather than it being inferred from the
  fields afterwards, which recovers the value a field held *before* somebody else
  wrote it. One hook on KSP's own `BaseField.SetValue` covers ProceduralParts,
  ROLib and stock; B9 Procedural Wings gets a second because it bypasses that
  seam. Needs Harmony, and is entirely optional - without it the mod behaves
  exactly as it did before.
- `hollowCoupling` on the in-game settings window, with worked examples, beside
  the margin and wing settings. What you pick there is saved to
  `PluginData/Settings.cfg` like the rest.

### Fixed

- A mirrored pair of control surfaces drifted 0.0625 m apart along otherwise
  identical wings whenever their parent wing was shortened. Two rules disagreed
  about what the surface measured before the edit began: on the mirrored side B9
  had already written the new length and its own cache together, so that side's
  anchor saw a flap that had never changed length while its hinge saw the full
  change. Neither side ever ran both rules.
- A control surface is refitted when its wing's thickness changes, and kept
  coplanar through a change to its parent's span.
- A control surface's edge is remembered rather than re-derived, so it stays on
  the edge it was placed on.
- A player's move is adopted before the rules run, and KSP's stored offset is
  updated when that move is accepted, so a part the player positioned is judged
  against where it actually is.
- Where only one edge of a wing joint lines up, the offset is spent rather than
  the chord.

### Changed

- The settings window's wing section is described correctly in the readme as four
  toggles rather than two.
- CKAN now suggests Harmony, which the foreign-write hooks use when it is
  present.

## [0.1.0]

First working release.

### Added

- Diameter synchronisation along stacks: changing a procedural part's diameter
  pushes the new size up and down the stack, stopping at the first part whose
  facing end was a different size than the changed part was before the change.
- Chord and thickness synchronisation along procedural wings, from one segment's
  tip to the next segment's root.
- Control surfaces: a flap lying along a wing takes the wing's thickness and
  span, root to root and tip to tip, while keeping its own chord.
- Support for ProceduralParts, ROLib (RO-Tanks and friends), SSTU and its forks,
  Procedural Fairings, and B9 Procedural Wings.
- `DimensionSync.cfg`, a ModuleManager-patchable catalogue of dimension fields,
  so other mods can be supported without a code change.
- `passThroughRigidParts`, off by default: when on, a part with nothing on the
  channel being propagated no longer stops the walk.
- A match tolerance, 1% by default, so parts that were meant to match but differ
  in the last decimal still count as the same size, with `marginMode` deciding
  whether the difference is closed, kept as an amount, or kept as a ratio.
- Control surfaces covering only part of a wing take the wing's interpolated
  thickness at their own stations, and a span scaled by how much they cover.
- A chord change carries on through a run of wing segments whose edges form one
  straight line, instead of stopping at the first chord that differs, so a delta
  built from several segments keeps its shape. `keepWingEdgesStraight` turns this
  off.
- Sweeping or lengthening a wing segment moves the segment outboard of it back
  onto its tip, so the joint stays closed and the outboard segment keeps the
  planform it was given. `alignWingJoints` turns this off.
- A control surface follows the wing edge it is mounted on when that edge becomes
  swept: it is turned to lie along the edge, moved onto it, and given the edge's
  slope as its offset, which is what shears its ends back to streamwise.
  `matchControlSurfaceSweep` turns this off.
- A control surface keeps its place along its wing when either of them gets
  longer. B9 grows one from its middle, so an aileron on a wing's outboard half
  would otherwise put half its new length back toward the fuselage.
  `anchorSpanChanges` turns this off.
- A settings window on the editor toolbar, holding the margin mode, the match
  tolerance and the two wing behaviours above. What it saves goes to
  `PluginData/Settings.cfg` and takes precedence over `DimensionSync.cfg`.

### Fixed

Against the pre-release prototype:

- `onFieldChanged` was being handed the field's new value where KSP passes its
  previous one. ProceduralParts and Procedural Fairings both start their handler
  by comparing that argument against the current value and returning when they
  match, so the callback did nothing and part meshes never rebuilt even though
  the KSPField had changed.
- `BaseField.GetValue` was being called with the Part as the host instead of the
  PartModule. KSP logs the resulting reflection failure and returns null, which
  became a -1 sentinel that failed every size comparison.
- Value clamping only recognised `UI_FloatRange`. ProceduralParts and ROLib both
  use `UI_FloatEdit`, which is not derived from it, so writes ignored the
  slider's real range.
- Change detection relied on `BaseField.OnValueModified`, which only fires for
  changes routed through `BaseField.SetValue`. Mods that assign their fields
  directly were invisible. Detection is now by polling.
