"""Turn AI-generated icon images (Gemini / "Nano Banana", generated on a flat #00FF00 background)
into Perseus ribbon icons: <name>.png + <name>_dark.png, 128px, transparent, palette-snapped.

Workflow (driven by Claude through Claude in Chrome, or by hand in the Gemini app):
  1. python process_ai_icons.py prompt <name> "<what the glyph shows>"   -> prints the Gemini prompt
  2. Generate in Gemini, save the image as  icons_ai/raw/<name>.png
     (Claude downloads it to ~/Downloads/perseus-icon-<name>.png; `ingest` moves it into raw/)
  3. python process_ai_icons.py build [name ...]   -> icons_ai/review/<name>.png, <name>_dark.png,
                                                      and review_sheet.png (old vs new, light/dark, 128/64/32)
  4. python process_ai_icons.py approve <name> ... -> copies the reviewed pair into ../src/ProjectPerseus/resources

The raw images in icons_ai/raw are the masters (like icons_svg for generate_icons.py).
NOTE: generate_icons.py writes every name in its ICONS table; remove a name from ICONS once its
icon comes from here, or re-running generate_icons.py will overwrite the AI icon.

Requires: pip install pillow numpy
"""
import os, sys, shutil, glob
import numpy as np
from PIL import Image, ImageDraw

HERE = os.path.dirname(os.path.abspath(__file__))
RAW = os.path.join(HERE, "icons_ai", "raw")
REVIEW = os.path.join(HERE, "icons_ai", "review")
RESOURCES = os.path.normpath(os.path.join(HERE, "..", "src", "ProjectPerseus", "resources"))
DOWNLOADS = os.path.join(os.path.expanduser("~"), "Downloads")

# Same palette as generate_icons.THEMES (ink, accent, warning, white, blue).
LIGHT = {"i": "#2f3d57", "a": "#00a0e0", "w": "#e6007e", "sp": "#ffffff", "b": "#1f4fd8"}
DARK = {"i": "#e3e9f5", "a": "#3cc8ff", "w": "#ff4fae", "sp": "#ffffff", "b": "#6f8cff"}
SIZE, MARGIN = 128, 8

# Rules from ICON_STYLE.md; keep the two in step.
PROMPT = ('Generate an image: a single minimal flat line icon for a software toolbar button "{title}". '
          'Glyph: {desc}. '
          'Style: flat 2D line icon drawn front-on or in plan view, NOT isometric, NOT perspective, no 3D. '
          'Thick uniform outline strokes with rounded ends and corners (like 2px strokes on a 32px grid), '
          'outlines only, no fills except tiny dots, at most 6 strokes in total, as simple as possible so it '
          'reads at 32 pixels. No gradients, no shading, no shadows, no text, no outer frame or tile. '
          'Centered, filling most of the canvas with a small even margin. '
          'Colours: dark slate {i} for the main object; cyan {a} only for the action part{warn}; nothing else. '
          'Background: solid flat pure green #00FF00 filling the whole square canvas. Square 1:1.')


def hex_rgb(h):
    return np.array([int(h[k:k + 2], 16) for k in (1, 3, 5)], dtype=np.float32)


def key_green(rgb):
    """Alpha + un-mixed colour from an image on a flat green background (handles anti-aliased edges).
    The background colour is measured from the corners: Gemini's "#00FF00" comes back as ~(15,245,6)."""
    h, w = rgb.shape[:2]
    k = max(4, min(h, w) // 64)
    corners = np.concatenate([rgb[:k, :k], rgb[:k, -k:], rgb[-k:, :k], rgb[-k:, -k:]]).reshape(-1, 3)
    bg = np.median(corners, axis=0)
    full = max(bg[1] - max(bg[0], bg[2]), 1.0)                    # green dominance of the background
    r, g, b = rgb[..., 0], rgb[..., 1], rgb[..., 2]
    spill = np.clip((g - np.maximum(r, b)) / full, 0, 1)          # 1 = background
    alpha = np.clip((1.0 - spill - 0.12) / 0.80, 0, 1)            # dead zones: noise -> 0, solid -> 1
    a = np.maximum(alpha, 1e-3)[..., None]
    fg = np.clip((rgb - (1 - a) * bg) / a, 0, 255)
    return alpha, fg


def snap(fg, alpha, src, dst):
    """Recolour each pixel to the nearest palette colour of `src`, written as `dst`'s colour."""
    keys = list(src)
    pal = np.stack([hex_rgb(src[k]) for k in keys])
    out = np.stack([hex_rgb(dst[k]) for k in keys])
    d = ((fg[..., None, :] - pal[None, None]) ** 2).sum(-1)
    return out[d.argmin(-1)], alpha


def to_icon(rgb, alpha):
    im = Image.fromarray(np.dstack([rgb, alpha * 255]).astype(np.uint8), "RGBA")
    box = Image.fromarray((alpha > 0.1).astype(np.uint8) * 255).getbbox()
    if box:
        im = im.crop(box)
    w, h = im.size
    side = max(w, h)
    sq = Image.new("RGBA", (side, side), (0, 0, 0, 0))
    sq.paste(im, ((side - w) // 2, (side - h) // 2))
    inner = SIZE - 2 * MARGIN
    sq = sq.convert("RGBa").resize((inner, inner), Image.LANCZOS).convert("RGBA")
    out = Image.new("RGBA", (SIZE, SIZE), (0, 0, 0, 0))
    out.paste(sq, (MARGIN, MARGIN))
    return out


def build(name):
    rgb = np.asarray(Image.open(os.path.join(RAW, name + ".png")).convert("RGB"), dtype=np.float32)
    alpha, fg = key_green(rgb)
    os.makedirs(REVIEW, exist_ok=True)
    for suffix, dst in (("", LIGHT), ("_dark", DARK)):
        col, a = snap(fg, alpha, LIGHT, dst)
        to_icon(col, a).save(os.path.join(REVIEW, name + suffix + ".png"))
    print("built", name)


def sheet(names):
    """Rows per icon: [old light | new light] on white, [old dark | new dark] on #2b2b2b, each 128/64/32."""
    cell, pad = 128, 12
    sizes = (128, 64, 32)
    block = sum(sizes) + pad * (len(sizes) + 1)
    W, H = block * 4, (cell + 2 * pad + 18) * len(names)
    img = Image.new("RGB", (W, H), "#888888")
    d = ImageDraw.Draw(img)
    for row, n in enumerate(names):
        y0 = row * (cell + 2 * pad + 18)
        d.text((4, y0 + 2), n + "   (old | new)  light  /  (old | new)  dark", fill="#000")
        for col, (folder, suffix, bg) in enumerate([(RESOURCES, "", "#ffffff"), (REVIEW, "", "#ffffff"),
                                                    (RESOURCES, "_dark", "#2b2b2b"), (REVIEW, "_dark", "#2b2b2b")]):
            x0 = col * block
            d.rectangle([x0 + 2, y0 + 16, x0 + block - 2, y0 + 16 + cell + 2 * pad], fill=bg)
            p = os.path.join(folder, n + suffix + ".png")
            if not os.path.exists(p):
                continue
            src = Image.open(p).convert("RGBA")
            x = x0 + pad
            for s in sizes:
                ic = src.resize((s, s), Image.LANCZOS)
                img.paste(ic, (x, y0 + 16 + pad + (cell - s) // 2), ic)
                x += s + pad
    out = os.path.join(REVIEW, "review_sheet.png")
    img.save(out)
    print("sheet", out)


def main(argv):
    cmd, args = (argv[0], argv[1:]) if argv else ("help", [])
    if cmd == "prompt":
        name, desc = args[0], args[1]
        warn = ", magenta {} only for a warning / violation part".format(LIGHT["w"]) if "--warn" in args else ""
        print(PROMPT.format(title=name.replace("_", " ").title(), desc=desc, warn=warn, **LIGHT))
    elif cmd == "ingest":
        os.makedirs(RAW, exist_ok=True)
        for p in glob.glob(os.path.join(DOWNLOADS, "perseus-icon-*.png")):
            name = os.path.basename(p)[len("perseus-icon-"):-4]
            shutil.move(p, os.path.join(RAW, name + ".png"))
            print("ingested", name)
    elif cmd == "build":
        names = args or [os.path.basename(p)[:-4] for p in sorted(glob.glob(os.path.join(RAW, "*.png")))]
        for n in names:
            build(n)
        sheet(names)
    elif cmd == "approve":
        for n in args:
            for suffix in ("", "_dark"):
                shutil.copy2(os.path.join(REVIEW, n + suffix + ".png"), os.path.join(RESOURCES, n + suffix + ".png"))
            print("approved", n, "->", RESOURCES)
    else:
        print(__doc__)


if __name__ == "__main__":
    main(sys.argv[1:])
