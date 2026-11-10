"""Adds "shopRows" to each merchant check in Data/locations.json: the ShopLineupParam rows that merchant sells
from, read from the randomizer's diste/Base/itemslots.txt.

Auto-tracking ticks a merchant check the first time one of those rows' purchase flags turns on, which happens
whatever the seed placed in the shop, so it gives nothing away. Only row numbers are taken from itemslots.txt
(its descriptions are thefifthmatt's); the rows themselves are read from the seed's regulation at run time.

Usage: python tools/build_merchant_shops.py "<randomizer folder>"
Run again after the randomizer adds merchants; existing shopRows lines are replaced.
"""
import json
import re
import sys
from pathlib import Path

LOCATIONS = Path(__file__).resolve().parent.parent / "Data" / "locations.json"

# Merchants with a unique name match on the name alone; the rest by name and the place in their description.
UNIQUE = {"Kalé": "Kale", "Patches": "Patches", "Iji": "Iji", "Pidia": "Pidia", "Moore": "Moore"}


def slots(itemslots: str):
    """(sold-by text, shop row ids) for every shop slot."""
    for block in itemslots.split("\n- Key: ")[1:]:
        text = re.search(r"\n  Text: ([^\n]*)", block)
        if not text or not text.group(1).startswith("Sold by"):
            continue
        rows = {int(r) for r in re.findall(r" - shop (\d+)\[", block)}
        if rows:
            yield text.group(1), rows


def norm(text: str) -> str:
    return re.sub(r"[^a-z0-9 ]", "", text.lower().replace("é", "e"))


def matches(location: dict, sold_by: str) -> bool:
    name, details = location["name"], location["details"]
    said = norm(sold_by)
    if name in UNIQUE:
        return said.startswith(norm(f"Sold by {UNIQUE[name]}")) and "after giving" not in said
    if name == "Twin Maiden Husks":
        # Their own stock, not the bell bearing stock they take over from other merchants.
        return said in (norm("Sold by Twin Maiden Husks"), norm("Sold by Twin Maiden Husks with Tarnished Pack installed"))
    # "Nomadic Merchant" + "Between Saintsbridge and ..." matches "Sold by the Nomadic Merchant between Saintsbridge and ...".
    place = norm(details.split(".")[0])
    return norm(name) in said and place[:40] in said


def main():
    if len(sys.argv) != 2:
        sys.exit(__doc__)
    itemslots = (Path(sys.argv[1]) / "diste" / "Base" / "itemslots.txt").read_text(encoding="utf-8")
    shops = list(slots(itemslots))
    locations = json.loads(LOCATIONS.read_text(encoding="utf-8"))
    text = LOCATIONS.read_text(encoding="utf-8")

    missing = []
    for location in locations:
        if location["category"] != "merchant":
            continue
        rows = sorted({row for sold_by, ids in shops if matches(location, sold_by) for row in ids})
        if not rows:
            missing.append(location["id"])
            continue
        # Insert (or replace) a shopRows line after this location's lotIds line, keeping the file's layout.
        pattern = re.compile(r'("id": "' + re.escape(location["id"]) + r'",(?:(?!\n  \}).)*?"lotIds": \[[^\]]*\])(,\n    "shopRows": \[[^\]]*\])?', re.S)
        text, count = pattern.subn(lambda m: m.group(1) + ',\n    "shopRows": [' + ", ".join(map(str, rows)) + "]", text)
        if count != 1:
            sys.exit(f"Could not place shopRows for {location['id']}")
        print(f"{location['id']}: {len(rows)} rows")

    json.loads(text)  # still valid
    LOCATIONS.write_text(text, encoding="utf-8")
    if missing:
        print("No shop found for:", ", ".join(missing))


if __name__ == "__main__":
    main()
