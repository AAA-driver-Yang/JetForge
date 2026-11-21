# -*- coding: utf-8 -*-
"""
生成 JetForge 的喷气发动机音效素材。

不再自己合成噪声——上一版合成出来"难听且不真实"，根子在**没有叶片通过频率的谐波结构**，
那是靠调滤波器参数补不出来的。现在直接拿 RocketSoundEnhancement 的喷气音源做派生
（RSE 是 GPL-3.0，用户已明确授权使用），并沿用它的分层思路：
每个音源一层，各自有自己的音量/音高曲线，靠叠加出"细"的质感。

处理只做三件事：混单声道、截一段稳态、等功率交叉淡化做成无缝循环。
不做重滤波——音色是 RSE 调好的，改滤波就是在破坏它。
"""

import io
import os
import struct
import wave

import numpy as np
ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

# RSE 的音源在游戏目录里，不进本仓库：用 JETFORGE_RSE_SOUNDS 指过去
SRC = os.environ.get("JETFORGE_RSE_SOUNDS", "")
DST = os.path.join(ROOT, "GameData/JetForge/Sounds")
OUT_SR = 44100
DUR = 4.0
FADE = 0.25


def load(name):
    p = os.path.join(SRC, name + ".wav")
    with wave.open(p, "rb") as w:
        ch, sw, sr, n = w.getnchannels(), w.getsampwidth(), w.getframerate(), w.getnframes()
        raw = w.readframes(n)
    if sw != 2:
        raise RuntimeError("期望 16 bit：" + p)
    x = np.frombuffer(raw, dtype="<i2").astype(np.float64) / 32768.0
    if ch > 1:
        x = x.reshape(-1, ch).mean(axis=1)
    if sr != OUT_SR:
        idx = np.arange(int(len(x) * OUT_SR / sr)) * (sr / OUT_SR)
        x = np.interp(idx, np.arange(len(x)), x)
    return x


def cut(x, skip=1.0):
    """跳过头一秒（起音/循环点那一带往往有噪声包络），取 DUR 秒稳态。"""
    a = int(skip * OUT_SR)
    need = int(DUR * OUT_SR)
    if len(x) < a + need:
        a = 0
    if len(x) < need:
        x = np.tile(x, int(np.ceil(need / len(x))))
    return x[a:a + need].copy()


def loopify(x):
    """等功率交叉淡化：尾巴淡到开头，循环点听不出接缝。"""
    n = int(FADE * OUT_SR)
    head = x[:n].copy()
    tail = x[-n:].copy()
    t = np.linspace(0.0, np.pi / 2.0, n)
    mixed = head * np.sin(t) + tail * np.cos(t)
    y = np.concatenate([mixed, x[n:len(x) - n]])
    return y


def norm(x, peak=0.80):
    m = np.max(np.abs(x))
    if m < 1e-9:
        return x
    return x * (peak / m)


def write(name, x):
    x = loopify(norm(x))
    pcm = np.clip(x * 32767.0, -32768, 32767).astype("<i2")
    p = os.path.join(DST, name + ".wav")
    with wave.open(p, "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(OUT_SR)
        w.writeframes(pcm.tobytes())
    lo = np.sum(np.abs(np.fft.rfft(x[:OUT_SR]))[:60] ** 2)
    hi = np.sum(np.abs(np.fft.rfft(x[:OUT_SR]))[200:] ** 2)
    print("  %-14s %6.2f s %7d B  低频/高频能量比 %8.1f" % (name, len(x) / OUT_SR, os.path.getsize(p), lo / max(hi, 1e-9)))


def soft(x, k=1.6):
    return np.tanh(x * k) / np.tanh(k)


def main():
    if not os.path.isdir(DST):
        os.makedirs(DST)
    print("从 RSE 音源派生（GPL-3.0）：")

    # ---- 巡航层：照抄 RSE 给原版涡扇的那几层，一层一个音源 ----
    write("jf_air", cut(load("Turbine_Air")))
    write("jf_air2", cut(load("Turbine_Air-2")))
    write("jf_res250", cut(load("Turbofan_Resonance-250hz")))
    write("jf_res50", cut(load("Turbofan_Resonance-50hz")))
    write("jf_big180", cut(load("Turbine_Big-180hz")))
    write("jf_whine", cut(load("Turbine_Whine-2.1khz")))
    # 大涵道才有的风扇层：扇叶通过频率那一声"呜——"
    write("jf_fan", cut(load("Turbine_Small-1.8khz")))

    # ---- 加力层：低频轰鸣 + 高频啸叫（真机加力是这两样一起上）----
    bass = cut(load("Turbine_Bass-180hz"))
    big = cut(load("Turbine_Big-180hz"))
    write("jf_ab_roar", soft(0.6 * bass + 0.5 * big))
    scr = cut(load("Turbine_Small-1.8khz"))
    scr2 = cut(load("Turbine_Small-1.28khz"))
    write("jf_ab_screech", soft(0.55 * scr + 0.45 * scr2, 2.2))

    # 旧的三个文件已经没人引用，删掉免得混淆
    for dead in ("jet_rumble", "turbine_whine", "ab_roar"):
        p = os.path.join(DST, dead + ".wav")
        if os.path.exists(p):
            os.remove(p)
            print("  删除旧文件 %s.wav" % dead)


if __name__ == "__main__":
    main()