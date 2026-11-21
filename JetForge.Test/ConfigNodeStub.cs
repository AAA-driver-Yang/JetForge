using System;
using System.Collections.Generic;

// KSP 的 ConfigNode 在 Assembly-CSharp 里，命令行回归拿不到。
// 这里只实现 Persistence 与 DesignUndo 用到的那几个成员，语义照 KSP 抄：
//   - SetValue 覆盖同名值（overwrite=true 时）
//   - AddNode 永远新建一个同名节点（这正是要在测试里盯住的地方：重复保存会堆节点）
//   - GetNode 返回第一个同名节点
//   - values / nodes 是公开可枚举的列表（真 KSP 里分别是 ValueList / NodeList）
//
// 「像不像真的」这件事在 2026-11 被抓过一次：这个桩原来既没有 `values`/`nodes` 也没有
// ToString 重写，于是测试里 `node.ToString()` 拿到的是类型名 "ConfigNode"——第 111 段那句
// "退回来的设计逐字段一致"因此一直是空断言（两份设计压出来都是 "ConfigNode"）。
// 桩太不像，比没有桩更糟：它让一条假断言看起来验了两年。
public class ConfigNode
{
    /// <summary>真 KSP 里的 `ConfigNode.Value`：一个键值对，字段名就叫 name/value。</summary>
    public class Value
    {
        public string name;
        public string value;

        public Value() { }

        public Value(string name, string value)
        {
            this.name = name;
            this.value = value;
        }
    }

    /// <summary>真 KSP 里的 `ConfigNode.ValueList`，就是一堆 Value。</summary>
    public sealed class ValueList : List<Value> { }

    /// <summary>真 KSP 里的 `ConfigNode.NodeList`。</summary>
    public sealed class NodeList : List<ConfigNode> { }

    public string name = "node";

    public ValueList values = new ValueList();
    public NodeList nodes = new NodeList();

    public ConfigNode() { }
    public ConfigNode(string name) { this.name = name; }

    public int ValueCount { get { return values.Count; } }
    public int NodeCount { get { return nodes.Count; } }
    public IList<ConfigNode> Nodes { get { return nodes; } }

    public void SetValue(string key, string value, bool overwrite = true)
    {
        for (int i = 0; i < values.Count; i++)
        {
            if (values[i].name != key) continue;
            if (!overwrite) return;                       // 不覆盖：什么都不做
            values[i].value = value;
            return;
        }
        values.Add(new Value(key, value));
    }

    public string GetValue(string key)
    {
        for (int i = 0; i < values.Count; i++)
            if (values[i].name == key) return values[i].value;
        return null;
    }

    public bool HasValue(string key) { return GetValue(key) != null; }

    public ConfigNode AddNode(string name)
    {
        ConfigNode n = new ConfigNode(name);
        nodes.Add(n);
        return n;
    }

    public ConfigNode AddNode(ConfigNode n)
    {
        nodes.Add(n);
        return n;
    }

    public ConfigNode GetNode(string name)
    {
        for (int i = 0; i < nodes.Count; i++)
            if (nodes[i].name == name) return nodes[i];
        return null;
    }

    public List<ConfigNode> GetNodes(string name)
    {
        List<ConfigNode> r = new List<ConfigNode>();
        for (int i = 0; i < nodes.Count; i++)
            if (nodes[i].name == name) r.Add(nodes[i]);
        return r;
    }

    public void RemoveNodes(string name)
    {
        nodes.RemoveAll(delegate (ConfigNode n) { return n.name == name; });
    }

    public void RemoveValues(string key)
    {
        values.RemoveAll(delegate (Value v) { return v.name == key; });
    }

    /// <summary>真 KSP 的 ConfigNode 打印出来就是节点文本；桩以前没重写，打印的是类型名。</summary>
    public override string ToString()
    {
        System.Text.StringBuilder sb = new System.Text.StringBuilder();
        Write(sb, 0);
        return sb.ToString();
    }

    // ---- 文件读写：照 KSP 的 ConfigNode.Save / ConfigNode.Load 抄个够用的版本 ----
    // 真实 KSP 里这两个是 ConfigNode 的成员（Load 是静态），格式是
    //   NAME
    //   {
    //       key = value
    //   }
    // 这里只支持一层子节点 + 一层缩进，足够 JetForge 的设计节点用。
    public void Save(string path)
    {
        System.Text.StringBuilder sb = new System.Text.StringBuilder();
        Write(sb, 0);
        System.IO.File.WriteAllText(path, sb.ToString(), System.Text.Encoding.UTF8);
    }

    private void Write(System.Text.StringBuilder sb, int depth)
    {
        string pad = new string(' ', depth * 4);
        sb.Append(pad).Append(name).Append('\n');
        sb.Append(pad).Append('{').Append('\n');
        for (int i = 0; i < values.Count; i++)
            sb.Append(pad).Append("    ").Append(values[i].name).Append(" = ").Append(values[i].value).Append('\n');
        for (int i = 0; i < nodes.Count; i++) nodes[i].Write(sb, depth + 1);
        sb.Append(pad).Append('}').Append('\n');
    }

    public static ConfigNode Load(string path)
    {
        if (!System.IO.File.Exists(path)) return null;
        string[] lines = System.IO.File.ReadAllLines(path);
        ConfigNode root = null;
        System.Collections.Generic.Stack<ConfigNode> stack = new System.Collections.Generic.Stack<ConfigNode>();
        for (int i = 0; i < lines.Length; i++)
        {
            string t = lines[i].Trim().TrimStart('\uFEFF');
            if (t.Length == 0) continue;
            if (t == "{") continue;
            if (t == "}")
            {
                if (stack.Count > 0) stack.Pop();
                continue;
            }
            // KSP 的写法是「名字单独一行，下一行才是 {」，所以要先看下一行。
            int peek = i + 1;
            while (peek < lines.Length && lines[peek].Trim().Length == 0) peek++;
            bool opens = peek < lines.Length && lines[peek].Trim().StartsWith("{");
            if (opens)
            {
                string nm = t.EndsWith("{") ? t.Substring(0, t.Length - 1).Trim() : t;
                ConfigNode n = new ConfigNode(nm);
                if (stack.Count == 0)
                {
                    if (root == null) root = n;
                    else root.AddNode(n);
                }
                else stack.Peek().AddNode(n);
                stack.Push(n);
                continue;
            }
            int eq = t.IndexOf('=');
            if (eq > 0 && stack.Count > 0)
                stack.Peek().SetValue(t.Substring(0, eq).Trim(), t.Substring(eq + 1).Trim(), true);
        }
        return root;
    }
}