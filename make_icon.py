# -*- coding: utf-8 -*-
"""生成 ClashSpeedTest 应用图标：蓝底圆角方块 + 白色粗体 C（与界面标题左侧图标一致）"""
from PIL import Image, ImageDraw, ImageFont

OUT = r"C:\Users\Maksim\WorkBuddy\2026-09-03-12-46-30\ClashSpeedTest\app.ico"
FONT_PATH = r"C:\Windows\Fonts\segoeuib.ttf"

BG = (9, 105, 218, 255)      # #0969DA
FG = (255, 255, 255, 255)    # 白色 C

SS = 8          # 超采样倍数，用于抗锯齿
BASE = 512      # 基准画布
PAD = 0.06      # 外边距比例，避免图标顶满画布
RADIUS_RATIO = 9 / 36      # 圆角半径 / 方块边长（对齐 XAML: 36px 方块, CornerRadius=9）
FONT_RATIO = 18 / 36       # 字号 / 方块边长（对齐 XAML: FontSize=18）


def make(size):
    """绘制指定尺寸的 RGBA 图标"""
    s = size * SS
    img = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)

    pad = s * PAD
    side = s - 2 * pad
    x0, y0 = pad, pad
    x1, y1 = x0 + side, y0 + side
    d.rounded_rectangle([x0, y0, x1, y1], radius=side * RADIUS_RATIO, fill=BG)

    font_size = side * FONT_RATIO
    font = ImageFont.truetype(FONT_PATH, int(round(font_size)))

    # 精确居中：以 "C" 的墨迹边界为基准
    bbox = d.textbbox((0, 0), "C", font=font)
    w, h = bbox[2] - bbox[0], bbox[3] - bbox[1]
    cx, cy = (x0 + x1) / 2, (y0 + y1) / 2
    tx, ty = cx - w / 2 - bbox[0], cy - h / 2 - bbox[1]
    d.text((tx, ty), "C", font=font, fill=FG)

    return img.resize((size, size), Image.LANCZOS)


sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256]
# 256 用 PNG 压缩存储（ICO 内嵌 PNG），其余用 BMP
make(256).save(
    OUT,
    format="ICO",
    sizes=[(s, s) for s in sizes],
    bitmap_format="png",
)
print("saved:", OUT)
with Image.open(OUT) as im:
    print("ico size:", im.size, "frames:", getattr(im, "n_frames", 1))
