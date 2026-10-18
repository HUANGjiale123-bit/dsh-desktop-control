/* IAccessible 的测试替身。
 *
 * ── 为什么需要它 ──
 *
 * 外部复核做了个变异实验：把 ResolveChild 里 `if (acc != null)` 改成恒不进入
 * （resolver 照常调用），结果**我的 16 项测试仍然全过**，而生产路径访问的
 * 节点从 3 个降到 2 个、孙节点读不到。
 *
 * 也就是说：测试只能发现"resolver 没调用"，发现不了"调用了但没用返回对象"。
 *
 * 我原本在注释里写"完整对象分支纯内存测不了" —— 那是逃避。复核说得对：
 * "生产测试可通过适配层/测试替身覆盖这段逻辑；真实 COM 路径另测，
 *  不能把两层都当成无法测试而跳过。"
 *
 * IAccessible 可以用普通 class 实现（去掉 [ComImport] 的约束后，接口方法
 * 和属性都能重写），所以替身是可行的。
 */

using Accessibility;

namespace Dsc
{
    /// <summary>一个只会返回固定名字的假 IAccessible，用于测试解析分支。</summary>
    internal sealed class FakeAccessible : IAccessible
    {
        private readonly string _name;
        public FakeAccessible(string name) { _name = name; }

        public int accChildCount { get { return 0; } }
        public object accParent { get { return null; } }
        public object accFocus { get { return null; } }
        public object accSelection { get { return null; } }

        public void accDoDefaultAction(object child) { }
        public void accSelect(int flagsSelect, object child) { }
        public object accHitTest(int xLeft, int yTop) { return null; }
        public void accLocation(out int left, out int top, out int width, out int height, object child)
        {
            left = top = width = height = 0;
        }
        public object accNavigate(int navDir, object start) { return null; }
        public object get_accChild(object child) { return null; }
        public string get_accDefaultAction(object child) { return null; }
        public string get_accDescription(object child) { return null; }
        public string get_accHelp(object child) { return null; }
        public int get_accHelpTopic(out string helpFile, object child) { helpFile = null; return 0; }
        public string get_accKeyboardShortcut(object child) { return null; }
        public string get_accName(object child) { return _name; }
        public object get_accRole(object child) { return 0; }
        public object get_accState(object child) { return 0; }
        public string get_accValue(object child) { return null; }
        public void set_accName(object child, string value) { }
        public void set_accValue(object child, string value) { }
    }
}
