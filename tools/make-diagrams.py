"""
生成"手机操作示意"插图（assets/）。

这些图是**示意图**，不是真机截图：不同品牌、不同系统版本的设置界面文字与层级会有差异，
所以这里用统一风格画出「点哪里、点什么」的导航路径，比放某一台手机的截图更通用。
如果你手上有多品牌真机，可以拍真实照片覆盖同名文件。

用法：python tools/make-diagrams.py
"""

from __future__ import annotations

import os

from PIL import Image, ImageDraw, ImageFont

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT_DIR = os.path.join(ROOT, "assets")

TEAL = (0, 150, 136)
TEAL_DARK = (0, 105, 92)
INK = (38, 50, 56)
INK_SOFT = (96, 110, 118)
LINE = (207, 216, 220)
BG = (245, 247, 248)
CARD = (255, 255, 255)
HILITE = (255, 243, 224)
ORANGE = (239, 108, 0)
RED = (198, 40, 40)

FONT_REG = "C:/Windows/Fonts/msyh.ttc"
FONT_BOLD = "C:/Windows/Fonts/msyhbd.ttc"


def font(size: int, bold: bool = False) -> ImageFont.FreeTypeFont:
    return ImageFont.truetype(FONT_BOLD if bold else FONT_REG, size)


def rounded(draw: ImageDraw.ImageDraw, box, radius, fill=None, outline=None, width=1):
    draw.rounded_rectangle(box, radius=radius, fill=fill, outline=outline, width=width)


def text(draw, xy, s, f, fill=INK, anchor="la"):
    draw.text(xy, s, font=f, fill=fill, anchor=anchor)


def text_center(draw, cx, y, s, f, fill=INK):
    draw.text((cx, y), s, font=f, fill=fill, anchor="ma")


def wrap(draw, x, y, s, f, fill=INK, max_width=400, line_gap=8, anchor="la"):
    """逐字换行，适配中文。anchor 用 "la"（左对齐）或 "ma"（水平居中）。返回结束后的 y。"""
    for hard_line in s.split("\n"):
        if not hard_line:
            y += f.size + line_gap
            continue
        line = ""
        for ch in hard_line:
            probe = line + ch
            if draw.textlength(probe, font=f) > max_width and line:
                draw.text((x, y), line, font=f, fill=fill, anchor=anchor)
                y += f.size + line_gap
                line = ch
            else:
                line = probe
        if line:
            draw.text((x, y), line, font=f, fill=fill, anchor=anchor)
            y += f.size + line_gap
    return y


def phone(draw, x, y, w=300, h=580, title="设置"):
    """画一台手机的外框，返回屏幕内容区 (left, top, right, bottom)。"""
    rounded(draw, (x, y, x + w, y + h), 30, fill=CARD, outline=LINE, width=3)
    rounded(draw, (x + w // 2 - 34, y + 12, x + w // 2 + 34, y + 24), 6, fill=(224, 224, 224))

    inner_left, inner_top, inner_right = x + 14, y + 36, x + w - 14
    text_center(draw, (inner_left + inner_right) // 2, inner_top + 8, title, font(19, True))
    draw.line((inner_left, inner_top + 40, inner_right, inner_top + 40), fill=LINE, width=2)

    rounded(draw, (x + w // 2 - 46, y + h - 16, x + w // 2 + 46, y + h - 10), 4, fill=(224, 224, 224))
    return inner_left, inner_top + 52, inner_right, y + h - 30


def row(draw, x0, x1, y, label, value="", highlight=False, switch=False, switch_on=False, height=52):
    rounded(draw, (x0, y, x1, y + height), 10, fill=HILITE if highlight else (250, 250, 250),
            outline=ORANGE if highlight else LINE, width=3 if highlight else 1)
    text(draw, (x0 + 16, y + height // 2), label, font(17, highlight),
         fill=ORANGE if highlight else INK, anchor="lm")

    if switch:
        sw_w, sw_h = 52, 28
        sx = x1 - 16 - sw_w
        sy = y + (height - sw_h) // 2
        rounded(draw, (sx, sy, sx + sw_w, sy + sw_h), sw_h // 2,
                fill=TEAL if switch_on else (189, 189, 189))
        knob_x = sx + sw_w - sw_h // 2 if switch_on else sx + sw_h // 2
        draw.ellipse((knob_x - 12, sy + 2, knob_x + 12, sy + sw_h - 2), fill=CARD)
    elif value:
        text(draw, (x1 - 16, y + height // 2), value, font(15), fill=INK_SOFT, anchor="rm")
    return y + height + 8


def arrow(draw, x0, y, x1, label=""):
    draw.line((x0, y, x1 - 14, y), fill=TEAL, width=4)
    draw.polygon([(x1, y), (x1 - 16, y - 9), (x1 - 16, y + 9)], fill=TEAL)
    if label:
        text_center(draw, (x0 + x1) // 2, y + 14, label, font(15, True), TEAL_DARK)


def caption(draw, cx, y, s, max_width=340):
    return wrap(draw, cx, y, s, font(15), INK_SOFT, max_width=max_width, line_gap=6, anchor="ma")


def bubble(draw, cx, y, s, max_width=320, fill=(255, 243, 224), outline=ORANGE):
    f = font(16, True)
    w = min(max_width, int(draw.textlength(s, font=f)) + 36)
    rounded(draw, (cx - w // 2, y, cx + w // 2, y + 44), 10, fill=fill, outline=outline, width=2)
    text_center(draw, cx, y + 11, s, f, outline)
    return y + 44


def header(draw, width, title, subtitle):
    text(draw, (50, 42), title, font(30, True), INK)
    text(draw, (50, 86), subtitle, font(17), INK_SOFT)


def footer(draw, width, height, note):
    text_center(draw, width // 2, height - 42, note, font(14), INK_SOFT)


def image_developer_options():
    W, H = 1560, 1060
    img = Image.new("RGB", (W, H), BG)
    d = ImageDraw.Draw(img)

    header(d, W, "第 1 步 ｜ 打开「开发者选项」和「USB 调试」",
           "所有安卓手机都是同一条路：先让「开发者选项」出现，再进去打开 USB 调试。")

    # --- 手机 1 ---
    x1 = 60
    l, t, r, b = phone(d, x1, 150, title="设置")
    y = t + 16
    for label in ["WLAN", "蓝牙", "个人热点"]:
        y = row(d, l, r, y, label, value="›")
    y = row(d, l, r, y, "关于手机", value="›", highlight=True)
    for label in ["显示", "声音与振动"]:
        y = row(d, l, r, y, label, value="›")
    bubble(d, (l + r) // 2, y + 16, "先点这里")
    caption(d, (l + r) // 2, 760, "① 打开「设置」，找到「关于手机」")

    # --- 手机 2 ---
    x2 = 480
    l2, t2, r2, b2 = phone(d, x2, 150, title="关于手机")
    y = t2 + 16
    for label in ["设备名称", "型号", "运行内存"]:
        y = row(d, l2, r2, y, label)
    y = row(d, l2, r2, y, "版本号", highlight=True)
    for label in ["内核版本", "Android 版本"]:
        y = row(d, l2, r2, y, label)
    bubble(d, (l2 + r2) // 2, y + 16, "连续点 7 次")
    caption(d, (l2 + r2) // 2, 760, "② 连续点击「版本号」7 次\n直到提示「您已处于开发者模式」")

    # --- 手机 3 ---
    x3 = 900
    l3, t3, r3, b3 = phone(d, x3, 150, title="开发者选项")
    y = t3 + 16
    y = row(d, l3, r3, y, "开发者选项", switch=True, switch_on=True)
    y = row(d, l3, r3, y, "USB 调试", switch=True, switch_on=True, highlight=True)
    y = row(d, l3, r3, y, "USB 调试（安全设置）", switch=True, switch_on=True, highlight=True)
    y = row(d, l3, r3, y, "通过 USB 安装应用", switch=True)
    bubble(d, (l3 + r3) // 2, y + 16, "小米/红米必开")
    caption(d, (l3 + r3) // 2, 760, "③ 打开「USB 调试」\n小米 / 红米还要打开「USB 调试（安全设置）」")

    arrow(d, x1 + 310, 430, x2 - 12, "点进去")
    arrow(d, x2 + 310, 430, x3 - 12, "点 7 次")

    # --- 底部品牌对照表 ---
    table_y = 852
    rounded(d, (60, table_y - 16, W - 60, H - 60), 12, fill=CARD, outline=LINE, width=1)
    text(d, (86, table_y), "各品牌「关于手机」的位置（找不到就用开发者选项页面右上角的搜索，输入 USB）",
         font(17, True), INK)
    items = [
        ("小米 / 红米 / POCO", "设置 → 我的设备 → 全部参数与信息"),
        ("华为 / 荣耀", "设置 → 关于手机"),
        ("OPPO / 一加 / 真我", "设置 → 关于本机"),
        ("vivo / iQOO", "设置 → 系统管理 → 关于手机"),
        ("三星", "设置 → 关于手机 → 软件信息"),
    ]
    col_w = (W - 200) // 2
    for i, (brand, path) in enumerate(items):
        cx = 86 + (i % 2) * col_w
        cy = table_y + 40 + (i // 2) * 30
        text(d, (cx, cy), f"· {brand}", font(15, True), TEAL_DARK)
        text(d, (cx + 200, cy), path, font(15), INK_SOFT)

    footer(d, W, H, "示意图：各品牌界面文字略有差异，认准「关于手机 / 版本号 / USB 调试」这几个关键词即可。")
    return img


def image_authorize_dialog():
    W, H = 1400, 900
    img = Image.new("RGB", (W, H), BG)
    d = ImageDraw.Draw(img)

    header(d, W, "第 2 步 ｜ 在手机上允许这台电脑调试",
           "插上数据线后，手机屏幕上一定会弹出这个对话框。没有它，电脑就连不上手机。")

    # 手机 + 弹窗
    l, t, r, b = phone(d, 90, 160, w=420, h=620, title="")
    d.rectangle((l, t, r, b), fill=(55, 71, 79))
    text_center(d, (l + r) // 2, t + 20, "手机屏幕（示意）", font(15), (176, 190, 197))

    dl, dt, dr, db = l + 26, t + 140, r - 26, t + 430
    rounded(d, (dl, dt, dr, db), 18, fill=CARD, outline=LINE, width=2)
    text_center(d, (dl + dr) // 2, dt + 26, "允许 USB 调试吗？", font(21, True), INK)

    y = dt + 74
    y = wrap(d, (dl + dr) // 2, y, "计算机 RSA 密钥指纹：",
             font(15), INK_SOFT, max_width=dr - dl - 60, line_gap=4, anchor="ma")
    rounded(d, (dl + 24, y, dr - 24, y + 44), 8, fill=(238, 242, 243))
    text_center(d, (dl + dr) // 2, y + 12, "3A:5F:...:9C:1D", font(15), INK_SOFT)
    y += 66

    # 勾选框
    box = (dl + 30, y, dl + 54, y + 24)
    rounded(d, box, 5, fill=CARD, outline=TEAL, width=3)
    d.line((box[0] + 5, y + 13, box[0] + 11, y + 19), fill=TEAL, width=4)
    d.line((box[0] + 11, y + 19, box[0] + 19, y + 5), fill=TEAL, width=4)
    text(d, (box[2] + 12, y + 12), "一律允许使用这台计算机进行调试", font(15, True), TEAL_DARK, anchor="lm")

    bubble(d, (l + r) // 2, db + 24, "一定要勾上这个")

    # 按钮
    by = db - 62
    rounded(d, (dl + 30, by, dl + 170, by + 46), 23, fill=(238, 242, 243))
    text_center(d, dl + 100, by + 13, "取消", font(17), INK_SOFT)
    rounded(d, (dr - 170, by, dr - 30, by + 46), 23, fill=TEAL)
    text_center(d, dr - 100, by + 13, "允许", font(17, True), CARD)

    # 右侧说明
    rx = 610
    rounded(d, (rx, 170, rx + 720, 470), 16, fill=CARD, outline=LINE, width=1)
    text(d, (rx + 30, 196), "做完这步之后", font(21, True), INK)
    lines = [
        ("勾选「一律允许」", "以后同一台电脑再连接就不用重复点允许了。"),
        ("在电脑上点「连接手机」", "程序顶部圆点变绿、显示手机型号，就说明连上了。"),
        ("手机保持解锁状态", "屏幕锁着的时候，部分系统会拒绝执行命令。"),
    ]
    y = 244
    for title, desc in lines:
        d.ellipse((rx + 32, y + 6, rx + 44, y + 18), fill=TEAL)
        text(d, (rx + 60, y), title, font(17, True), TEAL_DARK)
        text(d, (rx + 60, y + 28), desc, font(15), INK_SOFT)
        y += 74

    # 没弹窗怎么办
    rounded(d, (rx, 500, rx + 720, 800), 16, fill=(255, 243, 224), outline=ORANGE, width=2)
    text(d, (rx + 30, 526), "没弹出对话框？按顺序排查", font(21, True), ORANGE)
    tips = [
        "拔掉数据线，重新插一次；",
        "换一根数据线（很多线只能充电、不能传数据）；",
        "手机下拉通知栏，把 USB 用途从「仅充电」改成「传输文件 / MTP」；",
        "开发者选项里点「撤销 USB 调试授权」，再重新插线；",
        "确认「USB 调试」开关确实是打开状态——系统更新后有时会被自动关掉。",
    ]
    y = 574
    for i, tip in enumerate(tips, 1):
        text(d, (rx + 34, y), f"{i}.", font(15, True), ORANGE)
        y = wrap(d, rx + 62, y, tip, font(15), INK, max_width=620, line_gap=4) + 6

    footer(d, W, H, "示意图：弹窗文字各品牌略有不同，认准「USB 调试」和「允许」两个字即可。")
    return img


def main():
    os.makedirs(OUT_DIR, exist_ok=True)
    outputs = [
        ("手机-1-开启开发者选项.png", image_developer_options()),
        ("手机-2-授权USB调试.png", image_authorize_dialog()),
    ]
    for name, img in outputs:
        path = os.path.join(OUT_DIR, name)
        img.save(path)
        print(f"已生成 {os.path.relpath(path, ROOT)}  ({img.width}x{img.height})")


if __name__ == "__main__":
    main()
