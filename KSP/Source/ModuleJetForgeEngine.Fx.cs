using System;
using System.Collections.Generic;
using System.Text;
using JetForge.Core;
using JetForge.FX;
using JetForge.UI;
using UnityEngine;

namespace JetForge.KSP
{
    /// <summary>
    /// 发动机模块的尾焰与音效部分（partial 的另一半在 ModuleJetForgeEngine.cs）。
    ///
    /// 拆出来的理由只有一条：主文件 2000 多行，改飞行逻辑和改尾焰的人翻的是同一份文件，
    /// 谁都不敢动。这里放的全是"外形挂件"——羽流挂点、Waterfall 每帧写参数、音效重建，
    /// 它们与推进计算没有耦合，只读 `DpThrust/DpSFT5` 这些读数接口。
    /// </summary>
    public partial class ModuleJetForgeEngine
    {

        /// <summary>
        /// 重建尾焰。外形变了喷口位置与口径都会变，所以它跟外形一起重建。
        /// 简单模式没有真几何，用 MeshDesignFromSimple 折出来的形状兜底。
        ///
        /// 只有 Waterfall 一条路：自绘的加法锥已经删了（模组关系改成硬依赖 Waterfall）。
        /// 没装 Waterfall 就没有羽流——这是明确的取舍，不再维护两套尾焰。
        /// </summary>
        public void AttachPlume()
        {
            try
            {
                if (part == null) return;
                _plumeDesign = UseSimple ? MeshDesignFromSimple(SimpleCfg) : Design;
                if (_plumeDesign == null) return;

                if (_waterfall == null) _waterfall = new JetForgeWaterfall();
                _waterfall.Sync(part, _plumeDesign, HasAfterburner());
                readPlume = _waterfall.Short();
                readPlumeScale = _waterfall.DescribeScaleText();
                if (_waterfall.Attached)
                {
                    // 一行进 KSP.log：玩家下次报"加力尾焰还是跟着油门变"时，
                    // 先看这里——override=True 且 values[0] 是我们灌的值，才说明灌进去了。
                    Debug.Log("[JetForge] 羽流 " + _waterfall.Describe() + "｜" + _waterfall.Diag());
                    AfterburnerPlumeTick(_abFrac, StockBridge.FloorInput(currentThrottle), 0.0);
                }
            }
            catch (Exception ex)
            {
                Debug.LogError("[JetForge] AttachPlume 失败：" + ex);
            }
        }

        /// <summary>
        /// 音效挂载。和尾焰一样：设计变了就重挂，因为"大机器更响"这个系数是按喷口半径算的。
        /// </summary>
        public void AttachSound()
        {
            try
            {
                EngineDesignData d = UseSimple ? MeshDesignFromSimple(SimpleCfg) : Design;
                if (d == null || part == null) return;
                if (_sound == null) _sound = new FX.JetForgeSound();
                _sound.Attach(part, d);
                readSound = _sound.Status;
                if (!_sound.Attached)
                    Debug.LogWarning("[JetForge] 音效：" + readSound + "（素材没被 GameDatabase 找到？）");
            }
            catch (Exception ex)
            {
                Debug.LogError("[JetForge] AttachSound 失败：" + ex);
            }
        }

        /// <summary>
        /// 零件没了就把音源一起收掉。AudioSource 挂在零件下的子物体上，
        /// 零件被销毁时 Unity 会带走它，但编辑器里反复替换零件时不一定——
        /// 留着就是下次进 VAB 听见"幽灵发动机"还在响。
        ///
        /// 注意不能写 <c>override</c>：PartModule 没有声明 OnDestroy 虚方法，
        /// Unity 是按消息名反射调用的，所以这里只是一个普通方法。
        /// </summary>
        private void OnDestroy()
        {
            try
            {
                if (_sound != null) { _sound.Detach(); _sound = null; }
            }
            // 探测豁免：零件销毁时 RSE 那边可能已经先一步没了，拆不掉是常态。
            catch (Exception) { }
        }

        /// <summary>
        /// 加力开关与 Waterfall 的同步。只在状态真的变了才动反射（Toggle 自己判），
        /// 所以每帧调用不心疼；状态栏文字也只在变化时重拼，别每帧生成字符串。
        /// </summary>
        private void AfterburnerPlumeTick(double frac, double throttle, double t7)
        {
            if (_waterfall == null) return;
            try
            {
                bool wet = frac > 0.02 && HasAfterburner();
                // 干湿不再整模块互切：湿羽流的强度就是加力比例本身，加力没点亮它是 0。
                // 干羽流的强度给油门，两者同时在，点火瞬间不会"啪一下换一套"。
                _waterfall.SetLevel(wet ? frac : 0.0, throttle);
                // 羽流长什么样要由**这一刻**的状态决定：出口温度多少就挑哪一档模板。
                _waterfall.SetHotState(t7);

                int st = !wet ? 1 : (frac > 0.98 ? 3 : 2);
                if (_plumeTextState != st)
                {
                    _plumeTextState = st;
                    readPlume = _waterfall.Short();
                    // 缩放要等 Waterfall 在 Start 里建好 FX 才写得进去，所以每换一次状态就重读一遍
                    readPlumeScale = _waterfall.DescribeScaleText();
                }
            }
            catch (Exception ex)
            {
                Debug.LogError("[JetForge] Waterfall 切换失败：" + ex);
            }
        }

        private int _plumeTextState = -1;

        /// <summary>
        /// 尾焰取数，全部来自求解器真量，没有一个装饰系数：
        /// 出口速度由「净推力 + 进气量」反推（Vj = V0 + F/ṁ）；
        /// 出口静温由能量平衡反推（h_t = h_e + V²/2）；
        /// 出口马赫再由静温算出来。这样壅塞与否自动一致——
        /// 收敛喷管壅塞时 Te/Tt 落在 0.83 附近，算出来 Me 就是 1 出头。
        /// 加力判据用 EGT &gt; T4 上限：干态下 T5 永远低于 T4，超过就只能是加力或喷油过量。
        /// </summary>
        private void UpdatePlume(double throttle, double thrustN, PerformanceGrid.Sample s, double abFrac)
        {
            // Waterfall 在管羽流时，我们只负责告诉它"现在是干态还是加力"。
            if (_waterfall != null && _waterfall.Attached)
            {
                // 没加力的机器只留热浪：喷流层（plume/shock/core）全部关掉。
                // 它们由马赫驱动，不是由我们灌的油门控制器驱动，所以强度灌 0 也关不干净。
                //
                // 热浪自己还有一道闸：它是"喷口里那股热气的折射"，关车以后就该没有。
                // 以前它被无条件留着，于是玩家关掉发动机还能看见一根在抖的柱子。
                bool alive = EngineIgnited && !flameout && thrustN > 1.0;
                _waterfall.SetPlumeLayers(HasAfterburner(), alive && throttle > 0.04);
                AfterburnerPlumeTick(abFrac, throttle, s.T7);
                // 喘振的视觉：真机回火是"砰、砰"地一闪一闪，不是整体变亮。
                // Phase 是累计秒数，除以拍长才是 0~1 的位相。
                double bangPeriod = Math.Max(0.05, JetForge.Core.Surge.BangPeriod);
                _waterfall.SetSurge(_surge.Active, _surge.Phase / bangPeriod,
                                    _surge.Active ? Math.Min(1.0, 0.55 + 0.45 * _surge.PeakRisk) : 0.0);
                // 颜色跟排气温度、亮度跟推力：Waterfall 只认"干/加力谁亮"，火色是我们写进材质的。
                // 参考推力取设计点：满油门时正好 1.0，慢车就暗下去。
                _waterfall.SetVisualState(s.T7 > 1.0 ? s.T7 : DpT5(), thrustN, DpThrust());
                readPlumeColor = _waterfall.VisualNote;
            }
            // 没有自绘尾焰可退：Waterfall 硬依赖，挂不上就是没羽流（状态栏会写原因）。
        }
    }
}
