using System;
using System.Collections.Generic;

namespace JetForge.Core
{
    /// <summary>
    /// 向导改动的一步撤销。
    ///
    /// 设计向导是"一次给出整台机器"的：按下去之后，风扇直径、压比、级数、喷管一起变，
    /// 而且外形网格会跟着重建。玩家看着不顺眼想退回去，设计器里没有"上一步"——
    /// 只能凭记忆把十几个参数逐个调回来，那等于没有退路。
    ///
    /// 所以每次把初稿交给设计器之前，先把**交出去之前那一份**存进这里。只存设计数据，不存网格：
    /// 网格是按设计重建出来的，设计回去了网格自然跟着回去。
    ///
    /// 只留三步：撤销栈不是版本管理，玩家要的是"刚才那下按错了"。
    /// </summary>
    public static class WizardUndo
    {
        /// <summary>最多记几步。</summary>
        public const int Depth = 3;

        private static readonly List<EngineDesignData> _stack = new List<EngineDesignData>();

        public static int Count { get { return _stack.Count; } }

        public static bool Has { get { return _stack.Count > 0; } }

        /// <summary>记下"交出去之前"的设计。存的是克隆，之后别人改原对象也不影响这一步。</summary>
        public static void Push(EngineDesignData before)
        {
            if (before == null) return;
            _stack.Add(before.Clone());
            while (_stack.Count > Depth) _stack.RemoveAt(0);
        }

        /// <summary>退回上一步，返回那份设计的克隆；栈空返回 null。</summary>
        public static EngineDesignData Pop()
        {
            if (_stack.Count == 0) return null;
            EngineDesignData d = _stack[_stack.Count - 1];
            _stack.RemoveAt(_stack.Count - 1);
            return d != null ? d.Clone() : null;
        }

        /// <summary>换了一台机器（或换了存档）就别再跨机器撤销了。</summary>
        public static void Clear()
        {
            _stack.Clear();
        }

        /// <summary>按钮上那句话；没有可撤销的返回空串。</summary>
        public static string Describe()
        {
            if (!Has) return "";
            return string.Format(Loc.T("撤销向导改动（还能退 {0} 步）", "Undo wizard change ({0} step(s) left)"), Count);
        }
    }
}