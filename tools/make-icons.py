#!/usr/bin/env python3
"""Draws the MirrorDeck icon and writes the .ico for the app and the PNG tiles for the MSIX package.

Needs Pillow (pip install pillow). The artwork is original: a monitor with a phone in front of it.
It deliberately does not resemble Apple's AirPlay glyph, which is a trademark.

    python tools/make-icons.py
"""
from pathlib import Path
from PIL import Image, ImageDraw

ROOT = Path(__file__).resolve().parent.parent
TOP, BOTTOM = (64, 120, 242), (112, 72, 232)   # blue -> violet
WHITE = (255, 255, 255, 255)


def draw(size: int, padding: float = 0.0) -> Image.Image:
    """Renders at 4x and scales down, which is the cheapest way to get clean edges from Pillow."""
    s = size * 4
    img = Image.new("RGBA", (s, s), (0, 0, 0, 0))

    # background: rounded square with a vertical gradient
    gradient = Image.new("RGBA", (s, s))
    px = gradient.load()
    for y in range(s):
        t = y / (s - 1)
        row = tuple(round(TOP[i] + (BOTTOM[i] - TOP[i]) * t) for i in range(3)) + (255,)
        for x in range(s):
            px[x, y] = row
    inset = round(s * padding)
    mask = Image.new("L", (s, s), 0)
    ImageDraw.Draw(mask).rounded_rectangle([inset, inset, s - 1 - inset, s - 1 - inset], radius=round((s - 2 * inset) * 0.22), fill=255)
    img.paste(gradient, (0, 0), mask)

    d = ImageDraw.Draw(img)
    box = s - 2 * inset

    def u(v: float) -> int:  # unit square -> pixels inside the padded box
        return inset + round(box * v)

    line = max(2, round(box * 0.055))

    # monitor
    d.rounded_rectangle([u(0.16), u(0.24), u(0.84), u(0.66)], radius=round(box * 0.05), outline=WHITE, width=line)
    d.rectangle([u(0.46), u(0.66), u(0.54), u(0.74)], fill=WHITE)
    d.rounded_rectangle([u(0.34), u(0.74), u(0.66), u(0.74) + line], radius=line // 2, fill=WHITE)

    # phone, in front of the monitor's lower right corner
    phone = [u(0.60), u(0.44), u(0.80), u(0.82)]
    halo = round(box * 0.035)
    d.rounded_rectangle([phone[0] - halo, phone[1] - halo, phone[2] + halo, phone[3] + halo], radius=round(box * 0.06),
                        fill=tuple(round(TOP[i] + (BOTTOM[i] - TOP[i]) * 0.62) for i in range(3)) + (255,))
    d.rounded_rectangle(phone, radius=round(box * 0.04), fill=WHITE)

    return img.resize((size, size), Image.LANCZOS)


def main() -> None:
    assets = ROOT / "src" / "MirrorDeck" / "Assets"
    assets.mkdir(parents=True, exist_ok=True)
    sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256]
    # BMP frames instead of Pillow's default PNG: every Windows shell and resource compiler reads them
    draw(256).save(assets / "MirrorDeck.ico", format="ICO", sizes=[(n, n) for n in sizes], bitmap_format="bmp")

    tiles = ROOT / "packaging" / "Assets"
    tiles.mkdir(parents=True, exist_ok=True)
    draw(50).save(tiles / "StoreLogo.png")
    draw(44).save(tiles / "Square44x44Logo.png")
    draw(150, padding=0.14).save(tiles / "Square150x150Logo.png")
    wide = Image.new("RGBA", (310, 150), (0, 0, 0, 0))
    wide.paste(draw(150, padding=0.14), (80, 0))
    wide.save(tiles / "Wide310x150Logo.png")
    print("icons written to", assets, "and", tiles)


if __name__ == "__main__":
    main()
