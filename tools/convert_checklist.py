"""Convert the Elden Ring Randomizer Checklist workbook into Data/*.json, then
prove the result matches the workbook.

The workbook's logic lives entirely in conditional-format rules: a check's
fill colour says whether it is excluded, checked, available or locked. The
RULES table below is a hand translation of those rules. verify() re-evaluates
the original Excel formulas against the generated JSON on random game states,
so a translation slip fails loudly instead of shipping.

Stonesword Keys are the one deliberate addition: the workbook only mentions them
in descriptions. verify() runs with keys held, so those requirements can never
fire there and everything else must still match the workbook exactly.

Usage: python convert_checklist.py <checklist.xlsx> <Data dir> [--no-write]
Regenerating overwrites Data/*.json. Keep flagId changes in tools/flag_map.json,
which comes from the randomizer's itemslots.txt (pickups) and enemy.txt (bosses).
"""
import json
import random
import re
import sys
import unicodedata
from pathlib import Path

import openpyxl
from openpyxl.utils import column_index_from_string, get_column_letter

LOCATIONS = "LOCATION Checklist"
KEY_ITEMS = "KEY ITEMS Checklist"
SETTINGS = "Randomizer Settings"

# (label column, first row, last row, category, id prefix)
SECTIONS = [
    ("B", 4, 22, "keyItem", "key"),
    ("G", 4, 45, "majorBoss", "boss"),
    ("B", 26, 180, "otherBoss", "other"),
    ("G", 49, 91, "seedOrTear", "pickup"),
    ("G", 95, 118, "merchant", "merchant"),
    ("G", 122, 171, "blessing", "blessing"),
    ("G", 175, 179, "extraBoss", "extra"),
]

# Which checks each "excluded" (grey) rule applies to.
MINI_DUNGEON = ("B21:B22 B31:B37 B41 B46:B50 B64:B71 B81:B87 B94:B95 B106:B112 B114:B116 B118 "
                "B125:B128 B140:B142 B147:B150 B154:B156 B165:B166 B169 B175:B176 G99")
DLC = "B17:B22 B152:B180 G34:G45 G118 G122:G171"

REGION_ALIASES = {"Liurnia (Past Caria Manor)": "Liurnia (After Caria Manor)"}

# Keys needed by the checks whose workbook descriptions mention an imp seal.
STONESWORD_KEY = "Stonesword Key"
STONESWORD_CHECKS = {("B", 31): 2, ("B", 42): 1, ("B", 85): 2, ("B", 107): 2, ("B", 148): 2, ("G", 87): 1}
STONESWORD_ITEM = {
    "name": STONESWORD_KEY,
    "category": "key",
    "maxCount": 99,
    "dlc": False,
    "shortcut": False,
    "notes": "Set this to the keys you are holding now, and lower it when you spend them. Imp seals use keys up, "
             "including seals this tracker does not list. Spending your last keys locks an unchecked location behind "
             "that seal again, but you can still check it off. Not part of the original checklist's logic.",
}

SETTINGS_JSON = [
    {"id": "otherBosses", "name": "Other Bosses are important locations", "type": "bool", "default": True, "cell": "B7"},
    {"id": "seedsAndTears", "name": "Golden Seed Trees and Sacred Tear Churches", "type": "bool", "default": False, "cell": "B8"},
    {"id": "merchantShops", "name": "Merchant Shops", "type": "bool", "default": True, "cell": "B9"},
    {"id": "blessings", "name": "Shadow Realm Blessing pickups", "type": "bool", "default": False, "cell": "B10"},
    {"id": "excludeMiniDungeons", "name": "Exclude mini-dungeon locations", "type": "bool", "default": False, "cell": "B11"},
    {"id": "dlcEnabled", "name": "DLC enabled", "type": "bool", "default": True, "cell": "E5"},
    {"id": "randomizeGaolKeys", "name": "Randomize Lamenter's Gaol keys", "type": "bool", "default": True, "cell": "E6"},
    {"id": "startMode", "name": "DLC start", "type": "choice", "default": "normal",
     "options": ["normal", "dlcOnly", "dlcWithBaseGame"],
     "optionNames": ["Normal Progression", "DLC Start", "DLC Start with Base Game"]},
    {"id": "finalBossRunes", "name": "Great Runes to access the final boss", "type": "count", "default": 0, "max": 7, "cell": "K5",
     "notes": "Listed in the checklist's settings but not used by any of its logic."},
    {"id": "leyndellRunes", "name": "Great Runes to access Leyndell", "type": "count", "default": 2, "max": 7, "cell": "K6"},
    {"id": "mountaintopsRunes", "name": "Great Runes to access Mountaintops", "type": "count", "default": 0, "max": 7, "cell": "K7",
     "notes": "When 0, the Rold Medallion is required instead."},
    {"id": "deathlessRouting", "name": "Deathless routing", "type": "bool", "default": False, "cell": "K9",
     "notes": "When on, Volcano Manor stays locked until the Drawing-Room Key is obtained."},
    {"id": "kindlingShards", "name": "Required Messmer's Kindling Shards", "type": "count", "default": 0, "max": 20, "cell": "K13",
     "notes": "When 0, Messmer's Kindling is required instead."},
]

# KEY ITEMS rows the sheet keeps visible under "only show alternate route and shortcut items".
SHORTCUT_ROWS = {39, 47, 53, 62, 67, 68, 69}
ITEM_MAX_COUNT = {"Great Runes": 7, "Messmer's Kindling Shards": 20, "Deathroot": 9, "Seedbed Curse": 6, "Dragon Heart": 29}


def slug(text):
    text = unicodedata.normalize("NFKD", text).encode("ascii", "ignore").decode()
    return re.sub(r"[^a-z0-9]+", "_", text.replace("'", "").lower()).strip("_")


def clean(text):
    return re.sub(r"\s*\n\s*", " ", str(text)).strip()


def cells_in(spec):
    """Expand 'B21:B22 G99' into (column, row) pairs."""
    for part in spec.split():
        first, _, last = part.partition(":")
        col, start = re.fullmatch(r"([A-Z]+)(\d+)", first).groups()
        end = re.fullmatch(r"[A-Z]+(\d+)", last or first).group(1)
        for row in range(int(start), int(end) + 1):
            yield col, row


def read_locations(ws):
    locations, by_cell, used = [], {}, set()
    mini, dlc = set(cells_in(MINI_DUNGEON)), set(cells_in(DLC))
    for col, first, last, category, prefix in SECTIONS:
        offset = column_index_from_string(col)
        for row in range(first, last + 1):
            name = ws.cell(row, offset).value
            if name is None:
                continue
            region = clean(ws.cell(row, offset + 1).value)
            region = REGION_ALIASES.get(region, region)
            base = f"{prefix}_{slug(name)}_{slug(region)}"
            loc_id, n = base, 1
            while loc_id in used:
                n += 1
                loc_id = f"{base}_{n}"
            used.add(loc_id)
            loc = {
                "id": loc_id,
                "name": clean(name),
                "region": slug(region),
                "regionName": region,
                "details": clean(ws.cell(row, offset + 2).value),
                "category": category,
                "dlc": (col, row) in dlc,
                "miniDungeon": (col, row) in mini,
                "flagId": 0,
                "requires": [],
                "cell": f"{col}{row}",
            }
            locations.append(loc)
            by_cell[(col, row)] = loc
    return locations, by_cell


def read_items(ws):
    items, by_row = [], {}
    for row in [*range(5, 27), *range(32, 72)]:
        name = ws.cell(row, 2).value
        if name is None:
            continue
        by_row[row] = name
        existing = next((i for i in items if i["name"] == name), None)
        if existing:  # Shabriri Grape has one row per copy.
            existing["maxCount"] += 1
            continue
        item = {
            "name": name,
            "category": "key" if row <= 26 else "optional",
            "maxCount": ITEM_MAX_COUNT.get(name, 1),
            "dlc": 20 <= row <= 26,
            "shortcut": row in SHORTCUT_ROWS,
            "notes": clean(ws.cell(row, 4).value),
        }
        if row in (25, 26):
            item["miniDungeon"] = True
            item["onlyIfSetting"] = "randomizeGaolKeys"
        items.append(item)
    rusty_key = next(i for i, item in enumerate(items) if item["name"] == "Rusty Key")
    items.insert(rusty_key + 1, STONESWORD_ITEM)
    return items, by_row


def build_rules(by_cell):
    def c(ref):
        """check:<id> for a checkbox cell: column E belongs to B's row, J to G's."""
        col, row = re.fullmatch(r"([EJ])(\d+)", ref).groups()
        return "check:" + by_cell[("B" if col == "E" else "G", int(row))]["id"]

    left, right = "Dectus Medallion (Left)", "Dectus Medallion (Right)"
    makar, virgins = c("J12"), c("E129")
    deeproot = [[c("J16")], [c("J20")]]

    routes = [
        {"id": "base_game", "name": "Base game unlocked",
         "requires": [["setting:startMode=normal"], ["setting:startMode=dlcWithBaseGame"]]},
        {"id": "altus_access", "name": "Altus Plateau access",
         "requires": [[makar], [left, right], [virgins]]},
        {"id": "leyndell_access", "name": "Leyndell access",
         "requires": [[c("E120")], [c("J175"), "Great Runes x$leyndellRunes"]]},
        {"id": "mountaintops_access", "name": "Mountaintops access",
         "requires": [[c("J15"), "Rold Medallion"],
                      ["!setting:mountaintopsRunes=0", "Great Runes x$mountaintopsRunes"]]},
        {"id": "dlc_access", "name": "Shadow of the Erdtree access",
         "requires": [[c("J31")], ["setting:startMode=dlcOnly"], ["setting:startMode=dlcWithBaseGame"]]},
    ]

    def via(*tokens):
        return [list(tokens)]

    kindling = "Messmer's Kindling"
    shards = "Messmer's Kindling Shards"
    rules = [
        ("B4 B6:B8 B11:B12 B26:B62 B64:B78 B80:B82 B84:B95 G4 G6:G7 G10:G12 G49:G51 G53:G62 G65:G69 "
         "G95:G105 G107:G111 G179", via("route:base_game")),
        ("B79", [["route:base_game", c("J17")], ["route:base_game", "!Great Runes x1"]]),
        ("G8 G63 G106", via("Academy Glintstone Key")),
        ("B9 G9 G64", via(c("J8"))),
        ("B10", via(c("J9"), "Discarded Palace Key")),
        ("B5 G5 G52", via(c("J4"))),
        ("G17", via("Great Runes x1", "route:altus_access")),
        ("B15 B96:B116 B121:B128 G13 G70:G77 G79:G80 G112:G113 G115 G175", via("route:altus_access")),
        ("B63 G114", via(c("J7"))),
        ("B129 G21", [["!setting:deathlessRouting", "Academy Glintstone Key"],
                      ["Drawing-Room Key", makar], ["Drawing-Room Key", left, right]]),
        ("G22", [[c("J21")], [c("J176"), c("J177"), c("J178")]]),
        ("B83", via(c("J17"))),
        ("G176", via("Letter from Volcano Manor (Istvan)")),
        ("G177", via("route:altus_access", "Letter from Volcano Manor (Rileigh)")),
        ("B117:B118 G14 G16 G78", via("route:leyndell_access")),
        ("G15", via(c("J14"))),
        ("B13", via(c("J15"))),
        ("G18", via(c("J17"))),
        ("G19:G20", via(c("J18"))),
        ("B119", deeproot),
        ("B120", [["Weathered Dagger", *d] for d in deeproot]),
        ("B130 G23 G81", [[c("J7"), c("J17"), "Fingerslayer Blade"], *deeproot]),
        ("B14", via("Carian Inverted Statue")),
        ("B134:B142 G24:G25 G82:G86 G116", via("route:mountaintops_access")),
        ("B16", via(c("J24"))),
        ("G31 G91 G117", [["Pureblood Knight's Medal"], ["route:mountaintops_access"]]),
        ("B133", via("Imbued Sword Key")),
        ("G178", via("Red Letter", "route:mountaintops_access")),
        ("B131:B132", via("Dark Moon Ring", c("J23"))),
        ("B143:B150 G29 G89:G90", via("Haligtree Secret Medallion (Left)", "Haligtree Secret Medallion (Right)", c("J15"))),
        ("G30", via(c("J29"))),
        ("G26 G87", via(c("J25"))),
        ("G27:G28 G88", via(c("J26"))),
        ("B151", via(c("J28"))),
        ("G32", via(c("E151"))),
        ("G33", via(c("J32"))),
        ("G42 G162:G164", via(c("E165"))),
        ("G161", via(c("J41"))),
        ("B22 B171:B174 G43 G165", via(c("E156"))),
        ("G133", via(c("J34"))),
        ("B175", [[c("E156"), "!setting:randomizeGaolKeys"],
                  [c("E156"), "Gaol Upper Level Key", "Gaol Lower Level Key"]]),
        ("B180", via(c("J44"))),
        ("G45 G166:G171", [[c("J40"), "setting:kindlingShards=0", kindling],
                           [c("J40"), "!setting:kindlingShards=0", f"{shards} x$kindlingShards"]]),
        ("B21", via("Gaol Lower Level Key")),
        ("B17:B20 B152:B170 B176 G34:G41 G118 G122:G132 G134:G160", via("route:dlc_access")),
        ("B177:B179", via("route:dlc_access", "O Mother")),
        ("G44", via("route:dlc_access", "O Mother", "Hole-Laden Necklace")),
    ]
    return routes, rules


def apply_rules(by_cell, rules):
    seen = set()
    for spec, requires in rules:
        for cell in cells_in(spec):
            if cell in seen:
                raise SystemExit(f"{cell} is covered by two rules")
            seen.add(cell)
            by_cell[cell]["requires"] = requires
    missing = sorted(by_cell.keys() - seen)
    if missing:
        raise SystemExit(f"No rule for {missing}")


def apply_stonesword_keys(by_cell):
    for cell, count in STONESWORD_CHECKS.items():
        token = STONESWORD_KEY if count == 1 else f"{STONESWORD_KEY} x{count}"
        by_cell[cell]["requires"] = [[*option, token] for option in by_cell[cell]["requires"]]


def build_regions(locations, routes):
    regions, seen = [], set()
    for loc in locations:
        if loc["region"] not in seen:
            seen.add(loc["region"])
            regions.append({"id": loc["region"], "name": loc["regionName"], "requires": []})
    return regions + routes


# --- JSON output: one property per line, so requirement lists stay readable ---

def write_json(path, objects):
    def emit(obj):
        lines = ",\n".join(f"    {json.dumps(k)}: {json.dumps(v, ensure_ascii=False)}" for k, v in obj.items())
        return "  {\n" + lines + "\n  }"
    path.write_text("[\n" + ",\n".join(emit(o) for o in objects) + "\n]\n", encoding="utf-8")


# --- Excel conditional-format formula evaluator ---

TOKEN = re.compile(r'''\s*(?:(?P<ind>INDIRECT\("(?P<target>[^"]+)"\))|(?P<ref>\$?[A-Z]{1,2}\$?\d+)
    |(?P<num>\d+(?:\.\d+)?)|(?P<word>[A-Za-z]+)|(?P<op><=|>=|<>|[=<>(),]))''', re.X)


def parse_formula(text):
    tokens, pos = [], 0
    text = text.strip()
    while pos < len(text):
        m = TOKEN.match(text, pos)
        if not m:
            raise ValueError(f"Cannot tokenize {text!r} at {pos}")
        tokens.append(m)
        pos = m.end()
    index = 0

    def peek():
        return tokens[index] if index < len(tokens) else None

    def comparison():
        nonlocal index
        node = primary()
        while (t := peek()) is not None and t.group("op") in ("=", "<", ">", "<=", ">=", "<>"):
            index += 1
            node = ("cmp", t.group("op"), node, primary())
        return node

    def primary():
        nonlocal index
        t = peek()
        index += 1
        if t.group("ind"):
            sheet, _, ref = t.group("target").partition("!")
            return ("abs", sheet.strip("'"), ref.replace("$", ""))
        if t.group("ref"):
            m = re.fullmatch(r"(\$?)([A-Z]+)(\$?)(\d+)", t.group("ref"))
            return ("ref", m.group(1) == "$", m.group(2), m.group(3) == "$", int(m.group(4)))
        if t.group("num"):
            return ("lit", float(t.group("num")))
        word = t.group("word").upper()
        if word in ("TRUE", "FALSE") and (peek() is None or peek().group("op") != "("):
            return ("lit", word == "TRUE")
        index += 1  # opening bracket
        args = [comparison()]
        while peek().group("op") == ",":
            index += 1
            args.append(comparison())
        index += 1  # closing bracket
        return (word, args)

    return comparison()


def excel_equal(a, b):
    if a is None:
        a = False if isinstance(b, bool) else (0 if b is not None else None)
    if b is None:
        b = False if isinstance(a, bool) else (0 if a is not None else None)
    if isinstance(a, bool) != isinstance(b, bool):
        return False
    return a == b


def evaluate(node, state, sheet, col_off, row_off):
    kind = node[0]
    if kind == "lit":
        return node[1]
    if kind == "abs":
        return state.get((node[1], node[2]))
    if kind == "ref":
        _, abs_col, col, abs_row, row = node
        c = column_index_from_string(col) + (0 if abs_col else col_off)
        r = row + (0 if abs_row else row_off)
        return state.get((sheet, f"{get_column_letter(c)}{r}"))
    if kind == "cmp":
        a = evaluate(node[2], state, sheet, col_off, row_off)
        b = evaluate(node[3], state, sheet, col_off, row_off)
        if node[1] == "=":
            return excel_equal(a, b)
        if node[1] == "<>":
            return not excel_equal(a, b)
        a, b = a or 0, b or 0
        return {"<": a < b, ">": a > b, "<=": a <= b, ">=": a >= b}[node[1]]
    values = [bool(evaluate(n, state, sheet, col_off, row_off)) for n in node[1]]
    return {"AND": all, "OR": any, "NOT": lambda v: not v[0]}[kind](values)


STATUS_BY_FILL = {"FF666666": "excluded", "FFB6D7A8": "checked", "FF6AA84F": "available", "FFCC0000": "locked"}


def load_cf_rules(ws):
    rules = []
    for rng in ws.conditional_formatting:
        ranges = list(rng.sqref.ranges)
        base_col = min(r.min_col for r in ranges)
        base_row = min(r.min_row for r in ranges)
        for rule in rng.rules:
            status = STATUS_BY_FILL[rule.dxf.fill.bgColor.rgb]
            rules.append((rule.priority, rng.sqref, base_col, base_row, parse_formula(rule.formula[0]), status))
    return sorted(rules, key=lambda r: r[0])


def sheet_status(cf_rules, cell, state):
    col, row = re.fullmatch(r"([A-Z]+)(\d+)", cell).groups()
    col_index = column_index_from_string(col)
    for _, sqref, base_col, base_row, formula, status in cf_rules:
        if cell in sqref and evaluate(formula, state, LOCATIONS, col_index - base_col, int(row) - base_row):
            return status
    return None


# --- Evaluator for the generated JSON (the semantics the app must implement) ---

def token_true(token, game, regions):
    if token.startswith("!"):
        return not token_true(token[1:], game, regions)
    if token.startswith("check:"):
        return game["checks"][token[6:]]
    if token.startswith("route:"):
        return options_true(regions[token[6:]]["requires"], game, regions)
    if token.startswith("setting:"):
        key, _, value = token[8:].partition("=")
        return str(game["settings"][key]) == value if value else game["settings"][key] is True
    m = re.fullmatch(r"(.+) x(\$?)(\w+)", token)
    if m:
        needed = game["settings"][m.group(3)] if m.group(2) else int(m.group(3))
        return game["items"].get(m.group(1), 0) >= needed
    return game["items"].get(token, 0) >= 1


def options_true(options, game, regions):
    return not options or any(all(token_true(t, game, regions) for t in option) for option in options)


def json_status(loc, game, regions):
    s = game["settings"]
    if ((loc["miniDungeon"] and s["excludeMiniDungeons"])
            or (loc["dlc"] and not s["dlcEnabled"])
            or (not loc["dlc"] and s["startMode"] == "dlcOnly")
            or {"otherBoss": not s["otherBosses"], "seedOrTear": not s["seedsAndTears"],
                "merchant": not s["merchantShops"], "blessing": not s["blessings"]}.get(loc["category"], False)):
        return "excluded"
    if game["checks"][loc["id"]]:
        return "checked"
    reachable = options_true(regions[loc["region"]]["requires"], game, regions)
    return "available" if reachable and options_true(loc["requires"], game, regions) else "locked"


def random_state(locations, item_rows, rng):
    """Build matching game state for both evaluators. A per-state probability
    gives everything from fresh saves to near-complete ones."""
    p = rng.choice([0.05, 0.2, 0.5, 0.8, 0.95])
    flip = lambda: rng.random() < p
    settings = {
        "otherBosses": rng.random() < 0.9, "seedsAndTears": rng.random() < 0.9, "merchantShops": rng.random() < 0.9,
        "blessings": rng.random() < 0.9, "excludeMiniDungeons": rng.random() < 0.1, "dlcEnabled": rng.random() < 0.9,
        "randomizeGaolKeys": rng.random() < 0.5, "deathlessRouting": rng.random() < 0.5,
        "startMode": rng.choices(["normal", "dlcOnly", "dlcWithBaseGame"], [8, 1, 1])[0],
        "finalBossRunes": rng.randint(0, 7), "leyndellRunes": rng.randint(0, 7),
        "mountaintopsRunes": rng.randint(0, 7), "kindlingShards": rng.randint(0, 20),
    }
    excel = {}
    for s in SETTINGS_JSON:
        if "cell" in s:
            excel[(SETTINGS, s["cell"])] = float(settings[s["id"]]) if s["type"] == "count" else settings[s["id"]]
    for cell, mode in (("H5", "normal"), ("H6", "dlcOnly"), ("H7", "dlcWithBaseGame")):
        excel[(SETTINGS, cell)] = settings["startMode"] == mode

    items = {}
    for row, name in item_rows.items():
        if name in ITEM_MAX_COUNT:
            count = rng.randint(0, ITEM_MAX_COUNT[name]) if flip() else 0
            items[name] = count
            excel[(KEY_ITEMS, f"C{row}")] = float(count)
        elif name not in items:
            items[name] = int(flip())
            excel[(KEY_ITEMS, f"C{row}")] = bool(items[name])
    items[STONESWORD_KEY] = max(STONESWORD_CHECKS.values())  # Not in the workbook, so never the deciding factor here.

    checks = {}
    for loc in locations:
        checks[loc["id"]] = flip()
        col = "E" if loc["cell"][0] == "B" else "J"
        excel[(LOCATIONS, col + loc["cell"][1:])] = checks[loc["id"]]
    return {"settings": settings, "items": items, "checks": checks}, excel


def verify(ws, locations, regions, item_rows, states=4000):
    cf_rules = load_cf_rules(ws)
    by_region = {r["id"]: r for r in regions}
    rng = random.Random(1)
    seen = {loc["id"]: set() for loc in locations}
    mismatches = {}
    for _ in range(states):
        game, excel = random_state(locations, item_rows, rng)
        for loc in locations:
            actual = json_status(loc, game, by_region)
            expected = sheet_status(cf_rules, loc["cell"], excel)
            seen[loc["id"]].add(actual)
            if actual != expected:
                mismatches.setdefault(loc["id"], []).append((expected, actual))
    for loc_id, pairs in mismatches.items():
        print(f"MISMATCH {loc_id}: {len(pairs)}x, first sheet={pairs[0][0]} json={pairs[0][1]}")
    uncovered = [i for i, s in seen.items() if not {"available", "locked", "checked", "excluded"} <= s]
    print(f"Compared {len(locations)} checks across {states} random states: "
          f"{len(mismatches)} checks mismatched, {len(uncovered)} never reached all four statuses.")
    for loc_id in uncovered:
        print(f"  partial coverage: {loc_id} saw {sorted(seen[loc_id])}")
    return not mismatches


def write_vectors(path, ws, locations, item_rows, states=200):
    """Save random states with the statuses the workbook's own rules give them,
    so the C# tests can check the app's evaluator without needing the workbook."""
    cf_rules = load_cf_rules(ws)
    rng = random.Random(2)
    letters = {"excluded": "X", "checked": "C", "available": "A", "locked": "L"}
    text = lambda v: str(v).lower() if isinstance(v, bool) else str(v)
    vectors = []
    for _ in range(states):
        game, excel = random_state(locations, item_rows, rng)
        vectors.append({
            "settings": {k: text(v) for k, v in game["settings"].items()},
            "items": {k: v for k, v in game["items"].items() if v},
            "checked": "".join("1" if game["checks"][loc["id"]] else "0" for loc in locations),
            "expected": "".join(letters[sheet_status(cf_rules, loc["cell"], excel)] for loc in locations),
        })
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps({"locations": [loc["id"] for loc in locations], "states": vectors}, ensure_ascii=False),
                    encoding="utf-8")
    print(f"Wrote {states} test vectors to {path}")


def main():
    xlsx, data_dir = sys.argv[1], Path(sys.argv[2])
    wb = openpyxl.load_workbook(xlsx)
    ws = wb[LOCATIONS]
    locations, by_cell = read_locations(ws)
    items, item_rows = read_items(wb[KEY_ITEMS])
    routes, rules = build_rules(by_cell)
    apply_rules(by_cell, rules)
    apply_stonesword_keys(by_cell)
    regions = build_regions(locations, routes)

    if not verify(ws, locations, regions, item_rows):
        raise SystemExit("Generated logic does not match the workbook; nothing written.")
    if "--no-write" in sys.argv:
        return

    # Event flags by location id; merchants and NPC invaders have none and stay manual.
    flag_map = json.loads((Path(__file__).parent / "flag_map.json").read_text(encoding="utf-8"))
    lot_map = json.loads((Path(__file__).parent / "lot_map.json").read_text(encoding="utf-8"))
    for loc in locations:
        loc["flagId"] = flag_map.get(loc["id"], 0)
        loc["lotIds"] = lot_map.get(loc["id"], [])

    public_locations = [{k: v for k, v in loc.items() if k not in ("regionName", "cell")} for loc in locations]
    public_settings = [{k: v for k, v in s.items() if k != "cell"} for s in SETTINGS_JSON]
    data_dir.mkdir(parents=True, exist_ok=True)
    write_json(data_dir / "locations.json", public_locations)
    write_json(data_dir / "regions.json", regions)
    write_json(data_dir / "items.json", items)
    write_json(data_dir / "settings.json", public_settings)
    print(f"Wrote {len(locations)} locations, {len(regions)} regions/routes, {len(items)} items, "
          f"{len(public_settings)} settings to {data_dir}")
    write_vectors(data_dir.parent / "Tests" / "Vectors" / "sheet_vectors.json", ws, locations, item_rows)


if __name__ == "__main__":
    main()
