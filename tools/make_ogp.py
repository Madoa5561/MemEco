"""MemEco OGP 画像（1200x630）を生成する。

site/assets/ogp.png を出力。Pillow のみ使用。
"""
from __future__ import annotations

import math
from pathlib import Path

from PIL import Image, ImageDraw, ImageFilter, ImageFont

ROOT = Path(__file__).resolve().parents[1]
OUT = ROOT / "site" / "assets" / "ogp.png"
ICON = ROOT / "assets" / "icon-256.png"
SHOT = ROOT / "docs" / "preview.png"

W, H = 1200, 630
FONT_DIR = Path(r"C:\Windows\Fonts")


def font(name: str, size: int) -> ImageFont.FreeTypeFont:
    for candidate in (FONT_DIR / name, Path(name)):
        if candidate.exists():
            try:
                return ImageFont.truetype(str(candidate), size)
            except OSError:
                pass
    return ImageFont.load_default()


def gradient(size: tuple[int, int], top: tuple[int, int, int], bottom: tuple[int, int, int],
             left: tuple[int, int, int] | None = None,
             right: tuple[int, int, int] | None = None) -> Image.Image:
    w, h = size
    img = Image.new("RGB", size)
    px = img.load()
    for y in range(h):
        t = y / (h - 1)
        base = tuple(int(top[i] + (bottom[i] - top[i]) * t) for i in range(3))
        for x in range(w):
            if left and right:
                s = x / (w - 1)
                side = tuple(int(left[i] + (right[i] - left[i]) * s) for i in range(3))
                px[x, y] = tuple((base[i] + side[i]) // 2 for i in range(3))
            else:
                px[x, y] = base
    return img


def rounded_mask(size: tuple[int, int], radius: int) -> Image.Image:
    mask = Image.new("L", size, 0)
    ImageDraw.Draw(mask).rounded_rectangle([0, 0, size[0] - 1, size[1] - 1], radius=radius, fill=255)
    return mask


def add_glow(base: Image.Image, box: tuple[int, int, int, int], color: tuple[int, int, int, int],
             blur: int) -> None:
    layer = Image.new("RGBA", base.size, (0, 0, 0, 0))
    ImageDraw.Draw(layer).ellipse(box, fill=color)
    layer = layer.filter(ImageFilter.GaussianBlur(blur))
    base.alpha_composite(layer)


def main() -> None:
    canvas = gradient((W, H), (12, 20, 33), (9, 14, 23), left=(10, 18, 30), right=(15, 25, 40))
    canvas = canvas.convert("RGBA")

    # グリッド
    grid = Image.new("RGBA", (W, H), (0, 0, 0, 0))
    gd = ImageDraw.Draw(grid)
    for x in range(0, W, 72):
        gd.line([(x, 0), (x, H)], fill=(255, 255, 255, 7), width=1)
    for y in range(0, H, 72):
        gd.line([(0, y), (W, y)], fill=(255, 255, 255, 7), width=1)
    mask = Image.new("L", (W, H), 0)
    md = ImageDraw.Draw(mask)
    md.ellipse([-200, -260, 900, 520], fill=180)
    canvas.alpha_composite(Image.composite(grid, Image.new("RGBA", (W, H), (0, 0, 0, 0)), mask))

    add_glow(canvas, (560, 380, 1100, 760), (34, 211, 238, 60), 90)
    add_glow(canvas, (-120, -80, 460, 320), (74, 222, 128, 48), 100)

    # ------------------------------------------------------------ 右: スクリーンショット
    shot_h = 508
    shot_w = int(shot_h * 440 / 680)
    shot = Image.open(SHOT).convert("RGBA").resize((shot_w, shot_h), Image.LANCZOS)
    frame_pad = 10
    fx, fy = 812, 61
    fw, fh = shot_w + frame_pad * 2, shot_h + frame_pad * 2

    frame = Image.new("RGBA", (fw, fh), (0, 0, 0, 0))
    ImageDraw.Draw(frame).rounded_rectangle([0, 0, fw - 1, fh - 1], radius=22, fill=(22, 33, 53, 255),
                                            outline=(120, 160, 220, 60), width=1)
    frame.alpha_composite(shot, (frame_pad, frame_pad))
    frame.putalpha(Image.composite(rounded_mask((fw, fh), 22), Image.new("L", (fw, fh), 0), rounded_mask((fw, fh), 22)))

    # 影
    shadow = Image.new("RGBA", (W, H), (0, 0, 0, 0))
    ImageDraw.Draw(shadow).rounded_rectangle(
        [fx - 18, fy + 24, fx + fw + 24, fy + fh + 46], radius=26, fill=(0, 0, 0, 140)
    )
    shadow = shadow.filter(ImageFilter.GaussianBlur(34))
    canvas.alpha_composite(shadow)
    canvas.alpha_composite(frame, (fx, fy))

    d = ImageDraw.Draw(canvas)

    # ------------------------------------------------------------ 左: ブランド
    icon = Image.open(ICON).convert("RGBA").resize((76, 76), Image.LANCZOS)
    icon_mask = rounded_mask((76, 76), 18)
    canvas.paste(icon, (72, 118), icon_mask)

    brand_font = font("segoeuib.ttf", 62)
    sub_font = font("YuGothB.ttc", 15)
    d.text((168, 124), "MemEco", font=brand_font, fill=(233, 239, 249, 255))
    d.text((170, 194), "メモリエコ", font=sub_font, fill=(143, 163, 191, 255))

    title_font = font("YuGothB.ttc", 40)
    d.text((72, 258), "macOS 風のメモリ管理を", font=title_font, fill=(233, 239, 249, 255))
    d.text((72, 314), "Windows 11 に。", font=title_font, fill=(94, 234, 168, 255))

    desc_font = font("YuGothM.ttc", 20)
    d.text((74, 392), "入れっぱなしで、勝手に、静かに。", font=desc_font, fill=(195, 207, 226, 255))
    d.text((74, 424), "全自動メモリ管理アプリ", font=desc_font, fill=(195, 207, 226, 255))

    # バッジ
    chip_font = font("segoeuib.ttf", 17)
    x = 72
    for label, color in (("Windows 11", (143, 163, 191)), ("MIT License", (143, 163, 191)), ("v1.0.0", (74, 222, 128))):
        tw = d.textlength(label, font=chip_font)
        d.rounded_rectangle([x, 486, x + tw + 34, 522], radius=18, fill=(20, 30, 48, 230),
                            outline=(60, 80, 110, 140), width=1)
        d.text((x + 17, 492), label, font=chip_font, fill=color)
        x += tw + 34 + 12

    # フッター URL
    url_font = font("segoeuib.ttf", 16)
    d.text((74, 566), "memeco.moyashi.xyz", font=url_font, fill=(107, 125, 150, 255))

    OUT.parent.mkdir(parents=True, exist_ok=True)
    canvas.convert("RGB").save(OUT, "PNG", optimize=True)
    print("generated:", OUT, OUT.stat().st_size, "bytes")


if __name__ == "__main__":
    main()
