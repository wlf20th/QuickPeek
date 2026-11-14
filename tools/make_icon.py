"""生成 QuickPeek 应用图标。
方案：蓝紫渐变圆角底 + 白色放大镜；48px 以上在镜片里加文字光标，小尺寸保持纯净
（越小的尺寸越不能塞细节，否则一律糊成"减号"）。
输出：app.ico（多尺寸）+ preview.png（各尺寸预览）。"""
from PIL import Image, ImageDraw, ImageFilter
import os

S = 1024
OUT = os.path.dirname(os.path.abspath(__file__))
TOP = (78, 143, 255)
BOTTOM = (106, 90, 232)
RADIUS = 0.225

SIZES_DETAIL = [48, 64, 128, 256]      # 有光标细节
SIZES_PLAIN = [16, 20, 24, 32, 40]     # 纯放大镜


def vertical_gradient(size, top, bottom):
    img = Image.new("RGB", (1, size), top)
    d = ImageDraw.Draw(img)
    for y in range(size):
        t = y / max(1, size - 1)
        d.point((0, y), (
            round(top[0] + (bottom[0] - top[0]) * t),
            round(top[1] + (bottom[1] - top[1]) * t),
            round(top[2] + (bottom[2] - top[2]) * t),
        ))
    return img.resize((size, size), Image.BILINEAR)


def rounded_mask(size, radius):
    m = Image.new("L", (size, size), 0)
    ImageDraw.Draw(m).rounded_rectangle([0, 0, size - 1, size - 1], radius=radius, fill=255)
    return m


def build_background():
    mask = rounded_mask(S, int(S * RADIUS))
    bg = vertical_gradient(S, TOP, BOTTOM).convert("RGBA")
    bg.putalpha(mask)

    glow = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    ImageDraw.Draw(glow).ellipse(
        [-S * 0.30, -S * 0.72, S * 0.85, S * 0.30], fill=(255, 255, 255, 28))
    glow = glow.filter(ImageFilter.GaussianBlur(S * 0.055))
    glow.putalpha(Image.composite(glow.getchannel("A"), Image.new("L", (S, S), 0), mask))
    return Image.alpha_composite(bg, glow)


def build_glyph(detail: bool):
    g = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    d = ImageDraw.Draw(g)

    cx, cy = S * 0.445, S * 0.425
    r = S * 0.272
    ring = S * 0.077 if detail else S * 0.098
    tail = S * 0.094 if detail else S * 0.112

    p0 = (cx + r * 0.70, cy + r * 0.70)
    p1 = (S * 0.798, S * 0.778)
    d.line([p0, p1], fill=(255, 255, 255, 255), width=int(tail), joint="curve")
    for p in (p0, p1):
        d.ellipse([p[0] - tail / 2, p[1] - tail / 2, p[0] + tail / 2, p[1] + tail / 2],
                  fill=(255, 255, 255, 255))

    d.ellipse([cx - r, cy - r, cx + r, cy + r], fill=(255, 255, 255, 255))
    d.ellipse([cx - r + ring, cy - r + ring, cx + r - ring, cy + r - ring], fill=(0, 0, 0, 0))

    if not detail:
        return g

    # 镜片内：文字光标 I-beam（表示"看的是文本"）
    inner = r - ring
    cw = inner * 0.115
    ch = inner * 0.86
    serif = inner * 0.20
    d.rounded_rectangle([cx - cw / 2, cy - ch / 2, cx + cw / 2, cy + ch / 2],
                        radius=cw / 2, fill=(255, 255, 255, 255))
    for dy in (-ch / 2, ch / 2):
        d.rounded_rectangle(
            [cx - serif, cy + dy - cw * 0.20, cx + serif, cy + dy + cw * 0.20],
            radius=cw * 0.20, fill=(255, 255, 255, 255))
    return g


def compose(detail: bool):
    icon = Image.alpha_composite(build_background(), build_glyph(detail))
    edge = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    ImageDraw.Draw(edge).rounded_rectangle(
        [S * 0.004, S * 0.004, S * 0.996, S * 0.996],
        radius=int(S * (RADIUS - 0.004)), outline=(255, 255, 255, 40),
        width=max(2, int(S * 0.005)))
    return Image.alpha_composite(icon, edge)


def main():
    detail = compose(True)
    plain = compose(False)

    frames = {}
    for s in SIZES_DETAIL:
        frames[s] = detail.resize((s, s), Image.LANCZOS)
    for s in SIZES_PLAIN:
        frames[s] = plain.resize((s, s), Image.LANCZOS)

    ico = os.path.join(os.path.dirname(OUT), "app.ico")
    detail.resize((256, 256), Image.LANCZOS).save(
        ico, format="ICO", sizes=[(s, s) for s in sorted(frames)])
    detail.resize((256, 256), Image.LANCZOS).save(os.path.join(OUT, "icon_256.png"))

    order = [16, 20, 24, 32, 40, 48, 64, 128, 256]
    pad, cell = 22, 128
    W = pad + len(order) * (cell + pad)
    prev = Image.new("RGBA", (W, pad * 3 + cell * 2), (0, 0, 0, 0))
    prev.paste(Image.new("RGBA", (W, pad + cell + pad), (32, 32, 32, 255)), (0, 0))
    prev.paste(Image.new("RGBA", (W, pad + cell + pad), (243, 243, 243, 255)), (0, pad * 2 + cell))
    for i, s in enumerate(order):
        v = frames[s].resize((cell, cell), Image.NEAREST if s <= 32 else Image.LANCZOS)
        x = pad + i * (cell + pad)
        prev.alpha_composite(v, (x, pad))
        prev.alpha_composite(v, (x, pad * 2 + cell + pad))
    prev.convert("RGB").save(os.path.join(OUT, "preview.png"))

    print("ico:", ico, os.path.getsize(ico), "bytes; sizes", order)


if __name__ == "__main__":
    main()
