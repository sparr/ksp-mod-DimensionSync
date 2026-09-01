# Dimension Sync

A KSP editor mod that keeps the dimensions of connected parts in step.

Change a procedural part's diameter and the new size travels up and down the
stack, stopping at the first part whose facing end was a different size than the
changed part was *before* the change. Change a procedural wing's tip chord and
the next segment outboard takes it as its root chord. Stock parts, radially
attached parts and anything the player cannot edit are left alone.

Out of the box it knows about ProceduralParts, ROLib (RO-Tanks and friends),
Procedural Fairings, SSTU and B9 Procedural Wings; anything else can be added
from config.

Status: works, but young. Bug reports welcome.

## How it works

**Detection is by polling.** Every `LateUpdate` in the editor, each tracked
dimension field is compared with its value at the end of the previous frame.
That is deliberately different from subscribing to `BaseField.OnValueModified`,
which only fires for changes routed through `BaseField.SetValue`: the procedural
mods this exists to work with frequently assign their fields directly, and those
changes are invisible to the event. Polling sees every route into the field
whichever mod took it, and a change is picked up on the same frame the part
action window applied it.

**Writes go through the part action window's own protocol.** Setting a
neighbour's diameter reproduces `UIPartActionFieldItem.SetFieldValue` step for
step: symmetry counterparts first, then `BaseField.SetValue`, then
`uiControlEditor.onFieldChanged(field, previousValue)`. The second argument
matters. KSP passes the field's *previous* value there, and ProceduralParts and
Procedural Fairings both start their handler with "if the argument equals the
current value, do nothing" - so handing them the new value makes the callback a
no-op and the part's mesh never rebuilds, even though its KSPField now holds the
new number.

**Propagation walks a run of parts, not the whole tree.** There are three kinds
of joint. A *stack* joint is two parts joined by attach nodes pointing along
their own ±Y axes - tanks, adapters, fairing bases. A *span* joint is end to
end: one part surface-attached at another's tip, which is how procedural wing
segments chain, so the child's root meets the parent's tip. An *edge* joint is
side by side: one part surface-attached along another's flank, the way a control
surface sits on a wing, so root meets root and tip meets tip. Which of the two
surface joints applies is read off the geometry - whether the child's surface
node faces along its own root-to-tip axis or across it.

A dimension names the kinds of joint it travels over, and may name more than
one. A tank's diameter travels stack joints only, so it never crosses onto a
booster stuck to its side. A wing's chord travels end-to-end joints only, so it
reaches the next segment out but never the flap lying against it. A wing's
thickness travels both, because a flap has to be as thick as the wing where they
meet.

At each part the walk applies the new value to every field on the same *channel*
that still holds the pre-change value, then carries on out of the ends those
fields describe. Channels keep unrelated quantities apart: a hollow part's bore
never picks up its outside diameter, and a wing's thickness never lands on its
chord. So a cone with equal ends behaves as part of a uniform stack and passes
the change through, while a cone with a different far end absorbs it and stops -
and the same rule makes a constant-chord wing run carry a change all the way out
while a tapered segment absorbs it. If a field clamps the value, because its
editor control has a smaller range, propagation stops there too.

**Wings.** For a wing, "bottom" is the root - the end surface-attached to its
parent - and "top" is the tip. Along a chain of wing segments, chord (`width`),
`thickness` and the leading and trailing edge widths all carry from one
segment's tip to the next one's root. Sideways onto a control surface, only
`thickness` and `span` carry: a flap must be as thick as the wing where they
meet and as long as the wing it runs along, but its chord is how far forward it
reaches and is nobody else's business. B9 Procedural Wings draws the same line -
its own "Inherit base" button refuses outright when either part is a control
surface.

Sweep offsets are understood but off by default: they pair as equal and opposite
across a joint (`mirrored`), which is a shape decision rather than a fit-up
requirement.

## Configuration

`GameData/DimensionSync/DimensionSync.cfg` holds the settings and the catalogue
of known dimension fields. Other mods can extend it with ModuleManager:

```
@DIMENSION_SYNC:FOR[MyMod]
{
	FIELD
	{
		module = MyResizer
		field = tankDiameter
	}
	FIELD
	{
		module = MyResizer
		field = noseRadius
		isRadius = true
		ends = top
	}
}
```

Each node's braces have to be on their own lines. KSP's config parser reads a
one-line `FIELD { module = X  field = Y }` as a single nonsense value and the
entry is silently ignored.

| key | meaning |
| --- | --- |
| `module` | PartModule class name. Omit to match the field on any module. |
| `field` | KSPField name. |
| `isRadius` | `true` when the field holds a radius rather than a diameter. |
| `channel` | Which quantity this is; dimensions only sync within a channel. Built in: `outer` (default), `inner`, `width`, `thickness`, `span`, `edgeLeading`, `edgeTrailing`, `offset`. Any other name works. |
| `link` | Which joints it travels over, as a list: `stack` (default), `span`, `edge`. e.g. `link = span, edge`. |
| `spanAxis` | `x` (default), `y` or `z`: the part-local root-to-tip axis, used to classify surface joints. |
| `ends` | `both` (default), `top`/`tip`, `bottom`/`root`, or `none`. |
| `mirrored` | `true` when the two sides of a joint hold equal and opposite values. |
| `requiresEditorControl` | `false` for fields driven from a mod's own window rather than the PAW. |
| `requireGuiUnits` | Only treat the field as this dimension while its `guiUnits` match. |
| `enabled` | `false` to ignore the field. |

### Settings

| key | default | meaning |
| --- | --- | --- |
| `debug` | `false` | Log every tracked field and propagation step. |
| `tolerance` | `0.01` | How far apart two parts may be and still count as the same size, as a fraction of the larger. |
| `marginMode` | `none` | What to do about a neighbour that was near but not exactly equal: `none`, `absolute` or `proportional`. |
| `changeEpsilon` | `0.0001` | The smallest movement in a field that counts as a change at all. |
| `passThroughRigidParts` | `false` | When true, a part with nothing on the channel being propagated does not stop the walk. |
| `matchControlSurfaceSweep` | `true` | Keep a control surface's sweep matched to the wing carrying it. |
| `anchorSpanChanges` | `true` | Keep a part that runs alongside its host on the same stretch of it when its span changes. |
| `alignWingJoints` | `true` | Move a wing segment back onto the tip of the segment inboard of it when that one is swept or lengthened. |
| `keepWingEdgesStraight` | `true` | Carry a chord change through a run of wing segments whose edges form one straight line. |

**In game.** `marginMode`, `tolerance` and the two wing settings can also be
changed from the DimensionSync button on the editor toolbar. What you choose
there is saved to `GameData/DimensionSync/PluginData/Settings.cfg` and takes
precedence over `DimensionSync.cfg`; the other settings stay where config puts
them, so a ModuleManager patch controlling one cannot be overridden by mistake.

**Near misses.** Parts that were meant to match are rarely identical to the last
decimal, so anything within `tolerance` counts as the same size and follows
along. `marginMode` decides what happens to the difference. With a 3.00 m tank
next to a 3.02 m one, taking the 3.00 to 6.00:

| `marginMode` | the 3.02 becomes | |
| --- | --- | --- |
| `none` | 6.00 | the gap is closed |
| `absolute` | 6.02 | the gap is kept as an amount |
| `proportional` | 6.04 | the gap is kept as a ratio |

Every part in a run measures its own gap against the part that actually changed,
not against whichever neighbour was adjusted just before it, so margins do not
compound along a stack.

**Straight edges.** Whether a dimension crosses a joint is normally decided by
the two parts being the same size, which is right for a stack of tanks and wrong
for a wing. A delta built from three tapering segments has no two chords the same
anywhere, and yet its leading edge is one straight line — so narrowing a segment
in the middle leaves a kink, and stopping there leaves the kink. What matters at a
wing joint is whether an edge runs straight through it: where it did,
`keepWingEdgesStraight` puts each outboard tip wherever the edge needs it and
carries on. A triangle stays a triangle.

Note that this governs *carry-through* only. Whether a change enters the
neighbour at all still depends on the two chords meeting at the joint, because
that is what it means for them to be joined.

**Wings and control surfaces.** Three things about B9 wings do not fit "copy
this number onto that one", and all three are answered by moving a part rather
than by writing a field.

Sweeping a segment's tip moves the joint the next segment out is standing on.
B9's own "inherit base" button closes that by shearing the neighbour's root to
match — but that changes a planform the player chose. `alignWingJoints` moves
the neighbour instead, so it keeps its shape and follows the tip. The same rule
keeps the joint closed when the inboard segment's span changes.

A control surface's length grows from its middle rather than from its root, so
making one longer moves the end that was against the fuselage.
`anchorSpanChanges` puts it back on the stretch of wing it was on.

A control surface on a wing whose chords differ has to follow a *swept* edge.
Its offset fields hold a sweep rate per metre of span rather than a distance, so
the wing's own offset cannot be handed across — and setting the rate on its own
just skews the part. `matchControlSurfaceSweep` turns the surface to lie along
the edge, moves it onto the edge, and sets the rate, which is what shears its
ends back to streamwise.

`tolerance` and `changeEpsilon` are deliberately separate. The first is about
whether two *parts* are the same size and wants to be loose; the second is about
whether a *field* moved at all and has to stay tight, or a slider dragged in
millimetre steps would go unnoticed.

## What's covered

| Mod | Module | Fields |
| --- | --- | --- |
| ProceduralParts | `ProceduralShape*` | `diameter`, `top`/`bottomDiameter`, `inner`/`outerDiameter`, hollow-cone variants |
| ROLib (RO-Tanks, RO-Capsules, RO-Heatshields) | `ModuleROTank` | `currentDiameter` |
| SSTU and its forks | `SSTUModularPart`, `SSTUInterstageDecoupler`, `SSTUInterstageFairing`, … | `currentDiameter`, `currentTop`/`currentBottomDiameter`, `top`/`bottomDiameter` |
| Procedural Fairings | `ProceduralFairingBase` | `baseSize` (bottom), `topSize` (top) |
| B9 Procedural Wings, all forks | `WingProcedural` | `sharedBaseWidthRoot`/`Tip`, `sharedEdgeWidthLeading`/`TrailingRoot`/`Tip` (end to end); `sharedBaseThicknessRoot`/`Tip` (end to end and side by side); `sharedBaseLength` (side by side); `sharedBaseOffsetRoot`/`Tip` (off by default) |

The `currentDiameter`, `topDiameter` and `bottomDiameter` entries are registered
against any module, not just the ones listed, so SSTU derivatives and other forks
of that lineage are picked up without needing their own entry.

Known but deliberately off:

* **TweakScale** `tweakScale`. It is a diameter in metres only for stack scale
  types - elsewhere it is a percentage or an index - so the entry carries a
  `requireGuiUnits = m` guard. It stays disabled by default because scaling a
  part also changes its length, mass and cost, which is more than the player
  asked for by dragging a neighbour's diameter. Enable it in config if you want it.
* **AnisotropicPartResizer** (Hangar, Configurable Containers) `size`. A
  multiplier rather than a measurement, so there is nothing to match against a
  neighbour's diameter.
* **RealChute** `deployedDiameter` / `preDeployedDiameter`. Canopy sizes, not
  part sizes, and explicitly excluded so the generic name rules cannot catch them.

## Requirements for a mod to be supported

The dimension must be a numeric `KSPField`. The usual case is a field that is
`guiActiveEditor` with a `uiControlEditor` and an `onFieldChanged` callback,
which is exactly what a mod already needs for its own PAW slider to work.
DimensionSync honours `UI_FloatEdit` / `UI_FloatRange` / `UI_ScaleEdit` ranges
when writing, and skips fields whose module is disabled, so ProceduralParts'
inactive shape modules are correctly ignored.

A mod that keeps its dimensions out of the PAW and drives them from its own
window is supported too, as long as it notices the field changing by itself.
B9 Procedural Wings re-reads its fields every `Update` and rebuilds, so setting
`requiresEditorControl = false` on the descriptor is all it needs.

## Installing

Copy `GameData/DimensionSync` into your KSP `GameData` folder. There are no hard
dependencies. ModuleManager is only needed if you or another mod want to extend
the field catalogue by patch.

## Building

```sh
dotnet build src/DimensionSync.csproj
```

Set `KSPBT_GameRoot` in `src/DimensionSync.csproj.user` to your KSP directory.
The build assembles the whole releasable mod folder under `GameData/DimensionSync`:
the DLL into `Plugins/`, the licence, readme and changelog copied up from the
repository root, and `DimensionSync.version` rewritten with the current version.

Releasing is `<Version>` in `src/DimensionSync.csproj`, a build, a `CHANGELOG.md`
entry, and a GitHub release whose zip contains the `GameData` folder. The
`.version` file and `NetKAN/DimensionSync.netkan` between them let KSP-AVC and
CKAN pick the release up without further work.

## Tests

```sh
dotnet test test/DimensionSync.Tests                  # propagation rules, no game needed
DS_DISPLAY=:0 pytest test/integration                 # drives a real KSP install
```

The unit tests compile the game-independent half of the mod against a fake part
graph and cover the propagation rules directly.

The integration tests build stacks of real ProceduralParts, ROLib and Procedural
Fairings parts in a headless VAB, change one diameter the way the part action
window would, and check what happened to the neighbours - including whether the
neighbour's *mesh* was rebuilt, not just its KSPField overwritten. They need a
one-time `test/integration/setup_testenv.sh`; see
[test/integration/README.md](test/integration/README.md).

`DS_DISPLAY=:0 test/integration/walkthrough.sh` runs the same scenarios in a
visible KSP window, pausing before each operation to explain what is about to
happen and what should come of it.
