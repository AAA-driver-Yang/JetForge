"""生成 JetForge 的 1024² 分件贴图（每张配切线空间法线图）。

为什么要分件：以前整套机器只有三张（冷端/热端/喷管内壁），结果是
「短舱、机匣箍、进气唇口、风扇、离心盘」全是一个色，玩家看不出这台机器有几层结构；
进气道零件更是直接借用了发动机的冷端图，一眼就知道"这是同一张贴图拉出来的"。

这一版把材质分成七套，每一套说一件事：

  JetForge_Cold           发动机冷端压制蒙皮（拼接板 + 检修口盖 + 百叶 + 铆钉）
  JetForge_Hot            发动机热端（回火色 + 环箍 + 积碳）
  JetForge_Inner          喷管内壁（熏黑 + 纵向加强筋）
  JetForge_Casing         机匣（铸铁砂面 + 螺栓圈 + 油迹）—— 不再和蒙皮共用一张
  JetForge_Lip            进气唇口（抛光钢 + 同心车削纹 + 细小磕碰）
  JetForge_Intake_Outer   进气道外罩（浅色漆面 + 长条壁板 + 检修铭牌 + 轻微雨痕）
  JetForge_Intake_Inner   进气道内壁（深色抗侵蚀衬里 + 细砂粒 + 轴向流痕）

分辨率 512 → 1024。TEX_WORLD 保持不变（0.5 m 见方），所以平铺密度没动，
只是每米从 1024 像素变成 2048 像素——铆钉能画到 2 mm 量级，走近了才看得出。

无缝的做法还是老三样：缝/铆钉用周期函数、口盖等特征用取模坐标、噪声本身可平铺。
验收要按"平铺 6 次"看，不能只看一张。
"""
import numpy as np
ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
from PIL import Image

SIZE = 1024
OUT = os.path.join(ROOT, "GameData/JetForge/Textures")
TEX_WORLD = 0.5


def rng(seed):
    return np.random.default_rng(seed)


def seam_mask(coord, count, width):
    f = (coord * count) % 1.0
    d = np.minimum(f, 1.0 - f)
    return np.clip(1.0 - d / width, 0.0, 1.0)


def rivet_mask(x, y, cols, rows, radius):
    px = (x * cols) % 1.0
    py = (y * rows) % 1.0
    dx = np.minimum(px, 1.0 - px) / cols
    dy = np.minimum(py, 1.0 - py) / rows
    return np.clip(1.0 - np.sqrt(dx * dx + dy * dy) / radius, 0.0, 1.0)


def cell_tone(x, y, cols, rows, seed=0.0):
    ix = np.floor(x * cols)
    iy = np.floor(y * rows)
    h = np.sin(ix * 12.9898 + iy * 78.233 + seed) * 43758.5453
    return h - np.floor(h)


def smooth_noise(r, size, scale):
    small = r.random((max(2, size // scale), max(2, size // scale)))
    img = Image.fromarray((small * 255).astype(np.uint8)).resize((size, size), Image.BICUBIC)
    return np.asarray(img).astype(np.float32) / 255.0


def vprofile(r, size, n=8):
    pts = r.random(n)
    xs = np.linspace(0, n, size, endpoint=False)
    i = np.floor(xs).astype(int) % n
    j = (i + 1) % n
    t = xs - np.floor(xs)
    t = t * t * (3.0 - 2.0 * t)
    return (pts[i] * (1.0 - t) + pts[j] * t).reshape(size, 1)


def scratches(r, size, count, length, strength, axis_bias=0.35):
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
    print("  ", name, arr.shape)


def grid():
    y, x = np.mgrid[0:SIZE, 0:SIZE].astype(np.float32)
    x /= SIZE
    y /= SIZE
    return x, y


def fasteners(img, x, y, points, radius=0.004, depth=0.34, lit=22.0):
    """在若干位置画紧固件（取模坐标，所以跨边界也不会断）。"""
    for cx, cy in points:
        px = np.minimum(np.abs(x - cx), 1.0 - np.abs(x - cx))
        py = np.minimum(np.abs(y - cy), 1.0 - np.abs(y - cy))
        d = np.sqrt(px * px + py * py)
        dot = np.clip(1.0 - d / radius, 0, 1)
        ring = np.clip(1.0 - np.abs(d - radius * 0.85) / (radius * 0.35), 0, 1)
        img *= (1.0 - depth * dot)[..., None]
        img += (dot * lit)[..., None]
        img *= (1.0 - 0.18 * ring)[..., None]
    return img


def paneling(img, x, y, cols, rows, seam=0.008, ao=0.020, depth=0.50, tone_amp=0.13, seed=3.7):
    tone = cell_tone(x, y, cols, rows, seed)
    img = img * (0.935 + tone_amp * tone)[..., None]
    pv = seam_mask(x, cols, seam)
    ph = seam_mask(y, rows, seam)
    panel = np.clip(pv + ph, 0, 1)
    pao = np.clip(seam_mask(x, cols, ao) * 0.5 + seam_mask(y, rows, ao) * 0.5, 0, 1)
    return img * (1.0 - depth * panel - 0.10 * pao)[..., None], panel


# ---------------------------------------------------------------- 冷端
def cold():
    """发动机冷端：压制蒙皮。178 灰是量出来的（196 进游戏死白，166 又偏闷）。"""
    r = rng(7)
    x, y = grid()
    base = np.array([178.0, 182.0, 186.0])
    grad = 0.92 + 0.14 * smooth_noise(r, SIZE, 128)
    img = base[None, None, :] * grad[..., None]
    img, panel = paneling(img, x, y, 6, 3, 0.008, 0.020, 0.50)

    # 检修口盖：边框 + 四角紧固件。上沿提亮、下沿压暗才有"板嵌进去"的厚度。
    x0, x1 = 0.30 - 0.11, 0.30 + 0.11
    y0, y1 = 0.34 - 0.10, 0.34 + 0.10
    inside = ((x > x0) & (x < x1)) & ((y > y0) & (y < y1))
    dxe = np.minimum(np.abs(x - x0), np.abs(x - x1))
    dye = np.minimum(np.abs(y - y0), np.abs(y - y1))
    edge = np.maximum(np.clip(1.0 - dxe / 0.004, 0, 1) * ((y > y0) & (y < y1)),
                      np.clip(1.0 - dye / 0.004, 0, 1) * ((x > x0) & (x < x1)))
    img *= (1.0 - 0.45 * edge)[..., None]
    img *= np.where(inside, 1.030, 1.0)[..., None]
    img += (np.clip(1.0 - np.abs(y - y0) / 0.0016, 0, 1) * inside * 20.0)[..., None]
    img *= (1.0 - 0.20 * np.clip(1.0 - np.abs(y - y1) / 0.0020, 0, 1) * inside)[..., None]
    img = fasteners(img, x, y, [(x0 + 0.010, y0 + 0.010), (x1 - 0.010, y0 + 0.010),
                                (x0 + 0.010, y1 - 0.010), (x1 - 0.010, y1 - 0.010)],
                    radius=0.0045, depth=0.35, lit=22.0)

    # 百叶散热口：一组横向短槽（压太死变成黑洞，留一点金属反光）
    louver = np.zeros_like(x)
    for i in range(7):
        cy = 0.66 + i * 0.014
        px = np.minimum(np.abs(x - 0.72), 1.0 - np.abs(x - 0.72))
        louver = np.maximum(louver, np.clip(1.0 - (px / 0.11), 0, 1)
                            * np.clip(1.0 - np.abs(y - cy) / 0.0035, 0, 1))
    img *= (1.0 - 0.55 * louver)[..., None]
    img += (louver * 8.0)[..., None]

    # 沿缝的铆钉（周向 6 条、轴向 3 条各一排）
    riv = np.maximum(rivet_mask(x, y, 6, 3, 0.0035) * (panel > 0.02), 0.0)
    img *= (1.0 - 0.22 * riv)[..., None]
    img += (riv * 14.0)[..., None]

    img *= (1.0 - 0.16 * scratches(r, SIZE, 60, 160, 0.9, axis_bias=0.30))[..., None]
    img *= (1.0 - 0.10 * smooth_noise(r, SIZE, 24))[..., None]
    img += r.normal(0.0, 1.3, (SIZE, SIZE))[..., None]
    img = img.clip(0, 255)

    height = 1.0 + 0.35 * riv - 0.30 * panel - 0.45 * louver - 0.30 * edge
    height += (smooth_noise(r, SIZE, 40) - 0.5) * 0.06
    save(img, "JetForge_Cold.png")
    save(normal_from_height(height, 2.0), "JetForge_Cold_N.png")


# ---------------------------------------------------------------- 热端
def hot():
    """热端：回火色顺着轴向分层（vprofile），加环箍和积碳。"""
    r = rng(11)
    x, y = grid()
    base = np.array([96.0, 88.0, 82.0])
    img = base[None, None, :] * (0.9 + 0.2 * smooth_noise(r, SIZE, 96))[..., None]

    # 回火：蓝→金→紫，沿 v 方向（轴向）分层
    band = vprofile(r, SIZE, 9)
    blue = np.clip((band - 0.55) / 0.45, 0, 1)
    gold = np.clip(1.0 - np.abs(band - 0.45) / 0.22, 0, 1)
    img[..., 2] += blue * 46.0
    img[..., 0] += gold * 30.0
    img[..., 1] += gold * 16.0
    img[..., 0] -= blue * 20.0

    # 环箍：轴向 5 道（真机热端一段一段用箍连起来）
    ring = seam_mask(y, 5, 0.010) + seam_mask(y, 5, 0.030) * 0.35
    img *= (1.0 - 0.42 * np.clip(ring, 0, 1))[..., None]
    img += (np.clip(seam_mask(y, 5, 0.003), 0, 1) * 26.0)[..., None]

    # 积碳：中段偏暗的烟熏（沿轴向）
    soot = np.clip(vprofile(r, SIZE, 5).reshape(SIZE, 1) * 1.1 - 0.25, 0, 1)
    img *= (1.0 - 0.30 * soot)[..., None]
    img *= (1.0 - 0.12 * smooth_noise(r, SIZE, 32))[..., None]
    img += r.normal(0.0, 1.6, (SIZE, SIZE))[..., None]
    img = img.clip(0, 255)

    height = 1.0 - 0.45 * np.clip(ring, 0, 1) + (smooth_noise(r, SIZE, 48) - 0.5) * 0.09
    height -= 0.10 * soot
    save(img, "JetForge_Hot.png")
    save(normal_from_height(height, 1.8), "JetForge_Hot_N.png")


# ---------------------------------------------------------------- 喷管内壁
def inner():
    """喷管内壁：均匀熏黑 + 纵向加强筋。没有蒙皮拼缝——从喷口看进去不该有板缝。"""
    r = rng(23)
    x, y = grid()
    base = np.array([52.0, 50.0, 49.0])
    img = base[None, None, :] * (0.88 + 0.24 * smooth_noise(r, SIZE, 64))[..., None]

    rib = seam_mask(x, 12, 0.012)
    rib_ao = seam_mask(x, 12, 0.045)
    img *= (1.0 - 0.38 * rib - 0.14 * rib_ao)[..., None]
    img += (np.clip(seam_mask(x, 12, 0.004), 0, 1) * 16.0)[..., None]

    soot = np.clip(vprofile(r, SIZE, 7).reshape(SIZE, 1) * 1.2 - 0.1, 0, 1)
    img *= (1.0 - 0.25 * soot)[..., None]
    img *= (1.0 - 0.14 * scratches(r, SIZE, 26, 120, 0.35, axis_bias=0.12))[..., None]
    img += r.normal(0.0, 1.2, (SIZE, SIZE))[..., None]
    img = img.clip(0, 255)

    height = 1.0 - 0.50 * rib - 0.20 * rib_ao + (smooth_noise(r, SIZE, 28) - 0.5) * 0.05
    save(img, "JetForge_Inner.png")
    save(normal_from_height(height, 1.1), "JetForge_Inner_N.png")


# ---------------------------------------------------------------- 机匣
def casing():
    """机匣：铸铁砂面 + 螺栓圈 + 油迹。

    和冷端那张的区别是"不是蒙皮"：没有整齐的板缝，只有砂型铸造的粗糙面、
    一圈法兰螺栓、顺着重力往下流的老油迹。压气机机匣真机就是这种脏兮兮的灰。
    """
    r = rng(31)
    x, y = grid()
    base = np.array([118.0, 116.0, 112.0])
    # 砂面：细颗粒 + 中频斑块
    grain = smooth_noise(r, SIZE, 8)
    blotch = smooth_noise(r, SIZE, 40)
    img = base[None, None, :] * (0.86 + 0.20 * blotch + 0.10 * grain)[..., None]
    img += (r.normal(0.0, 4.5, (SIZE, SIZE)))[..., None]   # 砂型颗粒的逐像素噪声

    # 法兰：周向一道宽箍（轴向位置固定），上下各一条棱
    flange = np.clip(1.0 - np.abs(y - 0.5) / 0.055, 0, 1)
    img *= (1.0 + 0.10 * flange)[..., None]
    img *= (1.0 - 0.40 * np.clip(1.0 - np.abs(np.abs(y - 0.5) - 0.055) / 0.004, 0, 1))[..., None]

    # 螺栓圈：法兰上均匀 16 颗
    bolts = [(i / 16.0 + 0.031, 0.5) for i in range(16)]
    img = fasteners(img, x, y, bolts, radius=0.0075, depth=0.40, lit=26.0)

    # 油迹：从若干点往下（轴向）流的深色带
    oil = np.zeros_like(x)
    for cx, ln in ((0.18, 0.30), (0.46, 0.22), (0.78, 0.36)):
        px = np.minimum(np.abs(x - cx), 1.0 - np.abs(x - cx))
        along = ((y - 0.5) % 1.0)
        streak = np.clip(1.0 - px / 0.020, 0, 1) * np.clip(1.0 - along / ln, 0, 1)
        oil = np.maximum(oil, streak * (0.6 + 0.4 * smooth_noise(r, SIZE, 90)))
    img *= (1.0 - 0.35 * oil)[..., None]

    img *= (1.0 - 0.10 * scratches(r, SIZE, 40, 90, 0.5, axis_bias=0.5))[..., None]
    img = img.clip(0, 255)

    height = 1.0 + (grain - 0.5) * 0.16 + 0.25 * flange
    height -= 0.12 * np.clip(1.0 - np.abs(np.abs(y - 0.5) - 0.055) / 0.004, 0, 1)
    save(img, "JetForge_Casing.png")
    save(normal_from_height(height, 1.6), "JetForge_Casing_N.png")


# ---------------------------------------------------------------- 唇口
def lip():
    """唇口：抛光钢 + 同心车削纹。

    唇口是进气道唯一"亮"的地方：真机唇缘是裸金属，磨得发亮。
    同心纹用周向缝做（u 方向一道道的），因为唇口是车出来的回转面。
    """
    r = rng(41)
    x, y = grid()
    base = np.array([196.0, 198.0, 200.0])
    img = base[None, None, :] * (0.97 + 0.06 * smooth_noise(r, SIZE, 160))[..., None]

    # 车削纹：周向（x）上密集的浅纹
    turn = seam_mask(x, 90, 0.30)
    img *= (1.0 - 0.10 * turn)[..., None]
    img += (np.clip(seam_mask(x, 90, 0.06), 0, 1) * 10.0)[..., None]

    # 一圈亮边（唇尖）+ 一点点氧化变暗（靠后段）
    shine = np.clip(1.0 - np.abs(y - 0.5) / 0.16, 0, 1)
    img += (shine * 18.0)[..., None]
    img *= (1.0 - 0.10 * np.clip((y - 0.62) / 0.38, 0, 1))[..., None]

    # 磕碰：几个小凹点（唇缘最容易被石子崩）
    for cx, cy in ((0.12, 0.50), (0.37, 0.52), (0.62, 0.48), (0.88, 0.51)):
        px = np.minimum(np.abs(x - cx), 1.0 - np.abs(x - cx))
        py = np.minimum(np.abs(y - cy), 1.0 - np.abs(y - cy))
        d = np.sqrt(px * px + py * py)
        img *= (1.0 - 0.45 * np.clip(1.0 - d / 0.014, 0, 1))[..., None]

    img += r.normal(0.0, 0.9, (SIZE, SIZE))[..., None]
    img = img.clip(0, 255)

    height = 1.0 - 0.12 * turn + (smooth_noise(r, SIZE, 120) - 0.5) * 0.03
    save(img, "JetForge_Lip.png")
    save(normal_from_height(height, 1.0), "JetForge_Lip_N.png")


# ---------------------------------------------------------------- 进气道外罩
def intake_outer():
    """进气道外罩：浅色漆面 + 长条壁板 + 检修铭牌 + 雨痕。

    故意和发动机冷端拉开：漆面更浅更素（飞行器蒙皮），壁板是长条而不是方板，
    再加一块小铭牌——玩家一眼能认出"这是进气道，不是机身那段发动机"。
    """
    r = rng(53)
    x, y = grid()
    base = np.array([186.0, 188.0, 190.0])
    img = base[None, None, :] * (0.95 + 0.08 * smooth_noise(r, SIZE, 60))[..., None]

    # 长条壁板：周向 4 段、轴向 2 段（比发动机的 6×3 更"飞行器"）
    img, panel = paneling(img, x, y, 4, 2, 0.007, 0.018, 0.44, tone_amp=0.10, seed=8.1)

    # 铆钉排：沿两条长缝
    riv = rivet_mask(x, y, 4, 8, 0.0032)
    img *= (1.0 - 0.20 * riv)[..., None]
    img += (riv * 12.0)[..., None]

    # 检修铭牌：一块小矩形（淡蓝底 + 深色边），不带字
    mx0, mx1 = 0.62 - 0.045, 0.62 + 0.045
    my0, my1 = 0.24 - 0.020, 0.24 + 0.020
    inside = ((x > mx0) & (x < mx1)) & ((y > my0) & (y < my1))
    img = np.where(inside[..., None], img * 0.92 + np.array([8.0, 12.0, 22.0]), img)
    dxe = np.minimum(np.abs(x - mx0), np.abs(x - mx1))
    dye = np.minimum(np.abs(y - my0), np.abs(y - my1))
    edge = np.maximum(np.clip(1.0 - dxe / 0.003, 0, 1) * ((y > my0) & (y < my1)),
                      np.clip(1.0 - dye / 0.003, 0, 1) * ((x > mx0) & (x < mx1)))
    img *= (1.0 - 0.50 * edge)[..., None]

    # 雨痕：沿轴向（v）往下拖的浅灰带
    rain = np.zeros_like(x)
    for cx, ln in ((0.09, 0.55), (0.33, 0.40), (0.71, 0.62), (0.90, 0.30)):
        px = np.minimum(np.abs(x - cx), 1.0 - np.abs(x - cx))
        along = (y % 1.0)
        rain = np.maximum(rain, np.clip(1.0 - px / 0.012, 0, 1) * np.clip(1.0 - along / ln, 0, 1))
    img *= (1.0 - 0.10 * rain)[..., None]

    img *= (1.0 - 0.09 * scratches(r, SIZE, 30, 120, 0.6, axis_bias=0.15))[..., None]
    img += r.normal(0.0, 1.1, (SIZE, SIZE))[..., None]
    img = img.clip(0, 255)

    height = 1.0 - 0.30 * panel + 0.30 * riv - 0.20 * edge
    height += (smooth_noise(r, SIZE, 36) - 0.5) * 0.05
    save(img, "JetForge_Intake_Outer.png")
    save(normal_from_height(height, 1.7), "JetForge_Intake_Outer_N.png")


# ---------------------------------------------------------------- 进气道内壁
def intake_inner():
    """进气道内壁：深色抗侵蚀衬里 + 细砂粒 + 轴向流痕。

    和喷管内壁（熏黑 + 加强筋）必须能分开看：这里是"砂子磨过"的哑光面，
    没有筋、没有积碳，只有一道道顺着气流的浅痕和零星崩边。
    """
    r = rng(67)
    x, y = grid()
    base = np.array([74.0, 72.0, 70.0])
    img = base[None, None, :] * (0.92 + 0.16 * smooth_noise(r, SIZE, 50))[..., None]

    # 抗侵蚀涂层：大块色调略变（出厂是哑光灰绿，用旧了发褐）
    coat = smooth_noise(r, SIZE, 90)
    img *= (1.0 - 0.09 * coat + 0.03)[..., None]
    img[..., 0] += coat * 6.0

    # 砂粒：细小亮点（真机内壁被砂子打出麻面）
    speck = (r.random((SIZE, SIZE)) > 0.9985).astype(np.float32)
    img += (speck * 26.0)[..., None]

    # 轴向流痕：沿 v 的细长明暗
    flow = np.zeros_like(x)
    for cx, ln in ((0.07, 0.7), (0.21, 0.5), (0.44, 0.85), (0.63, 0.45), (0.82, 0.75)):
        px = np.minimum(np.abs(x - cx), 1.0 - np.abs(x - cx))
        along = (y % 1.0)
        flow = np.maximum(flow, np.clip(1.0 - px / 0.008, 0, 1) * np.clip(1.0 - along / ln, 0, 1))
    img *= (1.0 - 0.14 * flow)[..., None]

    img *= (1.0 - 0.12 * scratches(r, SIZE, 44, 150, 0.5, axis_bias=0.10))[..., None]
    img += r.normal(0.0, 1.4, (SIZE, SIZE))[..., None]
    img = img.clip(0, 255)

    height = 1.0 + (smooth_noise(r, SIZE, 20) - 0.5) * 0.10 - 0.10 * flow + 0.08 * speck
    save(img, "JetForge_Intake_Inner.png")
    save(normal_from_height(height, 1.3), "JetForge_Intake_Inner_N.png")


if __name__ == "__main__":
    import os
    import sys
    # Windows 控制台默认 GBK，直接 print 中文/上标会抛 UnicodeEncodeError。
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    os.makedirs(OUT, exist_ok=True)
    print(f"生成到 {OUT}（{SIZE}²，每张代表 {TEX_WORLD} m 见方）")
    cold()
    hot()
    inner()
    casing()
    lip()
    intake_outer()
    intake_inner()