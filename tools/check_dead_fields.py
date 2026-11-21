"""闸门：揪出「只声明过一次」的字段。

为什么需要它：这类字段编译不报、运行不报、回归也不报——它就是安静地躺在那里。
真出过的事（§234/§235）：字段表里一份 `Choices` 名单与 `NozzleKind` 的枚举顺序不符；
`TechLevel.T4UncooledLimit` 写着一个"无冷却叶片上限 1250 K"，而没有任何一处读它，
于是 1960s 的机器照样按 1390 K 算；`Version.KspVersionMin` 写 1.8.0，
发布元数据早在几轮前就改成了 1.12.0。三件事的共同点是：**没人读，所以永远对不上**。

口径（保守，宁可漏报）：字段名的全部出现次数 == 声明次数（1 次）才报。
`Fan.Diameter` 这种被反射按路径读的字段在代码里也会出现，因此不会被误报；
真的只靠反射读的（存档、`[KSPField]`、按路径读写），放进
`tools/dead_fields_allow.txt`，每行 `名字  # 为什么只靠反射`。

用法：`python tools/check_dead_fields.py [--write]`（`--write` 只重排白名单，不自动加条目）
"""
import io, os, re, sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
ALLOW = os.path.join(HERE, 'dead_fields_allow.txt')

DECL = re.compile(
    r'^\s*(?:public|internal|protected|private)'
    r'(?:\s+(?:static|readonly|const|override|virtual|new|volatile))*'
    r'\s+([\w<>\[\],\.\? ]+?)\s+(\w+)\s*(?:;|=)', re.M)
SKIP_TYPES = ('class', 'struct', 'interface', 'enum', 'delegate')
SKIP_NAMES = ('get', 'set', 'value')


def strip_comments(text):
    """去掉行注释与块注释，字符串字面量保留（注释里的同名字不算使用，但字符串里的算）。

    字符串要保留：`DesignSchema.Read(d, "Fan.Diameter")` 这类按路径读写得靠字符串里的名字
    才看得出来有人在用。副作用是注释里提到的名字会丢失——那正是我们想要的：
    「注释里写过」不等于「有人读过」。
    """
    out = []
    i, n = 0, len(text)
    while i < n:
        c = text[i]
        if c == '"' or c == "'":
            q = c
            out.append(c)
            i += 1
            while i < n:
                out.append(text[i])
                if text[i] == '\\':
                    if i + 1 < n:
                        out.append(text[i + 1])
                        i += 2
                        continue
                if text[i] == q:
                    i += 1
                    break
                i += 1
            continue
        if c == '/' and i + 1 < n and text[i + 1] == '/':
            while i < n and text[i] != '\n':
                i += 1
            continue
        if c == '/' and i + 1 < n and text[i + 1] == '*':
            i += 2
            while i + 1 < n and not (text[i] == '*' and text[i + 1] == '/'):
                i += 1
            i += 2
            continue
        out.append(c)
        i += 1
    return ''.join(out)


def sources():
    out = []
    for base in ('KSP/Source', 'JetForge.Test'):
        for dp, dn, fn in os.walk(os.path.join(ROOT, base.replace('/', os.sep))):
            for f in fn:
                if f.endswith('.cs'):
                    out.append(os.path.join(dp, f))
    return out


def load_allow():
    allow = {}
    if not os.path.exists(ALLOW):
        return allow
    for line in io.open(ALLOW, encoding='utf-8'):
        line = line.split('#')[0].strip()
        if not line:
            continue
        parts = line.split(None, 1)
        allow[parts[0]] = parts[1] if len(parts) > 1 else ''
    return allow


def scan():
    texts = {}
    for p in sources():
        try:
            texts[p] = strip_comments(io.open(p, encoding='utf-8', errors='replace').read())
        except IOError:
            continue

    decls = {}
    for p, t in texts.items():
        if os.sep + 'Core' + os.sep not in p:
            continue
        for m in DECL.finditer(t):
            typ, name = m.group(1).strip(), m.group(2)
            if typ.split()[-1] in SKIP_TYPES or '(' in typ or name in SKIP_NAMES:
                continue
            decls.setdefault(name, []).append((os.path.relpath(p, ROOT), typ))

    dead = []
    for name, where in sorted(decls.items()):
        word = re.compile(r'(?<![\w])' + re.escape(name) + r'(?![\w])')
        total = sum(len(word.findall(t)) for t in texts.values())
        if total <= len(where):
            dead.append((name, where[0][0], where[0][1]))
    return dead


def main():
    try:
        sys.stdout.reconfigure(encoding='utf-8')   # GBK 控制台打不出中文，会被判成乱码
    except Exception:
        pass
    allow = load_allow()
    dead = scan()
    bad = [d for d in dead if d[0] not in allow]
    stale = [k for k in allow if k not in set(d[0] for d in dead)]

    for name, path, typ in dead:
        tag = '白名单' if name in allow else 'FAIL'
        print('  %-5s %-26s %-22s %s' % (tag, name, typ[:22], path))
    for k in stale:
        print('  提示  白名单里的 %s 已经不再"只声明一次"（可能是真用起来了，可以删掉这条）' % k)

    if bad:
        print('FAIL 只写不读的字段 %d 个（不参与计算的字段比没有更糟：它看起来像一道闸）' % len(bad))
        print('     要么用起来，要么删掉；只靠反射读的写进 tools/dead_fields_allow.txt 并给出理由。')
        return 1
    print('OK 没有只写不读的字段（白名单 %d 条）' % len(allow))
    return 0


if __name__ == '__main__':
    sys.exit(main())