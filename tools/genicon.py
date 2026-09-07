# genicon.py — Aurora 应用图标生成器（圆角方形底 + 黑胶唱片）
# 用法: python genicon.py [输出目录，默认 ../assets]
# 产出: icon256.png + app.ico（内嵌 16/24/32/48/64/128/256 多尺寸）
#
# 设计: 深色圆角方形底座（对角渐变 + 顶部高光 + 内描边）
#       中央黑胶唱片（径向渐变盘体 + 胶纹环 + 高光弧 + 极光渐变标签 + 白中孔）
#       唱片带柔和投影，浮在底座上。1024 超采样 → LANCZOS 缩小抗锯齿。

import os
import sys
from PIL import Image, ImageDraw, ImageFilter

# ---- 调色板（与 App 主题一致）----
BG_TOP = (26, 33, 56)          # 底座左上 #1A2138
BG_BOTTOM = (11, 14, 20)       # 底座右下 #0B0E14
VINYL_EDGE = (10, 12, 17)      # 盘体边缘 #0A0C11
VINYL_CORE = (44, 51, 76)      # 盘体中心 #2C334C
AURORA_TEAL = (76, 201, 240)   # 青 #4CC9F0
AURORA_VIOLET = (124, 92, 255) # 紫 #7C5CFF
AURORA_PINK = (244, 114, 182)  # 粉 #F472B6

S = 1024                       # 超采样画布
C = S // 2
TILE_M = 24                    # 底座边距
TILE_R = 230                   # 底座圆角半径
VINYL_R = 392                  # 唱片半径


def lerp(a, b, t):
    return tuple(int(round(a[i] + (b[i] - a[i]) * t)) for i in range(3))


def diag_gradient(size, c1, c2):
    """左上→右下对角线性渐变（像素级小图放大）。"""
    n = 256
    grad = Image.new("RGB", (n, n))
    px = grad.load()
    for y in range(n):
        for x in range(n):
            px[x, y] = lerp(c1, c2, (x + y) / (2.0 * (n - 1)))
    return grad.resize((size, size), Image.LANCZOS).convert("RGBA")


def rounded_mask(size, m, r):
    mask = Image.new("L", (size, size), 0)
    ImageDraw.Draw(mask).rounded_rectangle(
        [m, m, size - m - 1, size - m - 1], radius=r, fill=255)
    return mask


def radial_disc(size, cx, cy, r, c_edge, c_core):
    """径向渐变圆盘：逐层递减圆（边缘色→中心色）。"""
    img = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    steps = 200
    for i in range(steps, -1, -1):
        t = i / steps
        rr = r * t
        col = lerp(c_core, c_edge, t)
        d.ellipse([cx - rr, cy - rr, cx + rr, cy + rr], fill=col + (255,))
    return img


def aurora_disc(size, cx, cy, r):
    """极光三色渐变圆（局部坐标：左上青 → 紫 → 右下粉）。"""
    n = 256
    grad = Image.new("RGB", (n, n))
    px = grad.load()
    for y in range(n):
        for x in range(n):
            t = (x + y) / (2.0 * (n - 1))
            px[x, y] = lerp(AURORA_TEAL, AURORA_VIOLET, t * 2) if t < 0.5 \
                else lerp(AURORA_VIOLET, AURORA_PINK, (t - 0.5) * 2)
    d2r = 2 * r
    grad = grad.resize((d2r, d2r), Image.LANCZOS).convert("RGBA")
    mask = Image.new("L", (d2r, d2r), 0)
    ImageDraw.Draw(mask).ellipse([0, 0, d2r - 1, d2r - 1], fill=255)
    out = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    out.paste(grad, (cx - r, cy - r), mask)
    return out


def render_vinyl(img, cx, cy, r):
    """把黑胶唱片画到 img 上（cx,cy 为中心，r 为半径，含投影）。"""
    k = r / 470.0  # 相对原设计（半径470）的缩放

    # 投影（偏移黑圆 + 高斯模糊）
    shadow = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    ImageDraw.Draw(shadow).ellipse(
        [cx - r, cy - r + int(12 * k), cx + r, cy + r + int(12 * k)],
        fill=(0, 0, 0, 110))
    shadow = shadow.filter(ImageFilter.GaussianBlur(int(22 * k)))
    img.alpha_composite(shadow)

    # 盘体
    img.alpha_composite(radial_disc(S, cx, cy, r, VINYL_EDGE, VINYL_CORE))

    d = ImageDraw.Draw(img)
    # 胶纹环 ×4
    for rr, a in ((428, 24), (392, 20), (356, 16), (318, 14)):
        rr = int(rr * k)
        d.ellipse([cx - rr, cy - rr, cx + rr, cy + rr],
                  outline=(255, 255, 255, a), width=max(2, int(3 * k)))
    # 高光弧
    ha = int(390 * k)
    d.arc([cx - ha, cy - ha, cx + ha, cy + ha], start=203, end=249,
          fill=(255, 255, 255, 28), width=max(3, int(20 * k)))
    # 极光标签 + 细环
    img.alpha_composite(aurora_disc(S, cx, cy, int(190 * k)))
    lr = int(202 * k)
    d.ellipse([cx - lr, cy - lr, cx + lr, cy + lr],
              outline=(255, 255, 255, 64), width=max(2, int(3 * k)))
    # 中孔
    h1, h2 = int(24 * k), int(21 * k)
    d.ellipse([cx - h1, cy - h1, cx + h1, cy + h1], fill=(0, 0, 0, 70))
    d.ellipse([cx - h2, cy - h2, cx + h2, cy + h2], fill=(245, 247, 250, 255))
    # 唱片外边缘描边
    er = r - 1
    d.ellipse([cx - er, cy - er, cx + er, cy + er],
              outline=(255, 255, 255, 34), width=max(2, int(3 * k)))


def render():
    img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    tile = rounded_mask(S, TILE_M, TILE_R)

    # 1) 底座（对角渐变，圆角）
    base = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    base.paste(diag_gradient(S, BG_TOP, BG_BOTTOM), (0, 0), tile)
    img.alpha_composite(base)

    # 2) 黑胶唱片（含投影）
    render_vinyl(img, C, C, VINYL_R)

    # 3) 顶部高光（底座上部淡白渐变，圆角裁切）
    hl = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    hd = ImageDraw.Draw(hl)
    for i in range(150):
        a = int(22 * (1 - i / 150.0))
        if a > 0:
            hd.line([(TILE_M, TILE_M + i), (S - TILE_M, TILE_M + i)],
                    fill=(255, 255, 255, a))
    hl.putalpha(Image.composite(hl.getchannel("A"), Image.new("L", (S, S), 0), tile))
    img.alpha_composite(hl)

    # 4) 底座内描边
    d = ImageDraw.Draw(img)
    d.rounded_rectangle([TILE_M + 1, TILE_M + 1, S - TILE_M - 2, S - TILE_M - 2],
                        radius=TILE_R - 1, outline=(255, 255, 255, 36), width=3)

    # 5) 最外层圆角裁切（底座外全透明）
    alpha = Image.composite(img.getchannel("A"), Image.new("L", (S, S), 0), tile)
    img.putalpha(alpha)
    return img


def main():
    out_dir = sys.argv[1] if len(sys.argv) > 1 else os.path.join(
        os.path.dirname(os.path.abspath(__file__)), "..", "assets")
    os.makedirs(out_dir, exist_ok=True)

    big = render()
    png256 = big.resize((256, 256), Image.LANCZOS)

    png_path = os.path.join(out_dir, "icon256.png")
    png256.save(png_path, "PNG")
    print("written:", png_path)

    ico_path = os.path.join(out_dir, "app.ico")
    png256.save(ico_path, "ICO",
                sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)])
    print("written:", ico_path)


if __name__ == "__main__":
    main()
