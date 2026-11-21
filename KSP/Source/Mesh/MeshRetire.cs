using System.Collections.Generic;
using UnityEngine;

namespace JetForge
{
    /// <summary>
    /// 退役网格的延迟销毁。
    ///
    /// 为什么不能"重建时立刻 Destroy 掉旧网格"：KSP 在编辑器里按住 ALT 复制零件、
    /// 或者用对称模式放置时，是**克隆整个零件 GameObject**——克隆出来的那一件和我们
    /// 原来那件共用同一个 Mesh 资源。这时候任何一边重建，立刻 Destroy 掉那个 Mesh，
    /// 另一边手里的 MeshFilter 就指向一块已经销毁的资源：渲染器还在、材质还在、
    /// 三角面数是 0 → 玩家看到的就是"零件网格全部变透明"，而且只有重新读载具
    /// （两件都从零重建）才能恢复。
    ///
    /// 所以要"攒一批再查有没有别人在用"。但**查的方式很要紧**：
    /// 第一版用 `Resources.FindObjectsOfTypeAll&lt;MeshFilter&gt;()` 全场扫，这个 API 会把
    /// 内存里所有该类对象（含未激活、含别的场景）拉成一个数组，再逐个比对——
    /// 飞行中花瓣网格随喷管开度频繁重建时，攒满一批就全场扫一次，帧就卡一下。
    ///
    /// 现在的判法：**只在编辑器场景里查**（克隆只发生在编辑器；飞行中零件都是
    /// 新建的，不存在共享网格），而且查的是"当前这艘船 + 编辑器里那艘船的零件"，
    /// 不是全内存。飞行中直接同步销毁，一个额外分配都没有。
    /// </summary>
    public static class MeshRetire
    {
        private static readonly List<Mesh> Pending = new List<Mesh>(32);

        /// <summary>攒到这么多就先冲一次（只在编辑器里冲）。</summary>
        private const int FlushAt = 24;

        public static int PendingCount { get { return Pending.Count; } }

        /// <summary>把一个"暂时不能确定是否独占"的网格挂起来。</summary>
        public static void Retire(Mesh m)
        {
            if (m == null) return;

            // 飞行中不存在克隆件：网格只被这一个 MeshFilter 用着，立刻销毁最省事，
            // 也不用攒队列、不用扫场景。
            if (!HighLogic.LoadedSceneIsEditor)
            {
                UnityEngine.Object.Destroy(m);
                return;
            }

            Pending.Add(m);
            if (Pending.Count >= FlushAt) Flush();
        }

        /// <summary>
        /// 冲一次队列：只销毁"船上已经没有 MeshFilter 引用"的那些。
        /// 返回真的销毁了几个（自检用它证明"复制出来的两件都还看得见"）。
        /// </summary>
        public static int Flush()
        {
            if (Pending.Count == 0) return 0;

            List<MeshFilter> live = LiveFilters();
            if (live == null)
            {
                // 编辑器还没准备好（读不到船）：这一批一个都不销毁，宁可多留几 MB。
                // 但队列不能无限长——攒太多就整批放手（泄漏几 MB，好过猜错把它杀掉）。
                if (Pending.Count > 256) Pending.Clear();
                return 0;
            }
            int killed = 0;
            for (int i = 0; i < Pending.Count; i++)
            {
                Mesh m = Pending[i];
                if (m == null) continue;
                bool used = false;
                for (int j = 0; j < live.Count; j++)
                {
                    MeshFilter mf = live[j];
                    if (mf != null && mf.sharedMesh == m) { used = true; break; }
                }
                if (used) continue;
                UnityEngine.Object.Destroy(m);
                killed++;
            }
            Pending.Clear();
            return killed;
        }

        /// <summary>
        /// 当前"活着"的 MeshFilter —— 只收这艘船（编辑器里就是正在装的那艘）的零件。
        /// 场景里还有别人的东西（KSC 的静态模型、其它飞船），它们不共享我们的网格，
        /// 没必要为了判引用把它们也拉进来。
        /// </summary>
        private static List<MeshFilter> LiveFilters()
        {
            List<MeshFilter> list = new List<MeshFilter>(128);
            try
            {
                if (EditorLogic.fetch != null && EditorLogic.fetch.ship != null && EditorLogic.fetch.ship.parts != null)
                {
                    List<Part> ps = EditorLogic.fetch.ship.parts;
                    for (int i = 0; i < ps.Count; i++)
                    {
                        Part p = ps[i];
                        if (p == null) continue;
                        MeshFilter[] fs = p.GetComponentsInChildren<MeshFilter>(true);
                        for (int k = 0; k < fs.Length; k++) if (fs[k] != null) list.Add(fs[k]);
                    }
                }
            }
            catch (System.Exception)
            {
                // 编辑器还没准备好：返回 null，让 Flush 决定"这一批先留着"
                return null;
            }
            return list;
        }
    }
}