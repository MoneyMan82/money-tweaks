"""Money Tweaks logo system generator.

The symbol is defined once as polygons on a 100-unit grid; the same geometry is written to SVG (vector) and rasterised
with Pillow (4x supersampled) for PNG/ICO, so every export matches.

    python branding/make_logo.py preview   -> branding/preview.png (all variants at real sizes, dark and light)
    python branding/make_logo.py build     -> writes the SVG/PNG/ICO assets for branding/, the app and the website

Symbol: an "M" whose top is a separate crossbar and whose centre V runs down into a stem, so the same shape reads as M
and T. Thin machined gaps keep the parts apart (precision); the whole mark leans forward and the crossbar's leading end
is cut at an angle (speed). Small sizes use a heavier cut with wider gaps so it stays crisp at 16 to 32 px.
"""
import sys
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

ROOT = Path(__file__).resolve().parent.parent
OUT = ROOT / 'branding'

ACCENT_A, ACCENT_B = '#4C8DFF', '#8B6CFF'   # the app's accent pair
LIGHT = '#E9EEF7'                           # the app's text colour
TILE_TOP, TILE_BOTTOM, TILE_RIM = '#141A28', '#0A0D14', '#26304A'
SLANT = 0.16                                # forward lean (x shift per unit of height)


def geometry(small=False):
    """(crossbar polygons, body polygons) on a 100x100 grid, before the slant."""
    g = 6 if small else 4            # machined gap
    w = 15 if small else 13          # stem weight
    top, bar_h, bottom = 16, (13 if small else 11), 84
    y0 = top + bar_h + g
    l0, r1 = 10, 90
    bar = [[(l0, top), (r1 + 3, top), (r1 - 3, top + bar_h), (l0, top + bar_h)]]
    s = (w + 1) / 2                                      # half stem width
    vy = 62                                              # where the V meets the stem
    notch = 47 if not small else 50                      # inner apex of the V
    d = 14 if not small else 17                          # where each diagonal's underside meets the leg
    # One M outline: legs, diagonals meeting in the centre, and the centre running down as a stem (the T).
    body = [[(l0, bottom), (l0, y0), (l0 + w + 2, y0), (50, notch), (r1 - w - 2, y0), (r1, y0), (r1, bottom), (r1 - w, bottom),
             (r1 - w, y0 + d), (50 + s, vy), (50 + s, bottom), (50 - s, bottom), (50 - s, vy), (l0 + w, y0 + d), (l0 + w, bottom)]]
    return bar, body


def slant(poly):
    return [(x + (50 - y) * SLANT, y) for x, y in poly]


def fit(polys_list, box):
    """Scale/centre the slanted mark into box = (x, y, size)."""
    pts = [p for polys in polys_list for poly in polys for p in slant(poly)]
    minx, maxx = min(p[0] for p in pts), max(p[0] for p in pts)
    miny, maxy = min(p[1] for p in pts), max(p[1] for p in pts)
    bx, by, size = box
    k = size / max(maxx - minx, maxy - miny)
    ox = bx + (size - (maxx - minx) * k) / 2 - minx * k
    oy = by + (size - (maxy - miny) * k) / 2 - miny * k
    return [[[(ox + x * k, oy + y * k) for x, y in slant(poly)] for poly in polys] for polys in polys_list]


# ---- SVG -------------------------------------------------------------------------------------------------------------

def svg_poly(poly, fill):
    return '<polygon fill="%s" points="%s"/>' % (fill, ' '.join(f'{x:.2f},{y:.2f}' for x, y in poly))


def svg_mark(box, mono=None, small=False, grad_id='mt-accent', body=LIGHT):
    bar, body_p = fit(geometry(small), box)
    bar_fill = mono or f'url(#{grad_id})'
    body_fill = mono or body
    return '\n  '.join([svg_poly(p, bar_fill) for p in bar] + [svg_poly(p, body_fill) for p in body_p])


def grad_def(grad_id='mt-accent'):
    return (f'<linearGradient id="{grad_id}" x1="0" y1="0" x2="1" y2="0">'
            f'<stop offset="0" stop-color="{ACCENT_A}"/><stop offset="1" stop-color="{ACCENT_B}"/></linearGradient>')


def svg_doc(w, h, body, defs='', title='Money Tweaks'):
    return (f'<svg xmlns="http://www.w3.org/2000/svg" width="{w}" height="{h}" viewBox="0 0 {w} {h}" role="img" aria-label="{title}">\n'
            f'  <title>{title}</title>\n  <defs>{defs}</defs>\n  {body}\n</svg>\n')


def svg_tile(size=1024, small=False):
    r = size * 0.225
    inset = size * 0.04
    defs = grad_def() + (f'<linearGradient id="mt-tile" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="{TILE_TOP}"/>'
                         f'<stop offset="1" stop-color="{TILE_BOTTOM}"/></linearGradient>')
    s = size - 2 * inset
    body = (f'<rect x="{inset}" y="{inset}" width="{s}" height="{s}" rx="{r}" fill="url(#mt-tile)" stroke="{TILE_RIM}" stroke-width="{size * 0.012:.1f}"/>\n  '
            + svg_mark((size * 0.2, size * 0.2, size * 0.6), small=small))
    return svg_doc(size, size, body, defs)


def svg_lockup(mono=None, text_color=LIGHT):
    h, w = 200, 980
    defs = grad_def()
    mark = svg_mark((10, 20, 160), mono=mono, body=text_color)
    font = "font-family=\"Bahnschrift, 'Segoe UI', 'DIN Alternate', Arial, sans-serif\" font-weight=\"600\" letter-spacing=\"6\""
    accent = mono or 'url(#mt-accent)'
    text = (f'<text x="210" y="128" font-size="84" {font} fill="{mono or text_color}">MONEY'
            f'<tspan dx="26" fill="{accent}">TWEAKS</tspan></text>')
    return svg_doc(w, h, mark + '\n  ' + text, defs)


# ---- raster ----------------------------------------------------------------------------------------------------------

def hexc(h, a=255):
    h = h.lstrip('#')
    return (int(h[0:2], 16), int(h[2:4], 16), int(h[4:6], 16), a)


def hgrad(size, c0, c1, x0, x1, vertical=False):
    """Two-stop gradient image (horizontal from x0 to x1, or vertical)."""
    w, h = size
    strip = Image.new('RGBA', (w if not vertical else 1, h if vertical else 1))
    n = h if vertical else w
    a, b = hexc(c0), hexc(c1)
    for i in range(n):
        t = min(1, max(0, (i - x0) / max(1, x1 - x0)))
        c = tuple(int(a[k] + (b[k] - a[k]) * t) for k in range(4))
        strip.putpixel((0, i) if vertical else (i, 0), c)
    return strip.resize(size)


def raster_mark(img, box, mono=None, small=False, S=4, body=LIGHT):
    """Draws the mark onto img (RGBA) inside box, supersampled."""
    W, H = img.size
    big = Image.new('RGBA', (W * S, H * S), (0, 0, 0, 0))
    bx, by, size = box
    bar, body_p = fit(geometry(small), (bx * S, by * S, size * S))
    mask_bar = Image.new('L', big.size, 0)
    for p in bar: ImageDraw.Draw(mask_bar).polygon(p, fill=255)
    mask_body = Image.new('L', big.size, 0)
    for p in body_p: ImageDraw.Draw(mask_body).polygon(p, fill=255)
    xs = [x for p in bar for x, _ in p]
    bar_fill = Image.new('RGBA', big.size, hexc(mono)) if mono else hgrad(big.size, ACCENT_A, ACCENT_B, min(xs), max(xs))
    big.paste(bar_fill, (0, 0), mask_bar)
    big.paste(Image.new('RGBA', big.size, hexc(mono or body)), (0, 0), mask_body)
    small_img = big.resize((W, H), Image.LANCZOS)
    img.alpha_composite(small_img)


def raster_tile(size, small=None, S=4):
    if small is None: small = size <= 32
    big = Image.new('RGBA', (size * S, size * S), (0, 0, 0, 0))
    inset, r = size * S * 0.04, size * S * 0.225
    mask = Image.new('L', big.size, 0)
    ImageDraw.Draw(mask).rounded_rectangle([inset, inset, size * S - inset, size * S - inset], radius=r, fill=255)
    big.paste(hgrad(big.size, TILE_TOP, TILE_BOTTOM, 0, size * S, vertical=True), (0, 0), mask)
    rim = Image.new('L', big.size, 0)
    ImageDraw.Draw(rim).rounded_rectangle([inset, inset, size * S - inset, size * S - inset], radius=r, outline=255, width=max(S, int(size * S * 0.012)))
    big.paste(Image.new('RGBA', big.size, hexc(TILE_RIM)), (0, 0), rim)
    tile = big.resize((size, size), Image.LANCZOS)
    m = 0.19 if size > 32 else 0.15          # small icons: a bigger mark
    raster_mark(tile, (size * m, size * m, size * (1 - 2 * m)), small=small)
    return tile


def raster_lockup(height, dark=True, mono=None):
    k = height / 200
    img = Image.new('RGBA', (int(980 * k), height), (0, 0, 0, 0))
    raster_mark(img, (10 * k, 20 * k, 160 * k), mono=mono, body=LIGHT if dark else '#111827')
    S = 4
    big = Image.new('RGBA', (img.width * S, img.height * S), (0, 0, 0, 0))
    font = ImageFont.truetype('C:/Windows/Fonts/bahnschrift.ttf', int(84 * k * S))
    font.set_variation_by_name('SemiBold')
    d = ImageDraw.Draw(big)
    x = 210 * k * S
    text_col = hexc(mono or (LIGHT if dark else '#111827'))
    for ch in 'MONEY':
        d.text((x, 128 * k * S), ch, font=font, fill=text_col, anchor='ls'); x += font.getlength(ch) + 6 * k * S
    x += 26 * k * S - 6 * k * S + 6 * k * S
    start = x
    tmask = Image.new('L', big.size, 0)
    td = ImageDraw.Draw(tmask)
    for ch in 'TWEAKS':
        td.text((x, 128 * k * S), ch, font=font, fill=255, anchor='ls'); x += font.getlength(ch) + 6 * k * S
    fill = Image.new('RGBA', big.size, hexc(mono)) if mono else hgrad(big.size, ACCENT_A, ACCENT_B, start, x)
    big.paste(fill, (0, 0), tmask)
    img.alpha_composite(big.resize(img.size, Image.LANCZOS))
    return img.crop((0, 0, int(x / S + 12 * k), height))


# ---- outputs ---------------------------------------------------------------------------------------------------------

def preview():
    W, H = 1400, 760
    out = Image.new('RGBA', (W, H), hexc('0B0E14'))
    d = ImageDraw.Draw(out)
    f = ImageFont.truetype('C:/Windows/Fonts/segoeui.ttf', 18)
    out.alpha_composite(raster_tile(320), (30, 30))
    mk = Image.new('RGBA', (320, 320)); raster_mark(mk, (20, 20, 280)); out.alpha_composite(mk, (380, 30))
    mono = Image.new('RGBA', (320, 320), hexc('F3F4F6')); raster_mark(mono, (20, 20, 280), mono='#111827'); out.alpha_composite(mono, (730, 30))
    out.alpha_composite(raster_lockup(120), (30, 380))
    light = Image.new('RGBA', (700, 160), hexc('F3F4F6')); light.alpha_composite(raster_lockup(100, dark=False), (20, 30)); out.alpha_composite(light, (30, 530))
    x = 1080
    for s in (256, 64, 48, 32, 24, 16):
        if s == 256: continue
        out.alpha_composite(raster_tile(s), (x, 40 + [64, 48, 32, 24, 16].index(s) * 90))
        lt = Image.new('RGBA', (s + 20, s + 20), hexc('F3F4F6')); lt.alpha_composite(raster_tile(s), (10, 10)); out.alpha_composite(lt, (x + 110, 30 + [64, 48, 32, 24, 16].index(s) * 90))
        d.text((x + 220, 40 + [64, 48, 32, 24, 16].index(s) * 90), f'{s}px', font=f, fill=hexc('8A94A8'))
    for i, s in enumerate((32, 16)):  # mark alone (favicon-like) at real size
        m = Image.new('RGBA', (s, s)); raster_mark(m, (0, 0, s), small=True); out.alpha_composite(m, (1100 + i * 60, 520))
    out.save(OUT / 'preview.png')
    print('wrote preview.png')


def ico(path, sizes):
    imgs = [raster_tile(s) for s in sizes]
    # BMP entries: PNG-compressed ICO entries made a "Bad IL format" assembly before (see PROGRESS.md).
    imgs[-1].save(path, format='ICO', bitmap_format='bmp', sizes=[(s, s) for s in sizes], append_images=imgs[:-1])


def build():
    # Vector sources.
    (OUT / 'mark.svg').write_text(svg_doc(100, 100, svg_mark((4, 4, 92)), grad_def()), encoding='utf-8')
    (OUT / 'mark-mono.svg').write_text(svg_doc(100, 100, svg_mark((4, 4, 92), mono='currentColor')).replace('<svg ', '<svg fill="currentColor" ', 1), encoding='utf-8')
    (OUT / 'app-icon.svg').write_text(svg_tile(), encoding='utf-8')
    (OUT / 'lockup.svg').write_text(svg_lockup(), encoding='utf-8')
    (OUT / 'lockup-light-bg.svg').write_text(svg_lockup(text_color='#111827'), encoding='utf-8')
    (OUT / 'lockup-mono.svg').write_text(svg_lockup(mono='currentColor'), encoding='utf-8')
    # High-resolution PNGs.
    raster_tile(1024).save(OUT / 'logo-1024.png')
    raster_tile(1024).save(OUT / 'app-icon-1024.png')
    m = Image.new('RGBA', (1024, 1024)); raster_mark(m, (40, 40, 944)); m.save(OUT / 'mark-1024.png')
    for name, col in (('mark-mono-white-1024.png', '#FFFFFF'), ('mark-mono-black-1024.png', '#000000')):
        m = Image.new('RGBA', (1024, 1024)); raster_mark(m, (40, 40, 944), mono=col); m.save(OUT / name)
    raster_lockup(400).save(OUT / 'lockup-dark-bg.png')
    raster_lockup(400, dark=False).save(OUT / 'lockup-light-bg.png')
    raster_lockup(400, mono='#FFFFFF').save(OUT / 'lockup-mono-white.png')
    raster_lockup(400, mono='#000000').save(OUT / 'lockup-mono-black.png')
    # App.
    raster_tile(256).save(ROOT / 'src' / 'MoneyTweaks' / 'Assets' / 'logo.png')
    ico(ROOT / 'src' / 'MoneyTweaks' / 'Assets' / 'MoneyTweaks.ico', [16, 24, 32, 48, 64, 128, 256])
    # Website.
    web = ROOT / 'docs' / 'assets'
    raster_tile(512).save(web / 'logo-512.png')
    raster_tile(64).save(web / 'favicon-64.png')
    raster_tile(180).save(web / 'apple-touch-icon.png')
    ico(web / 'favicon.ico', [16, 32, 48])
    (web / 'favicon.svg').write_text(svg_tile(64, small=True), encoding='utf-8')
    (web / 'logo.svg').write_text(svg_tile(), encoding='utf-8')
    social(web / 'social-card.png')
    print('built')


def social(path):
    W, H = 1200, 630
    img = Image.new('RGBA', (W, H), hexc('07090D'))
    grid = Image.new('RGBA', (W, H), (0, 0, 0, 0))
    gd = ImageDraw.Draw(grid)
    for i in range(0, W, 40): gd.line([(i, 0), (i, H)], fill=hexc('FFFFFF', 10))
    for i in range(0, H, 40): gd.line([(0, i), (W, i)], fill=hexc('FFFFFF', 10))
    img.alpha_composite(grid)
    d = ImageDraw.Draw(img)
    img.alpha_composite(raster_tile(300), (90, 165))
    lock = raster_lockup(150)
    word = lock.crop((int(210 * 150 / 200) - 4, 0, lock.width, lock.height))
    img.alpha_composite(word, (430, 170))
    f1 = ImageFont.truetype('C:/Windows/Fonts/segoeui.ttf', 34)
    d.text((440, 330), 'Free Windows 10/11 performance tweaker', font=f1, fill=hexc('6FA0FF'))
    d.text((440, 385), 'Reversible \u2022 Open source \u2022 No ads', font=f1, fill=hexc('8A94A8'))
    img.convert('RGB').save(path)


if __name__ == '__main__':
    {'preview': preview, 'build': build}[sys.argv[1] if len(sys.argv) > 1 else 'preview']()
