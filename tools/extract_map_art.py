"""Stitch the game's own world map art from the player's install. Read-only on the game folder.

The art belongs to FromSoftware: it is generated locally for personal use, so keep the
results on your own machine and do not publish them with this project.
"""
import ctypes
import io
import re
import struct
import zlib
from collections import defaultdict
from xml.etree import ElementTree

from PIL import Image

import game_archive

TILE_INDEX = "/menu/71_maptile.tpfbhd"
TILE_DATA = "/menu/71_maptile.tpfbdt"
TILE_MASKS = "/menu/71_maptile.mtmskbnd.dcx"
INDEX_ENTRY_SIZE = 36
TILE_NAME = re.compile(r"MENU_MapTile_M(\d+)_L(\d+)_(\d+)_(\d+)_(\w{8})")
TILE_PIXELS = 256


class TileSet:
    """Every map tile in the install, read from the archive once and kept in memory."""

    def __init__(self, game_dir, oodle_path):
        self.game_dir = game_dir
        self._masks = None
        archives, _ = game_archive.open_archives(game_dir)
        holder = next(a for a in archives.values() if a.find(TILE_INDEX) is not None)
        self.index = holder.read(TILE_INDEX)
        self.data = holder.read(TILE_DATA)
        self.oodle = ctypes.WinDLL(str(oodle_path))
        self.tiles = defaultdict(list)

        count = struct.unpack_from("<I", self.index, 0x0C)[0]
        for i in range(count):
            pos = 0x40 + i * INDEX_ENTRY_SIZE
            size = struct.unpack_from("<q", self.index, pos + 8)[0]
            offset = struct.unpack_from("<I", self.index, pos + 24)[0]
            name_offset = struct.unpack_from("<I", self.index, pos + 32)[0]
            end = name_offset
            while self.index[end:end + 2] != b"\0\0":
                end += 2
            m = TILE_NAME.search(self.index[name_offset:end].decode("utf-16-le"))
            if m:
                map_id, level, x, y = (int(g) for g in m.groups()[:4])
                self.tiles[(map_id, level)].append((x, y, int(m.group(5), 16), offset, size))

    def _decompress(self, data):
        fmt = data[0x28:0x2C]
        size, comp = struct.unpack(">II", data[0x1C:0x24])
        start = data.index(b"DCA\0") + 8
        if fmt == b"DFLT":
            return zlib.decompress(data[start:start + comp])
        if fmt != b"KRAK":
            raise ValueError(f"Unsupported DCX format {fmt}")
        out = ctypes.create_string_buffer(size)
        n = self.oodle.OodleLZ_Decompress(data[start:start + comp], ctypes.c_longlong(comp), out, ctypes.c_longlong(size),
                                          0, 0, 0, None, None, None, None, None, None, 3)
        if n != size:
            raise ValueError("Oodle decompression failed")
        return out.raw

    def _tile(self, offset, size):
        raw = self._decompress(self.data[offset:offset + size])
        return Image.open(io.BytesIO(raw[raw.index(b"DDS "):])).convert("RGB")

    def fragment_masks(self, map_id):
        """Each cell's relevant map fragments, from the game's own tile masks: {(x, y): bits}.

        Many tile versions are stale development art the game never asks for, so the mask
        is what says which version is really drawn.
        """
        if self._masks is None:
            archives, _ = game_archive.open_archives(self.game_dir)
            holder = next(a for a in archives.values() if a.find(TILE_MASKS) is not None)
            binder = self._decompress(holder.read(TILE_MASKS))
            self._masks = {}
            count = struct.unpack_from("<I", binder, 0x0C)[0]
            header = struct.unpack_from("<q", binder, 0x20)[0]
            for i in range(count):
                pos = 0x40 + i * header
                size = struct.unpack_from("<q", binder, pos + 16)[0]
                offset = struct.unpack_from("<I", binder, pos + 24)[0]
                name_offset = struct.unpack_from("<I", binder, pos + 32)[0]
                end = name_offset
                while binder[end:end + 2] != b"\0\0":
                    end += 2
                m = re.search(r"_M(\d+)\.mtmsk", binder[name_offset:end].decode("utf-16-le"))
                cells = {}
                for entry in ElementTree.fromstring(binder[offset:offset + size]).iter("MapTileMask"):
                    cell = int(entry.get("id"))
                    cells[(cell // 100, cell % 100)] = int(entry.get("mask"))
                self._masks[int(m.group(1))] = cells
        return self._masks[map_id]

    def stitch(self, map_id, revealed=True):
        """Returns a whole sheet at full detail, north up: as if every map fragment were found, or none."""
        masks = self.fragment_masks(map_id)
        versions = {(x, y, bits): (offset, size) for x, y, bits, offset, size in self.tiles[(map_id, 0)]}
        cells = {(x, y) for x, y, _ in versions}
        columns = max(c[0] for c in cells) + 1
        rows = max(c[1] for c in cells) + 1
        sheet = Image.new("RGB", (columns * TILE_PIXELS, rows * TILE_PIXELS))
        for x, y in cells:
            wanted = masks.get((x, y), 0) if revealed else 0
            found = versions.get((x, y, wanted)) or versions.get((x, y, 0))
            if found:
                sheet.paste(self._tile(*found), (x * TILE_PIXELS, (rows - 1 - y) * TILE_PIXELS))
        return sheet
