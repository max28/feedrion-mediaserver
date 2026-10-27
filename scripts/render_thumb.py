"""Draw assets/thumb.png, the plugin's picture in the Emby and Jellyfin catalogues.

Manual, never run by a workflow: the committed PNG is what ships. The mark is
Feedrion's logo, drawn from its geometry; the word uses Inter Bold, passed in
because the font is not part of this repository.

    python3 scripts/render_thumb.py --font path/to/Inter-Bold.ttf
"""

import argparse
import pathlib

from PIL import Image, ImageDraw, ImageFont

W, H, SS = 800, 450, 4  # 16:9, the shape both catalogues show; drawn 4x then reduced
BG = (12, 54, 91)  # #0c365b, the logo's own blue
FG = (255, 255, 255)


def draw_mark(d: ImageDraw.ImageDraw, ox: float, oy: float, scale: float) -> None:
    """The logo without its disc, from its 1000-unit box."""

    def p(x: float, y: float) -> tuple[float, float]:
        return ox + x * scale, oy + y * scale

    def sector(r: float, start: float, end: float, fill) -> None:
        (x0, y0), (x1, y1) = p(405 - r, 645 - r), p(405 + r, 645 + r)
        d.pieslice([x0, y0, x1, y1], start, end, fill=fill)

    # The two arcs: quarter rings around the dot, outer one first. The hollows
    # overshoot the quarter so no sliver of the ring survives along its edges.
    sector(380, 270, 360, FG)
    sector(310, 265, 365, BG)
    sector(240, 270, 360, FG)
    sector(170, 265, 365, BG)
    (x0, y0), (x1, y1) = p(315, 555), p(495, 735)
    d.ellipse([x0, y0, x1, y1], fill=FG)
    f = [(215, 265), (375, 265), (375, 335), (285, 335), (285, 405), (375, 405),
         (375, 475), (285, 475), (285, 585), (215, 585)]
    d.polygon([p(x, y) for x, y in f], fill=FG)


def main() -> None:
    a = argparse.ArgumentParser()
    a.add_argument("--font", required=True, type=pathlib.Path)
    a.add_argument("--out", type=pathlib.Path,
                   default=pathlib.Path(__file__).resolve().parent.parent / "assets" / "thumb.png")
    args = a.parse_args()

    img = Image.new("RGB", (W * SS, H * SS), BG)
    d = ImageDraw.Draw(img)
    font = ImageFont.truetype(str(args.font), 84 * SS)
    word = "Feedrion"
    tw = d.textlength(word, font=font)
    mark = 168 * SS  # height of the mark (y 265..735 in its box)
    scale = mark / 470
    gap = 36 * SS
    total = 570 * scale + gap + tw
    left = (W * SS - total) / 2
    top = (H * SS - mark) / 2
    draw_mark(d, left - 215 * scale, top - 265 * scale, scale)
    asc, desc = font.getmetrics()
    d.text((left + 570 * scale + gap, H * SS / 2 - (asc - desc) / 2 - desc * 0.15), word,
           font=font, fill=FG)
    img.resize((W, H), Image.LANCZOS).save(args.out, optimize=True)
    print(args.out)


if __name__ == "__main__":
    main()
