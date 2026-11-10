"""Generate the Map tab's data from the player's own install: Data/map_data.json holds a position
for each location, and Data/map_*.jpg are the game's world map art.

Nothing here reads seed-specific placements, so the output spoils nothing. The art belongs to
FromSoftware, so keep the output to your own copy rather than publishing it.

Usage: python build_map_data.py <ELDEN RING/Game> <Data dir> [--cache file.pickle]
Needs numpy, pillow and cryptography.
"""
import ctypes
import json
import pickle
import re
import struct
import sys
from collections import Counter, deque
from dataclasses import dataclass
from pathlib import Path

import numpy as np
from cryptography.hazmat.primitives.ciphers import Cipher, algorithms, modes
from PIL import Image

import extract_map_art

Image.MAX_IMAGE_PIXELS = None

TILE = 256.0
REGULATION_KEY = bytes.fromhex("99BFFC366A6BC8C6F5827D093602D676C42892A01C207FB024D3AF4E493FEF99")
MAPS = {60: ("base", "The Lands Between"), 61: ("dlc", "Land of Shadow")}
UNDERGROUND_AREA = 12
PIN_SPREAD_METERS = 22.0

# Every sheet is the game's own art, at one pixel per metre: art pixel = (x - 7040, 16512 - z).
# Measured against terrain: it is a half tile off the grid, since a tile's position is its centre.
ART_OFFSET_X = -7040.0
ART_OFFSET_Z = 16512.0
SURFACE_AREA = 60
ART_UNDERGROUND_SHEET = 1
UNDERGROUND_MAP = "underground"
UNDERGROUND_IMAGE = "map_underground.jpg"
ROUNDTABLE_MAP = "m11_10_00_00"


@dataclass(frozen=True)
class ArtSheet:
    """Which part of a stitched sheet to keep: left, top, right, bottom in full size pixels."""
    sheet: int
    crop: tuple
    downscale: int

    @property
    def width(self):
        return (self.crop[2] - self.crop[0]) // self.downscale

    @property
    def height(self):
        return (self.crop[3] - self.crop[1]) // self.downscale

    def uv(self, x, z):
        left, top, right, bottom = self.crop
        return (round((x + ART_OFFSET_X - left) / (right - left), 5),
                round((ART_OFFSET_Z - z - top) / (bottom - top), 5))

    def frame(self, area):
        """The sheet's place in the world, so u = (x - worldLeft) / worldWidth, v = (worldTop - z) / worldHeight."""
        left, top, right, bottom = self.crop
        return {"area": area, "worldLeft": left - ART_OFFSET_X, "worldTop": ART_OFFSET_Z - top,
                "worldWidth": right - left, "worldHeight": bottom - top}


def point(x, y, z):
    return {"x": round(float(x), 2), "y": None if y is None else round(float(y), 2), "z": round(float(z), 2)}


# The DLC sheet is cropped to the land, leaving out its wide border and the tree above it.
ART_SHEETS = {
    SURFACE_AREA: ArtSheet(0, (0, 0, 9700, 9550), 2),
    61: ArtSheet(10, (3650, 3450, 7750, 7750), 1),
}

# Moves region names apart where they would overlap, as fractions of the map.
LABEL_NUDGES = {
    "Consecrated Snowfield": (-0.02, -0.035),
    "Mountaintops of the Giants": (0.03, 0.03),
    "Capital Outskirts": (0.045, 0.012),
    "Altus Plateau": (-0.01, -0.012),
    "Nokron Eternal City": (0.0, -0.045),
    "Siofra River": (-0.075, 0.0),
    "Mohgwyn": (0.05, 0.03),
}

# Regions big enough to name on the map.
LABELLED_REGIONS = {
    "Limgrave", "Stormhill", "Weeping Peninsula", "Liurnia of the Lakes", "Moonlight Altar", "Caelid",
    "Greyoll's Dragonbarrow", "Altus Plateau", "Mt. Gelmir", "Capital Outskirts", "Leyndell",
    "Forbidden Lands", "Mountaintops of the Giants", "Flame Peak", "Consecrated Snowfield",
    "Gravesite Plain", "Scadu Altus", "Scaduview", "Rauh Base", "Ancient Ruins of Rauh", "Cerulean Coast",
    "Charo's Hidden Grave", "Abyssal Woods", "Foot of the Jagged Peak", "Jagged Peak", "Shadow Keep",
}

# MapName.txt splits some regions into smaller names and has one misspelling.
REGION_ALIASES = {
    "Liurnia of the Lake": "Liurnia of the Lakes",
    "Bellum Highway": "Liurnia of the Lakes",
    "Southwest Mountaintops": "Mountaintops of the Giants",
    "Northeast Mountaintops": "Mountaintops of the Giants",
    "Southeast Mountaintops": "Mountaintops of the Giants",
    "Southeast Caelid": "Caelid",
}

# NPCs that move, and checks with no map part of their own: (kind, value).
HAND_PLACED = {
    "key_rold_medallion_leyndell": ("location", "boss_morgott_the_omen_king_leyndell"),
    "merchant_patches_limgrave": ("tile", "Murkwater Cave Entrance"),
    "merchant_iji_liurnia": ("tile", "South of Caria Manor"),
    "merchant_moore_gravesite_plain": ("tile", "Scorched Ruins"),

    # The game places this dungeon's interior out at sea, so these go at its entrance in the Fissure.
    "boss_putrescent_knight_cerulean_coast": ("world", (61, 11945.0, 9067.0)),
    "blessing_scadutree_fragment_cerulean_coast_3": ("world", (61, 11945.0, 9067.0)),
}

# Roundtable Hold is not a place on the world map, so these have no pin.
OFF_MAP = {"merchant_twin_maiden_husks_roundtable_hold"}

# Where the tables below say nothing: these NPC merchants and invaders are found by name instead.
NAMED_PARTS_TEXT = {
    "extra_old_knight_istvan_limgrave": "Dropped by Old Knight Istvan",
    "extra_rileigh_the_idle_altus_plateau": "Dropped by Rileigh the Idle",
    "extra_june_hoslow_knight_of_blood_mountaintops_of_the_giants": "Dropped by Juno Hoslow",
}


class Oodle:
    def __init__(self, path):
        self.lib = ctypes.WinDLL(str(path))

    def dcx(self, data):
        fmt = data[0x28:0x2C]
        size, comp = struct.unpack(">II", data[0x1C:0x24])
        start = data.index(b"DCA\0") + 8
        if fmt != b"KRAK":
            raise ValueError(f"Unsupported DCX format {fmt}")
        out = ctypes.create_string_buffer(size)
        n = self.lib.OodleLZ_Decompress(data[start:start + comp], ctypes.c_longlong(comp), out, ctypes.c_longlong(size),
                                        0, 0, 0, None, None, None, None, None, None, 3)
        if n != size:
            raise ValueError("Oodle decompression failed")
        return out.raw


def read_utf16(data, off):
    end = off
    while data[end:end + 2] != b"\0\0":
        end += 2
    return data[off:end].decode("utf-16-le", "replace")


def read_msb_parts(data):
    """Returns [(name, type, x, y, z)] from a decompressed MSB."""
    pos = 0x10
    while pos:
        count = struct.unpack_from("<i", data, pos + 4)[0]
        name = read_utf16(data, struct.unpack_from("<q", data, pos + 8)[0])
        offsets = struct.unpack_from(f"<{count - 1}q", data, pos + 16)
        pos = struct.unpack_from("<q", data, pos + 16 + 8 * (count - 1))[0]
        if name == "PARTS_PARAM_ST":
            parts = []
            for off in offsets:
                name_off, _, ptype = struct.unpack_from("<qii", data, off)
                x, y, z = struct.unpack_from("<3f", data, off + 0x20)
                parts.append((read_utf16(data, off + name_off), ptype, x, y, z))
            return parts
    return []


def load_all_parts(vanilla, oodle, cache):
    if cache and cache.exists():
        return pickle.loads(cache.read_bytes())
    parts = {}
    for path in sorted(vanilla.glob("*.msb.dcx")):
        parts[path.name[:12]] = read_msb_parts(oodle.dcx(path.read_bytes()))
    if cache:
        cache.write_bytes(pickle.dumps(parts))
    return parts


def read_conversions(regulation_path, oodle):
    """Links between map tiles from WorldMapLegacyConvParam: (area,x,z) -> {(area,x,z): Link}.

    Each row pairs one point in a tile with the same point in another, usually where a dungeon
    meets the open world, so a link holds the offset between the two and every such point.
    """
    raw = regulation_path.read_bytes()
    dec = Cipher(algorithms.AES(REGULATION_KEY), modes.CBC(raw[:16])).decryptor()
    binder = oodle.dcx(dec.update(raw[16:]) + dec.finalize())

    count = struct.unpack_from("<I", binder, 0x0C)[0]
    header = struct.unpack_from("<Q", binder, 0x20)[0]
    param = None
    for i in range(count):
        pos = 0x40 + i * header
        name = read_utf16(binder, struct.unpack_from("<I", binder, pos + 32)[0])
        if name.endswith("WorldMapLegacyConvParam.param"):
            size = struct.unpack_from("<q", binder, pos + 8)[0] or struct.unpack_from("<q", binder, pos + 16)[0]
            off = struct.unpack_from("<I", binder, pos + 24)[0]
            param = binder[off:off + size]
    rows = struct.unpack_from("<H", param, 0x0A)[0]
    edges = {}

    def link(a, b, point_a, point_b):
        found = edges.setdefault(a, {}).get(b)
        if found is None:
            edges[a][b] = Link(tuple(q - p for p, q in zip(point_a, point_b)), [point_b])
        else:
            found.points.append(point_b)

    for i in range(rows):
        off = struct.unpack_from("<q", param, 0x40 + i * 24 + 8)[0]
        src = tuple(param[off + 4:off + 7])
        src_point = struct.unpack_from("<3f", param, off + 8)
        dst = tuple(param[off + 20:off + 23])
        dst_point = struct.unpack_from("<3f", param, off + 24)
        link(src, dst, src_point, dst_point)
        link(dst, src, dst_point, src_point)
    return edges


@dataclass
class Link:
    """How to move from one tile's coordinates to another's, and the shared points in the second tile."""
    offset: tuple
    points: list


@dataclass(frozen=True)
class Place:
    """A position in the open world: area, then global x, height and z. Height is None when unknown.

    For a place inside a dungeon, entrance is the open world point the game links that dungeon to.
    """
    area: int
    x: float
    y: object
    z: float
    entrance: object = None


def tile_center(index, level):
    scale = 2 ** level
    return (index * scale + (scale - 1) / 2) * TILE


def to_world(map_id, x, y, z, edges):
    """Converts a position in a map tile to a Place, or None when nothing links it to the open world."""
    m = re.fullmatch(r"m(\d+)_(\d+)_(\d+)_(\d+)", map_id)
    area, gx, gz, level = (int(g) for g in m.groups())

    # Suffixes of 10 and up are alternate versions of a normal tile, not larger ones.
    level = 0 if level >= 10 else level
    if area in MAPS:
        return Place(area, tile_center(gx, level) + x, y, tile_center(gz, level) + z)

    # Walk the conversion table to an open world tile, adding up each step's offset.
    start = (area, gx, gz)
    queue = deque([(start, (x, y, z), [])])
    seen = {start}
    while queue:
        node, (cx, cy, cz), points = queue.popleft()
        if node[0] in MAPS:
            wx, wz = node[1] * TILE + cx, node[2] * TILE + cz
            linked = [(node[1] * TILE + px, py, node[2] * TILE + pz) for px, py, pz in points]
            entrance = min(linked, key=lambda p: (p[0] - wx) ** 2 + (p[2] - wz) ** 2)
            return Place(node[0], wx, None if cy is None else cy, wz, tuple(round(v, 2) for v in entrance))
        for nxt, step in edges.get(node, {}).items():
            if nxt not in seen:
                seen.add(nxt)
                dx, dy, dz = step.offset
                queue.append((nxt, (cx + dx, None if cy is None else cy + dy, cz + dz), step.points))
    return None


def read_enemies(path):
    """Defeat flag -> [(map, part name)] from enemy.txt."""
    by_flag, cur = {}, {}
    def finish():
        if cur.get("flag") and cur.get("map") and cur.get("name"):
            by_flag.setdefault(cur["flag"], []).append((cur["map"], cur["name"]))
    for line in open(path, encoding="utf-8", errors="replace"):
        if line.startswith("- ID: "):
            finish()
            cur = {}
        elif line.startswith("  Map: "):
            cur["map"] = line[7:].strip()
        elif line.startswith("  Name: "):
            cur["name"] = line[8:].strip()
        elif line.startswith("  DefeatFlag: "):
            cur["flag"] = int(line[14:])
    finish()
    return by_flag


PART_IN_MAP = re.compile(r"\[(?:event )?(?:asset|enemy) (\S+) \(.*? in (m\d+_\d+_\d+_\d+)")
SHOP_NPC = re.compile(r"enemy (\S+) \(([^()]*?) - id .*?\) in (m\d+_\d+_\d+_\d+)")


def read_slots(path):
    slots, cur = [], None
    for line in open(path, encoding="utf-8"):
        if line.startswith("- Key: "):
            cur = {"key": line[7:].strip().strip("'"), "lots": [], "text": "", "parts": [], "npcs": [], "tile": None}
            slots.append(cur)
        elif cur is None:
            continue
        elif line.startswith("  - "):
            cur["lots"] += [int(x) for x in re.findall(r" - lot (\d+)", line)]
            cur["parts"] += PART_IN_MAP.findall(line)
            cur["npcs"] += [(p, m) for p, n, m in SHOP_NPC.findall(line) if "Talk Dummy" not in n]
            m = re.search(r" in (m\d+_\d+_\d+_\d+)", line)
            if m and not cur["tile"]:
                cur["tile"] = m.group(1)
        elif line.startswith("  Text: "):
            cur["text"] = line[8:].strip().strip("'\"")
    return slots


def part_position(map_id, name, parts, edges):
    """Returns (Place, position in its own map) for a named part, or None."""
    for part in parts.get(map_id, ()):
        if part[0] == name:
            place = to_world(map_id, part[2], part[3], part[4], edges)
            return (place, part[2:5]) if place else None
    return None


def locate(locations, slots, enemies, parts, edges):
    """Location id -> (Place, map id it is in, position in that map or None)."""
    by_lot, by_text = {}, []
    for slot in slots:
        for lot in slot["lots"]:
            by_lot.setdefault(lot, slot)
        by_text.append(slot)

    def from_slot(slot):
        for name, map_id in slot["parts"]:
            found = part_position(map_id, name, parts, edges) if map_id != ROUNDTABLE_MAP else None
            if found:
                return found[0], map_id, found[1]

        # A shop's list can name several copies of its seller; the Roundtable one is not the place to go.
        for name, map_id in slot["npcs"]:
            found = part_position(map_id, name, parts, edges) if map_id != ROUNDTABLE_MAP else None
            if found:
                return found[0], map_id, found[1]

        # Only the tile is known, so the position is its middle and the height is unknown.
        if slot["tile"]:
            found = to_world(slot["tile"], 0.0, None, 0.0, edges)
            if found:
                return found, slot["tile"], None
        return None

    results, unresolved = {}, []
    for loc in locations:
        found = None
        if loc["flagId"] in enemies:
            for map_id, name in enemies[loc["flagId"]]:
                pos = part_position(map_id, name, parts, edges)
                if pos:
                    found = (pos[0], map_id, pos[1])
                    break
        if not found:
            for lot in loc.get("lotIds", []):
                if lot in by_lot and (found := from_slot(by_lot[lot])):
                    break
        if not found:
            want = NAMED_PARTS_TEXT.get(loc["id"], loc["details"].rstrip("."))
            for slot in by_text:
                if slot["text"] and want and want.lower() in slot["text"].lower() and (found := from_slot(slot)):
                    break
        if found:
            results[loc["id"]] = found
        else:
            unresolved.append(loc["id"])
    return results, unresolved


def save_art(tile_set, art, path):
    """Saves a sheet of the game's map art, fully revealed, cropped and scaled for the app."""
    sheet = tile_set.stitch(art.sheet, True).crop(art.crop)
    if art.downscale > 1:
        sheet = sheet.resize((art.width, art.height), Image.LANCZOS)
    sheet.save(path, quality=90)


def read_region_names(path):
    names = {}
    for line in open(path, encoding="utf-8", errors="replace"):
        parts = line.strip().split(" ", 1)
        if len(parts) == 2:
            names[parts[0]] = parts[1]
    return names


def region_of(tile_name):
    name = re.split(r" - |;|,", tile_name)[0].strip()
    return REGION_ALIASES.get(name, name)


def main():
    game, out = Path(sys.argv[1]), Path(sys.argv[2])
    cache = Path(sys.argv[sys.argv.index("--cache") + 1]) if "--cache" in sys.argv else None
    randomizer = game / "randomizer"
    base = randomizer / "diste" / "Base"
    oodle = Oodle(randomizer / "oo2core_6_win64.dll")

    parts = load_all_parts(randomizer / "diste" / "Vanilla", oodle, cache)
    edges = read_conversions(randomizer / "regulation.bin", oodle)
    enemies = read_enemies(base / "enemy.txt")
    slots = read_slots(base / "itemslots.txt")
    locations = json.loads((out / "locations.json").read_text(encoding="utf-8"))
    tile_names = read_region_names(randomizer / "diste" / "Names" / "MapName.txt")
    print(f"map files {len(parts)}, conversion nodes {len(edges)}, enemies with flags {len(enemies)}, slots {len(slots)}")

    located, unresolved = locate(locations, slots, enemies, parts, edges)
    for loc_id, (kind, value) in HAND_PLACED.items():
        if kind == "location":
            located[loc_id] = located[value]
            continue
        if kind == "world":
            # The way in is placed by hand; where the check sits inside its own map is still true.
            area, x, z = value
            _, map_id, local = located.get(loc_id, (None, "hand placed", None))
            located[loc_id] = (Place(area, x, None, z, (x, None, z)), map_id, local)
            continue
        tile = next((t for t, n in tile_names.items() if value in n and t.endswith("_00") and t.startswith("m6")), None)
        if tile is None:
            raise SystemExit(f"No map tile is named like '{value}' for {loc_id}")
        located[loc_id] = (to_world(tile, 0.0, None, 0.0, edges), tile, None)
    for loc_id in OFF_MAP:
        located.pop(loc_id, None)

    tile_set = extract_map_art.TileSet(game, randomizer / "oo2core_6_win64.dll")
    underground_labels = {}
    output_maps = []
    pins = []
    for area, (map_key, title) in MAPS.items():
        labels = {}
        for map_id in parts:
            m = re.fullmatch(r"m(\d+)_(\d+)_(\d+)_(\d+)", map_id)
            if int(m.group(1)) != area or int(m.group(4)) != 0:
                continue
            name = tile_names.get(map_id)
            if name and region_of(name) in LABELLED_REGIONS:
                labels.setdefault(region_of(name), []).append((int(m.group(2)) * TILE, int(m.group(3)) * TILE))

        art = ART_SHEETS[area]
        uv = art.uv
        w, h = art.width, art.height
        image_name = f"map_{map_key}.jpg"
        save_art(tile_set, art, out / image_name)
        if area == SURFACE_AREA:
            save_art(tile_set, ArtSheet(ART_UNDERGROUND_SHEET, art.crop, art.downscale), out / UNDERGROUND_IMAGE)

        output_maps.append({
            "id": map_key,
            "name": title,
            "image": image_name,
            "width": w,
            "height": h,
            **art.frame(area),
            "labels": [{"name": n,
                        "u": round(uv(*np.mean(c, axis=0))[0] + LABEL_NUDGES.get(n, (0, 0))[0], 5),
                        "v": round(uv(*np.mean(c, axis=0))[1] + LABEL_NUDGES.get(n, (0, 0))[1], 5)}
                       for n, c in sorted(labels.items()) if len(c) >= 3],
        })
        placed = []
        for loc in locations:
            found = located.get(loc["id"])
            if found and found[0].area == area:
                place, source, local = found
                x, z = place.x, place.z

                # Checks in one spot, like two bosses in a dungeon, fan out so each pin can be clicked.
                # Only the drawn pin moves: the world position stays where the check really is.
                near = sum(1 for px, pz in placed if (px - x) ** 2 + (pz - z) ** 2 < PIN_SPREAD_METERS ** 2)
                placed.append((x, z))
                x += near * PIN_SPREAD_METERS * np.cos(near * 2.4)
                z += near * PIN_SPREAD_METERS * np.sin(near * 2.4)
                u, v = uv(x, z)
                underground = source.startswith(f"m{UNDERGROUND_AREA}_")
                sheet = UNDERGROUND_MAP if underground and area == SURFACE_AREA else map_key
                pins.append({
                    "id": loc["id"], "map": sheet, "u": u, "v": v, "underground": underground,
                    "area": area,
                    "world": point(place.x, place.y, place.z),
                    "mapId": source,
                    "local": point(*local) if local else None,
                    "entrance": point(*place.entrance) if place.entrance else None,
                })
                if sheet == UNDERGROUND_MAP:
                    underground_labels.setdefault(loc["region"], []).append((u, v))

        if area == SURFACE_AREA:
            region_names = {l["region"]: l["region"].replace("_", " ").title() for l in locations}
            output_maps.append({
                "id": UNDERGROUND_MAP,
                "name": "Underground",
                "image": UNDERGROUND_IMAGE,
                "width": w,
                "height": h,
                **art.frame(area),
                "labels": [{"name": region_names[r],
                            "u": round(float(np.mean([p[0] for p in c])) + LABEL_NUDGES.get(region_names[r], (0, 0))[0], 5),
                            "v": round(float(np.mean([p[1] for p in c])) - 0.012 + LABEL_NUDGES.get(region_names[r], (0, 0))[1], 5)}
                           for r, c in sorted(underground_labels.items()) if len(c) >= 2],
            })

    (out / "map_data.json").write_text(json.dumps({"maps": output_maps, "pins": pins}, ensure_ascii=False, indent=1), encoding="utf-8")

    # Report how well positions agree with the checklist's own region names.
    cats = Counter(l["category"] for l in locations)
    placed = Counter(l["category"] for l in locations if l["id"] in {p["id"] for p in pins})
    print("pins by category:", {c: f"{placed[c]}/{cats[c]}" for c in cats})
    print("off the map (no position or no open world link):", [l["id"] for l in locations if l["id"] not in {p["id"] for p in pins}])
    print("outside 0..1:", [p["id"] for p in pins if not (0 <= p["u"] <= 1 and 0 <= p["v"] <= 1)])
    print("sizes:", [(m["id"], m["width"], m["height"], len(m["labels"])) for m in output_maps])

    # Nearest named tile for each open world pin, compared with the sheet's region.
    region_words = {l["id"]: re.findall(r"[a-z]+", l["region"].replace("_", " ")) for l in locations}
    agree, disagree = 0, []
    for loc in locations:
        found = located.get(loc["id"])
        if not found:
            continue
        area, x, z = found[0].area, found[0].x, found[0].z
        tile = f"m{area}_{int(round(x / TILE)):02d}_{int(round(z / TILE)):02d}_00"
        name = tile_names.get(tile, "").lower()
        if not name:
            continue
        words = [w for w in region_words[loc["id"]] if w not in ("of", "the", "after", "manor", "caria")]
        if any(w in name for w in words):
            agree += 1
        else:
            disagree.append(f"{loc['id']} -> {tile_names.get(tile)}")
    print(f"region agreement: {agree} agree, {len(disagree)} differ")
    for d in disagree:
        print("  ", d)


if __name__ == "__main__":
    main()
