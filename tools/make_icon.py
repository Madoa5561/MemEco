"""MemEco アイコン生成（assets/icon.ico）。

256px で描いて 4x スーパーサンプリング → 各サイズの ICO を書き出す。
"""
from __future__ import annotations

import math
from pathlib import Path

from PIL import Image, ImageDraw, ImageFilter

ROOT = Path(__file__).resolve().parents[1]
OUT = ROOT / "assets"
OUT.mkdir(parents=True, exist_ok=True)

S = 1024  # 作業解像度


def lerp(a: float, b: float, t: float) -> float:
    return a + (b - a) * t


def gradient(size: int, top: tuple[int, int, int], bottom: tuple[int, int, int],
             left: tuple[int, int, int] | None = None,
             right: tuple[int, int, int] | None = None) -> Image.Image:
    img = Image.new("RGB", (size, size))
    px = img.load()
    for y in range(size):
        t = y / (size - 1)
        r, g, b = (int(lerp(top[i], bottom[i], t)) for i in range(3))
        for x in range(size):
            if left and right:
                h = x / (size - 1)
                r2, g2, b2 = (int(lerp(left[i], right[i], h)) for i in range(3))
                r3, g3, b3 = (r + r2) // 2, (g + g2) // 2, (b + b2) // 2
                px[x, y] = (r3, g3, b3)
            else:
                px[x, y] = (r, g, b)
    return img


def rounded_mask(size: int, radius: int) -> Image.Image:
    mask = Image.new("L", (size, size), 0)
    d = ImageDraw.Draw(mask)
    d.rounded_rectangle([0, 0, size - 1, size - 1], radius=radius, fill=255)
    return mask


def chip(size: int) -> Image.Image:
    """メモリチップ + 解放矢印。"""
    img = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    cx = cy = size / 2
    w = size * 0.46  # チップ本体の一辺
    r = size * 0.075
    x0, y0 = cx - w / 2, cy - w / 2
    x1, y1 = cx + w / 2, cy + w / 2

    # ピン（左右 3 本ずつ + 上下 2 本ずつ）
    pin_w = size * 0.052
    pin_len = size * 0.075
    pin_r = pin_w * 0.42
    for i in range(3):
        yy = cy + (i - 1) * w * 0.28
        d.rounded_rectangle([x0 - pin_len, yy - pin_w / 2, x0, yy + pin_w / 2], radius=pin_r,
                            fill=(148, 233, 255, 235))
        d.rounded_rectangle([x1, yy - pin_w / 2, x1 + pin_len, yy + pin_w / 2], radius=pin_r,
                            fill=(148, 233, 255, 235))
    for i in range(2):
        xx = cx + (i - 0.5) * w * 0.42
        d.rounded_rectangle([xx - pin_w / 2, y0 - pin_len, xx + pin_w / 2, y0], radius=pin_r,
                            fill=(148, 233, 255, 235))
        d.rounded_rectangle([xx - pin_w / 2, y1, xx + pin_w / 2, y1 + pin_len], radius=pin_r,
                            fill=(148, 233, 255, 235))

    # チップ本体（グラデーション）
    body = gradient(int(w), (52, 211, 153), (14, 165, 233), left=(52, 211, 153), right=(14, 165, 233))
    body_mask = Image.new("L", body.size, 0)
    bm = ImageDraw.Draw(body_mask)
    bm.rounded_rectangle([0, 0, body.size[0] - 1, body.size[1] - 1], radius=int(r), fill=255)
    img.paste(body, (int(x0), int(y0)), body_mask)

    # 内側の縁
    d.rounded_rectangle([x0 + size * 0.012, y0 + size * 0.012, x1 - size * 0.012, y1 - size * 0.012],
                        radius=int(r), outline=(255, 255, 255, 70), width=max(2, int(size * 0.006)))

    # 中央の「解放」下向き矢印（白）
    ah = w * 0.34
    aw = w * 0.22
    shaft = ah * 0.42
    top = cy - ah / 2
    d.rounded_rectangle([cx - shaft / 2, top, cx + shaft / 2, cy + ah * 0.05], radius=shaft * 0.3,
                        fill=(255, 255, 255, 245))
    d.polygon([(cx - aw / 2, cy + ah * 0.02), (cx + aw / 2, cy + ah * 0.02),
               (cx, cy + ah / 2 + ah * 0.10)], fill=(255, 255, 255, 245))
    return img


def main() -> None:
    bg = gradient(S, (13, 23, 38), (17, 30, 49), left=(10, 20, 34), right=(20, 35, 56))
    mask = rounded_mask(S, int(S * 0.22))
    canvas = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    canvas.paste(bg, (0, 0), mask)

    # うっすらグロー
    glow = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    gd = ImageDraw.Draw(glow)
    gd.ellipse([S * 0.12, S * 0.10, S * 0.88, S * 0.86], fill=(45, 212, 191, 46))
    glow = glow.filter(ImageFilter.GaussianBlur(S * 0.09))
    canvas.alpha_composite(Image.composite(glow, Image.new("RGBA", (S, S), (0, 0, 0, 0)), mask))

    canvas.alpha_composite(chip(S))

    # 上辺のハイライト
    hl = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    hd = ImageDraw.Draw(hl)
    hd.arc([S * 0.06, S * 0.04, S * 0.94, S * 0.94], 200, 340, fill=(255, 255, 255, 38),
           width=int(S * 0.012))
    canvas.alpha_composite(Image.composite(hl, Image.new("RGBA", (S, S), (0, 0, 0, 0)), mask))

    base = canvas.resize((256, 256), Image.LANCZOS)
    base.save(OUT / "icon-256.png")
    sizes = [(16, 16), (20, 20), (24, 24), (32, 32), (40, 40), (48, 48), (64, 64), (128, 128), (256, 256)]
    base.save(OUT / "icon.ico", sizes=sizes)
    print("generated:", OUT / "icon.ico", "sizes:", sizes)


if __name__ == "__main__":
    main()
