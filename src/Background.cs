// Background.cs —— 窗口之外的那一部分：保存时长和全局快捷键、托盘图标、屏幕角上的提示。
//
// 快捷键只有程序开着才管用，所以关掉窗口后程序留在托盘里继续运行，要从托盘菜单里点「退出」才真正结束。
// 这里的东西都在一个单独的线程上（有自己的消息循环），不依赖主窗口：
// 界面无论是程序自己的窗口还是 Edge 的应用窗口，快捷键和托盘都照常工作。
//
// 按快捷键、点托盘菜单、点界面上的时长，最后走的都是 Recorder.cs 里的 SaveRecent。

using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ReplayClipper
{
    static partial class Program
    {
        [DllImport("user32.dll", SetLastError = true)]
        static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);
        [DllImport("user32.dll")]
        static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        [DllImport("user32.dll")]
        static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")]
        static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern bool GetMonitorInfoW(IntPtr monitor, ref MONITORINFO info);
        [DllImport("shcore.dll")]
        static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);
        [DllImport("user32.dll")]
        static extern bool MessageBeep(uint type);
        [DllImport("user32.dll")]
        static extern bool AllowSetForegroundWindow(uint pid);

        [StructLayout(LayoutKind.Sequential)]
        struct MONITORINFO
        {
            public int cbSize;
            public int Left, Top, Right, Bottom;                      // 整块屏幕
            public int WorkLeft, WorkTop, WorkRight, WorkBottom;      // 去掉任务栏以后
            public uint dwFlags;
        }

        // ------------------------------------------------------------------ 保存时长（设置里的 slots）

        class Slot
        {
            public int Seconds;
            public string Hotkey = "";      // "Ctrl+Alt+F7" 这样的写法；空 = 没有快捷键
        }

        // 最多几个时长；每个最短、最长多少秒（最长就是回放时长的上限，见 Recorder.cs 的 MaxReplaySeconds）
        const int MaxSlots = 5, MinSlotSeconds = 15, MaxSlotSeconds = 12 * 3600;

        static readonly object SlotLock = new object();
        static List<Slot> Slots = DefaultSlots();                                // 总是按时长从短到长排着
        static readonly HashSet<string> HotkeysInUse = new HashSet<string>();    // 没能向系统登记上的快捷键（被别的程序占着）

        static List<Slot> DefaultSlots()
        {
            var list = new List<Slot>();
            int[] seconds = { 180, 300, 900, 1800 };
            for (int i = 0; i < seconds.Length; i++)
            {
                var s = new Slot();
                s.Seconds = seconds[i];
                s.Hotkey = "Alt+F" + (7 + i);
                list.Add(s);
            }
            return list;
        }

        static List<Slot> CurrentSlots()
        {
            lock (SlotLock) return new List<Slot>(Slots);
        }

        static List<object> SlotsJson(bool withState)
        {
            var list = new List<object>();
            lock (SlotLock)
            {
                foreach (Slot s in Slots)
                {
                    var d = new Dictionary<string, object>();
                    d["seconds"] = s.Seconds;
                    d["hotkey"] = s.Hotkey;
                    if (withState) d["inUse"] = s.Hotkey.Length > 0 && HotkeysInUse.Contains(s.Hotkey);
                    list.Add(d);
                }
            }
            return list;
        }

        static Dictionary<string, object> SlotError(int index, string field, string code)
        {
            var d = new Dictionary<string, object>();
            d["index"] = index;
            d["field"] = field;      // "seconds" 或 "hotkey"
            d["code"] = code;        // range / duplicate / invalid / modifier / reserved / in_use
            return d;
        }

        // 读一组时长（来自设置文件或界面），顺序不变。有问题的地方记进 errors：哪一行、哪一栏、什么问题。
        static List<Slot> ParseSlots(object raw, List<object> errors)
        {
            var list = new List<Slot>();
            var items = raw as IEnumerable;
            if (items == null || raw is string) return list;
            foreach (object o in items)
            {
                var d = o as Dictionary<string, object>;
                if (d == null) continue;
                int index = list.Count;
                var s = new Slot();
                s.Seconds = IntOf(d, "seconds", 0);
                if (s.Seconds < MinSlotSeconds || s.Seconds > MaxSlotSeconds) errors.Add(SlotError(index, "seconds", "range"));
                else if (list.Exists(x => x.Seconds == s.Seconds)) errors.Add(SlotError(index, "seconds", "duplicate"));

                string text = (BodyStr(d, "hotkey") ?? "").Trim();
                if (text.Length > 0)
                {
                    Hotkey k = ParseHotkey(text);
                    string problem = k == null ? "invalid" : HotkeyProblem(k);
                    if (problem == null && list.Exists(x => x.Hotkey == k.Text)) problem = "duplicate";
                    if (problem != null) errors.Add(SlotError(index, "hotkey", problem));
                    s.Hotkey = k != null ? k.Text : text;
                }
                list.Add(s);
            }
            return list;
        }

        // 设置文件里的 slots：有问题的行去掉、有问题的快捷键清空，剩下的按时长排好。一行能用的都没有时返回空列表。
        static List<Slot> SlotsFromSettings(object raw)
        {
            var errors = new List<object>();
            List<Slot> list = ParseSlots(raw, errors);
            var drop = new HashSet<int>();
            foreach (Dictionary<string, object> e in errors)
            {
                int index = (int)e["index"];
                if ((string)e["field"] == "seconds") drop.Add(index);
                else list[index].Hotkey = "";
            }
            var kept = new List<Slot>();
            for (int i = 0; i < list.Count && kept.Count < MaxSlots; i++) if (!drop.Contains(i)) kept.Add(list[i]);
            kept.Sort(delegate(Slot a, Slot b) { return a.Seconds.CompareTo(b.Seconds); });
            return kept;
        }

        // 界面上「时长与快捷键」点了保存。要么整组生效，要么（有一处不行）什么都不变。
        static Dictionary<string, object> ApplySlots(Dictionary<string, object> body)
        {
            object raw;
            var errors = new List<object>();
            List<Slot> list = body.TryGetValue("slots", out raw) ? ParseSlots(raw, errors) : new List<Slot>();
            if (list.Count < 1 || list.Count > MaxSlots)
                throw new ApiError(400, Tr("时长的数量应为 1 至 " + MaxSlots + " 个。", "Between 1 and " + MaxSlots + " lengths are required."));
            if (errors.Count > 0) throw new ApiError(400, Tr("设置无效。", "The settings are invalid.")) { Details = errors };

            var sorted = new List<Slot>(list);
            sorted.Sort(delegate(Slot a, Slot b) { return a.Seconds.CompareTo(b.Seconds); });

            // 先向系统登记新的快捷键。有登记不上的（被别的程序占着）就作罢：原来的时长和快捷键都保持不变
            List<string> refused = ReplaceHotkeys(sorted);
            if (refused.Count > 0)
            {
                for (int i = 0; i < list.Count; i++)
                    if (refused.Contains(list[i].Hotkey)) errors.Add(SlotError(i, "hotkey", "in_use"));
                throw new ApiError(409, Tr("快捷键已被其他程序占用。", "A hotkey is already in use by another program.")) { Details = errors };
            }

            lock (SlotLock) Slots = sorted;
            SaveSettings();
            var sb = new StringBuilder("slots:");
            foreach (Slot s in sorted) sb.Append(' ').Append(s.Seconds).Append('s').Append(s.Hotkey.Length > 0 ? "=" + s.Hotkey : "");
            Log(sb.ToString());

            Dictionary<string, object> res = Ok();
            res["slots"] = SlotsJson(true);
            return res;
        }

        // 一段时长的说法，和页面里的 fmtSpan 一致：180 → "3 分钟"，90 → "1 分 30 秒"，45 → "45 秒"；
        // 整小时说小时（7200 → "2 小时"），不是整小时的仍按分钟说（5400 → "90 分钟"）
        static string SpanText(int seconds)
        {
            seconds = Math.Max(0, seconds);
            if (seconds >= 3600 && seconds % 3600 == 0) return (seconds / 3600) + Tr(" 小时", " h");
            int m = seconds / 60, s = seconds % 60;
            if (Lang == "en") return m > 0 ? m + " min" + (s > 0 ? " " + s + " s" : "") : s + " s";
            if (m == 0) return s + " 秒";
            return m + " 分" + (s > 0 ? " " + s + " 秒" : "钟");
        }

        // ------------------------------------------------------------------ 快捷键的写法

        const uint ModAlt = 0x1, ModControl = 0x2, ModShift = 0x4, ModNoRepeat = 0x4000;

        class Hotkey
        {
            public uint Mods, Vk;
            public string Text;      // 规范写法：修饰键按 Ctrl、Alt、Shift 的顺序，后面是键名
        }

        // 键名 → 系统的虚拟键码。键名和页面里按 KeyboardEvent.code 得出的名字是同一套（见 index.html 的 CODE_KEYS）
        static readonly Dictionary<string, uint> KeyCodes = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<string, string> KeyNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        static void AddKey(string name, uint vk)
        {
            KeyCodes[name] = vk;
            KeyNames[name] = name;
        }

        static void BuildKeyTable()
        {
            for (char c = 'A'; c <= 'Z'; c++) AddKey(c.ToString(), c);
            for (int i = 0; i <= 9; i++) { AddKey(i.ToString(Inv), (uint)(0x30 + i)); AddKey("Num" + i, (uint)(0x60 + i)); }
            for (int i = 1; i <= 24; i++) AddKey("F" + i, (uint)(0x6F + i));
            string[] names = { "Space", "Tab", "Enter", "Backspace", "Insert", "Delete", "Home", "End", "PageUp", "PageDown",
                               "Left", "Up", "Right", "Down", "Pause", "ScrollLock", "Num*", "Num+", "Num-", "Num.", "Num/",
                               "`", "-", "=", "[", "]", "\\", ";", "'", ",", ".", "/" };
            uint[] codes = { 0x20, 0x09, 0x0D, 0x08, 0x2D, 0x2E, 0x24, 0x23, 0x21, 0x22,
                             0x25, 0x26, 0x27, 0x28, 0x13, 0x91, 0x6A, 0x6B, 0x6D, 0x6E, 0x6F,
                             0xC0, 0xBD, 0xBB, 0xDB, 0xDD, 0xDC, 0xBA, 0xDE, 0xBC, 0xBE, 0xBF };
            for (int i = 0; i < names.Length; i++) AddKey(names[i], codes[i]);
        }

        // "Ctrl+Alt+F7" → 修饰键和键码。写法不对、键名不认识（包括 Win、Esc）时返回 null。
        static Hotkey ParseHotkey(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            lock (KeyCodes) if (KeyCodes.Count == 0) BuildKeyTable();
            string rest = text.Trim();
            uint mods = 0;
            while (true)
            {
                int plus = rest.IndexOf('+');
                if (plus <= 0 || plus == rest.Length - 1) break;      // 末尾的加号是键名的一部分（"Num+"）
                string head = rest.Substring(0, plus).Trim();
                uint m = string.Equals(head, "Ctrl", StringComparison.OrdinalIgnoreCase) ? ModControl
                       : string.Equals(head, "Alt", StringComparison.OrdinalIgnoreCase) ? ModAlt
                       : string.Equals(head, "Shift", StringComparison.OrdinalIgnoreCase) ? ModShift : 0;
                if (m == 0) break;
                mods |= m;
                rest = rest.Substring(plus + 1).Trim();
            }
            uint vk;
            if (!KeyCodes.TryGetValue(rest, out vk)) return null;
            var k = new Hotkey();
            k.Mods = mods;
            k.Vk = vk;
            k.Text = ((mods & ModControl) != 0 ? "Ctrl+" : "") + ((mods & ModAlt) != 0 ? "Alt+" : "")
                   + ((mods & ModShift) != 0 ? "Shift+" : "") + KeyNames[rest];
            return k;
        }

        // 这个组合能不能当全局快捷键。null = 可以；"modifier" = 要带 Ctrl 或 Alt；"reserved" = 系统自己在用。
        // 规则和页面里的 hotkeyProblem 一致：功能键可以单独用，其他键必须带 Ctrl 或 Alt（只带 Shift 不够，那是正常打字）。
        static string HotkeyProblem(Hotkey k)
        {
            bool ctrl = (k.Mods & ModControl) != 0, alt = (k.Mods & ModAlt) != 0, shift = (k.Mods & ModShift) != 0;
            bool functionKey = k.Vk >= 0x70 && k.Vk <= 0x87;
            if (!functionKey && !ctrl && !alt) return "modifier";
            if (k.Vk == 0x7B && k.Mods == 0) return "reserved";                      // F12
            if (k.Vk == 0x73 && alt && !ctrl && !shift) return "reserved";           // Alt+F4
            if (k.Vk == 0x09 && alt) return "reserved";                              // Alt+Tab（带不带 Shift / Ctrl 都是）
            if (k.Vk == 0x20 && alt && !ctrl && !shift) return "reserved";           // Alt+Space
            if (k.Vk == 0x2E && ctrl && alt && !shift) return "reserved";            // Ctrl+Alt+Del
            return null;
        }

        // ------------------------------------------------------------------ 后台线程：控制窗口、快捷键、托盘、提示

        const string ControlWindowTitle = "ReplayClipper.Control";      // 卸载程序和 --quit 靠这个标题找到它（见 Uninstall.cs）
        const int SuspendTimeoutMs = 30000;

        // 仅测试用：不向系统登记快捷键（免得测试实例抢走真正的按键），其余照常——向控制窗口发 WM_HOTKEY 仍会触发保存
        static readonly bool FakeHotkeys = Environment.GetEnvironmentVariable("REPLAYCLIP_FAKE_HOTKEYS") == "1";

        static WindowsFormsSynchronizationContext BgSync;
        static ControlWindow ControlWnd;
        static NotifyIcon Tray;
        static ContextMenuStrip TrayMenu;
        static ToastWindow Toast;
        static readonly Dictionary<int, int> HotkeyIds = new Dictionary<int, int>();      // 登记号 → 保存多少秒；只在后台线程上读写
        static bool HotkeysSuspended;
        static System.Threading.Timer SuspendTimer;
        static System.Threading.Timer HotkeyRetryTimer;     // 被别的程序占着的快捷键：隔一会儿再试着登记
        const int HotkeyRetryMs = 30000;

        static readonly ManualResetEvent ExitRequested = new ManualResetEvent(false);
        static Action ShowWindowHandler;        // 把界面窗口显示出来（由当前使用的那种窗口提供）
        static Action HideWindowHandler;        // 把界面窗口收进托盘（只有程序自己的窗口支持）
        static Action ExitHandler;              // 结束主循环
        static Action<string> PageNotifier;     // 给页面递一句话（只有程序自己的窗口支持）

        static void StartBackground()
        {
            var ready = new ManualResetEvent(false);
            var t = new Thread(delegate()
            {
                try
                {
                    BgSync = new WindowsFormsSynchronizationContext();
                    SynchronizationContext.SetSynchronizationContext(BgSync);
                    ControlWnd = new ControlWindow();
                    SuspendTimer = new System.Threading.Timer(delegate
                    {
                        BgPost(delegate
                        {
                            if (!HotkeysSuspended) return;
                            Log("hotkeys resumed after timeout");
                            ResumeHotkeys();
                        });
                    }, null, Timeout.Infinite, Timeout.Infinite);
                    HotkeyRetryTimer = new System.Threading.Timer(delegate { BgPost(RetryRefusedHotkeys); }, null, HotkeyRetryMs, HotkeyRetryMs);
                    CreateTray();
                    Log("control window hwnd=0x" + ControlWnd.Handle.ToInt64().ToString("X"));
                    ready.Set();
                    Application.Run();
                }
                catch (Exception ex) { Log("background thread: " + ex); }
                finally
                {
                    BgSync = null;
                    ready.Set();
                }
            });
            t.SetApartmentState(ApartmentState.STA);
            t.IsBackground = true;
            t.Name = "background";
            t.Start();
            ready.WaitOne(10000);
            BgSend(ResumeHotkeys);
            // 开机自启动时窗口不出现，界面上"已被占用"的标记没人看得见：在屏幕角上说一声，不然按了没反应也不知道为什么
            List<string> inUse;
            lock (SlotLock) inUse = new List<string>(HotkeysInUse);
            if (inUse.Count > 0)
                ShowToast(Tr("快捷键已被其他程序占用", inUse.Count == 1 ? "Hotkey used by another program" : "Hotkeys used by another program"),
                          Tr(string.Join("、", inUse.ToArray()) + "，可在主界面的“编辑”中更改", string.Join(", ", inUse.ToArray()) + ". Change it under Edit in the main window."), 8000);
        }

        // 启动时登记不上的快捷键，占着它的程序关掉后就能用了：定时再试，不用重启本程序
        static void RetryRefusedHotkeys()
        {
            if (HotkeysSuspended || FakeHotkeys || ControlWnd == null) return;
            lock (SlotLock) { if (HotkeysInUse.Count == 0) return; }
            List<Slot> slots = CurrentSlots();
            for (int i = 0; i < slots.Count; i++)
            {
                if (HotkeyIds.ContainsKey(i + 1)) continue;
                Hotkey k = ParseHotkey(slots[i].Hotkey);
                if (k == null) continue;
                lock (SlotLock) { if (!HotkeysInUse.Contains(k.Text)) continue; }
                if (!RegisterHotKey(ControlWnd.Handle, i + 1, k.Mods | ModNoRepeat, k.Vk)) continue;
                HotkeyIds[i + 1] = slots[i].Seconds;
                lock (SlotLock) HotkeysInUse.Remove(k.Text);
                Log("hotkey available again: " + k.Text);
            }
        }

        static void StopBackground()
        {
            BgSend(delegate
            {
                UnregisterHotkeys();
                if (Tray != null) { Tray.Visible = false; Tray.Dispose(); Tray = null; }
                if (Toast != null) { Toast.Close(); Toast = null; }
                Application.ExitThread();
            });
        }

        static void BgPost(Action action)
        {
            WindowsFormsSynchronizationContext sync = BgSync;
            if (sync == null) return;
            sync.Post(delegate
            {
                try { action(); }
                catch (Exception ex) { Log("background: " + ex.Message); }
            }, null);
        }

        static void BgSend(Action action)
        {
            WindowsFormsSynchronizationContext sync = BgSync;
            if (sync == null) return;
            try { sync.Send(delegate { action(); }, null); }
            catch (Exception ex) { Log("background: " + ex.Message); }
        }

        static void ShowMainWindow()
        {
            Action a = ShowWindowHandler;
            if (a == null) return;
            try { a(); }
            catch (Exception ex) { Log("ShowMainWindow: " + ex.Message); }
        }

        // 用户在"关闭窗口"的对话框里选好了：留在后台，还是退出。勾了"不再提醒"的话记下来，以后照办
        static void CloseChoice(string action, bool remember)
        {
            if (action != "tray" && action != "exit") throw new ApiError(400, "bad action");
            if (remember)
            {
                SettingCloseAction = action;
                SaveSettings();
            }
            if (action == "exit") { RequestExit(true); return; }
            Action a = HideWindowHandler;
            if (a != null) a();
        }

        static Func<string, string, string> FolderPicker;      // 打开系统的选择文件夹对话框（只有程序自己的窗口支持）

        // 真正退出：托盘菜单的「退出」、设置成关窗口就退出、系统关机或注销、安装程序或卸载程序要求关闭。
        // byUser：是用户（或替用户办事的安装、卸载程序）要退出，这时把回放缓存也删掉——程序关着的时候不留几个 GB 在盘上。
        // 系统关机、注销时没有时间稳妥地删，留着，下次开始录制时自会清理。
        static void RequestExit(bool byUser)
        {
            if (byUser) DiscardBufferOnExit = true;
            ExitRequested.Set();
            Action a = ExitHandler;
            if (a == null) return;
            try { a(); }
            catch (Exception ex) { Log("RequestExit: " + ex.Message); }
        }

        static void NotifyPage(string message)
        {
            Action<string> a = PageNotifier;
            if (a == null) return;
            try { a(message); } catch { }
        }

        // 一个从不显示的顶层窗口。全局快捷键登记在它上面；系统关机、安装程序请程序让路、卸载程序请程序退出，
        // 消息也都发到这里（主窗口缩在托盘里时照样收得到）。
        class ControlWindow : NativeWindow
        {
            const int WM_CLOSE = 0x0010, WM_QUERYENDSESSION = 0x0011, WM_ENDSESSION = 0x0016, WM_CANCELMODE = 0x001F,
                      WM_CONTEXTMENU = 0x007B, WM_HOTKEY = 0x0312;

            public ControlWindow()
            {
                var cp = new CreateParams();
                cp.Caption = ControlWindowTitle;
                cp.Style = unchecked((int)0x80000000);      // WS_POPUP，不带 WS_VISIBLE
                cp.ExStyle = 0x00000080;                    // 不进任务栏和 Alt+Tab
                CreateHandle(cp);
            }

            protected override void WndProc(ref Message m)
            {
                switch (m.Msg)
                {
                    case WM_HOTKEY:
                        OnHotkey(m.WParam.ToInt32());
                        return;
                    case WM_CLOSE:
                        RequestExit(true);      // --quit 和卸载程序走的是这里
                        return;
                    case WM_QUERYENDSESSION:
                        m.Result = new IntPtr(1);
                        return;
                    case WM_ENDSESSION:
                        if (m.WParam != IntPtr.Zero) RequestExit(false);
                        m.Result = IntPtr.Zero;
                        return;
                    case WM_CONTEXTMENU:
                    {
                        // 在指定位置打开托盘菜单（坐标在 lParam 里，-1 = 鼠标所在处）
                        long l = m.LParam.ToInt64();
                        Point at = l == -1 ? Cursor.Position : new Point((short)(l & 0xFFFF), (short)((l >> 16) & 0xFFFF));
                        if (TrayMenu != null) { SetForegroundWindow(Handle); TrayMenu.Show(at); }
                        return;
                    }
                    case WM_CANCELMODE:
                        if (TrayMenu != null) TrayMenu.Close();
                        return;
                }
                base.WndProc(ref m);
            }
        }

        // ---- 向系统登记 / 撤销快捷键。都在后台线程上做：快捷键属于登记它的那个线程。

        static void UnregisterHotkeys()
        {
            if (!FakeHotkeys) foreach (int id in HotkeyIds.Keys) UnregisterHotKey(ControlWnd.Handle, id);
            HotkeyIds.Clear();
        }

        // 登记这一组（先撤掉原来的）。返回没登记上的那些。登记号 = 在这一组里排第几（从 1 起）。
        static List<string> RegisterHotkeys(List<Slot> slots)
        {
            UnregisterHotkeys();
            var refused = new List<string>();
            for (int i = 0; i < slots.Count; i++)
            {
                Hotkey k = ParseHotkey(slots[i].Hotkey);
                if (k == null) continue;
                if (!FakeHotkeys && !RegisterHotKey(ControlWnd.Handle, i + 1, k.Mods | ModNoRepeat, k.Vk)) { refused.Add(k.Text); continue; }
                HotkeyIds[i + 1] = slots[i].Seconds;
            }
            return refused;
        }

        static void RememberInUse(List<string> refused)
        {
            lock (SlotLock)
            {
                HotkeysInUse.Clear();
                foreach (string s in refused) HotkeysInUse.Add(s);
            }
        }

        // 登记设置里现有的那一组（启动时、暂停结束后）。登记不上的记下来，界面上会标出。
        static void ResumeHotkeys()
        {
            SuspendTimer.Change(Timeout.Infinite, Timeout.Infinite);
            HotkeysSuspended = false;
            List<string> refused = RegisterHotkeys(CurrentSlots());
            foreach (string hotkey in refused) Log("hotkey unavailable: " + hotkey);
            RememberInUse(refused);
        }

        // 把系统里登记的快捷键换成新的一组。有登记不上的就原样返回它们，并恢复成原来那一组。
        static List<string> ReplaceHotkeys(List<Slot> wanted)
        {
            var refused = new List<string>();
            BgSend(delegate
            {
                refused.AddRange(RegisterHotkeys(wanted));
                if (refused.Count > 0) ResumeHotkeys();
                else
                {
                    SuspendTimer.Change(Timeout.Infinite, Timeout.Infinite);
                    HotkeysSuspended = false;
                    RememberInUse(refused);
                }
            });
            return refused;
        }

        // 界面上正在录入新的快捷键时暂停响应：这样按下一个已经在用的组合，是被录下来，而不是触发一次保存。
        // 界面万一没来得及说"好了"（比如页面被关掉），半分钟后自动恢复。
        static void SuspendHotkeys(bool on)
        {
            BgSend(delegate
            {
                if (on)
                {
                    if (!HotkeysSuspended) UnregisterHotkeys();
                    HotkeysSuspended = true;
                    SuspendTimer.Change(SuspendTimeoutMs, Timeout.Infinite);
                }
                else if (HotkeysSuspended) ResumeHotkeys();
            });
        }

        static void OnHotkey(int id)
        {
            int seconds;
            if (!HotkeyIds.TryGetValue(id, out seconds)) return;
            Task ignored = Task.Run(() => SaveFromBackground(seconds, "hotkey"));
        }

        // 按快捷键或点托盘菜单保存：人多半正在游戏里，看不到窗口，所以在屏幕角上给提示，完成时再响一声
        static async Task SaveFromBackground(int seconds, string source)
        {
            int shown = Math.Min(seconds, ReplaySeconds());
            if (Interlocked.Read(ref SaveStartedTicks) != 0)
            {
                ShowToast(Tr("上一次保存尚未完成", "The previous save hasn't finished yet"), null, 2500);
                return;
            }
            string saving = Tr("正在保存最近 " + SpanText(shown) + "…", "Saving the last " + SpanText(shown) + "…");
            ShowToast(saving, null, 0);
            try
            {
                // 存得久的（一秒以上）：后面跟上百分比。宽度按两位数定好，数字变的时候提示不跟着一伸一缩
                Dictionary<string, object> r = await SaveRecent(seconds, source, delegate(double done)
                {
                    ShowToast(saving + " " + Math.Min(99, (int)(done * 100)) + "%", null, 0, saving + " 00%");
                });
                // 缓存里还没攒够这么长时，照实说存下了多长
                int got = (int)Math.Round(ToD(r["duration"]));
                if (got <= 0 || got >= shown - 2) got = shown;
                ShowToast(Tr("已保存最近 " + SpanText(got), "Saved the last " + SpanText(got)), r["warn"] as string, 2500);
                MessageBeep(0x40);
            }
            catch (Exception ex)
            {
                if (!(ex is ApiError)) Log("save (" + source + "): " + ex);
                ShowToast(Tr("保存失败", "Couldn't save"), ex.Message, 4000);
                MessageBeep(0x10);
            }
        }

        // ------------------------------------------------------------------ 屏幕角上的提示

        // ms：显示多久；0 = 一直显示到下一条提示把它换掉。sizeAs：按这句话的宽度定大小（标题里有会变的数字时用）
        static void ShowToast(string title, string detail, int ms, string sizeAs = null)
        {
            BgPost(delegate
            {
                if (Toast == null) Toast = new ToastWindow();
                Toast.Present(title, detail, ms, sizeAs);
            });
        }

        static string UiFontName()
        {
            if (Lang != "en") return "Microsoft YaHei UI";
            try { using (var f = new FontFamily("Segoe UI Variable Text")) return f.Name; }
            catch { return "Segoe UI"; }
        }

        // 程序图标（两枚叠在一起的圆角三角形，和 app.ico、页面左上角的是同一个图形）。按需要的大小现画，什么缩放比例下都清楚。
        static void DrawAppGlyph(Graphics g, RectangleF box)
        {
            GraphicsState state = g.Save();
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.TranslateTransform(box.X, box.Y);
            g.ScaleTransform(box.Width / 64f, box.Height / 64f);
            DrawGlyphPart(g, new[] { new PointF(11, 16.5f), new PointF(11, 55), new PointF(43.5f, 35.75f) },
                Color.FromArgb(0x5a, 0x3d, 0xf0), Color.FromArgb(0x35, 0x26, 0xb8));
            DrawGlyphPart(g, new[] { new PointF(21.5f, 9.5f), new PointF(21.5f, 48), new PointF(54, 28.75f) },
                Color.FromArgb(0x5f, 0xd9, 0xff), Color.FromArgb(0x2f, 0x78, 0xff));
            g.Restore(state);
        }

        static void DrawGlyphPart(Graphics g, PointF[] triangle, Color top, Color bottom)
        {
            using (var path = new GraphicsPath())
            {
                path.AddPolygon(triangle);
                RectangleF r = path.GetBounds();
                r.Inflate(6, 6);
                using (var brush = new LinearGradientBrush(r, top, bottom, LinearGradientMode.Vertical))
                using (var pen = new Pen(brush, 10.4f))
                {
                    pen.LineJoin = LineJoin.Round;
                    g.FillPath(brush, path);
                    g.DrawPath(pen, path);
                }
            }
        }

        // 深色小卡片：图标 + 一行标题（+ 一行说明）。不抢焦点、鼠标点击穿透、保持在最上面，
        // 出现在前台窗口所在那块屏幕的右上角（正全屏玩着游戏时，就是游戏那块屏）。
        class ToastWindow : Form
        {
            readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
            string title = "", detail = "";
            Font titleFont, detailFont;
            Rectangle iconBox, titleBox, detailBox;
            bool ownBorder = true;

            const TextFormatFlags TextFlags = TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;

            public ToastWindow()
            {
                FormBorderStyle = FormBorderStyle.None;
                ShowInTaskbar = false;
                StartPosition = FormStartPosition.Manual;
                AutoScaleMode = AutoScaleMode.None;
                BackColor = Color.FromArgb(0x2c, 0x2c, 0x2c);
                Opacity = 0.97;
                SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
                timer.Tick += delegate { timer.Stop(); Hide(); };
            }

            protected override bool ShowWithoutActivation { get { return true; } }

            protected override CreateParams CreateParams
            {
                get
                {
                    CreateParams cp = base.CreateParams;
                    // 不抢焦点、不进任务栏和 Alt+Tab、鼠标点击穿透、保持在最上面
                    cp.ExStyle |= 0x08000000 | 0x00000080 | 0x00000020 | 0x00000008;
                    return cp;
                }
            }

            protected override void OnHandleCreated(EventArgs e)
            {
                base.OnHandleCreated(e);
                try
                {
                    // Windows 11：圆角和细边框交给系统画；更早的系统上是直角，边框自己画
                    int round = 2, border = 0x00454545;
                    ownBorder = DwmSetWindowAttribute(Handle, 33, ref round, 4) != 0;
                    if (!ownBorder) DwmSetWindowAttribute(Handle, 34, ref border, 4);
                }
                catch { }
            }

            public void Present(string newTitle, string newDetail, int ms, string sizeAs)
            {
                title = newTitle ?? "";
                detail = newDetail ?? "";
                timer.Stop();

                IntPtr monitor = MonitorFromWindow(GetForegroundWindow(), 1);      // 没有前台窗口时取主显示器
                var mi = new MONITORINFO();
                mi.cbSize = Marshal.SizeOf(typeof(MONITORINFO));
                Rectangle area = Screen.PrimaryScreen.WorkingArea;
                if (GetMonitorInfoW(monitor, ref mi)) area = Rectangle.FromLTRB(mi.WorkLeft, mi.WorkTop, mi.WorkRight, mi.WorkBottom);
                float scale = 1f;
                try
                {
                    uint dx, dy;
                    if (GetDpiForMonitor(monitor, 0, out dx, out dy) == 0) scale = dx / 96f;
                }
                catch
                {
                    try { scale = GetDpiForSystem() / 96f; } catch { }
                }

                // 版式（缩放前的尺寸）：四周留 14，图标 20，图标和文字相距 12，文字最宽 320
                Func<float, int> px = v => (int)Math.Round(v * scale);
                if (titleFont != null) titleFont.Dispose();
                if (detailFont != null) detailFont.Dispose();
                string family = UiFontName();
                titleFont = new Font(family, 14f * scale, detail.Length > 0 ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel);
                detailFont = new Font(family, 12f * scale, FontStyle.Regular, GraphicsUnit.Pixel);
                var limit = new Size(px(320), int.MaxValue);
                Size ts = TextRenderer.MeasureText(sizeAs ?? title, titleFont, limit, TextFlags);
                Size ds = detail.Length > 0 ? TextRenderer.MeasureText(detail, detailFont, limit, TextFlags) : Size.Empty;
                int pad = px(14), icon = px(20), gap = px(12), between = detail.Length > 0 ? px(2) : 0;
                int textW = Math.Max(ts.Width, ds.Width), textH = ts.Height + between + ds.Height;
                int w = pad + icon + gap + textW + pad + px(2), h = Math.Max(px(48), textH + pad * 2);
                int textTop = (h - textH) / 2;
                iconBox = new Rectangle(pad, detail.Length > 0 ? textTop + px(1) : (h - icon) / 2, icon, icon);
                titleBox = new Rectangle(pad + icon + gap, textTop, textW, ts.Height);
                detailBox = new Rectangle(pad + icon + gap, textTop + ts.Height + between, textW, ds.Height);

                int margin = px(24);
                int x = area.Right - w - margin, y = area.Top + margin;
                if (!Visible)
                {
                    Bounds = new Rectangle(x, y, w, h);
                    Show();
                }
                // 落到缩放比例不同的显示器上时，系统可能会改窗口大小：按实际像素再摆一次
                SetWindowPos(Handle, new IntPtr(-1), x, y, w, h, 0x0010);
                SetWindowPos(Handle, new IntPtr(-1), x, y, w, h, 0x0010);
                Invalidate();

                // ms = 0：一直显示到下一条提示把它换掉（保存很长的一段要好一会儿，"正在保存"得一直在）
                if (ms > 0)
                {
                    timer.Interval = ms;
                    timer.Start();
                }
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                Graphics g = e.Graphics;
                g.Clear(BackColor);
                DrawAppGlyph(g, iconBox);
                TextRenderer.DrawText(g, title, titleFont, titleBox, Color.White, TextFlags);
                if (detail.Length > 0) TextRenderer.DrawText(g, detail, detailFont, detailBox, Color.FromArgb(0xd0, 0xd0, 0xd0), TextFlags);
                if (ownBorder)
                    using (var pen = new Pen(Color.FromArgb(0x45, 0x45, 0x45)))
                        g.DrawRectangle(pen, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
            }
        }

        // ------------------------------------------------------------------ 托盘图标和它的菜单

        static void CreateTray()
        {
            TrayMenu = new ContextMenuStrip();
            TrayMenu.Renderer = new TrayMenuRenderer();
            TrayMenu.ShowImageMargin = false;
            TrayMenu.ShowCheckMargin = false;
            TrayMenu.DropShadowEnabled = false;
            TrayMenu.BackColor = Color.FromArgb(0x2c, 0x2c, 0x2c);
            TrayMenu.ForeColor = Color.White;
            TrayMenu.Opening += delegate(object s, System.ComponentModel.CancelEventArgs e) { FillTrayMenu(); e.Cancel = false; };
            TrayMenu.Opened += delegate
            {
                try
                {
                    int round = 2, border = 0x00454545;      // Windows 11：和系统菜单一样的圆角
                    if (DwmSetWindowAttribute(TrayMenu.Handle, 33, ref round, 4) == 0) DwmSetWindowAttribute(TrayMenu.Handle, 34, ref border, 4);
                }
                catch { }
            };
            FillTrayMenu();

            Tray = new NotifyIcon();
            Tray.Text = AppName;
            try
            {
                using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("app.ico"))
                    Tray.Icon = s != null ? new Icon(s, SystemInformation.SmallIconSize) : SystemIcons.Application;
            }
            catch { Tray.Icon = SystemIcons.Application; }
            Tray.ContextMenuStrip = TrayMenu;
            Tray.MouseClick += delegate(object s, MouseEventArgs e) { if (e.Button == MouseButtons.Left) ShowMainWindow(); };
            Tray.Visible = true;
            UpdateTrayState();
        }

        // 鼠标停在托盘图标上时的提示：程序名加一句现在的录制状态。只在后台线程上调用。
        static void UpdateTrayState()
        {
            if (Tray == null) return;
            string state = RecState();
            string what = state == "recording" ? Tr("后台录制中", "Recording")
                        : state == "off" || state == "unconfigured" ? Tr("未在录制", "Not recording")
                        : RecStatusLine(state);
            string text = AppName + " · " + what;
            if (text.Length > 63) text = text.Substring(0, 62) + "…";      // 系统对这句话的长度有限制
            if (Tray.Text != text) Tray.Text = text;
        }

        // 从托盘菜单开始录制：窗口多半没开着，结果用屏幕角上的提示告诉用户
        static void StartFromTray()
        {
            BeginStart();
            Task ignored = Task.Run(async () =>
            {
                for (int i = 0; i < 120 && RecState() == "starting"; i++) await Task.Delay(250);
                string state = RecState();
                if (state == "recording") ShowToast(Tr("后台录制已开始", "Background recording started"), null, 2500);
                else if (state != "starting") ShowToast(RecStatusLine(state), state == "error" || state == "nodisk" || state == "nomonitor" ? RecStateJson()["errorText"] as string : null, 4000);
            });
        }

        static Font TrayFont;

        // 每次打开前重新填：时长、快捷键和语言都可能变过
        static void FillTrayMenu()
        {
            float scale = 1f;
            try { scale = Math.Max(1f, GetDpiForSystem() / 96f); } catch { }
            // 每一项只加上下的留白（菜单的宽度是系统按文字算的，不理会左右的留白）；文字左右的位置由 TrayMenuRenderer 来定
            var itemPadding = new Padding(0, (int)(7 * scale), 0, (int)(7 * scale));
            string fontName = UiFontName();
            if (TrayFont == null || TrayFont.Name != fontName) TrayFont = new Font(fontName, 9f);      // 换了语言才会换字体
            TrayMenu.Font = TrayFont;
            TrayMenu.Padding = new Padding((int)(4 * scale));
            while (TrayMenu.Items.Count > 0) TrayMenu.Items[0].Dispose();      // 销毁时它会把自己从菜单里拿掉

            // 第一行：现在的录制状态（只是给人看的，点不了）
            string state = RecState();
            var status = new ToolStripMenuItem(RecStatusLine(state));
            status.Padding = itemPadding;
            status.Enabled = false;
            TrayMenu.Items.Add(status);
            TrayMenu.Items.Add(new ToolStripSeparator());

            var open = new ToolStripMenuItem(Tr("打开 " + AppName, "Open " + AppName));
            open.Padding = itemPadding;
            open.Click += delegate { ShowMainWindow(); };
            TrayMenu.Items.Add(open);
            TrayMenu.Items.Add(new ToolStripSeparator());

            // 和首页一样：比回放时长还长的几档只算一档（都是"保存全部"）
            int replay = ReplaySeconds();
            var seen = new HashSet<int>();
            foreach (Slot slot in CurrentSlots())
            {
                int seconds = slot.Seconds, shown = Math.Min(seconds, replay);
                if (!seen.Add(shown)) continue;
                var item = new ToolStripMenuItem(Tr("保存最近 ", "Save the last ") + SpanText(shown));
                item.Padding = itemPadding;
                // 前面的几个空格只为了把菜单撑宽一点，让时长和快捷键之间隔得开
                if (slot.Hotkey.Length > 0) { item.ShortcutKeyDisplayString = "    " + slot.Hotkey; item.ShowShortcutKeys = true; }
                item.Click += delegate { Task ignored = Task.Run(() => SaveFromBackground(seconds, "tray")); };
                TrayMenu.Items.Add(item);
            }
            TrayMenu.Items.Add(new ToolStripSeparator());

            // 开始 / 停止录制。还没设置过的话先去录制设置；停止前要问一句（没保存的画面会丢），那一步在窗口里做
            var toggle = new ToolStripMenuItem(state == "unconfigured" ? Tr("录制设置", "Recording settings")
                                             : state == "off" ? Tr("开始录制", "Start recording") : Tr("停止录制", "Stop recording"));
            toggle.Padding = itemPadding;
            toggle.Click += delegate
            {
                if (state == "unconfigured") RequestUi("settings");
                else if (state == "off") StartFromTray();
                else RequestUi("stop");
            };
            TrayMenu.Items.Add(toggle);

            var exit = new ToolStripMenuItem(Tr("退出", "Exit"));
            exit.Padding = itemPadding;
            exit.Click += delegate { RequestExit(true); };
            TrayMenu.Items.Add(exit);
        }

        class TrayMenuColors : ProfessionalColorTable
        {
            static readonly Color Surface = Color.FromArgb(0x2c, 0x2c, 0x2c);
            public override Color ToolStripDropDownBackground { get { return Surface; } }
            public override Color MenuBorder { get { return Color.FromArgb(0x45, 0x45, 0x45); } }
            public override Color MenuItemBorder { get { return Color.Transparent; } }
            public override Color ImageMarginGradientBegin { get { return Surface; } }
            public override Color ImageMarginGradientMiddle { get { return Surface; } }
            public override Color ImageMarginGradientEnd { get { return Surface; } }
        }

        // 和界面里的小菜单（语言）同一种样子：深色底，悬停的那一项是略亮的圆角条，快捷键用次要文字色
        class TrayMenuRenderer : ToolStripProfessionalRenderer
        {
            public TrayMenuRenderer() : base(new TrayMenuColors()) { RoundedEdges = false; }

            protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
            {
                if (!e.Item.Selected || !e.Item.Enabled) return;
                Graphics g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                var r = new RectangleF(0.5f, 0.5f, e.Item.Width - 1f, e.Item.Height - 1f);
                float d = 8f * g.DpiX / 96f;
                using (var path = new GraphicsPath())
                using (var brush = new SolidBrush(Color.FromArgb(0x3a, 0x3a, 0x3a)))
                {
                    path.AddArc(r.X, r.Y, d, d, 180, 90);
                    path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
                    path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
                    path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
                    path.CloseFigure();
                    g.FillPath(brush, path);
                }
            }

            protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
            {
                var item = e.Item as ToolStripMenuItem;
                bool shortcut = item != null && item.ShortcutKeyDisplayString != null && e.Text == item.ShortcutKeyDisplayString;
                // 文字的位置自己定：两边留出同样的一段，上下居中；快捷键靠右（默认的排法贴着左上角）
                int inset = (int)(12 * e.Graphics.DpiX / 96f);
                e.TextRectangle = new Rectangle(inset, 0, e.Item.Width - inset * 2, e.Item.Height);
                e.TextFormat = TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix
                             | (shortcut ? TextFormatFlags.Right : TextFormatFlags.Left);
                e.TextColor = shortcut ? Color.FromArgb(0x9d, 0x9d, 0x9d) : Color.White;
                if (!e.Item.Enabled)
                {
                    // 点不了的那一行（第一行的录制状态）：用次要文字色自己画，系统默认的灰色在深色底上看不清
                    TextRenderer.DrawText(e.Graphics, e.Text, e.TextFont, e.TextRectangle, Color.FromArgb(0xc8, 0xc8, 0xc8), e.TextFormat);
                    return;
                }
                base.OnRenderItemText(e);
            }

            protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
            {
                int y = e.Item.Height / 2;
                using (var pen = new Pen(Color.FromArgb(0x3f, 0x3f, 0x3f)))
                    e.Graphics.DrawLine(pen, 0, y, e.Item.Width, y);
            }
        }

        // ------------------------------------------------------------------ 对已经在运行的那一份说话

        // 第二次启动程序时：请已经在运行的那一份把窗口显示出来（它可能正缩在托盘里，没有窗口可找）
        static bool AskRunningInstanceToShow()
        {
            try
            {
                string[] info = File.ReadAllLines(InstanceFile);
                if (info.Length < 2) return false;
                uint pid;
                if (info.Length >= 3 && uint.TryParse(info[2].Trim(), out pid)) AllowSetForegroundWindow(pid);      // 允许它把窗口切到前台
                var req = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + info[0].Trim() + "/api/show");
                req.Method = "POST";
                req.Proxy = null;
                req.Timeout = 3000;
                req.ContentLength = 0;
                req.Headers["X-Token"] = info[1].Trim();
                using (var resp = (HttpWebResponse)req.GetResponse()) return resp.StatusCode == HttpStatusCode.OK;
            }
            catch { return false; }
        }
    }
}
