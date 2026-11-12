p = 'Program.cs'
s = open(p, encoding='utf-8').read()
start = s.index('        Console.WriteLine("6) Bordas para redimensionar')
end = s.index('        // ---------------- 4. Excel ----------------')
new = open('new_tests.cs.txt', encoding='utf-8').read()
s = s[:start] + new + s[end:]
if '// ---------------- investigação 2 ----------------' in s:
    a = s.index('        // ---------------- investigação 2 ----------------')
    b = s.index('        // ---------------- 4. Excel ----------------', a)
    s = s[:a] + s[b:]
s = s.replace('new[] { "A", "B", "C" }, new[] { "D", "E", "F" } }));',
              'new[] { "A", "B", "C" }, new[] { "D", "E", "F" } }, 240));')
open(p, 'w', encoding='utf-8').write(s)
print("ok")
