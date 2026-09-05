# Changelog

All notable changes to DimensionSync are recorded here. This project follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and
[semantic versioning](https://semver.org/).

## [Unreleased]

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
