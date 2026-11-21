using System;
using System.Collections.Generic;
using System.Text;

namespace JetForge.Core
{
    /// <summary>
    /// 撤销栈的通用部分。两份设计各有一条（残酷模式 58 个字段那份、简单模式 18 个字段那份），
    /// 逻辑一模一样——把"改之前的快照"压进去、手松开时才压一次、栈顶封顶二十步。
    ///
    /// 指纹与克隆都由调用方给：这两件事跟具体那份设计绑在一起，
    /// 写进通用类就只能反射，而反射在 **KSP 的 Mono 上比手写慢一个数量级**（还在每帧路径上）。
    /// </summary>
    public class UndoStack<T> where T : class
    {
        /// <summary>最多记几步。</summary>
        public const int Depth = 20;

        private readonly List<T> _stack = new List<T>();

        /// <summary>上一次记下来的那份（用来在真正改动时当作"改之前"）。</summary>
        private T _known;

        /// <summary>上一次记下来时的指纹；null 表示还没起过。</summary>
        private string _sig;

        private readonly Func<T, string> _fp;
        private readonly Func<T, T> _clone;

        public UndoStack(Func<T, string> fingerprint, Func<T, T> clone)
        {
            if (fingerprint == null) throw new ArgumentNullException("fingerprint");
            if (clone == null) throw new ArgumentNullException("clone");
            _fp = fingerprint;
            _clone = clone;
        }

        public int Count { get { return _stack.Count; } }

        public bool Has { get { return _stack.Count > 0; } }

        /// <summary>换机器、换存档、载入预设之后调用：以前那些步不再属于这台机器。</summary>
        public void Reset(T now)
        {
            _stack.Clear();
            _known = now != null ? _clone(now) : null;
            _sig = _fp(now);
        }

        /// <summary>
        /// 手松开时调一次。返回 true 表示这一步记进去了（调用方可以用它来提示"能撤销了"）。
        /// </summary>
        public bool Bind(T now)
        {
            if (now == null) return false;
            string sig = _fp(now);
            if (_sig == null)
            {
                _known = _clone(now);
                _sig = sig;
                return false;
            }
            if (string.Equals(sig, _sig, StringComparison.Ordinal)) return false;

            // 记的是"改之前"那份，不是当前这份。
            _stack.Insert(0, _known != null ? _clone(_known) : _clone(now));
            while (_stack.Count > Depth) _stack.RemoveAt(_stack.Count - 1);
            _known = _clone(now);
            _sig = sig;
            return true;
        }

        /// <summary>退回上一步，返回那份设计的克隆；栈空返回 null。</summary>
        public T Pop()
        {
            if (_stack.Count == 0) return null;
            T d = _stack[0];
            _stack.RemoveAt(0);
            T back = d != null ? _clone(d) : null;
            _known = back != null ? _clone(back) : null;
            _sig = _fp(back);
            return back;
        }

        public void Clear()
        {
            _stack.Clear();
            _known = null;
            _sig = null;
        }

        /// <summary>按钮上那句话；没有可撤销的也照样给一句（按钮不能忽隐忽现）。</summary>
        public string Describe()
        {
            if (!Has) return Loc.T("撤销（没有可退的）", "Undo (nothing to undo)");
            return string.Format(Loc.T("撤销（还能退 {0} 步）", "Undo ({0} step(s) left)"), Count);
        }
    }

    /// <summary>
    /// 把 `ConfigNode` 走成一行文本。**不能直接用 `ConfigNode.ToString()`**：
    /// 在这个 KSP 版本里它没有被重写，返回的是类型名 "ConfigNode"——拿它当指纹，
    /// 任何两份设计都"一样"。（第 111 段那条"逐字段一致"就是这么空着的。）
    /// </summary>
    internal static class ConfigFingerprint
    {
        public static string Of(ConfigNode n)
        {
            if (n == null) return "";
            try
            {
                StringBuilder sb = new StringBuilder(1024);
                Dump(n, sb);
                return sb.ToString();
            }
            catch (Exception)
            {
                // 算不出指纹不该让设计器崩：退回"这次不算改动"，宁可少记一步。
                return null;
            }
        }

        private static void Dump(ConfigNode n, StringBuilder sb)
        {
            sb.Append('[').Append(n.name).Append(']');
            foreach (ConfigNode.Value v in n.values)
            {
                sb.Append(v.name).Append('=').Append(v.value).Append(';');
            }
            foreach (ConfigNode c in n.nodes) Dump(c, sb);
        }
    }

    /// <summary>
    /// 残酷模式（`EngineDesignData`，58 个字段）的撤销栈。
    ///
    /// 导轨（`WizardUndo`）只管"按一下向导，整台机器换掉"那一步；设计器里那几十根滑杆
    /// 一根都没有退路——玩家拖错了一根、或者想试试"缩短两厘米会怎样"，只能凭记忆把数拖回去。
    /// 而这个模组最要紧的玩法恰恰是"调一格看读数怎么变"，也就是会大量地试错。
    ///
    /// 做法：**不拦每一次改动，只在手松开的时候看一眼有没有真的变**。
    /// 拖动一根滑杆时 `GUILayout.HorizontalSlider` 每帧都返回新值，逐帧记录会把二十步
    /// 全花在同一次拖动上；等 `MouseUp`/`KeyUp` 再比一次快照，一次拖动就只记一步。
    /// "有没有真的变"用存档格式算指纹——逐个手写比较表迟早会漏，
    /// 而 `Save` 到 `ConfigNode` 本来就覆盖了全部字段（新加字段自动跟上）。
    ///
    /// 只留二十步：撤销栈不是版本管理，玩家要的是"刚才那下拖错了"。
    /// </summary>
    public sealed class DesignUndo : UndoStack<EngineDesignData>
    {
        public DesignUndo() : base(Fingerprint, Clone) { }

        private static EngineDesignData Clone(EngineDesignData d)
        {
            return d != null ? d.Clone() : null;
        }

        /// <summary>
        /// 设计的指纹。走存档格式而不是手写字段列表：
        /// 手写的比较表跟字段表是两份真相，加字段时只改一处就漏。
        /// </summary>
        public static string Fingerprint(EngineDesignData d)
        {
            if (d == null) return "";
            try
            {
                ConfigNode n = new ConfigNode("design");
                d.Save(n);
                return ConfigFingerprint.Of(n);
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    /// <summary>
    /// 简单模式（`SimpleDesign`，18 个字段）的撤销栈。
    ///
    /// 单开一条而不是共用一条：两种模式的编辑对象是两个互不相干的类，
    /// 混在一条栈上的话"在简单模式里退一步"可能退回一份残酷模式的设计——那不是撤销，是换机器。
    /// 两条栈各自记各自的，切模式时都不清空（切回来还能退）。
    ///
    /// 指纹同样走 `SimpleDesignPersistence.Save`：简单模式也有一堆滑杆，
    /// 而存盘键表就是这个类"有哪些可改的数"的现成清单。
    /// </summary>
    public sealed class SimpleUndo : UndoStack<SimpleDesign>
    {
        public SimpleUndo() : base(Fingerprint, Clone) { }

        private static SimpleDesign Clone(SimpleDesign d)
        {
            return d != null ? d.Clone() : null;
        }

        public static string Fingerprint(SimpleDesign d)
        {
            if (d == null) return "";
            try
            {
                ConfigNode n = new ConfigNode("simple");
                SimpleDesignPersistence.Save(d, n);
                return ConfigFingerprint.Of(n);
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}