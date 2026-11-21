using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;

namespace JetForge.Core
{
    /// <summary>
    /// 设计参数的指纹。用来回答一个很具体的问题："这张性能表，还是不是同一个设计算出来的？"
    ///
    /// 为什么不用手写字段列表：设计结构会一直加字段（这二十来轮里加了涡轮轮毂比、
    /// 加力喷油量、设计马赫/高度、T7…），手写的指纹一旦漏了新字段，缓存就会把"改过参数的
    /// 旧表"当新表返回——玩家改完参数发现推力没变，这比慢 0.2 秒难受得多。
    ///
    /// 所以这里用反射把**所有**公有实例字段按名字排序后串起来。漏字段这件事就从
    /// "要靠记性"变成"物理上不可能"（新加的公有字段自动进指纹）。
    /// </summary>
    public static class DesignSignature
    {
        public static string Of(object obj)
        {
            StringBuilder sb = new StringBuilder(256);
            Walk(obj, sb, new HashSet<object>());
            return sb.ToString();
        }

        private static void Walk(object o, StringBuilder sb, HashSet<object> seen)
        {
            if (o == null) { sb.Append("null;"); return; }

            Type t = o.GetType();
            if (o is string || t.IsEnum || t.IsPrimitive)
            {
                sb.Append(t.Name).Append('=');
                if (o is double) sb.Append(((double)o).ToString("R", CultureInfo.InvariantCulture));
                else if (o is float) sb.Append(((float)o).ToString("R", CultureInfo.InvariantCulture));
                else sb.Append(Convert.ToString(o, CultureInfo.InvariantCulture));
                sb.Append(';');
                return;
            }

            // 只有引用类型可能成环；值类型（struct spec）直接展开。
            if (!t.IsValueType)
            {
                if (seen.Contains(o)) { sb.Append("cycle;"); return; }
                seen.Add(o);
            }

            sb.Append(t.Name).Append('{');
            FieldInfo[] fs = t.GetFields(BindingFlags.Public | BindingFlags.Instance);
            Array.Sort(fs, delegate (FieldInfo a, FieldInfo b) { return string.CompareOrdinal(a.Name, b.Name); });
            for (int i = 0; i < fs.Length; i++)
            {
                sb.Append(fs[i].Name).Append(':');
                Walk(fs[i].GetValue(o), sb, seen);
            }
            sb.Append('}');
        }
    }
}