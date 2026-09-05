"""One pytest case per in-game scenario.

The scenario list itself lives in test/DimensionSync.GameTests/Scenarios.cs;
these cases only report what the harness found.
"""
import pytest


def test_harness_ran(report):
    """Fail loudly when the game ran but produced an empty report.

    Without this, a harness that never reached the editor would show up as zero
    collected cases, which reads like success.
    """
    assert report, "the in-game harness reported no scenarios at all"


def test_no_scenario_failed(report):
    """Fail if anything in the report failed, whether or not it has its own case.

    The per-scenario cases are parametrised from the PREVIOUS run's report,
    because pytest fixes its case list before the game has run. A scenario added
    since then therefore has no case of its own, and on its first run a failing
    one is reported as success - the run is green because nobody asked about it.

    This asks about the report as a whole, so a new scenario counts from the
    first run rather than the second.
    """
    failed = sorted(name for name, s in report.items() if s["status"] == "failed")
    assert not failed, "scenarios failed in game: " + ", ".join(failed)


def test_scenario(report, scenario_name):
    """Report one in-game scenario's outcome as a pytest case."""
    scenario = report.get(scenario_name)
    if scenario is None:
        pytest.skip(f"{scenario_name} was not in this run's report")

    detail = "\n".join(scenario["messages"])
    if scenario["status"] == "skipped":
        pytest.skip(detail or "skipped in game")
    assert scenario["status"] == "passed", f"{scenario_name}:\n{detail}"
