using System;
using System.Collections.Generic;
using System.IO;

namespace JetForge.Core
{
    /// <summary>
    /// 玩家自己存下来的发动机。真机预设（Presets）是固定的四台起点，
    /// 这里是"我调出来的那台"——存成文件，换飞船、重开存档都还在。
    ///
    /// 为什么要有：设计目前只活在零件的存档节点里。想在另一枚火箭上装"同一台发动机"，
    /// 除了复制零件没有别的办法，而复制零件得先进 VAB 找到那台、再拆下来。
    /// </summary>
    public sealed class UserDesignEntry
    {
        public string Name;
        public string Path;
        public DateTime Modified;
    }

    public static class UserLibrary
    {
        public const string Extension = ".cfg";
        public const int MaxNameLength = 40;

        /// <summary>
        /// 文件名不能直接用玩家起的名字：Windows 不允许 \ / : * ? " &lt; &gt; |，
        /// 而且 Unity 下路径非法会在写文件时抛异常——那时候玩家的设计已经点过「应用」了，来不及。
        /// </summary>
        public static string Sanitize(string name)
        {
            if (string.IsNullOrEmpty(name)) return "未命名";
            char[] bad = Path.GetInvalidFileNameChars();
            System.Text.StringBuilder sb = new System.Text.StringBuilder(name.Length);
            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                bool ok = true;
                for (int k = 0; k < bad.Length; k++) if (bad[k] == c) { ok = false; break; }
                if (c == '\\' || c == '/' || c == ':' || c == '*' || c == '?'
                    || c == '"' || c == '<' || c == '>' || c == '|') ok = false;
                sb.Append(ok ? c : '_');
            }
            string s = sb.ToString().Trim();
            if (s.Length == 0) return "未命名";
            if (s.Length > MaxNameLength) s = s.Substring(0, MaxNameLength);
            return s;
        }

        public static void EnsureDir(string dir)
        {
            if (string.IsNullOrEmpty(dir)) return;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
        }

        public static List<UserDesignEntry> List(string dir)
        {
            List<UserDesignEntry> o = new List<UserDesignEntry>();
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return o;
            string[] files;
            try { files = Directory.GetFiles(dir, "*" + Extension); }
            catch (Exception) { return o; }

            for (int i = 0; i < files.Length; i++)
            {
                UserDesignEntry e = new UserDesignEntry();
                e.Path = files[i];
                e.Name = Path.GetFileNameWithoutExtension(files[i]);
                try { e.Modified = File.GetLastWriteTime(files[i]); }
                catch (Exception) { e.Modified = DateTime.MinValue; }
                o.Add(e);
            }
            o.Sort(delegate (UserDesignEntry a, UserDesignEntry b)
            {
                return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            });
            return o;
        }

        /// <summary>存一份。同名直接覆盖（玩家改完再存一次是最常见的动作）。</summary>
        public static string Save(string dir, string name, EngineDesignData design)
        {
            if (design == null) return null;
            EnsureDir(dir);
            string path = Path.Combine(dir, Sanitize(name) + Extension);
            Snapshot(dir, path);
            try
            {
                ConfigNode root = new ConfigNode("JETFORGE_USER");
                ConfigNode node = root.AddNode(EngineDesignData.NodeName);
                design.Save(node);
                root.Save(path);
                return path;
            }
            catch (Exception)
            {
                // 玩家点「保存」的那一刻不能抛：磁盘满、目录只读、名字撞上系统保留名，
                // 这些都只该让按钮旁边多一行红字。
                return null;
            }
        }

        /// <summary>
        /// 读一份。失败不抛异常——文件可能是手改坏的、旧版本写的、或者干脆不是我们的文件。
        /// 读取失败只返回 null 并给出原因，绝不能让设计器窗口崩在打开的那一刻。
        /// </summary>
        public static EngineDesignData Load(string path, out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                error = Loc.T("文件不存在", "file not found");
                return null;
            }
            try
            {
                ConfigNode root = ConfigNode.Load(path);
                if (root == null)
                {
                    error = "不是有效的 cfg";
                    return null;
                }
                ConfigNode node = root.GetNode(EngineDesignData.NodeName);
                if (node == null)
                {
                    error = "文件里没有 " + EngineDesignData.NodeName + " 节点";
                    return null;
                }
                return EngineDesignData.Load(node);
            }
            catch (Exception e)
            {
                error = e.GetType().Name + ": " + e.Message;
                return null;
            }
        }

        public static bool Delete(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return false;
            try
            {
                File.Delete(path);
                return true;
            }
            catch (Exception) { return false; }
        }

        /// <summary>
        /// 改名。**搬文件，不重写内容**——文件可能是老版本存的、也可能被玩家手改过，
        /// 重新序列化会把不认识的键洗掉。改名只是换文件名，内容一个字节都不该动。
        /// 目标已有同名文件时拒绝并给理由（不覆盖别人的设计）。
        /// </summary>
        public static string Rename(string dir, string path, string newName, out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                error = Loc.T("文件不存在", "file not found");
                return null;
            }
            string dst = Path.Combine(string.IsNullOrEmpty(dir) ? Path.GetDirectoryName(path) : dir,
                                      Sanitize(newName) + Extension);
            if (string.Equals(dst, path, StringComparison.OrdinalIgnoreCase)) return path;
            if (File.Exists(dst))
            {
                error = Loc.T("已经有叫「", "there is already one named '") + Path.GetFileNameWithoutExtension(dst) + Loc.T("」的了", "'");
                return null;
            }
            try
            {
                File.Move(path, dst);
                return dst;
            }
            catch (Exception e)
            {
                error = e.GetType().Name + ": " + e.Message;
                return null;
            }
        }

        /// <summary>
        /// 复制一份。同样按字节搬——副本必须和原件的效果完全一样，
        /// 一旦这里改成「读出来再存」，副本就可能因为版本差异与原件不同，
        /// 而玩家复制一份的动机恰恰是「保住现在这台」。
        /// </summary>
        public static string Copy(string dir, string path, string newName, out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                error = Loc.T("文件不存在", "file not found");
                return null;
            }
            string dst = Path.Combine(string.IsNullOrEmpty(dir) ? Path.GetDirectoryName(path) : dir,
                                      Sanitize(newName) + Extension);
            if (string.Equals(dst, path, StringComparison.OrdinalIgnoreCase))
            {
                error = Loc.T("副本不能和原件同名", "a copy cannot reuse the original name");
                return null;
            }
            if (File.Exists(dst))
            {
                error = Loc.T("已经有叫「", "there is already one named '") + Path.GetFileNameWithoutExtension(dst) + Loc.T("」的了", "'");
                return null;
            }
            try
            {
                File.Copy(path, dst);
                return dst;
            }
            catch (Exception e)
            {
                error = e.GetType().Name + ": " + e.Message;
                return null;
            }
        }

        /// <summary>给复制用的缺省名：原件名 + 「副本」，撞名就一直加号，不覆盖任何东西。</summary>
        public static string CopyName(string dir, string name)
        {
            string baseName = Sanitize(name) + " 副本";
            string candidate = baseName;
            int n = 2;
            while (true)
            {
                string p = Path.Combine(dir ?? "", candidate + Extension);
                if (string.IsNullOrEmpty(dir) || !File.Exists(p)) return candidate;
                candidate = baseName + " " + n;
                n++;
                if (n > 99) return candidate;   // 兜底：不给玩家一个无限循环的按钮
            }
        }

        // ---------------- 存档历史 ----------------

        public const string HistoryDir = ".history";
        public const int HistoryKeep = 5;

        static string HistoryFolder(string dir, string baseName)
        {
            return Path.Combine(dir, HistoryDir, Sanitize(baseName));
        }

        /// <summary>
        /// 最近一次留历史失败的理由（成功或不涉及留历史时为 null）。
        /// 存历史是附加动作，失败不该让「保存」失败；但也不能无声——
        /// 玩家以为有后悔药、点「史」却发现空的，那种失望比没这个功能更糟。
        /// </summary>
        public static string LastSnapshotError { get; private set; }

        /// <summary>
        /// 覆盖之前先把旧的存一份。玩家改完再存一次是最常见的动作，也是最容易后悔的动作：
        /// 这台机器调坏了，想回到「上一版」的时候，原文件已经被覆盖没了。
        /// 只留最近 <see cref="HistoryKeep"/> 份，多了删最旧的——存档目录不该无声长大。
        /// </summary>
        static void Snapshot(string dir, string path)
        {
            LastSnapshotError = null;
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
                string folder = HistoryFolder(dir ?? Path.GetDirectoryName(path),
                                              Path.GetFileNameWithoutExtension(path));
                EnsureDir(folder);
                string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                string dst = Path.Combine(folder, stamp + Extension);
                int n = 2;
                while (File.Exists(dst))
                {
                    dst = Path.Combine(folder, stamp + "-" + n + Extension);
                    n++;
                    if (n > 99) break;
                }
                File.Copy(path, dst);
                Prune(folder);
            }
            catch (Exception e)
            {
                LastSnapshotError = e.GetType().Name + ": " + e.Message;
            }
        }

        static void Prune(string folder)
        {
            try
            {
                string[] files = Directory.GetFiles(folder, "*" + Extension);
                if (files.Length <= HistoryKeep) return;
                System.Array.Sort(files, StringComparer.Ordinal);   // 时间戳命名，字典序即时间序
                for (int i = 0; i < files.Length - HistoryKeep; i++) File.Delete(files[i]);
            }
            catch (Exception e)
            {
                // 删不掉旧历史不是致命错（磁盘满/被占用），但要说出来：不然历史会一直涨。
                LastSnapshotError = Loc.T("旧版本没删掉：", "could not prune old versions: ") + e.GetType().Name;
            }
        }

        /// <summary>某个设计的历史快照，新的在前。</summary>
        public static List<UserDesignEntry> History(string dir, string name)
        {
            List<UserDesignEntry> o = new List<UserDesignEntry>();
            if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(name)) return o;
            string folder = HistoryFolder(dir, name);
            string[] files;
            try
            {
                if (!Directory.Exists(folder)) return o;
                files = Directory.GetFiles(folder, "*" + Extension);
            }
            catch (Exception) { return o; }
            System.Array.Sort(files, StringComparer.Ordinal);
            for (int i = files.Length - 1; i >= 0; i--)
            {
                UserDesignEntry e = new UserDesignEntry();
                e.Path = files[i];
                e.Name = Path.GetFileNameWithoutExtension(files[i]);
                try { e.Modified = File.GetLastWriteTime(files[i]); }
                catch (Exception) { e.Modified = DateTime.MinValue; }
                o.Add(e);
            }
            return o;
        }

        /// <summary>
        /// 回到某个快照。还原之前把当前状态也存一份——「恢复」本身也可能点错，
        /// 那一下如果不可逆，这个按钮就变成了删除键。
        /// </summary>
        public static string Restore(string dir, string name, string snapshotPath, out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(name))
            {
                error = Loc.T("没有指定设计", "no design named");
                return null;
            }
            if (string.IsNullOrEmpty(snapshotPath) || !File.Exists(snapshotPath))
            {
                error = Loc.T("这份历史已经不在了", "that snapshot is gone");
                return null;
            }
            string target = Path.Combine(dir, Sanitize(name) + Extension);
            try
            {
                Snapshot(dir, target);
                File.Copy(snapshotPath, target, true);
                return target;
            }
            catch (Exception e)
            {
                error = e.GetType().Name + ": " + e.Message;
                return null;
            }
        }

        // ---------------- 分享（跟别人交换设计） ----------------

        /// <summary>
        /// 分享用的子目录名。玩家之间怎么传这个文件不归模组管（论坛附件、网盘、聊天窗口都行），
        /// 模组只管两头：从库里挑一份放进去，或把别人放进来的一份收进库里。
        /// </summary>
        public const string SharedDirName = "Shared";

        public static string SharedDir(string dir)
        {
            return Path.Combine(dir ?? "", SharedDirName);
        }

        /// <summary>列 Shared 里的设计文件；目录还不存在时返回空表，不建目录。</summary>
        public static List<UserDesignEntry> Shared(string dir)
        {
            return List(SharedDir(dir));
        }

        /// <summary>
        /// 把库里一份放进 Shared。**按字节拷，不重新序列化**——同一个理由见 Copy：
        /// 分享出去的东西应该与原件一模一样，少一个键都算改别人的设计。
        /// 重名不覆盖，追号（与 CopyName 同一策略）。
        /// </summary>
        public static string Export(string dir, string path, out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                error = Loc.T("文件不存在", "file not found");
                return null;
            }
            string to = SharedDir(dir);
            string baseName = Path.GetFileNameWithoutExtension(path);
            string name = baseName;
            int n = 2;
            string dst = Path.Combine(to, name + Extension);
            try
            {
                EnsureDir(to);
                while (File.Exists(dst) && n <= 99)
                {
                    name = baseName + " " + n;
                    dst = Path.Combine(to, name + Extension);
                    n++;
                }
                File.Copy(path, dst);
                return dst;
            }
            catch (Exception e)
            {
                error = e.GetType().Name + ": " + e.Message;
                return null;
            }
        }

        /// <summary>
        /// 把 Shared 里一份收进库。重名**也照收**，但改名叫「原名（导入）」——拒绝的话
        /// 玩家就得先删掉自己那台才能收别人的，那是拿别人的东西换自己的。
        /// </summary>
        public static string Import(string dir, string sharedPath, out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(sharedPath) || !File.Exists(sharedPath))
            {
                error = Loc.T("文件不存在", "file not found");
                return null;
            }
            string baseName = Path.GetFileNameWithoutExtension(sharedPath);
            string name = baseName;
            string dst = Path.Combine(dir ?? "", Sanitize(name) + Extension);
            if (File.Exists(dst))
            {
                baseName = Sanitize(baseName) + "（导入）";
                name = baseName;
                dst = Path.Combine(dir, name + Extension);
                int n = 2;
                while (File.Exists(dst) && n <= 99)
                {
                    name = baseName + " " + n;
                    dst = Path.Combine(dir, name + Extension);
                    n++;
                }
            }
            try
            {
                EnsureDir(dir);
                File.Copy(sharedPath, dst);
                return dst;
            }
            catch (Exception e)
            {
                error = e.GetType().Name + ": " + e.Message;
                return null;
            }
        }
    }
}