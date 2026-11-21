import io, sys

p = 'Program.cs'
s = io.open(p, encoding='utf-8').read()
body = io.open('_sec29.cs.txt', encoding='utf-8').read().rstrip() + '\n'

block = '\n' + body
i = s.rindex('\n        private static bool HasFinding')
s = s[:i] + block + s[i:]

anchor = '''            Console.WriteLine("=== 28. 加力点火边界：高空点不着、油多会吹熄 ===");
            RunAbLightCases(d);'''
add = anchor + '''

            Console.WriteLine();
            Console.WriteLine("=== 29. 全参数空间不变式体检：油门单调、高度单调、加力不低于干态 ===");
            RunInvariantCases();'''
if anchor not in s:
    print('ANCHOR MISSING'); sys.exit(1)
s = s.replace(anchor, add, 1)
io.open(p, 'w', encoding='utf-8', newline='').write(s)
print('spliced ok')