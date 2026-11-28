"""SH-T07 Stage B: the coordinator and shelter operator journey in a real browser (Playwright, headless Chromium).

1. Replays the SH-T04 exercise through the owners (SH-A04/SH-A06): reservations, a closure, a withdrawn assignment,
   an admission. Then restarts the services on the same state with the Stage B reception accounts.
2. In the dashboard checks that the five facts stay separate, that a withdrawn assignment never reads as current,
   that each account sees and does only what its scope allows, and that a lost connection labels the last observed
   revision instead of showing instructions as current.

    python -m pip install playwright==1.49.1 && python -m playwright install chromium
    python tests/sheltering_browser.py [--no-build] [--screenshots DIR]
"""

import argparse
import json
from pathlib import Path
import sys
from tempfile import TemporaryDirectory

from playwright.sync_api import expect, sync_playwright

expect.set_options(timeout=15_000)  # CI runners are slower than a workstation

sys.path.insert(0, str(Path(__file__).resolve().parent))
import sheltering_reservations_integration as owners  # noqa: E402
from offline_demo import ARTIFACTS, build, start_services  # noqa: E402

INCIDENT = "incident-42"


def assignments(client):
    return {item["caseId"]: item for item in
            client.call("sheltering/queries/shelter-assignments?incidentId=" + INCIDENT)["assignments"]}


def journey(page, base, tokens, shots):
    def use_token(name):
        page.fill("#sheltering-token-input", tokens[name] if name else "")
        page.click("#sheltering-token button[type=submit]")

    def shot(name):
        if shots:
            page.screenshot(path=str(shots / f"{name}.png"), full_page=True)

    panel = page.locator("#sheltering-panel")
    rows = page.locator("#sheltering-assignments tbody tr")
    page.goto(base)
    expect(panel).to_be_visible(timeout=20_000)
    expect(rows).to_have_count(5, timeout=20_000)
    client = owners.Client(base)
    expected = assignments(client)
    # The SH-T04 exercise races two people for the last place, so who is withdrawn varies between runs.
    # Pick the people from the owners' state, not from fixed IDs.
    by_status = {}
    for case_id, item in sorted(expected.items()):
        by_status.setdefault(item["status"], []).append(case_id)
    withdrawn_case = by_status["AwaitingReplacement"][0]
    reserved_case = by_status["Reserved"][0]
    reserved_at = expected[reserved_case]["reservation"]["shelterId"]
    present_case = by_status["Admitted"][0]
    present_at = expected[present_case]["reservation"]["shelterId"]
    operator = {"shelter-a": "op-shelter-a", "shelter-b": "op-shelter-b"}
    other = "shelter-a" if reserved_at == "shelter-b" else "shelter-b"

    # Without an account: owner facts are shown separately and reception is refused, not guessed.
    for case_id, item in expected.items():
        row = page.locator(f"#sheltering-assignments tr[data-case-id='{case_id}']")
        expect(row).to_have_attribute("data-status", item["status"])
        expect(row.locator(".fact-route")).to_contain_text("nieopublikowana")
        expect(row.locator(".fact-checkin")).to_contain_text("brak właściciela")
        expect(row.locator(".fact-reception")).to_contain_text("brak uprawnień")
    withdrawn = page.locator(f"#sheltering-assignments tr[data-case-id='{withdrawn_case}'] .fact-reservation")
    expect(withdrawn).to_contain_text("wycofana")
    expect(withdrawn).not_to_contain_text("zarezerwowano")
    expect(page.locator("#sheltering-reception")).to_contain_text("Podaj token")
    expect(page.locator("#sheltering-groups")).to_contain_text("group-household")
    shot("1-anonymous")

    # Coordinator: reads every shelter, cannot report an admission.
    use_token("coordinator-42")
    reception = page.locator("#sheltering-reception li")
    expect(reception).to_have_count(2)
    reserved = page.locator(f"#sheltering-assignments tr[data-case-id='{reserved_case}'] .fact-reservation")
    expect(reserved).to_have_text(f"zarezerwowano w {reserved_at}")
    expect(page.locator(f"#sheltering-assignments tr[data-case-id='{present_case}'] .fact-reception")).to_contain_text(
        f"{present_at} · obecny")
    page.locator("#sheltering-reception button", has_text=f"Przyjmij {reserved_case}").click()
    expect(page.locator("#sheltering-status")).to_contain_text("RoleNotPermitted")
    shot("2-coordinator")

    # Connection lost: the last observed revision is labelled and nothing reads as a current instruction.
    page.route("**/api/sheltering/**", lambda route: route.abort())
    use_token("coordinator-42")
    expect(page.locator("#sheltering-status")).to_contain_text("Dane nieaktualne")
    expect(page.locator("#sheltering-status")).to_have_attribute("data-stale", "true")
    expect(reserved).to_have_text(f"ostatnio: zarezerwowano w {reserved_at} (niepotwierdzone)")
    expect(withdrawn).to_contain_text("wycofana")
    expect(page.locator("#sheltering-reception button")).to_have_count(0)
    shot("3-stale")
    page.unroute("**/api/sheltering/**")

    # Operator of the other shelter: sees only that shelter.
    use_token(operator[other])
    expect(page.locator("#sheltering-status")).not_to_contain_text("nieaktualne")
    expect(reception).to_have_count(1)
    expect(reception.first).to_have_attribute("data-shelter-id", other)

    # Operator of the reserved shelter: admits the reserved person (a partial arrival) and sees them counted once.
    present = client.call(f"sheltering/queries/shelter-assignments?incidentId={INCIDENT}")  # unchanged so far
    before = sum(1 for item in present["assignments"]
                 if item["status"] == "Admitted" and item["reservation"]["shelterId"] == reserved_at)
    use_token(operator[reserved_at])
    expect(reception).to_have_count(1)
    expect(reception.first).to_have_attribute("data-shelter-id", reserved_at)
    expect(reception.first).to_contain_text(f"obecni {before}")
    reception.first.locator("button", has_text=f"Przyjmij {reserved_case}").click()
    expect(page.locator("#sheltering-status")).to_contain_text("Przyjęcie zapisane")
    expect(reception.first).to_contain_text(f"obecni {before + 1}")
    row = page.locator(f"#sheltering-assignments tr[data-case-id='{reserved_case}']")
    expect(row.locator(".fact-reception")).to_contain_text(f"{reserved_at} · obecny")
    expect(row).to_have_attribute("data-status", "Admitted")
    shot("4-operator-b")


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--port", type=int, default=5880)
    parser.add_argument("--no-build", action="store_true")
    parser.add_argument("--screenshots", type=Path, help="Directory for one screenshot per step")
    parser.add_argument("--headed", action="store_true")
    args = parser.parse_args()
    if not args.no_build:
        build()
    owners.use_fixtures()
    if args.screenshots:
        args.screenshots.mkdir(parents=True, exist_ok=True)
    with TemporaryDirectory(prefix="sheltering-browser-", dir=ARTIFACTS) as directory:
        state = Path(directory)
        with start_services(args.port, state, sheltering=True) as base:
            owners.exercise(owners.Client(base))
        with start_services(args.port, state, sheltering=True, shelter_operators=True) as base:
            tokens = json.loads((state / "operator-tokens.json").read_text(encoding="utf-8"))
            with sync_playwright() as playwright:
                browser = playwright.chromium.launch(headless=not args.headed)
                page = browser.new_page(viewport={"width": 1400, "height": 1000})
                errors = []
                page.on("pageerror", lambda error: errors.append(str(error)))
                try:
                    journey(page, base, tokens, args.screenshots)
                except Exception:
                    if args.screenshots:
                        page.screenshot(path=str(args.screenshots / "failure.png"), full_page=True)
                    print("Panel status at failure:", page.locator("#sheltering-status").text_content(), flush=True)
                    raise
                finally:
                    browser.close()
                assert not errors, errors
    print("Sheltering browser journey passed: separate facts, withdrawn guidance, coordinator and shelter operator "
          "scopes, stale projection label and an operator admission.")


if __name__ == "__main__":
    main()
