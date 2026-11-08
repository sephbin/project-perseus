"""Perseus ribbon icon generator. Vector masters are authored here on a 32x32 grid
(2px strokes, cyan = action, magenta = violation/safety) and rasterised to 128px PNGs
for the Revit ribbon (high-DPI; WPF downsamples via DecodePixelWidth).

Requires:  pip install resvg-py
Usage:     python generate_icons.py <png_dir> [<svg_dir>]
           e.g. python generate_icons.py ../src/ProjectPerseus/resources icons_svg
Light icons are <name>.png, dark icons <name>_dark.png (ThemeIconManager convention)."""
import os, sys, io
import resvg_py

# name -> body (uses {i}=ink, {a}=accent, {w}=warning)
ICONS = {
# ---- Sync
"upload": '''<path d="M9 22a6 6 0 0 1 .8-11.9A8 8 0 0 1 25 12.5a4.8 4.8 0 0 1-1 9.5" fill="none" stroke="{i}"/>
<path d="M16 28V15M11 19.5l5-5 5 5" fill="none" stroke="{a}"/>''',
"pull_edits": '''<rect x="5" y="4" width="15" height="19" rx="2" fill="none" stroke="{i}"/>
<path d="M9 9h7M9 13h7M9 17h4" fill="none" stroke="{i}"/>
<circle cx="23" cy="22" r="6" fill="none" stroke="{a}"/>
<path d="M23 19v6M20.5 22.5l2.5 2.5 2.5-2.5" fill="none" stroke="{a}"/>''',
"ownership": '''<circle cx="12" cy="10" r="4.500" fill="none" stroke="{i}"/>
<path d="M3.500 26c0-5 3.800-8.500 8.500-8.500 2 0 3.800.6 5.200 1.700" fill="none" stroke="{i}"/>
<circle cx="21.500" cy="21.500" r="3.500" fill="none" stroke="{a}"/>
<path d="M24 24l4.500 4.500M26.500 26.500l1.800-1.800" fill="none" stroke="{a}"/>''',
# ---- Element IDs
"audit_ids": '''<path d="M5 8h5M5 14h5M5 20h5" fill="none" stroke="{i}"/>
<path d="M13 8h8M13 14h6M13 20h5" fill="none" stroke="{i}"/>
<circle cx="22" cy="21" r="5" fill="none" stroke="{a}"/>
<path d="M26 25l3.5 3.5" fill="none" stroke="{a}"/>''',
"renumber_ids": '''<path d="M6 7l2-1.5V13M6 13h4" fill="none" stroke="{i}"/>
<path d="M5.5 19.5a2.3 2.3 0 1 1 4 1.6L5.5 26h5" fill="none" stroke="{i}"/>
<path d="M15 9h12M15 16h12M15 23h12" fill="none" stroke="{a}"/>
''',
# ---- Key schedules
"schedule_manage": '''<rect x="4" y="5" width="24" height="22" rx="2" fill="none" stroke="{i}"/>
<path d="M4 11h24M12 11v16M20 11v16M4 17h24M4 22h24" fill="none" stroke="{i}" stroke-width="1.5"/>
<circle cx="24" cy="24" r="5" fill="{bg}" stroke="{a}"/>
<path d="M24 22v4M22 24h4" fill="none" stroke="{a}" stroke-width="1.6"/>''',
"schedule_export": '''<rect x="4" y="5" width="18" height="22" rx="2" fill="none" stroke="{i}"/>
<path d="M4 11h18M4 17h18M10 11v16M16 11v16" fill="none" stroke="{i}" stroke-width="1.5"/>
<path d="M18 22h11M25 18l4 4-4 4" fill="none" stroke="{a}"/>''',
"schedule_import": '''<rect x="10" y="5" width="18" height="22" rx="2" fill="none" stroke="{i}"/>
<path d="M10 11h18M10 17h18M16 11v16M22 11v16" fill="none" stroke="{i}" stroke-width="1.5"/>
<path d="M3 22h11M10 18l4 4-4 4" fill="none" stroke="{a}"/>''',
# ---- Violations
"violations_toggle": '''<path d="M2.5 16S8 7.5 16 7.5 29.5 16 29.5 16 24 24.5 16 24.5 2.5 16 2.5 16z" fill="none" stroke="{i}"/>
<circle cx="16" cy="16" r="4.5" fill="none" stroke="{w}"/>
<path d="M16 14v3M16 19v.1" fill="none" stroke="{w}" stroke-width="1.8"/>''',
"violations_mode": '''<path d="M5 12l8-4 8 4v10l-8 4-8-4z" fill="none" stroke="{i}" stroke-dasharray="3 2.4"/>
<path d="M5 12l8 4 8-4M13 16v10" fill="none" stroke="{i}" stroke-dasharray="3 2.4"/>
<path d="M22 8h6M25 5l3 3-3 3M28 24h-6M25 21l-3 3 3 3" fill="none" stroke="{a}"/>''',
"violations_list": '''<path d="M13 8h15M13 16h15M13 24h15" fill="none" stroke="{i}"/>
<path d="M4 6.5l2.5 4.5h-5zM4 14.5l2.5 4.5h-5z" fill="none" stroke="{w}" stroke-width="1.6" transform="translate(0 0)"/>
<circle cx="4" cy="24" r="2.8" fill="none" stroke="{w}" stroke-width="1.6"/>''',
"freefall": '''<defs><linearGradient id="g" x1="0" y1="0" x2="1" y2="1">
<stop offset="0" stop-color="{w}" stop-opacity=".18"/><stop offset=".5" stop-color="{w}" stop-opacity=".45"/><stop offset="1" stop-color="{w}" stop-opacity=".25"/></linearGradient>
<linearGradient id="s" x1="0" y1="0" x2="1" y2="1">
<stop offset="0" stop-color="#fff" stop-opacity=".95"/><stop offset="1" stop-color="#fff" stop-opacity="0"/></linearGradient></defs>
<path d="M16 3.5l11 4v7.5c0 7-4.5 11-11 13.5C9.5 26 5 22 5 15V7.5z" fill="url(#g)" stroke="{i}"/>
<path d="M16 5.2l-8.3 3v6.800c0 2.200.4 4.200 1.300 6C8 16 8.500 11 16 5.200z" fill="url(#s)" stroke="none" opacity=".85"/>
<path d="M9.200 10v5c0 3 1.200 5.500 3 7.500" fill="none" stroke="#fff" stroke-width="1.500" opacity=".8"/>
<path d="M16 9.5v7" fill="none" stroke="{w}" stroke-width="2.4"/>
<circle cx="16" cy="21" r="1.4" fill="{w}" stroke="none"/>
<!-- crack --><path d="M24 6.4L20.6 11.2L22.8 14.2L19.4 19L21 22L17.6 26.4" fill="none" stroke="{i}" stroke-width="1.5"/><path d="M24.6 7.2L21.4 11.6L23.6 14.4L20.2 19.2L21.8 22.2" fill="none" stroke="{sp}" stroke-width=".7" opacity=".85"/><path d="M20.6 11.2L24.6 11.6M19.4 19L17.8 19.6" fill="none" stroke="{i}" stroke-width="1.1"/>
<path d="M26 1.5c.3 3 1.2 3.9 4.2 4.2-3 .3-3.9 1.2-4.2 4.2-.3-3-1.2-3.9-4.2-4.2 3-.3 3.9-1.2 4.2-4.2z" fill="{sp}" stroke="{a}" stroke-width="1"/>
<path d="M5 22c.2 1.6.7 2.1 2.3 2.3-1.6.2-2.1.7-2.3 2.3-.2-1.6-.7-2.1-2.3-2.3 1.6-.2 2.1-.7 2.3-2.3z" fill="{sp}" stroke="{a}" stroke-width=".8"/>''',
# ---- Setup
"project_rules": '''<defs><linearGradient id="g" x1="0" y1="0" x2="1" y2="1">
<stop offset="0" stop-color="{a}" stop-opacity=".18"/><stop offset=".5" stop-color="{a}" stop-opacity=".45"/><stop offset="1" stop-color="{a}" stop-opacity=".25"/></linearGradient>
<linearGradient id="s" x1="0" y1="0" x2="1" y2="1">
<stop offset="0" stop-color="#fff" stop-opacity=".95"/><stop offset="1" stop-color="#fff" stop-opacity="0"/></linearGradient></defs>
<path d="M16 3.5l11 4v7.5c0 7-4.5 11-11 13.5C9.5 26 5 22 5 15V7.5z" fill="url(#g)" stroke="{i}"/>
<path d="M16 5.2l-8.3 3v6.800c0 2.200.4 4.200 1.300 6C8 16 8.500 11 16 5.200z" fill="url(#s)" stroke="none" opacity=".85"/>
<path d="M9.200 10v5c0 3 1.200 5.500 3 7.500" fill="none" stroke="#fff" stroke-width="1.500" opacity=".8"/>
<path d="M11 15l3.8 3.800 6.500-7.500" fill="none" stroke="{a}" stroke-width="2.400"/>
<path d="M26 1.5c.3 3 1.2 3.9 4.2 4.2-3 .3-3.9 1.2-4.2 4.2-.3-3-1.2-3.9-4.2-4.2 3-.3 3.9-1.2 4.2-4.2z" fill="{sp}" stroke="{a}" stroke-width="1"/>
<path d="M5 22c.2 1.6.7 2.1 2.3 2.3-1.6.2-2.1.7-2.3 2.3-.2-1.6-.7-2.1-2.3-2.3 1.6-.2 2.1-.7 2.3-2.3z" fill="{sp}" stroke="{a}" stroke-width=".8"/>''',
"settings": '''<path d="M5 9h8M21 9h6M5 16h2M15 16h12M5 23h12M25 23h2" fill="none" stroke="{i}"/>
<circle cx="17" cy="9" r="3" fill="none" stroke="{a}"/>
<circle cx="11" cy="16" r="3" fill="none" stroke="{a}"/>
<circle cx="21" cy="23" r="3" fill="none" stroke="{a}"/>''',
"reset_identity": '''<path d="M24.2 10.3A10 10 0 1 1 14.3 6.2" fill="none" stroke="{a}" stroke-width="3.2"/>
<path d="M13.5 3.2Q20.5 1.8 22.8 5.8Q20.5 9.8 13.5 8.8z" fill="{a}" stroke="{a}" stroke-width="1"/>
<circle cx="18.2" cy="5.4" r="1.2" fill="{w}" stroke="none"/>
<path d="M23 5.8h2.4l1.2-1.2M25.4 5.8l1.2 1.2" fill="none" stroke="{w}" stroke-width="1.2"/>
<rect x="12" y="13" width="8" height="8" rx="1.500" fill="none" stroke="{i}" stroke-width="1.800"/>''',
"diagnose": '''<circle cx="14" cy="14" r="9" fill="none" stroke="{i}"/>
<path d="M20.500 20.500l8 8" fill="none" stroke="{i}"/>
<path d="M6.500 16.500c2-4.500 4-4.500 6-.5s4 4 5.500.5" fill="none" stroke="{a}" stroke-width="2.200"/>
<path d="M16.500 13.200Q19.500 9.800 21.800 12.200Q20 15.500 16.500 16z" fill="{a}" stroke="{a}" stroke-width=".8"/>
<circle cx="19" cy="12.400" r=".8" fill="{w}" stroke="none"/>
<path d="M21.800 12.200l2-.8M23 11.600l.2-1.200M23 11.700l1 .8" fill="none" stroke="{w}" stroke-width=".9"/>''',
"about": '''<path d="M24.2 10.3A10 10 0 1 1 14.3 6.2" fill="none" stroke="{i}" stroke-width="3.2"/>
<path d="M13.5 3.2Q20.5 1.8 22.8 5.8Q20.5 9.8 13.5 8.8z" fill="{i}" stroke="{i}" stroke-width="1"/>
<circle cx="18.2" cy="5.4" r="1.2" fill="{w}" stroke="none"/>
<path d="M23 5.8h2.4l1.2-1.2M25.4 5.8l1.2 1.2" fill="none" stroke="{w}" stroke-width="1.2"/>
<path d="M16 15v6.500" fill="none" stroke="{a}" stroke-width="2.400"/>
<circle cx="16" cy="11.500" r="1.400" fill="{a}" stroke="none"/>''',
}


# ---- smooth snakes: one tapered polygon (tail -> body -> head), no separate head piece
import math
def _sm(x):
    x = max(0.0, min(1.0, x)); return x*x*(3-2*x)

def _snake(pts, half, cap=3.0):
    """pts: centreline samples; half(t): half-width at t in [0,1]. Returns (path d, tip, tangent, last, normal, halfEnd)."""
    n = len(pts); L = []; R = []
    for k, (x, y) in enumerate(pts):
        a = pts[max(k-1, 0)]; b = pts[min(k+1, n-1)]
        tx, ty = b[0]-a[0], b[1]-a[1]; m = math.hypot(tx, ty) or 1; tx, ty = tx/m, ty/m
        nx, ny = -ty, tx; h = half(k/(n-1))
        L.append((x+nx*h, y+ny*h)); R.append((x-nx*h, y-ny*h))
    x, y = pts[-1]; a = pts[-2]
    tx, ty = x-a[0], y-a[1]; m = math.hypot(tx, ty); tx, ty = tx/m, ty/m
    nx, ny = -ty, tx; h = half(1.0)
    capp = []
    for k in range(1, 12):                       # half-ellipse snout, left -> right
        ph = math.pi*k/12
        capp.append((x+nx*h*math.cos(ph)+tx*cap*math.sin(ph), y+ny*h*math.cos(ph)+ty*cap*math.sin(ph)))
    poly = L + capp + R[::-1]
    d = "M" + " L".join("%.2f %.2f" % p for p in poly) + "z"
    return d, (x+tx*cap, y+ty*cap), (tx, ty), (x, y), (nx, ny), h

def _tongue(tip, t, w=1.1, ln=1.8, fk=1.5):
    tx, ty = t; x, y = tip
    ex, ey = x+tx*ln, y+ty*ln
    def rot(a):
        c, s_ = math.cos(a), math.sin(a); return (tx*c-ty*s_, tx*s_+ty*c)
    f1 = rot(0.6); f2 = rot(-0.6)
    return ('<path d="M%.2f %.2fL%.2f %.2fM%.2f %.2fL%.2f %.2fM%.2f %.2fL%.2f %.2f" fill="none" stroke="{w}" stroke-width="%s"/>'
            % (x, y, ex, ey, ex, ey, ex+f1[0]*fk, ey+f1[1]*fk, ex, ey, ex+f2[0]*fk, ey+f2[1]*fk, w))

def _ring(body, inner):
    th0, th1, N = -22, 298, 100
    pts = [(16+10*math.cos(math.radians(th0+(th1-th0)*k/N)), 16+10*math.sin(math.radians(th0+(th1-th0)*k/N))) for k in range(N+1)]
    half = lambda t: 0.15 + 1.25*_sm(t/0.14)
    d, tip, tg, last, nr, h = _snake(pts, half, cap=2.8)
    ex, ey = last[0]-tg[0]*1.2+nr[0]*-0.0, last[1]-tg[1]*1.2
    # eye sits on the outer (radial) side of the head
    rx, ry = last[0]-16, last[1]-16; m = math.hypot(rx, ry); rx, ry = rx/m, ry/m
    ex, ey = last[0]-tg[0]*0.4+rx*0.7, last[1]-tg[1]*0.4+ry*0.7
    return ('<path d="%s" fill="{%s}" stroke="{%s}" stroke-width=".6"/>' % (d, body, body)
            + _tongue(tip, tg) + inner)

ICONS["reset_identity"] = _ring('a', '<rect x="12" y="13" width="8" height="8" rx="1.5" fill="none" stroke="{i}" stroke-width="1.8"/>')
ICONS["about"] = _ring('i', '<path d="M16 15v6.500" fill="none" stroke="{a}" stroke-width="2.400"/><circle cx="16" cy="11.500" r="1.400" fill="{a}" stroke="none"/>')

def _diag():
    N = 60
    pts = [(6.5+9.5*k/N, 14.5+2.6*math.sin(2*math.pi*1.0*k/N+0.3)) for k in range(N+1)]
    half = lambda t: 0.15 + 0.95*_sm(t/0.18)
    d, tip, tg, last, nr, h = _snake(pts, half, cap=1.8)
    return ('<circle cx="14" cy="14" r="9" fill="none" stroke="{i}"/><path d="M20.5 20.5l8 8" fill="none" stroke="{i}"/>'
            + '<path d="%s" fill="{a}" stroke="{a}" stroke-width=".4"/>' % d
            + _tongue(tip, tg, 0.8))
ICONS["diagnose"] = _diag()

# ---- Markers: elements ringed by parameter value (cyan + magenta rings, a lock badge)
ICONS["markers"] = (
    '<path d="M6 25V7h7v18" fill="none" stroke="{i}" stroke-width="1.6"/>'
    '<path d="M4 25h11" fill="none" stroke="{i}" stroke-width="1.6"/>'
    '<circle cx="9.5" cy="16" r="8" fill="none" stroke="{w}"/>'
    '<path d="M20 25V12h6v13" fill="none" stroke="{i}" stroke-width="1.6"/>'
    '<circle cx="23" cy="19" r="6.5" fill="none" stroke="{a}"/>'
    '<circle cx="26.5" cy="8" r="4.2" fill="{a}" stroke="none"/>'
    '<path d="M24.8 8.2h3.4v2.4h-3.4z M25.6 8.2V7.3a.9.9 0 0 1 1.8 0v.9" fill="none" stroke="{sp}" stroke-width="1"/>'
)

# ---- Federator: old version (dashed) vs new version (solid), a changed element ringed
ICONS["federator"] = (
    '<rect x="4" y="4" width="16" height="16" rx="1.5" fill="none" stroke="{i}" stroke-width="1.6" stroke-dasharray="2.5 2"/>'
    '<rect x="10" y="10" width="17" height="17" rx="1.5" fill="{bg}" stroke="{i}" stroke-width="1.8"/>'
    '<path d="M14 23v-8h5" fill="none" stroke="{i}" stroke-width="1.6"/>'
    '<circle cx="21" cy="19" r="4" fill="none" stroke="{a}"/>'
    '<path d="M24 22l3.5 3.5" fill="none" stroke="{a}"/>'
)

GROUPS = [
    ("Sync",         ["upload", "pull_edits", "ownership"]),
    ("Element IDs",  ["audit_ids", "renumber_ids"]),
    ("Key Schedules",["schedule_manage", "schedule_export", "schedule_import"]),
    ("Violations",   ["violations_toggle", "violations_mode", "violations_list", "freefall"]),
    ("Markers",      ["markers"]),
    ("Federator",    ["federator"]),
    ("Setup",        ["project_rules", "settings", "reset_identity", "diagnose", "about"]),
]

THEMES = {
    "light": dict(i="#2f3d57", a="#00a0e0", w="#e6007e", bg="#ffffff", sp="#ffffff", b="#1f4fd8"),
    "dark":  dict(i="#e3e9f5", a="#3cc8ff", w="#ff4fae", bg="#2b2b2b", sp="#ffffff", b="#6f8cff"),
}

def svg(name, theme):
    body = ICONS[name].format(**THEMES[theme])
    return ('<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 32 32" fill="none" '
            'stroke-width="2" stroke-linecap="round" stroke-linejoin="round">' + body + '</svg>')

def main(png_dir, svg_dir=None):
    os.makedirs(png_dir, exist_ok=True)
    if svg_dir: os.makedirs(svg_dir, exist_ok=True)
    for name in ICONS:
        for theme in THEMES:
            suffix = "_dark" if theme == "dark" else ""
            s = svg(name, theme)
            if svg_dir:
                open(os.path.join(svg_dir, f"{name}{suffix}.svg"), "w").write(s)
            png = bytes(resvg_py.svg_to_bytes(svg_string=s, width=128, height=128))
            open(os.path.join(png_dir, f"{name}{suffix}.png"), "wb").write(png)

if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2] if len(sys.argv) > 2 else None)
