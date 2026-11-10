"""Drive ErRandoCompanion.Native.dll through its C API from outside .NET, as the mod will.

Usage: python native_smoke.py <ErRandoCompanion.Native.dll> <Data dir> <seeds dir> [randomizer dir]
Use a throwaway copy of the Seeds folder: this opens, edits and creates runs in it.
Prints counts and statuses only, never anything about where items are.
"""
import ctypes
import json
import sys

dll_path, data_dir, seeds_dir = sys.argv[1:4]
randomizer_dir = sys.argv[4] if len(sys.argv) > 4 else None

dll = ctypes.CDLL(dll_path)
for name in ("er_init", "er_call"):
    getattr(dll, name).restype = ctypes.c_void_p
    getattr(dll, name).argtypes = [ctypes.c_char_p]
dll.er_free.argtypes = [ctypes.c_void_p]
dll.er_free.restype = None


def reply(pointer):
    text = ctypes.string_at(pointer).decode("utf-8")
    dll.er_free(pointer)
    return json.loads(text)


def call(op, **args):
    return reply(dll.er_call(json.dumps({"op": op, **args}).encode("utf-8")))


def ok(answer):
    assert answer["ok"], answer
    return answer["result"]


failures = []


def expect(label, condition):
    print(("PASS " if condition else "FAIL ") + label)
    if not condition:
        failures.append(label)


expect("calls before init are refused", not call("summary")["ok"])

summary = ok(reply(dll.er_init(json.dumps({
    "dataDirectory": data_dir, "seedsDirectory": seeds_dir, "randomizerDirectory": randomizer_dir, "readOwnProcess": False,
}).encode("utf-8"))))
print("opened at start:", summary["seedName"], f"{summary['checked']}/{summary['total']} checked, {summary['available']} available")

seeds = ok(call("listSeeds"))
print("runs:", seeds)

summary = ok(call("openSeed", name="My Seed"))
expect(f"My Seed opens with the counts the app shows (0/245, 94 available): got {summary['checked']}/{summary['total']}, {summary['available']}",
       (summary["checked"], summary["total"], summary["available"]) == (0, 245, 94))

checks = ok(call("checks", filter="all"))
available = ok(call("checks", filter="available"))
expect(f"check lists agree with the summary ({len(checks)} listed, {len(available)} available)",
       len(checks) == summary["total"] and len(available) == summary["available"])
expect("every check has an id, name, region and status",
       all(c["id"] and c["name"] and c["region"] and c["status"] in ("available", "locked", "checked") for c in checks))

first = available[0]["id"]
summary = ok(call("setTarget", id=first, number=3))
expect("a target can be set", [t["number"] for t in summary["targets"]] == [3] and summary["targets"][0]["active"])
summary = ok(call("setChecked", id=first, checked=True))
expect("checking a target clears it and counts the check", summary["checked"] == 1 and summary["targets"] == [])
summary = ok(call("setChecked", id=first, checked=False))
expect("unchecking restores the count", summary["checked"] == 0)

expect("items are listed", len(ok(call("items"))) > 20)
settings = ok(call("settings"))
expect("settings are listed with values", len(settings) > 5 and all("value" in s for s in settings))

poll = ok(call("poll"))
expect(f"polling reports the game's state ({poll['status']}: {poll['message']})",
       poll["status"] in ("notRunning", "antiCheatActive", "loading", "connected", "accessDenied"))
if poll["status"] == "connected":
    print("  player:", poll["player"])

for label, answer in [
    ("an unknown op is an error", call("fly")),
    ("a missing argument is an error", call("setChecked", id=first)),
    ("an unknown check is an error", call("setTarget", id="nowhere", number=1)),
    ("a target number out of range is an error", call("setTarget", id=first, number=9)),
    ("a bad setting value is an error", call("setSetting", id="dlcEnabled", value="maybe")),
    ("broken JSON is an error", reply(dll.er_call(b"{ not json"))),
]:
    expect(f"{label}: {answer.get('error', '')[:70]}", not answer["ok"])

if randomizer_dir:
    summary = ok(call("openNewestSeed"))
    expect(f"the newest seed opens as a run with its log loaded ({summary['seedName']})", summary["spoiler"]["loaded"])
    print("  newest seed:", f"{summary['checked']}/{summary['total']} checked, {summary['available']} available,",
          "warning:", "yes" if summary["warning"] else "none", "| problem:", summary["problem"])
    print("  runs now:", ok(call("listSeeds")))
    again = ok(call("openNewestSeed"))
    expect("asking again stays on the same run", again["seedName"] == summary["seedName"])

dll.er_shutdown()
print()
print("all passed" if not failures else f"{len(failures)} failed: {failures}")
sys.exit(1 if failures else 0)
