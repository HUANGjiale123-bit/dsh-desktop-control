import io, sys

sys.stdout.reconfigure(encoding='utf-8', errors='replace')

p = 'src/Probe.cs'
lines = io.open(p, encoding='utf-8', newline='').read().split('\n')

# 找 WalkBounded 定义的范围：从它的注释块开始，到 FindContentWindows 的注释之前
start = None
for i, l in enumerate(lines):
    if 'private static void WalkBounded(' in l:
        # 往上找注释块起始
        j = i
        while j > 0 and not lines[j - 1].strip().startswith('/// <summary>'):
            j -= 1
        start = j
        break
assert start is not None, '找不到 WalkBounded'

end = None
for i in range(start, len(lines)):
    if 'private static List<IntPtr> FindContentWindows' in lines[i]:
        # 往上找注释起始
        j = i
        while j > 0 and not lines[j - 1].strip().startswith('/*'):
            j -= 1
        end = j
        break
assert end is not None and end > start, '找不到结束位置'

print('  替换 %d-%d 行（%d 行）' % (start + 1, end, end - start))

new_block = '''        /// <summary>
        /// 有界遍历（薄适配层）。
        ///
        /// 真正的逻辑在 BoundedWalk.cs —— 它不碰 COM，所以能被 WalkSelfTest
        /// 用假数据检验。这里只负责把 IAccessible 包装成 BoundedWalk 要的
        /// 四个委托，并**如实保留**截断原因。
        ///
        /// 旧版本把遍历逻辑直接写在这里、和 COM 缠在一起，结果没法测 ——
        /// 外部复核用纯内存复现抓到三个确定的错误：
        ///   · 简单子元素的 Owner 是父对象，却无条件枚举它的孩子 → 重复递归
        ///   · 永远只取 start=0 的前 64 个孩子 → 第 65 个以后消失且不报截断
        ///   · 没有身份去重 → 环会膨胀
        /// </summary>
        private static WalkResult WalkBounded(
            IAccessibleShim root, List<string> errors, int maxNodes)
        {
            var identities = new Dictionary<object, string>();
            int counter = 0;

            // 给每个节点一个稳定身份。简单子元素 = "父身份#childId"，
            // 完整对象 = 分配一个序号（同一对象在本次遍历里复用同一个序号）。
            // **不能按 Name + Rect 去重** —— 不同按钮可能同名，
            // 也可能暂时拥有相同矩形。
            Func<object, string> identity = delegate(object o)
            {
                MsaaRef r = o as MsaaRef;
                if (r == null) return "x" + (counter++);
                if (r.SimpleChild)
                    return IdentityOf(r.Owner, identities, ref counter) + "#" + r.ChildId;
                return IdentityOf(r.Owner, identities, ref counter);
            };

            return BoundedWalk.Run(
                new MsaaRef { Owner = root.Accessible, ChildId = 0, Depth = 0 },
                delegate(object o)
                {
                    MsaaRef r = (MsaaRef)o;
                    var d = Msaa.ReadNode(r, errors);
                    return new WalkNode
                    {
                        Identity = identity(o),
                        Name = d["name"] as string ?? "",
                        Role = d["role"] as string ?? "",
                        Rect = d["rect"] as int[],
                        HasDefaultAction = !string.IsNullOrEmpty(d["defaultAction"] as string ?? ""),
                    };
                },
                delegate(object o, int startOff, int count)
                {
                    MsaaRef r = (MsaaRef)o;
                    var page = new WalkPage();
                    int obtained;
                    var kids = Msaa.ChildrenBatch(r.Owner, startOff, count, out obtained, errors);
                    page.Obtained = obtained;
                    page.Total = -1;
                    foreach (var k in kids) page.Children.Add(k);
                    return page;
                },
                delegate(object o) { return ((MsaaRef)o).SimpleChild; },
                maxNodes,
                40);
        }

        /// <summary>给一个 IAccessible 分配稳定身份（本次遍历内）。</summary>
        private static string IdentityOf(
            Accessibility.IAccessible obj, Dictionary<object, string> map, ref int counter)
        {
            if (obj == null) return "null";
            string id;
            if (map.TryGetValue(obj, out id)) return id;
            id = "o" + (counter++);
            map[obj] = id;
            return id;
        }

'''.split('\n')

lines[start:end] = new_block
io.open(p, 'w', encoding='utf-8', newline='').write('\n'.join(lines))
print('  ✓ 已替换')
