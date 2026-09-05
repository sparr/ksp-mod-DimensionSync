# Integration tests

These drive a real KSP install: the harness plugin builds stacks of real
procedural parts in the VAB, changes one diameter the way the part action window
would, and checks what DimensionSync did to the neighbours.

## One-time setup

```sh
KSP_ROOT="/path/to/Kerbal Space Program" test/integration/setup_testenv.sh
```

That creates `testenv/KSP`, a throwaway install that symlinks the bulk of your
real one and contains only Squad essentials, ModuleManager, ProceduralParts,
ROLib/ROTanks, Procedural Fairings, B9 Procedural Wings, their shared
dependencies (ROUtils, KSPCommunityFixes, Harmony, Shabby, TexturesUnlimited),
DimensionSync and the harness. Your own install is never written to.

Two things about that install are load-bearing and easy to get wrong if you
build one by hand:

* **`KSP_Data` must be a real directory, not a symlink.** KSP composes paths
  like `KSP_Data/../saves`, and `..` resolves through a symlink to the *target's*
  parent - so a symlinked `KSP_Data` sends saves, craft files and
  `PartDatabase.cfg` into your real install, and `EditorDriver` then throws on
  the save directory it cannot find. The setup script mirrors the tree instead:
  real directories, symlinked files.
* **Every `KSPAssembly` dependency has to be present.** Miss one and KSP quietly
  declines to load the dependent plugin, its parts compile with no PartModules,
  and every scenario fails for reasons unrelated to DimensionSync. The harness
  logs an error when a part spawns with zero modules, which is the giveaway.

Two more traps worth knowing when writing scenarios:

* PartLoader turns underscores in a part's cfg name into dots, so
  `B9_Aero_Wing_Procedural_TypeA` on disk is `B9.Aero.Wing.Procedural.TypeA` at
  runtime. `EditorBuilder.FindPart` tries both spellings.
* KSP's config parser wants each node's braces on their own lines. A one-line
  `FIELD { module = X  field = Y }` in a DimensionSync patch parses as a single
  nonsense value and the entry is silently dropped.

## Running

```sh
pytest test/integration                  # private Xvfb display, ~95s
DS_DISPLAY=:0 pytest test/integration    # your X display, hardware GL, ~30s
pytest test/integration --reuse-report   # re-report the last run without KSP
test/integration/run_game_tests.sh       # same thing without pytest
```

Without `DS_DISPLAY` everything runs on its own Xvfb display. That is the right
default: KSP cannot take focus from whatever you are doing, and input sent to it
cannot reach your other windows. Software GL makes it roughly three times slower
and otherwise identical. Set `DS_DISPLAY` only when you want to watch.

Keep `testenv/PartDatabase.seed.cfg` up to date from a clean run
(`cp testenv/KSP/PartDatabase.cfg testenv/PartDatabase.seed.cfg`). A seed that
matches this install skips drag-cube generation, which is most of the startup
cost - especially under software GL.

`testenv/KSP/KSP.log` has the full `[DimensionSync]` trace, and
`dstest-results.json` is the raw report. A run takes about half a minute on a
real display; on Xvfb it is several minutes, most of it software-rendering drag
cubes.

## How it works

`TestBootstrap` (main menu) starts a sandbox game straight into the VAB.
`TestRunner` (editor) walks the scenarios in `Scenarios.cs`, writes the JSON
report and quits the game.

Each scenario sets its parts' starting diameters *before* attaching them, so
building the fixture cannot trigger the propagation being tested.

Scenarios write fields two ways: `WriteMode.PartActionWindow` reproduces what
the stock PAW does (set the field, then call `onFieldChanged` with the field's
*previous* value), and `WriteMode.DirectAssignment` is a bare field write with
no events, which is how several mods resize their own parts - and the only way
to drive B9 Procedural Wings, whose fields never appear in the PAW.
DimensionSync has to notice both.

The wing scenarios chain segments with `EditorBuilder.SurfaceAttach`, since
procedural wings connect root-to-tip by surface attachment rather than by stack
nodes.

`DirectAssignment` is a single `FieldInfo.SetValue` and nothing else, which is
deliberately cruder than any real mod: the point is to prove the poll notices a
change that emits no events at all. One visible consequence, in
`pp_direct_field_write_is_detected`, is that the part the harness edited keeps
its old mesh until something touches it - nothing ever told its mod to rebuild.
The neighbour DimensionSync writes to *does* rebuild, because that write goes
through the part action window's protocol. A real mod writing its own field
calls its own updater afterwards, the way ProceduralParts' `SeekVolume` calls
`OnShapeDimensionChanged`. DimensionSync leaves the edited part alone on
purpose: re-firing the callback there would make a well-behaved mod rebuild
twice, and ProceduralParts would translate the attached parts twice with it.

## Guided walkthrough

```sh
DS_DISPLAY=:0 test/integration/walkthrough.sh   # on your desktop, to watch
test/integration/walkthrough.sh                 # on a private Xvfb display
```

Runs the same scenarios in a KSP window, stopping before each operation
to say what it is about to do and what should come of it, then showing the
result. Space or **Next** advances, **Skip scenario** moves on, **Run the rest**
finishes without pausing, **Stop** ends early. The game is left running at the
end. Ship editing is locked while a step is waiting so a stray click cannot pick
a part up; the camera is left free, and is re-framed when a fixture is built but
not afterwards, so moving it yourself sticks.

Driving it from a script is only safe on the Xvfb display. Screenshot it with
`DISPLAY=:99 import -window root shot.png` and step it with
`DISPLAY=:99 xdotool key --window <id> space`. Sending keys to a window on your
own display risks leaving a key stuck down system-wide.
