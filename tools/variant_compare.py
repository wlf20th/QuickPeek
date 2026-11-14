"""三个镜片内容方案横向对比，挑一个观感最好的。"""
from PIL import Image, ImageDraw
import os
import make_icon as M

S = M.S


def glyph_two_lines():
    g = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    d = ImageDraw.Draw(g)
    cx, cy, r = S * 0.445, S * 0.425, S * 0.268
    ring = S * 0.079
    tail = S * 0.096
    p0 = (cx + r * 0.70, cy + r * 0.70)
    p1 = (S * 0.795, S * 0.775)
    d.line([p0, p1], fill=(255, 255, 255, 255), width=int(tail), joint="curve")
    for p in (p0, p1):
        d.ellipse([p[0] - tail / 2, p[1] - tail / 2, p[0] + tail / 2, p[1] + tail / 2],
                  fill=(255, 255, 255, 255))
    d.ellipse([cx - r, cy - r, cx + r, cy + r], fill=(255, 255, 255, 255))
    d.ellipse([cx - r + ring, cy - r + ring, cx + r - ring, cy + r - ring], fill=(0, 0, 0, 0))

    inner = r - ring
    x0 = cx - inner * 0.55
    # 第一行：粗、长
    h1 = inner * 0.26
    y1 = cy - inner * 0.22
    d.rounded_rectangle([x0, y1 - h1 / 2, x0 + inner * 1.02, y1 + h1 / 2],
                        radius=h1 / 2, fill=(255, 255, 255, 255))
    # 第二行：细、短、半透明
    h2 = inner * 0.17
    y2 = cy + inner * 0.34
    d.rounded_rectangle([x0, y2 - h2 / 2, x0 + inner * 0.56, y2 + h2 / 2],
                        radius=h2 / 2, fill=(255, 255, 255, 150))
    return g


def glyph_clean():
    g = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    d = ImageDraw.Draw(g)
    cx, cy, r = S * 0.445, S * 0.425, S * 0.268
    ring = S * 0.082
    tail = S * 0.098
    p0 = (cx + r * 0.70, cy + r * 0.70)
    p1 = (S * 0.795, S * 0.775)
    d.line([p0, p1], fill=(255, 255, 255, 255), width=int(tail), joint="curve")
    for p in (p0, p1):
        d.ellipse([p[0] - tail / 2, p[1] - tail / 2, p[0] + tail / 2, p[1] + tail / 2],
                  fill=(255, 255, 255, 255))
    d.ellipse([cx - r, cy - r, cx + r, cy + r], fill=(255, 255, 255, 255))
    d.ellipse([cx - r + ring, cy - r + ring, cx + r - ring, cy + r - ring], fill=(0, 0, 0, 0))
    return g


def glyph_caret():
    """镜片里放一根文字光标 + 下面一条高亮条，强调"划词"。"""
    g = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    d = ImageDraw.Draw(g)
    cx, cy, r = S * 0.445, S * 0.425, S * 0.268
    ring = S * 0.079
    tail = S * 0.096
    p0 = (cx + r * 0.70, cy + r * 0.70)
    p1 = (S * 0.795, S * 0.775)
    d.line([p0, p1], fill=(255, 255, 255, 255), width=int(tail), joint="curve")
    for p in (p0, p1):
        d.ellipse([p[0] - tail / 2, p[1] - tail / 2, p[0] + tail / 2, p[1] + tail / 2],
                  fill=(255, 255, 255, 255))
    d.ellipse([cx - r, cy - r, cx + r, cy + r], fill=(255, 255, 255, 255))
    d.ellipse([cx - r + ring, cy - r + ring, cx + r - ring, cy + r - ring], fill=(0, 0, 0, 0))

    inner = r - ring
    # 高亮条（被选中的行）
    hb = inner * 0.34
    d.rounded_rectangle([cx - inner * 0.62, cy + inner * 0.06,
                         cx + inner * 0.40, cy + inner * 0.06 + hb],
                        radius=hb / 4, fill=(255, 255, 255, 120))
    # 文字光标 I-beam
    cw = inner * 0.10
    ch = inner * 0.78
    d.rounded_rectangle([cx - cw / 2, cy - ch / 2, cx + cw / 2, cy + ch / 2],
                        radius=cw / 2, fill=(255, 255, 255, 255))
    for dy in (-ch / 2, ch / 2):
        d.rounded_rectangle([cx - cw * 1.7, cy + dy - cw * 0.18,
                             cx + cw * 1.7, cy + dy + cw * 0.18], radius=cw * 0.18,
                            fill=(255, 255, 255, 255))
    return g


variants = [("A 两行文字", glyph_two_lines), ("B 纯放大镜", glyph_clean), ("C 光标+选中", glyph_caret)]

sizes = [256, 64, 48, 32, 16]
cell, pad = 150, 26
W = pad + len(variants) * (cell + 200)
H = pad + len(sizes) * (cell + 14) + pad
sheet = Image.new("RGB", (W, H), (28, 28, 30))
dsheet = ImageDraw.Draw(sheet)

for col, (name, fn) in enumerate(variants):
    icon = Image.alpha_composite(M.build_background(), fn())
    icon = Image.alpha_composite(icon, Image.new("RGBA", (S, S), (0, 0, 0, 0)))
    x = pad + col * (cell + 200)
    dsheet.text((x, 6), name, fill=(230, 230, 230))
    for row, s in enumerate(sizes):
        fr = icon.resize((s, s), Image.LANCZOS)
        view = fr.resize((cell, cell), Image.NEAREST if s <= 32 else Image.LANCZOS)
        sheet.paste(view, (x, pad + 14 + row * (cell + 14)), view)

sheet.save(os.path.join(M.OUT, "compare.png"))
print("saved compare.png", sheet.size)
