# Dimension Sync

A KSP editor mod that keeps the dimensions of connected parts in step.

Change a procedural part's diameter and the new size travels up and down the
stack, stopping at the first part whose facing end was a different size than the
changed part was *before* the change. Change a procedural wing's tip chord and
the next segment outboard takes it as its root chord. Stock parts, radially
attached parts and anything the player cannot edit are left alone.

Hollow parts are understood: a bore and an outside diameter can follow one
another, a stack can be matched to either, and a part slid inside another's bore
keeps its clearance when either of them is resized.

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

**Other mods are also asked what they changed.** Polling sees that a field moved
but not who moved it, and cannot tell a player reaching for a slider from
another mod writing the same field a frame later - which is the difference
between
leaving a part where somebody put it and rearranging it. The number that settles
it is what the field held *before* the other mod wrote it, and by the time a
watcher notices, nothing anywhere remembers.

Surveying the mods that account for nearly all the parts this targets turned up
one seam rather than four: ProceduralParts, ROLib and stock all write their
counterparts through KSP's own `BaseField.SetValue`, so one hook covers all
three, and a mod nobody here has heard of is likely covered already. B9
Procedural Wings needs a second hook only because it bypasses that seam - it
assigns the backing field and its own cache together, so the counterpart never
experiences a change at all.

This needs Harmony, which some installs have and some do not, so it is entirely
optional: everything is reflection, no assembly is referenced, and without
Harmony the stock-variant source still works, the rest report nothing, and the
mod behaves exactly as it did before.

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
fields describe. Channels keep unrelated quantities apart: a wing's thickness
never lands on its chord.

A bore and an outside diameter are the deliberate exception - they match each
other, because a plug built to fit a bore is as ordinary as one built to fit an
outside. Inside a single part they still cannot, and nothing has to be added to
stop them: the walk writes only fields that *still hold the pre-change value*,
and a part's bore and its outside are never the same number. The match therefore
only bites across a joint, where the neighbour really was built to that size.
Making a part's own two diameters follow one another is a separate rule with its
own setting - see [Hollow parts](#hollow-parts).

So a cone with equal ends behaves as part of a uniform stack and passes the
change through, while a cone with a different far end absorbs it and stops - and
the same rule makes a constant-chord wing run carry a change all the way out
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

**Hollow parts.** Two relationships here are not "two fields holding the same
number", so neither is a channel. Both answer to `hollowCoupling`.

*A part's own two diameters.* The bore and the outside describe one wall from
opposite sides and are never equal, so one can never be propagated onto the
other. They are coupled within the part instead, and what that produces is fed
back into the walk, so it travels out to the neighbours exactly as a player's
edit does. A hollow cone keeps each end's pair separate: its top bore answers to
its top outside, not to its bottom.

*A part nested inside another's bore.* A part slid into a bore is deliberately
*smaller* than it - the clearance is the point - so again the walk can never
carry a change between them; what has to be preserved is the relationship. It
reads from either end: move the bore and the part inside follows, resize the
part and the bore makes room. Two parts count as nested when the smaller lies
inside
the bore and one of the **four** end-plane pairings is flush - both ends of each
part against both ends of the other. A part can be turned end for end about its
joint, so the same face still touches while its opposite end points down the
bore, and it can be slid until its far face lines up with its host's far face; a
rule looking only at the face they were attached by would miss both. They need
not share an axis, and they do not need matching diameters - the coplanar
surface is the whole criterion.

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
| `hollowCoupling` | `hard` | How a bore follows the outside around it, and how a nested part follows the bore around it: `hard`, `soft`, `proportional` or `constant`. |
| `changeEpsilon` | `0.0001` | The smallest movement in a field that counts as a change at all. |
| `passThroughRigidParts` | `false` | When true, a part with nothing on the channel being propagated does not stop the walk. |
| `matchControlSurfaceSweep` | `true` | Keep a control surface's sweep matched to the wing carrying it. |
| `anchorSpanChanges` | `true` | Keep a part that runs alongside its host on the same stretch of it when its span changes. |
| `alignWingJoints` | `true` | Move a wing segment back onto the tip of the segment inboard of it when that one is swept or lengthened. |
| `keepWingEdgesStraight` | `true` | Carry a chord change through a run of wing segments whose edges form one straight line. |

**In game.** `marginMode`, `tolerance`, `hollowCoupling` and the four wing
settings can also be changed from the DimensionSync button on the editor
toolbar. What you choose
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

### Hollow parts

`hollowCoupling` decides two things at once: how a part's bore follows its own
outside, and how a nested part follows the bore around it. They are the same
question asked about two surfaces, so they take the same answer.

| mode | a bore, as the outside moves | a nested part, as the bore moves |
| --- | --- | --- |
| `hard` | never moves; a change that will not fit around it stops short | never moves |
| `soft` | gives up exactly the room the outside needs, and no more | moves only once it no longer fits, and only just enough |
| `proportional` | stays the same fraction of the outside | stays the same fraction of the bore |
| `constant` | the wall keeps its thickness | the clearance keeps its size |

`hard` is the default because it is the only one that never changes a number
nobody asked about. A stack change the bore refuses leaves the craft visibly
mismatched - a 1.00 m tank sitting on a 2.01 m one - and that is information
rather than damage: something you asked for did not fit, and you can see exactly
where. `soft` makes the same promise for every change that *does* fit, and
differs only where `hard` would stop short: taking that same tank down to 1.00 m
drops its bore to 0.99, so the stack matches.

The two proportional-style modes are for shapes rather than fit-up, and they move
the second surface on every edit whether or not anything was in the way.

`soft` is worth distinguishing from `constant` on nested parts, because they look
alike until the bore gets tight. With a 1.500 m part inside a 2.500 m bore, taken
down to 2.000 m:

| `hollowCoupling` | the nested part becomes | |
| --- | --- | --- |
| `hard` | 1.500 | untouched; it still fits |
| `soft` | 1.500 | untouched; it still fits |
| `proportional` | 1.200 | the same fraction of the bore |
| `constant` | 1.000 | the same 1.000 m of clearance |

Take that bore to 1.200 instead and `soft` finally acts, putting the part at
1.190 - just inside - where `hard` leaves it sticking through.

Also on the in-game window, with the same worked examples.

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

Releasing, in order:

1. `<Version>` in `src/DimensionSync.csproj`. It drives the assembly attributes
   and rewrites `DimensionSync.version` at build time, so nothing else needs the
   number typed into it.
2. A `CHANGELOG.md` entry, and a matching `VERSION` block at the top of
   `GameData/DimensionSync/changelog.cfg`. The second is what Kerbal Changelog
   shows in game; the two are maintained by hand and drift silently if only one
   is updated.
3. `dotnet build src/DimensionSync.csproj`, which assembles the whole releasable
   folder under `GameData/DimensionSync` - the DLL into `Plugins/`, and the
   licence, readme and changelog copied up from the repository root.
4. The full suite, green.
5. A git tag, and a GitHub release whose zip contains the `GameData` folder.

The `.version` file and `NetKAN/DimensionSync.netkan` between them let KSP-AVC
and CKAN pick the release up without further work. `release_status` in the netkan
is the one thing that says how finished this is, and is still `development`.

## Tests

```sh
dotnet test test/DimensionSync.Tests                  # propagation rules, no game needed
DS_DISPLAY=:0 pytest test/integration                 # drives a real KSP install
```

The unit tests compile the game-independent half of the mod against a fake part
graph and cover the propagation rules directly.

The integration tests build stacks of real ProceduralParts, ROLib, Procedural
Fairings and B9 parts in a headless VAB and change them the way a player would -
through the part action window's own numeric boxes, through B9's window, and by
dragging the offset gizmo with synthetic mouse input - then check what happened
to the neighbours, including whether the neighbour's *mesh* was rebuilt and not
merely its KSPField overwritten. Ninety-nine scenarios, about fourteen minutes.
They need a one-time `test/integration/setup_testenv.sh`; see
[test/integration/README.md](test/integration/README.md).

`DS_ONLY=substring,substring` narrows a run to matching scenarios, and
`DS_KSP_DIR=/path/to/KSP` runs against a copy of the install - two runs sharing
one install destroy each other's evidence, and the result is a run that quietly
did not happen rather than one that fails.

`DS_DISPLAY=:0 test/integration/walkthrough.sh` runs the same scenarios in a
visible KSP window, pausing before each operation to explain what is about to
happen and what should come of it.
