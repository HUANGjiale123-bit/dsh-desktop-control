import io, sys

sys.stdout.reconfigure(encoding='utf-8', errors='replace')

p = 'src/Probe.cs'
s = io.open(p, encoding='utf-8').read()

# ① 调用点 1（握手前基线）
old1 = """                List<ProbeNode> bn; int brc; string btr;
                WalkBounded(new IAccessibleShim(hsRoot), new List<string>(), out bn, out brc, out btr);
                beforeMsaa = bn.Count;
                beforeRect = brc;"""
new1 = """                WalkResult bw = WalkBounded(
                    new IAccessibleShim(hsRoot), new List<string>(), 800);
                beforeMsaa = bw.UniqueCount;
                beforeRect = bw.RectCount;"""
if old1 in s:
    s = s.replace(old1, new1, 1)
    print('  ✓ 调用点 1')
else:
    print('  ✗ 调用点 1 未匹配')

# ② 调用点 2（候选遍历）
old2 = """                List<ProbeNode> ns;
                int rc;
                string trunc;
                WalkBounded(r, errors, out ns, out rc, out trunc);"""
new2 = """                WalkResult wr = WalkBounded(r, errors, 3000);
                List<ProbeNode> ns = ToProbeNodes(wr);
                int rc = wr.RectCount;
                string trunc = wr.TruncatedBy;"""
if old2 in s:
    s = s.replace(old2, new2, 1)
    print('  ✓ 调用点 2')
else:
    print('  ✗ 调用点 2 未匹配（看实际写法）')

# ③ 记录里补上遍历完整性
old3 = """                rec["truncatedBy"] = trunc;"""
new3 = """                rec["truncatedBy"] = trunc;
                rec["traversalComplete"] = wr.TraversalComplete;
                rec["duplicateEdges"] = wr.DuplicateEdges;
                // ★ 这两个不是一回事：
                //   traversalComplete = 当次可达范围走完了
                //   contentStatus    = 有没有摸到"像内容"的东西
                rec["contentStatus"] = rc > 0 ? "has_rects" : "shell_only";"""
if old3 in s:
    s = s.replace(old3, new3, 1)
    print('  ✓ 补上遍历完整性')

# ④ 加 ToProbeNodes
anchor = """        /// <summary>给一个 IAccessible 分配稳定身份（本次遍历内）。</summary>"""
helper = '''        /** WalkResult → 旧的 ProbeNode 列表（下游代码还在用这个类型）。 */
        private static List<ProbeNode> ToProbeNodes(WalkResult wr)
        {
            var list = new List<ProbeNode>();
            for (int i = 0; i < wr.Nodes.Count; i++)
            {
                list.Add(new ProbeNode
                {
                    Name = wr.Nodes[i].Name,
                    Role = wr.Nodes[i].Role,
                    Rect = wr.Nodes[i].Rect,
                    HasDefaultAction = wr.Nodes[i].HasDefaultAction,
                    Depth = wr.Depths[i],
                });
            }
            return list;
        }

''' + anchor
if 'ToProbeNodes' not in s:
    s = s.replace(anchor, helper, 1)
    print('  ✓ ToProbeNodes 已加')

io.open(p, 'w', encoding='utf-8', newline='').write(s)
