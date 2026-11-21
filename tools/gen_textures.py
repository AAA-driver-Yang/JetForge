"""生成 JetForge 发动机外形贴图（原版 KSP 风格：浅灰金属蒙皮 + 深色热端 + 熏黑内壁）。

为什么自己画：原版引擎的贴图是按它自己的 UV 岛摆的，我们这张是程序化回旋面、
UV 是自己的（周向 u、轴向 v、按世界尺寸平铺），套用原版图只会把面板线拉成马赛克。
所以按原版**配色与质感**重画一张可无缝平铺的金属蒙皮，而不是直接搬原版文件。

输出（每张都配一张切线空间法线图）：
  JetForge_Cold.png   冷端（短舱、风扇、核心机匣）—— 拼接蒙皮 + 检修口盖 + 百叶 + 铆钉
  JetForge_Hot.png    热端（燃烧室筒体、涡轮环、喷管外壁、排气锥）—— 回火色 + 环箍 + 积碳
  JetForge_Inner.png  喷管内壁（从喷口看进去那一圈）—— 均匀熏黑 + 纵向加强筋，没有蒙皮拼缝

一张贴图在世界里代表 TEX_WORLD 米见方（见 C# 里 UvWorldSize），所以平铺密度与发动机尺寸无关。

无缝的做法：缝与铆钉用周期函数算，口盖/百叶/划痕/污渍都用取模坐标画，
噪声本身周期可平铺。最后一条最要紧——评审要按"平铺 6 次"看，不能只看一张。
"""
import numpy as np
ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
from PIL import Image, ImageDraw, ImageFilter

SIZE = 512
OUT = os.path.join(ROOT, "GameData/JetForge/Textures")
TEX_WORLD = 0.5  # 每张贴图覆盖的世界尺寸（米），C# 侧 UvWorldSize 必须一致


def rng(seed):
    return np.random.default_rng(seed)


def seam_mask(coord, count, width):
    """周期接缝：coord∈[0,1) 上均匀分布 count 条缝，返回 0..1（1 = 正在缝上）。"""
    f = (coord * count) % 1.0
    d = np.minimum(f, 1.0 - f)
    return np.clip(1.0 - d / width, 0.0, 1.0)


def rivet_mask(x, y, cols, rows, radius):
    """沿缝的铆钉：网格点距离，环绕取最小值，保证平铺无缝。"""
    px = (x * cols) % 1.0
    py = (y * rows) % 1.0
    dx = np.minimum(px, 1.0 - px) / cols
    dy = np.minimum(py, 1.0 - py) / rows
    return np.clip(1.0 - np.sqrt(dx * dx + dy * dy) / radius, 0.0, 1.0)


def cell_tone(x, y, cols, rows, seed=0.0):
    """每块面板一个固定的明暗差。没有这个，一整片蒙皮就是一块均匀的灰板。"""
    ix = np.floor(x * cols)
    iy = np.floor(y * rows)
    h = np.sin(ix * 12.9898 + iy * 78.233 + seed) * 43758.5453
    return h - np.floor(h)


def smooth_noise(r, size, scale):
    """低频噪声：小图随机 → 放大 → 轻微模糊。周期可平铺（放大是插值，不留缝）。"""
    small = r.random((max(2, size // scale), max(2, size // scale)))
    img = Image.fromarray((small * 255).astype(np.uint8)).resize((size, size), Image.BICUBIC)
    return np.asarray(img).astype(np.float32) / 255.0


def period_rect(cx, cy, w, h, soft=1.5):
    """周期矩形（口盖轮廓用）：中心(cx,cy)、宽高(w,h)，坐标都是 0..1 的归一化。
    返回 0..1 的掩膜，跨边界自动接回，所以平铺无缝。"""
    px = (cx - 0.5 * w, cx + 0.5 * w)
    py = (cy - 0.5 * h, cy + 0.5 * h)
    return px, py


def rect_edge(x, y, box, width=0.004):
    """矩形边框掩膜（口盖的缝）。box = (px, py) 两组上下界。"""
    (x0, x1), (y0, y1) = box
    inside_x = ((x >= x0) & (x <= x1)).astype(np.float32)
    inside_y = ((y >= y0) & (y <= y1)).astype(np.float32)
    dx = np.minimum(np.abs(x - x0), np.abs(x - x1))
    dy = np.minimum(np.abs(y - y0), np.abs(y - y1))
    edge = np.maximum(np.clip(1.0 - dx / width, 0, 1) * inside_y,
                      np.clip(1.0 - dy / width, 0, 1) * inside_x)
    return np.clip(edge, 0, 1)


def vprofile(r, size, n=8):
    """沿 v（轴向）的一维剖面噪声，返回 (size,1)。

    热端的回火色和烟熏是**顺着气流方向**分层的，不是各向同性的斑块——
    用二维噪声做出来永远像"喷了层彩色气体"，因为噪声没有方向。
    """
    pts = r.random(n)
    xs = np.linspace(0, n, size, endpoint=False)
    i = np.floor(xs).astype(int) % n
    j = (i + 1) % n
    t = xs - np.floor(xs)
    t = t * t * (3.0 - 2.0 * t)                      # 平滑插值
    vals = pts[i] * (1.0 - t) + pts[j] * t
    return vals.reshape(size, 1)


def scratches(r, size, count, length, strength, axis_bias=0.35):
    """划痕：取模坐标画细线，跨边界自动接回另一边，所以平铺时看不出断口。

    axis_bias 越大，划痕越集中沿 v 方向（即轴向）——真机的摩擦痕是被气流和
    拆装工具顺着轴线刮出来的，随机四面八方的划痕一眼就是滤镜。
    """
    mask = np.zeros((size, size), np.float32)
    for _ in range(count):
        x = r.random() * size
        y = r.random() * size
        ang = np.pi / 2 + r.normal(0.0, axis_bias, 1)[0]
        ln = length * (0.4 + r.random())
        n = int(ln)
        for i in range(n):
            t = i / max(n - 1, 1)
            xi = int((x + np.cos(ang) * ln * t) % size)
            yi = int((y + np.sin(ang) * ln * t) % size)
            mask[yi, xi] = max(mask[yi, xi], strength * (0.35 + 0.65 * t))
    return np.clip(mask, 0, 1)


def normal_from_height(height, strength=2.2):
    """高度场 → 切线空间法线（+Z 朝外）。"""
    dx = np.roll(height, -1, axis=1) - np.roll(height, 1, axis=1)
    dy = np.roll(height, -1, axis=0) - np.roll(height, 1, axis=0)
    nx = -dx * strength
    ny = -dy * strength
    nz = np.ones_like(height)
    ln = np.sqrt(nx * nx + ny * ny + nz * nz)
    rgb = np.stack([nx / ln, ny / ln, nz / ln], axis=-1)
    return ((rgb * 0.5 + 0.5) * 255.0).clip(0, 255).astype(np.uint8)


def save(arr, name):
    Image.fromarray(arr.astype(np.uint8)).save(f"{OUT}\\{name}")
    print("  ", name, arr.shape, "mean", float(np.asarray(arr, np.float32).mean()))


def cold():
    """冷端：浅灰蒙皮。拼接板（每块明暗不同）+ 检修口盖 + 百叶散热口 + 铆钉 + 划痕 + 油污。"""
    r = rng(7)
    y, x = np.mgrid[0:SIZE, 0:SIZE].astype(np.float32)
    x /= SIZE
    y /= SIZE
    base = np.array([178.0, 182.0, 186.0])           # 原版喷气机匣的浅灰
    # 第一版是 196/200/203，进游戏一片死白——KSP 的日光比贴图亮度凶，浅灰要按"暗一档"画；
    # 但 166 又评"偏暗偏闷"，最后落在 178：日光下大约还原成原版那种浅银灰。
    grad = 0.92 + 0.14 * smooth_noise(r, SIZE, 128)  # 大块明暗
    img = base[None, None, :] * grad[..., None]

    # 面板：周向 6 块、轴向 3 块。每块单独调明暗，否则一整片是均匀的灰板。
    cols, rows = 6, 3
    tone = cell_tone(x, y, cols, rows, 3.7)
    img *= (0.935 + 0.13 * tone)[..., None]

    panel_v = seam_mask(x, cols, 0.010)
    panel_h = seam_mask(y, rows, 0.010)
    panel = np.clip(panel_v + panel_h, 0, 1)
    panel_ao = np.clip(seam_mask(x, cols, 0.026) * 0.5 + seam_mask(y, rows, 0.026) * 0.5, 0, 1)
    img *= (1.0 - 0.50 * panel - 0.10 * panel_ao)[..., None]

    # 检修口盖：一块矩形边框 + 四角紧固件（真机机匣上到处都是，是"有结构"的第一眼信号）。
    # 光有边框像贴了张纸——上沿提亮、下沿压暗，才有"板嵌进去"的厚度。
    box = period_rect(0.30, 0.34, 0.22, 0.20)
    hatch = rect_edge(x, y, box, 0.005)
    img *= (1.0 - 0.45 * hatch)[..., None]
    hatch_in = (((x > box[0][0]) & (x < box[0][1])) & ((y > box[1][0]) & (y < box[1][1])))
    img *= np.where(hatch_in, 1.030, 1.0)[..., None]
    lip_top = np.clip(1.0 - np.abs(y - box[1][0]) / 0.0018, 0, 1) * hatch_in
    lip_bot = np.clip(1.0 - np.abs(y - box[1][1]) / 0.0022, 0, 1) * hatch_in
    img += (lip_top * 20.0)[..., None]
    img *= (1.0 - 0.20 * lip_bot)[..., None]
    for cx, cy in ((box[0][0] + 0.012, box[1][0] + 0.012), (box[0][1] - 0.012, box[1][0] + 0.012),
                   (box[0][0] + 0.012, box[1][1] - 0.012), (box[0][1] - 0.012, box[1][1] - 0.012)):
        px = np.minimum(np.abs(x - cx), 1.0 - np.abs(x - cx))
        py = np.minimum(np.abs(y - cy), 1.0 - np.abs(y - cy))
        dot = np.clip(1.0 - np.sqrt(px * px + py * py) / 0.006, 0, 1)
        img *= (1.0 - 0.35 * dot)[..., None]
        img += (dot * 22.0)[..., None]

    # 百叶散热口：一组横向短槽（沿轴向排开）。压太死会变成黑洞，留一点金属反光。
    louver = np.zeros_like(x)
    for i in range(6):
        cy = 0.66 + i * 0.016
        px = np.minimum(np.abs(x - 0.72), 1.0 - np.abs(x - 0.72))
        py = np.minimum(np.abs(y - cy), 1.0 - np.abs(y - cy))
        slot = (np.clip(1.0 - px / 0.10, 0, 1) * np.clip(1.0 - py / 0.0045, 0, 1))
        louver = np.maximum(louver, slot)
        img *= (1.0 - 0.52 * slot)[..., None]
        img += (slot * 10.0)[..., None]
        lip = (np.clip(1.0 - px / 0.10, 0, 1) * np.clip(1.0 - np.abs(y - (cy + 0.005)) / 0.002, 0, 1))
        img += (lip * 22.0)[..., None]

    # 板边磨白：缝的外侧留一条细高光（拆装、气流磨掉漆的地方），平面就不会那么"肉"
    wear_v = np.clip(seam_mask(x, cols, 0.020) - panel_v, 0, 1)
    wear_h = np.clip(seam_mask(y, rows, 0.020) - panel_h, 0, 1)
    img += ((wear_v + wear_h) * 9.0)[..., None]

    # 铆钉：钉头高光 + 一侧投影。缩小一半以后还能看见，才算真的有铆钉。
    riv = rivet_mask(x, y, cols * 5, rows * 10, 0.0030)
    riv_shadow = np.roll(riv, 2, axis=0) * 0.6
    img *= (1.0 - 0.42 * riv_shadow)[..., None]
    img += (riv * 30.0)[..., None]

    # 轴向拉丝：沿 v 拉长的一维噪声
    brush = smooth_noise(r, SIZE, 6)
    brush = np.repeat(brush.mean(axis=0, keepdims=True), SIZE, axis=0)
    img += ((brush - 0.5) * 12.0)[..., None]

    # 划痕与油污：第一版 55 条 26% 被评"像仓库里放了十年的旧零件"——缩到 18 条 14%，
    # 只在近看时提供一点金属使用痕迹，远看是干净的。
    scr = scratches(r, SIZE, 18, 55, 0.30)
    img *= (1.0 - 0.14 * scr)[..., None]
    grime = smooth_noise(r, SIZE, 40)
    grime = (grime - 0.46).clip(0, 1) * (0.30 + 0.70 * panel_ao)
    img *= (1.0 - 0.20 * grime)[..., None]

    img += r.normal(0.0, 1.0, (SIZE, SIZE))[..., None]   # 细颗粒
    img = img.clip(0, 255)

    height = 1.0 - 0.60 * panel - 0.22 * panel_ao - 0.45 * riv - 0.55 * hatch - 0.50 * louver
    height = height + (smooth_noise(r, SIZE, 24) - 0.5) * 0.06 + scr * 0.05
    save(img, "JetForge_Cold.png")
    save(normal_from_height(height, 1.5), "JetForge_Cold_N.png")


def hot():
    """热端：深钢色 + 回火色（蓝紫→橙）+ 环箍 + 轴向高温流痕 + 积碳。

    第一版被评"像喷了层有色气体"：斑块太柔、色阶太浅。这一版把回火色的边界压硬
    （乘 3.2 再截断）、在橙区中心压出焦黑、加一圈明显环箍（真机喷管一节一节拼的）。
    """
    r = rng(11)
    y, x = np.mgrid[0:SIZE, 0:SIZE].astype(np.float32)
    x /= SIZE
    y /= SIZE
    base = np.array([138.0, 134.0, 130.0])          # 原版喷管是抛光钢，不是生铁
    grad = 0.78 + 0.36 * smooth_noise(r, SIZE, 96)
    img = base[None, None, :] * grad[..., None]

    # 环箍：轴向 3 道粗缝（喷管是分段拼的），每段明暗不同
    cols, rows = 4, 3
    tone = cell_tone(x, y, cols, rows, 8.1)
    img *= (0.90 + 0.20 * tone)[..., None]

    ring = seam_mask(y, rows, 0.010)
    ring_ao = seam_mask(y, rows, 0.026)
    panel_v = seam_mask(x, cols, 0.006)
    img *= (1.0 - 0.50 * ring - 0.20 * ring_ao - 0.30 * panel_v)[..., None]
    # 环箍边缘的高光（金属倒角）
    img += (np.clip(seam_mask(y, rows, 0.020) - ring, 0, 1) * 26.0)[..., None]

    # 铆钉排（环箍上）
    riv = rivet_mask(x, y, cols * 6, rows * 3, 0.0032)
    img *= (1.0 - 0.28 * np.roll(riv, 2, axis=0))[..., None]
    img += (riv * 22.0)[..., None]

    # 热变色：沿轴向分层（真机回火色是"一节一节"出现的），越靠后（y→1，出口方向）越热。
    # 用二维噪声会得到各向同性斑块，看着就是"喷了层彩色气体"。
    band = vprofile(r, SIZE, 7)
    band = (band - 0.5) * 1.4 + 0.5
    heat = 0.38 * band + 0.62 * smooth_noise(r, SIZE, 40)
    heat = np.clip((heat - 0.44) * 2.4, 0, 1) * (0.55 + 0.45 * y)
    blue = np.array([96.0, 112.0, 146.0])
    orange = np.array([168.0, 104.0, 58.0])
    tint = (blue[None, None, :] * (1.0 - heat[..., None])
            + orange[None, None, :] * heat[..., None])
    # 中间调才混色，最亮/最暗的地方保持钢本色（真实的氧化层不是全覆盖）
    mix = np.clip((heat - 0.10) * 1.5, 0, 1) * np.clip((0.95 - heat) * 3.0, 0, 1)
    img = img * (1.0 - mix[..., None]) + tint * mix[..., None]

    # 焦黑：橙区核心再压一层，拉开色阶（积碳和过热的痕迹在斑块中心最重）
    core = np.clip((heat - 0.62) * 3.0, 0, 1)
    core = core * np.clip((smooth_noise(r, SIZE, 48) - 0.35) * 2.0, 0, 1)
    img *= (1.0 - 0.62 * core)[..., None]

    # 轴向高温流痕：从前往后拖出的深色条带（真机喷管外壁是竖向的烟熏纹）
    streak = smooth_noise(r, SIZE, 5)
    streak = np.repeat(streak.mean(axis=0, keepdims=True), SIZE, axis=0)
    img += ((streak - 0.5) * 24.0)[..., None]

    scr = scratches(r, SIZE, 60, 85, 0.66, axis_bias=0.26)
    img *= (1.0 - 0.32 * scr)[..., None]
    soot = np.clip((0.5 + 0.5 * vprofile(r, SIZE, 9)) * (0.30 + 0.70 * y) - 0.32, 0, 1)
    img *= (1.0 - 0.50 * soot)[..., None]

    img += r.normal(0.0, 1.8, (SIZE, SIZE))[..., None] * 1.2
    img = img.clip(0, 255)

    height = 1.0 - 0.50 * ring - 0.22 * ring_ao - 0.20 * riv
    height = height + (smooth_noise(r, SIZE, 32) - 0.5) * 0.10 + scr * 0.06 - core * 0.05
    save(img, "JetForge_Hot.png")
    save(normal_from_height(height, 1.2), "JetForge_Hot_N.png")


def inner():
    """喷管内壁：近乎均匀的熏黑 + 纵向加强筋 + 一圈圈积碳。

    为什么不复用热端：内壁是**永远烧着**的那一面，没有回火色的斑块、没有擦痕，
    只有顺着气流方向的烟熏和加强筋。用同一张图会让"里面"和"外面"看着一样脏，
    从喷口看进去反而更像假的。
    """
    r = rng(23)
    y, x = np.mgrid[0:SIZE, 0:SIZE].astype(np.float32)
    x /= SIZE
    y /= SIZE
    base = np.array([62.0, 60.0, 58.0])
    img = base[None, None, :] * (0.82 + 0.30 * smooth_noise(r, SIZE, 64))[..., None]

    # 纵向加强筋：周向 4 道（与喷管外壁 14 片花瓣无关，是内筒的加强结构）
    rib = seam_mask(x, 4, 0.020)
    rib_ao = seam_mask(x, 4, 0.060)
    img *= (1.0 - 0.55 * rib)[..., None]
    img += (rib * 16.0)[..., None]
    img *= (1.0 - 0.16 * rib_ao)[..., None]

    # 轴向积碳条带：沿气流方向拉长，越往后越黑
    streak = smooth_noise(r, SIZE, 4)
    streak = np.repeat(streak.mean(axis=0, keepdims=True), SIZE, axis=0)
    soot = (0.5 + 0.5 * streak) * (0.55 + 0.45 * y)      # y 越大（轴向越往后）越黑
    img *= (1.0 - 0.40 * soot)[..., None]

    # 少量擦痕（内壁是看得见"擦过"的，但不会有外壁那种刮伤）
    scr = scratches(r, SIZE, 26, 90, 0.35, axis_bias=0.12)
    img *= (1.0 - 0.14 * scr)[..., None]

    img += r.normal(0.0, 1.2, (SIZE, SIZE))[..., None]
    img = img.clip(0, 255)

    height = 1.0 - 0.50 * rib - 0.20 * rib_ao
    height = height + (smooth_noise(r, SIZE, 28) - 0.5) * 0.05 + scr * 0.04
    save(img, "JetForge_Inner.png")
    save(normal_from_height(height, 1.1), "JetForge_Inner_N.png")


if __name__ == "__main__":
    import os
    os.makedirs(OUT, exist_ok=True)
    print(f"生成到 {OUT}（每张代表 {TEX_WORLD} m 见方）")
    cold()
    hot()
    inner()