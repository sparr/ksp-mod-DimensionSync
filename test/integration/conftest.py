"""Session fixture that runs the in-game harness once and hands out its report.

KSP is far too slow to start per test, so the whole scenario list runs in a
single launch and each scenario becomes one pytest case.
"""
import json
import os
import re
import shutil
import subprocess
from pathlib import Path

import pytest

#: Repository root, two levels up from test/integration.
ROOT = Path(__file__).resolve().parents[2]

#: The throwaway install built by setup_testenv.sh.
KSP = ROOT / "testenv" / "KSP"

#: Where the in-game harness writes its JSON report.
RESULTS = KSP / "dstest-results.json"

#: Seconds to let KSP run before giving up on it.
TIMEOUT = int(os.environ.get("DS_TIMEOUT", "600"))


def pytest_addoption(parser):
    """Register --reuse-report, for re-reading the last run without relaunching KSP."""
    parser.addoption(
        "--reuse-report",
        action="store_true",
        help="use the existing dstest-results.json instead of launching KSP",
    )


def _build_and_deploy():
    """Rebuild the mod and the harness and copy them into the test install.

    The config is always refreshed and then has debug turned back on, so a change
    to the shipped defaults reaches the test install instead of being masked by a
    stale copy left over from an earlier run.
    """
    for project in ("src/DimensionSync.csproj",
                    "test/DimensionSync.GameTests/DimensionSync.GameTests.csproj"):
        subprocess.run(
            ["dotnet", "build", str(ROOT / project), "-v", "quiet", "--nologo"],
            check=True, cwd=ROOT,
        )
    target = KSP / "GameData" / "DimensionSync"
    (target / "Plugins").mkdir(parents=True, exist_ok=True)
    shutil.copy(ROOT / "GameData/DimensionSync/Plugins/DimensionSync.dll", target / "Plugins")
    for name in ("DimensionSync.cfg", "changelog.cfg"):
        source = ROOT / "GameData/DimensionSync" / name
        if source.exists():
            shutil.copy(source, target)
    config = target / "DimensionSync.cfg"
    config.write_text(re.sub(r"^(\s*)debug = false", r"\1debug = true",
                             config.read_text(), flags=re.M))


def _seed_part_database():
    """Restore PartDatabase.cfg before the run.

    KSP consumes the file while loading and only writes a fresh one on a clean
    exit, so a killed run leaves the install without it. Without it every part's
    drag cubes are re-rendered, which on software GL turns a 40-second startup
    into ten minutes.
    """
    seed = ROOT / "testenv" / "PartDatabase.seed.cfg"
    if seed.exists():
        shutil.copy(seed, KSP / "PartDatabase.cfg")


def _launch():
    """Run KSP until it writes the report and quits, or until TIMEOUT.

    With DS_DISPLAY set the game runs on that X display and gets hardware GL;
    without it, it runs headless on Xvfb with software GL, which works but is
    several times slower.
    """
    args = ["-dstest", "-dstest-out", str(RESULTS)]
    display = os.environ.get("DS_DISPLAY")
    if display:
        # A real X display means hardware GL, which is a great deal quicker.
        cmd = ["./KSP.x86_64", *args]
        env = {**os.environ, "DISPLAY": display}
    else:
        cmd = ["xvfb-run", "-n", "99", "-s", "-screen 0 640x480x24",
               "./KSP.x86_64", "-force-glcore", *args]
        env = {**os.environ, "LIBGL_ALWAYS_SOFTWARE": "1"}

    with open(KSP / "run.out", "wb") as out:
        subprocess.run(cmd, cwd=KSP, env=env, stdout=out, stderr=subprocess.STDOUT,
                       timeout=TIMEOUT)


@pytest.fixture(scope="session")
def report(request):
    """Run the harness once per session and return its scenarios keyed by name.

    A timeout is swallowed here rather than raised: the report may still have
    been written before the game hung on the way out, and if it was not, the
    missing-report branch below gives a far more useful failure than a traceback.
    """
    if not (KSP / "KSP.x86_64").exists():
        pytest.skip(f"no test install at {KSP}; run test/integration/setup_testenv.sh")

    if not request.config.getoption("--reuse-report"):
        _build_and_deploy()
        _seed_part_database()
        RESULTS.unlink(missing_ok=True)
        (KSP / "KSP.log").unlink(missing_ok=True)
        try:
            _launch()
        except subprocess.TimeoutExpired:
            pass

    if not RESULTS.exists():
        log = KSP / "KSP.log"
        tail = "\n".join(log.read_text(errors="replace").splitlines()[-40:]) if log.exists() else ""
        pytest.fail(f"the in-game harness produced no report.\nKSP.log tail:\n{tail}")

    return {s["name"]: s for s in json.loads(RESULTS.read_text())["scenarios"]}


def _scenario_names():
    """Scenario ids, read from a previous report so collection can be static.

    pytest fixes the case list at collection time, before the fixture has run
    the game, so the ids come from the previous run's report. A scenario added
    since then shows up on the second run; one removed since is skipped by
    test_scenario rather than failing.
    """
    if RESULTS.exists():
        try:
            return [s["name"] for s in json.loads(RESULTS.read_text())["scenarios"]]
        except Exception:
            pass
    return []


def pytest_generate_tests(metafunc):
    """Turn each known scenario name into its own pytest case."""
    if "scenario_name" in metafunc.fixturenames:
        names = _scenario_names()
        metafunc.parametrize("scenario_name", names or ["<no previous report>"])
