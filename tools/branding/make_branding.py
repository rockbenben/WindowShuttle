#!/usr/bin/env python3
"""Regenerates every branding asset from one set of geometry constants.

Why a generator instead of checked-in art: assets/branding/ used to hold a single binary .ico with
no source of any kind, so the repository could not reproduce, resize, or recolor its own icon —
the only way to change it was to redraw it from scratch. Everything below comes out of the constants
in GEOMETRY and PALETTE, and both the editable SVG master and the shipped raster are emitted from
those same numbers, so the two can never drift.

    python tools/branding/make_branding.py

Outputs (all under assets/branding/):
    windowshuttle.svg     editable master, 256 box
    windowshuttle.ico     16 / 32 / 48 / 256, per-size artwork (small sizes drop detail, see DETAIL)
    logo-256.png      for READMEs and release pages
    social-card.png   1280x640, GitHub's social preview size

Requires Pillow only (no SVG rasteriser is available on the dev box, so the raster path draws the
same geometry directly rather than going through the SVG).
"""
from __future__ import annotations


import pathlib
from PIL import Image, ImageDraw, ImageFont

ROOT = pathlib.Path(__file__).resolve().parents[2]
OUT = ROOT / "assets" / "branding"

# ── Palette ─────────────────────────────────────────────────────────────────────────────────────
# Straight out of the app's own AppTheme. The mark uses the two colours that carry meaning in the
# product — Beam is the primary monitor, Live is the monitor the cursor is on — so the icon is made
# of the same two signals the user reads inside the window, not a decorative pair chosen for looks.
# Nothing here is a new brand colour: introducing one would break that tie, which is the single most
# valuable thing this identity owns.
PALETTE = {
    "tile":   "#14161E",   # AppTheme.Room lifted a little: pure Room disappears on a black taskbar
    "edge":   "#2C3141",   # AppTheme.Rule — hairline that gives the tile an outline on dark grounds
    "beam":   "#FFD166",   # AppTheme.Beam  — the primary screen
    "live":   "#6FC3F7",   # AppTheme.Live  — the screen the cursor is on
    "room":   "#0B0C11",   # AppTheme.Room  — social card ground
    "panel":  "#20242F",
    "ink":    "#EAEBF0",
    "dim":    "#A9AEBE",
    "faint":  "#848A9E",
}

# ── Geometry, in a 256 box ──────────────────────────────────────────────────────────────────────
# Two screens interlocking on the diagonal, at DIFFERENT sizes: Beam (the primary) big and behind,
# Live (the screen the cursor is on) smaller and in front, cut out of the one behind it so the pair
# reads as depth rather than as two shapes that happen to touch.
#
# The size difference is the load-bearing part, and it is exactly what an equal pair was missing:
# cascaded windows are all the SAME SIZE, so two equal rectangles read as the Windows "restore down"
# glyph however they are arranged. Two displays are never the same size. Drawing the primary as the
# big one is also simply true — the app's own monitor map sizes each screen to how large it looks
# where it sits, not to its pixel count.
#
# The diagonal interlock is kept on purpose (the alternative was measured, see below): it fills the
# square tile — 82% across, 57% down — where a side-by-side row of two 16:10 screens reaches only
# 36% down and leaves the icon sparse, top-heavy and unfinished.
GEOMETRY = {
    "box": 256,
    "tile_radius": 58,
    "beam": (90, 98, 146, 91),    # cx, cy, w, h — the primary: bigger, behind, upper-left
    "live": (174, 166, 104, 65),  # the cursor's screen: smaller, in front, lower-right
    "screen_radius": 12,
    "gap": 9,                     # tile-coloured cut around the front screen
}

# Tried and dropped, so nobody re-litigates them:
#   · TWO EQUAL OVERLAPPING SCREENS (what this was through v1) — the same arrangement as now, but
#     both screens the same size, which is the single thing that makes cascaded windows look like
#     cascaded windows. Fixed by the size difference above, not by throwing the arrangement away.
#   · SIDE BY SIDE, no overlap — tried as the replacement for the above and it lost more than it
#     fixed: two 16:10 screens in a row reach only 36% down a square tile, so the mark went sparse
#     and top-heavy, and with no overlap the two shapes stopped having any relationship to each
#     other at all. The interlock is what carries "these two trade places".
#   · SHEARING the two screens to add motion — tried at 10°, and it made them read as two leaning
#     *cards* or sticky notes while throwing away the one thing the upright version had going for it
#     ("these are displays"). A screen that leans stops being a screen.
#   · A LOOM SHUTTLE (a pointed-both-ends lens, split across its waist into the two colours). On
#     paper it was the best idea here: it is where the product's name comes from, it is
#     180°-symmetric by nature, and it points. Rendered, a two-tone lens is unmistakably an EYE —
#     and slimming it toward a real shuttle's proportions made it worse, not better, because a narrow
#     pointed lens is the canonical eye shape. An eye reads as tracking and surveillance, which is
#     the opposite of what this app promises ("nothing phoned home"). Killed after four proportions.
#   · ANY INTERIOR DETAIL on the screens — a bezel, a chin, or (tried last) a hollow "slot" on the
#     screen the window left plus a solid chip where it landed, drawn only at 48 and 256 so small
#     sizes would degrade cleanly. It told the card's story on paper. On screen both marks read as
#     the same dark bar — a MINUS SIGN — so the screens turned into buttons with a dash through them,
#     and the hollow-versus-solid distinction that carried the whole idea transmitted nothing. The
#     two colours are the mark; anything drawn inside them is subtracted from it.
#   · dropping the tile so the colours fill more of the box — better at 16px on a dark taskbar, but
#     Beam and Live are both mid-lightness, so on a *light* taskbar the mark loses its ground and
#     floats with almost no contrast. The tile is contrast insurance, not decoration.

# Small sizes get less: at 16px a hairline and a 9-unit cut are sub-pixel mush, and the pair needs to
# be bigger relative to the box to survive. Values are (scale, gap, tile_stroke).
DETAIL = {16: (1.14, 6, False), 32: (1.10, 7, False), 48: (1.04, 8, True), 256: (1.00, 9, True)}


def _rr(draw, box, radius, fill=None, outline=None, width=1):
    draw.rounded_rectangle(box, radius=radius, fill=fill, outline=outline, width=width)


def _screen_box(spec, scale: float):
    """(cx, cy, w, h) -> [x0, y0, x1, y1], grown about the tile centre. Small sizes push the pair
    outward so the two colours keep enough pixels to stay distinguishable at 16px."""
    cx, cy, w, h = spec
    c = GEOMETRY["box"] / 2
    cx, cy = c + (cx - c) * scale, c + (cy - c) * scale
    w, h = w * scale, h * scale
    return [cx - w / 2, cy - h / 2, cx + w / 2, cy + h / 2]


def _cut(box, gap: float):
    """The tile-coloured halo drawn under the front screen."""
    return [box[0] - gap, box[1] - gap, box[2] + gap, box[3] + gap]


def render_icon(size: int, supersample: int = 8) -> Image.Image:
    """Draws the mark at `size`. Antialiasing comes from drawing big and downsampling — Pillow has no
    antialiased shape rasteriser of its own, so a 1:1 draw would come out with stairstepped corners."""
    g = GEOMETRY
    scale, gap, stroke = DETAIL[size]
    s = supersample * size / g["box"]                 # units -> supersampled pixels
    dim = size * supersample
    img = Image.new("RGBA", (dim, dim), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)

    _rr(d, [0, 0, g["box"] * s, g["box"] * s], g["tile_radius"] * s,
        fill=PALETTE["tile"],
        outline=PALETTE["edge"] if stroke else None,
        width=max(1, int(2 * s)))

    r = g["screen_radius"] * scale * s
    _rr(d, [v * s for v in _screen_box(g["beam"], scale)], r, fill=PALETTE["beam"])

    # The front screen is cut out of the back one by a tile-coloured stroke drawn *under* it, so the
    # overlap reads as depth rather than as two shapes that happen to touch.
    front = [v * s for v in _screen_box(g["live"], scale)]
    halo = gap * scale * s
    _rr(d, _cut(front, halo), r + halo, fill=PALETTE["tile"])
    _rr(d, front, r, fill=PALETTE["live"])

    return img.resize((size, size), Image.LANCZOS)


def write_svg(path: pathlib.Path) -> None:
    g, p = GEOMETRY, PALETTE

    def rect(box, r, **attrs):
        x0, y0, x1, y1 = box
        extra = "".join(f' {k.replace("_", "-")}="{v}"' for k, v in attrs.items())
        return (f'<rect x="{x0:.0f}" y="{y0:.0f}" width="{x1 - x0:.0f}" height="{y1 - y0:.0f}" '
                f'rx="{r}"{extra}/>')

    path.write_text(
        f'''<svg xmlns="http://www.w3.org/2000/svg" width="{g["box"]}" height="{g["box"]}"
     viewBox="0 0 {g["box"]} {g["box"]}" role="img" aria-label="WindowShuttle">
  <!-- Generated by tools/branding/make_branding.py — edit the constants there, not this file. -->
  <rect x="1" y="1" width="{g["box"] - 2}" height="{g["box"] - 2}" rx="{g["tile_radius"]}"
        fill="{p["tile"]}" stroke="{p["edge"]}" stroke-width="2"/>
  <!-- Beam = the primary, drawn bigger because that is how large it looks on the desk. -->
  {rect(_screen_box(g["beam"], 1.0), g["screen_radius"], fill=p["beam"])}
  <!-- Live = the screen the cursor is on, cut out of the one behind it so the pair reads as depth. -->
  {rect(_cut(_screen_box(g["live"], 1.0), g["gap"]), g["screen_radius"] + g["gap"], fill=p["tile"])}
  {rect(_screen_box(g["live"], 1.0), g["screen_radius"], fill=p["live"])}
</svg>
''', encoding="utf-8")


# ── Social card ─────────────────────────────────────────────────────────────────────────────────
# A different job from the README screenshot, so a different picture: the reader is scrolling a feed
# at thumbnail size and deciding whether to click, which means big shapes and big type only.
#
# The picture is the app's own monitor map — the product's real face, not an abstraction of it. What
# changed from the version this card started as is the TYPE, and the reason is what survives 300px:
# set in Segoe UI Bold the biggest thing on the card was the product NAME, and a name means nothing
# to someone who has never heard of it, while the tagline under it dissolved. Leading with the claim
# in a display face, at more than twice the tagline's old size, puts "what does it do" in the one
# place a feed reader actually looks. Whatever the headline says, it must be the same feature the
# picture shows — see the note above the Chinese line.
FONTS = pathlib.Path("C:/Windows/Fonts")


def font(name: str, size: int, index: int = 0) -> ImageFont.FreeTypeFont:
    return ImageFont.truetype(str(FONTS / name), size, index=index)


def _swap_arrows(d: ImageDraw.ImageDraw, cx: float, cy: float, out_col: str, back_col: str,
                 width: float, gap: float, thick: float) -> None:
    """Two opposed arrows — the exchange marker. Vector, so it cannot fall back to a missing glyph.

    Each arrow takes the colour of the screen it leaves: the one pointing right carries the primary's
    windows away, the one pointing left brings the other screen's back. A single colour for both (the
    earlier version) credited the whole exchange to one of the two screens, which is half a lie about
    a mark whose entire job is to say that both sides move."""
    half, head = width / 2, width * 9 / 46
    for sign, dy, color in ((1, -gap / 2, out_col), (-1, gap / 2, back_col)):
        y = cy + dy
        tip = cx + sign * half
        d.line([(cx - sign * half, y), (tip - sign * head * 0.6, y)], fill=color, width=int(thick))
        d.polygon([(tip, y), (tip - sign * head, y - head * 0.72), (tip - sign * head, y + head * 0.72)],
                  fill=color)


def render_social_card(w: int = 1280, h: int = 640, supersample: int = 2) -> Image.Image:
    p = PALETTE
    W, H = w * supersample, h * supersample
    img = Image.new("RGB", (W, H), p["room"])
    d = ImageDraw.Draw(img)

    def S(v):
        return v * supersample

    # ONE measure, 88 to 1192, and every element is set to it: the wordmark row, the headline, the
    # Chinese line and the monitor strip all begin at the same left edge and end at the same right
    # edge. The version before this had the type left-aligned and the strip centred, which is two
    # layouts stacked rather than one composition — there was no single left edge for the eye to
    # find, and a dead quadrant opened up to the right of the short headline.
    LEFT, RIGHT = 88, 1192
    MEASURE = RIGHT - LEFT

    # Faint top-down lift so the card is not a flat black rectangle in a feed of flat black rectangles.
    for y in range(H):
        t = (y / H) ** 1.4
        d.line([(0, y), (W, y)], fill=tuple(int(a + (b - a) * t) for a, b in
                                            zip((0x15, 0x17, 0x20), (0x08, 0x09, 0x0D))))

    # Bahnschrift is Windows' own DIN descendant, and DIN is signage type — the lettering of
    # directional wayfinding. This product is about which screen a window goes to, so the display
    # face is saying the same thing as the picture, and its condensed grotesque forms are built for
    # exactly this kind of full-measure banner setting. It ships on every Win10/11 machine, which
    # keeps this generator dependency-free. Segoe UI Bold is the face anyone would reach for by
    # default; that is why it is not the one. Consolas carries the data line — the app has a CLI, so
    # the mono voice is true rather than decorative.
    wordmark = font("bahnschrift.ttf", S(54))
    zh = font("msyh.ttc", S(26))
    meta = font("consola.ttf", S(21))

    # The headline is set TO the measure rather than at a fixed size: measure the string once, then
    # scale so it spans the column exactly. The clamp is the guard — copy half this length would
    # otherwise produce absurd type, and a card generator that can render absurd type eventually
    # will. Bahnschrift is a variable-width face, so this has to be measured, not calculated.
    HEADLINE = "Stop dragging windows across monitors"
    probe = S(100)
    fitted = int(probe * S(MEASURE) / d.textlength(HEADLINE, font=font("bahnschrift.ttf", probe)))
    head = font("bahnschrift.ttf", max(S(64), min(fitted, S(108))))

    mark = render_icon(256).resize((S(74), S(74)), Image.LANCZOS)
    img.paste(mark, (S(LEFT), S(62)), mark)
    d.text((S(LEFT + 96), S(66)), "WindowShuttle", font=wordmark, fill=p["ink"])

    # Top right rather than a footer: it closes the head of the card against the mark on the left,
    # and leaves the whole lower half to the picture.
    strip_txt = "Windows 10/11  ·  single exe  ·  no install  ·  MIT"
    d.text((S(RIGHT) - d.textlength(strip_txt, font=meta), S(94)), strip_txt, font=meta,
           fill=p["faint"])

    d.text((S(LEFT), S(170)), HEADLINE, font=head, fill=p["ink"])

    # The Chinese line carries the mechanism the English headline leaves out, so a bilingual reader
    # gets claim + how, and a reader of either language alone still gets one complete thought.
    #
    # Headline and picture have to agree. An earlier pass paired "Flick a window to the next screen"
    # with this strip and they described different things — the flick moves ONE window in a direction,
    # while the marker between screens 1 and 3 says those two exchange everything they hold.
    #
    # The headline is now the *problem* rather than one of the seven actions, which is what lets it sit
    # over this picture honestly: an exchange between two screens is one way of not dragging, and so is
    # every other action in the app. Naming the action was also just too narrow — the whole-screen swap
    # is one seventh of what this does, and it had quietly taken over the one-liner, the positioning
    # paragraph and the repo description as well.
    d.text((S(LEFT + 2), S(292)), "划一下，或按个快捷键——窗口自己过去", font=zh, fill=p["dim"])

    # The strip is the app's own monitor map, drawn to the same rules: each screen sized to how large
    # it looks on the desk, the primary outlined in Beam, the cursor's screen in Live. Showing the
    # product's real picture beats an abstract hero. Screens are ordered so the swapping pair (the
    # primary and the cursor's) end up adjacent — an exchange marker straddling an uninvolved screen
    # would be a lie about which two move.
    #
    # Widths are relative and then scaled to the measure, so the strip cannot drift out of alignment
    # with the type when any one screen is retuned.
    BASE = [("2", 250, 156, p["edge"], 2, None), ("1", 316, 178, p["beam"], 3, "Primary"),
            ("3", 236, 133, p["live"], 3, None)]
    GAP, CHANNEL = 40, 76           # CHANNEL = the column the exchange marker sits in
    k = MEASURE / (sum(b[1] for b in BASE) + GAP * (len(BASE) - 1) + CHANNEL)
    band_h, y0 = 178 * k, 384

    num = font("bahnschrift.ttf", int(S(44 * k)))       # screen numbers, same face as the headline
    badge_f = font("bahnschrift.ttf", int(S(20 * k)))

    x = LEFT
    edges = []
    for label, bw_, bh_, border, stroke, badge in BASE:
        sw, sh = bw_ * k, bh_ * k
        y = y0 + (band_h - sh) / 2
        _rr(d, [S(x), S(y), S(x + sw), S(y + sh)], S(13 * k),
            fill=p["panel"], outline=border, width=int(S(stroke)))
        d.text((S(x + 20 * k), S(y + 16 * k)), label, font=num,
               fill=p["ink"] if border == p["edge"] else border)
        if badge:
            tw = d.textlength(badge, font=badge_f) + S(22 * k)
            _rr(d, [S(x + sw) - tw - S(14 * k), S(y + 14 * k),
                    S(x + sw) - S(14 * k), S(y + 14 * k) + S(28 * k)],
                S(14 * k), outline=border, width=int(S(2 * k)))
            d.text((S(x + sw) - tw - S(3 * k), S(y + 16 * k)), badge, font=badge_f, fill=border)
        # Stand-in window rows, clamped to what each screen can actually hold: an early pass laid out
        # a fixed three rows and the shortest screen's rows ran straight out through its bottom edge.
        top, row, pad = y + 62 * k, 28 * k, 16 * k
        for i, cw in enumerate((sw - 96 * k, sw - 124 * k, sw - 150 * k)):
            if cw > 30 * k and top + i * row + 22 * k <= y + sh - pad:
                _rr(d, [S(x + 78 * k), S(top + i * row), S(x + 78 * k + cw), S(top + i * row + 22 * k)],
                    S(6 * k), fill=p["room"])
        edges.append(x + sw)
        x += sw + GAP * k + (CHANNEL * k if label == "1" else 0)

    # The exchange marker, and the one place this card spends its boldness. It is the entire claim —
    # three static monitors say "multi-monitor tool", these say "those two trade places" — and in the
    # version before this it was 46px wide on a 1280px card, about eleven pixels at the size a feed
    # actually renders. Everything else was bigger than the only thing that mattered.
    #
    # Drawn, not typed: U+21C4 (⇄) is absent from Segoe UI and came out as a tofu box. Same rule as
    # the mouse glyph inside the app — a mark this load-bearing cannot depend on a font having a
    # codepoint, because when it does not there is no fallback, just a blank rectangle.
    _swap_arrows(d, S(edges[1] + (GAP + CHANNEL) * k / 2), S(y0 + band_h / 2),
                 out_col=p["beam"], back_col=p["live"],
                 width=S(84 * k), gap=S(26 * k), thick=S(6 * k))

    return img.resize((w, h), Image.LANCZOS)


def main() -> None:
    OUT.mkdir(parents=True, exist_ok=True)
    write_svg(OUT / "windowshuttle.svg")

    sizes = sorted(DETAIL)
    frames = [render_icon(s) for s in sizes]
    # Pillow writes one entry per size from a single image by downscaling; passing the per-size
    # artwork explicitly via append_images keeps the simplified 16/32 versions instead.
    frames[-1].save(OUT / "windowshuttle.ico", format="ICO",
                    sizes=[(s, s) for s in sizes], append_images=frames[:-1])
    frames[-1].save(OUT / "logo-256.png")
    render_social_card().save(OUT / "social-card.png")

    for f in ("windowshuttle.svg", "windowshuttle.ico", "logo-256.png", "social-card.png"):
        print(f"  {f:18} {(OUT / f).stat().st_size / 1024:7.1f} KB")


if __name__ == "__main__":
    main()
