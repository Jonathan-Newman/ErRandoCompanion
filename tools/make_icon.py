"""Draw the app icon: a gold map pin holding a checkmark, on the app's dark background.

Usage: python make_icon.py <output.ico> [preview.png]
Needs numpy and pillow.
"""
import sys

import numpy as np
from PIL import Image, ImageDraw

CANVAS = 1024
SIZES = [16, 20, 24, 32, 40, 48, 64, 128, 256]
BACKGROUND_TOP = (46, 40, 31)
BACKGROUND_BOTTOM = (17, 15, 12)
BORDER = (150, 118, 62)
GOLD_TOP = (250, 224, 150)
GOLD_BOTTOM = (190, 140, 62)
INK = (24, 20, 15)


def vertical_gradient(top, bottom):
    t = np.linspace(0, 1, CANVAS)[:, None, None]
    colours = np.array(top, float) * (1 - t) + np.array(bottom, float) * t
    return Image.fromarray(np.broadcast_to(colours, (CANVAS, CANVAS, 3)).astype(np.uint8), "RGB")


def pin_shape():
    """A teardrop: a circle whose sides run straight down to a point."""
    cx, cy, radius, tip = 512, 400, 268, 900
    distance = tip - cy
    angle = np.arcsin(radius / distance)
    left = (cx - radius * np.cos(angle), cy + radius * np.sin(angle))
    right = (cx + radius * np.cos(angle), cy + radius * np.sin(angle))
    mask = Image.new("L", (CANVAS, CANVAS), 0)
    d = ImageDraw.Draw(mask)
    d.ellipse((cx - radius, cy - radius, cx + radius, cy + radius), fill=255)
    d.polygon([left, right, (cx, tip)], fill=255)
    return mask


def draw():
    icon = Image.new("RGBA", (CANVAS, CANVAS), (0, 0, 0, 0))

    tile = Image.new("L", (CANVAS, CANVAS), 0)
    ImageDraw.Draw(tile).rounded_rectangle((24, 24, 1000, 1000), radius=190, fill=255)
    icon.paste(vertical_gradient(BACKGROUND_TOP, BACKGROUND_BOTTOM), mask=tile)
    ImageDraw.Draw(icon).rounded_rectangle((24, 24, 1000, 1000), radius=190, outline=BORDER, width=22)

    icon.paste(vertical_gradient(GOLD_TOP, GOLD_BOTTOM), mask=pin_shape())

    # Thick enough to stay readable at 16 pixels.
    ImageDraw.Draw(icon).line([(388, 400), (486, 502), (648, 306)], fill=INK, width=74, joint="curve")
    return icon


def main():
    icon = draw()
    icon.save(sys.argv[1], format="ICO", sizes=[(s, s) for s in SIZES])
    if len(sys.argv) > 2:
        sheet = Image.new("RGB", (sum(SIZES) + 10 * len(SIZES) + 10, 276), (60, 60, 60))
        x = 10
        for size in SIZES:
            small = icon.resize((size, size), Image.LANCZOS)
            sheet.paste(small, (x, 10), small)
            x += size + 10
        sheet.save(sys.argv[2])


if __name__ == "__main__":
    main()
