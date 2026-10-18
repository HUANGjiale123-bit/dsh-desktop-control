/* 探测器自身的测试 —— **不需要任何真实应用**。
 *
 * ── 为什么先写这个 ──
 *
 * 外部复核做了四个纯内存复现，证明旧的 WalkBounded 会重复计数、漏页、
 * 缺身份去重。他给的四条用例就是这里的四个 Section：
 *
 *   1 父 + 2 个简单子元素    → 3，而且**不能**再递归
 *   1 父 + 70 个完整对象孩子  → 71（要翻页），不许悄悄只拿 65 个
 *   一个对象把自己列为孩子    → 1，要能识别成环
 *   空树 / 有缺页             → 截断原因必须传上来
 *
 * 这个测试用**假的节点源**（内存对象），完全不碰 COM 和桌面。
 * 所以它可以在任何机器上跑，也应该在每次改遍历器前后跑。
 *
 * 用法：dsc.exe selftest_walk
 */

using System;
using System.Collections.Generic;

namespace Dsc
{
    internal static class WalkSelfTest
    {
        /// <summary>假节点：一个对象，带自己的身份、孩子表、是否简单子元素。</summary>
        private sealed class FakeNode
        {
            public string Id;
            public string Name = "";
            public bool Simple;
            public List<FakeNode> Kids = new List<FakeNode>();
            public Func<FakeNode> SelfLoop;   // 用于造环
        }

        private static int _pass;
        private static int _fail;
        private static readonly List<string> _lines = new List<string>();

        private static void Ok(string what)
        {
            _pass++;
            _lines.Add("  ✓ " + what);
        }

        private static void Bad(string what)
        {
            _fail++;
            _lines.Add("  ✗ " + what);
        }

        private static WalkResult WalkOne(FakeNode root, int maxNodes, int maxDepth)
        {
            return BoundedWalk.Run(
                root,
                delegate(object o)
                {
                    FakeNode f = (FakeNode)o;
                    return new WalkNode
                    {
                        Identity = f.Id,
                        Name = f.Name,
                        Role = "10",
                        Rect = f.Simple ? null : new int[] { 0, 0, 10, 10 },
                        HasDefaultAction = false,
                    };
                },
                delegate(object o, int start, int count)
                {
                    FakeNode f = (FakeNode)o;
                    var page = new WalkPage();
                    // 模拟"简单子元素不能再展开"：它的孩子表永远是空的，
                    // 但**它会声称自己的 Owner 有一堆孩子** —— 这正是旧代码踩的坑。
                    int total = f.Kids.Count;
                    page.Total = total;
                    for (int i = start; i < total && page.Children.Count < count; i++)
                    {
                        page.Children.Add(f.Kids[i]);
                        page.Obtained++;
                    }
                    return page;
                },
                delegate(object o) { return ((FakeNode)o).Simple; },
                maxNodes,
                maxDepth);
        }

        internal static int Run()
        {
            _pass = 0; _fail = 0; _lines.Clear();
            _lines.Add("── 1. 一个父对象 + 两个简单子元素 ──");

            {
                var root = new FakeNode { Id = "root" };
                for (int i = 0; i < 2; i++)
                {
                    var kid = new FakeNode { Id = "kid" + i, Name = "输入法", Simple = true };
                    // 关键：简单子元素冒充"自己有父对象的全部孩子"
                    kid.Kids = null;
                    root.Kids.Add(kid);
                }
                var r = WalkOne(root, 3000, 40);
                if (r.UniqueCount == 3) Ok("节点数 = 3（父 + 两个简单子）");
                else Bad(string.Format("节点数 = {0}，应该是 3（旧的会得 3000）", r.UniqueCount));
                if (r.TruncatedBy == null) Ok("没有截断");
                else Bad("截断原因是 " + r.TruncatedBy + "，不该有");
            }

            _lines.Add("");
            _lines.Add("── 2. 一个父对象 + 70 个完整对象孩子（要翻页）──");

            {
                var root = new FakeNode { Id = "root70" };
                for (int i = 0; i < 70; i++)
                    root.Kids.Add(new FakeNode { Id = "c" + i, Name = "按钮" + i });
                var r = WalkOne(root, 3000, 40);
                if (r.UniqueCount == 71) Ok("节点数 = 71（父 + 70 个孩子，跨了两页）");
                else Bad(string.Format("节点数 = {0}，应该是 71（旧的会只拿 65 个且不报截断）", r.UniqueCount));
                if (r.TruncatedBy == null) Ok("翻页完整，没有截断标记");
                else Bad("不该截断，却报了 " + r.TruncatedBy);
            }

            _lines.Add("");
            _lines.Add("── 3. 一个对象把自己列为孩子（环）──");

            {
                var root = new FakeNode { Id = "self" };
                root.Kids.Add(root);              // 自环
                var r = WalkOne(root, 3000, 40);
                if (r.UniqueCount == 1) Ok("节点数 = 1（识别成环，不再展开）");
                else Bad(string.Format("节点数 = {0}，应该是 1（旧的会得 41）", r.UniqueCount));
                if (r.HitCycle) Ok("记录了重复边");
                else Bad("没有识别出环");
            }

            _lines.Add("");
            _lines.Add("── 4. 预算与截断必须传上去 ──");

            {
                var root = new FakeNode { Id = "big" };
                for (int i = 0; i < 100; i++)
                    root.Kids.Add(new FakeNode { Id = "b" + i });
                var r = WalkOne(root, 10, 40);
                if (r.UniqueCount == 10) Ok("node_budget 生效（只走 10 个）");
                else Bad(string.Format("节点数 = {0}，应该是 10", r.UniqueCount));
                if (r.TruncatedBy == "node_budget") Ok("截断原因 = node_budget");
                else Bad("截断原因是 " + (r.TruncatedBy ?? "(null)"));
                if (!r.TraversalComplete) Ok("TraversalComplete = false（没走完）");
                else Bad("TraversalComplete 不该是 true");
            }

            _lines.Add("");
            _lines.Add("── 5. 深度上限 ──");

            {
                var root = new FakeNode { Id = "d0" };
                var cur = root;
                for (int i = 1; i <= 50; i++)
                {
                    var nxt = new FakeNode { Id = "d" + i };
                    cur.Kids.Add(nxt);
                    cur = nxt;
                }
                var r = WalkOne(root, 3000, 5);
                if (r.TruncatedBy == "depth_budget") Ok("depth_budget 生效");
                else Bad("截断原因是 " + (r.TruncatedBy ?? "(null)"));
                if (r.MaxDepthSeen <= 5) Ok("最大深度没有超过上限（" + r.MaxDepthSeen + "）");
                else Bad("最大深度 " + r.MaxDepthSeen + " 超过了 5");
            }

            _lines.Add("");
            _lines.Add("── 6. 整数孩子必须经生产路径解析（含失败分支）──");
            {
                // ── 这一项为什么重写 ──
                //
                // 上一版直接构造"已经解析好的 MsaaRef"，把转换步骤整个跳过了 ——
                // 所以在"生产代码根本没调 get_accChild"的情况下它照样全过。
                // 外部复核把它拆穿了，还给了复现：应访问 3 个节点、实际 2 个、
                // get_accChild 调用 0 次。
                //
                // 现在**走 Msaa.ResolveChild 这个生产函数**，只把最底层的
                // resolver 换成假的。把 ResolveChild 里的转换拿掉，这一项必须红。
                int resolverCalls = 0;
                Func<Accessibility.IAccessible, int, object> fakeResolver = delegate(
                    Accessibility.IAccessible parent, int childId)
                {
                    resolverCalls++;
                    return null;   // 先测"正常无对象"
                };

                // ① 整数 + resolver 返回 null → simple
                string why;
                Dsc.MsaaRef simple = Dsc.Msaa.ResolveChild(
                    null, (object)7, delegate(Accessibility.IAccessible pp, int cc)
                    { resolverCalls++; return null; }, out why);
                if (simple != null && simple.SimpleChild && simple.ResolvedKind == "simple")
                    Ok("整数 + 无对象 → 简单子元素（ResolvedKind=simple）");
                else Bad("整数 + 无对象 的分类不对：" + (simple == null ? "null" : simple.ResolvedKind));

                if (resolverCalls == 1) Ok("resolver 被调用了一次（不是绕过）");
                else Bad(string.Format("resolver 调用了 {0} 次，应该 1 次", resolverCalls));

                // ② 整数 + resolver 抛异常 → unresolved（不能当成确认的叶节点）
                Dsc.MsaaRef bad;
                try
                {
                    bad = Dsc.Msaa.ResolveChild(
                        null, (object)8, delegate(Accessibility.IAccessible pp, int cc)
                        { throw new InvalidOperationException("provider 崩了"); }, out why);
                }
                catch (Exception e)
                {
                    bad = null;
                    why = "ResolveChild 自己抛了: " + e.Message;
                }
                if (bad != null && bad.ResolvedKind == "unresolved")
                    Ok("整数 + 查询失败 → unresolved（不是确认的叶节点）");
                else Bad("失败分支没标成 unresolved：" + (bad == null ? why : bad.ResolvedKind));

                if (!string.IsNullOrEmpty(why)) Ok("失败原因被记下来：" + why);
                else Bad("失败但没记原因");

                // ③ 完整对象 → 保留对象本身，允许展开
                if (simple != null && simple.RawKind == "int") Ok("整数来源的 RawKind=int");
                else Bad("RawKind 没被赋值：" + (simple == null ? "null" : simple.RawKind));

                _lines.Add("  （注：完整对象分支需要真 IAccessible，纯内存测不了；");
                _lines.Add("   它走的是同一个 ResolveChild 的第一分支。）");
            }

            _lines.Add("");
            Console.WriteLine(string.Join("\n", _lines.ToArray()));
            Console.WriteLine();
            Console.WriteLine("══════════════════════════════");
            if (_fail == 0) Console.WriteLine(string.Format("  {0} 项通过", _pass));
            else Console.WriteLine(string.Format("  {0} 项通过，{1} 项失败  ← 先修", _pass, _fail));
            Console.WriteLine("══════════════════════════════");
            return _fail == 0 ? 0 : 1;
        }
    }
}
