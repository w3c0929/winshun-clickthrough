// Click-through · ClickThrough.exe
// A resident WinShun plugin (JSON-RPC 2.0 on stdin/stdout, one line each).
// It reproduces AltSnap's ClickThrough: a window gets WS_EX_TRANSPARENT (clicks
// fall through) plus WS_EX_LAYERED with a faded alpha, stays on top, and is
// restored by toggling again, on shutdown, or (after a crash) on the next start.
//
// The exe declares the "elevated" permission, so when WinShun itself runs as
// administrator it also runs as administrator (from WinShun's confirmed copy)
// and can drive windows owned by elevated processes, which a normal-rights
// process cannot (UIPI). Build:
//   csc.exe /nologo /target:exe /codepage:65001 /out:ClickThrough.exe ClickThrough.cs

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace ClickThrough
{
    // ---- a small JSON reader/writer (the SDK's wire format needs no more) ----
    static class Json
    {
        public static object Parse(string text)
        {
            var p = new Parser(text);
            object value = p.ReadValue();
            p.SkipSpace();
            return value;
        }

        public static string Stringify(object value)
        {
            var b = new StringBuilder();
            Write(b, value);
            return b.ToString();
        }

        static void Write(StringBuilder b, object v)
        {
            if (v == null) { b.Append("null"); return; }
            string s = v as string;
            if (s != null) { WriteString(b, s); return; }
            if (v is bool) { b.Append((bool)v ? "true" : "false"); return; }
            if (v is int || v is long || v is uint || v is short || v is byte)
            {
                b.Append(Convert.ToString(v, CultureInfo.InvariantCulture));
                return;
            }
            if (v is double || v is float || v is decimal)
            {
                double d = Convert.ToDouble(v, CultureInfo.InvariantCulture);
                if (double.IsNaN(d) || double.IsInfinity(d)) b.Append("null");
                else b.Append(d.ToString("R", CultureInfo.InvariantCulture));
                return;
            }
            var map = v as IDictionary<string, object>;
            if (map != null)
            {
                b.Append('{');
                bool first = true;
                foreach (var pair in map)
                {
                    if (!first) b.Append(',');
                    first = false;
                    WriteString(b, pair.Key);
                    b.Append(':');
                    Write(b, pair.Value);
                }
                b.Append('}');
                return;
            }
            var list = v as System.Collections.IEnumerable;
            if (list != null)
            {
                b.Append('[');
                bool first = true;
                foreach (object item in list)
                {
                    if (!first) b.Append(',');
                    first = false;
                    Write(b, item);
                }
                b.Append(']');
                return;
            }
            WriteString(b, Convert.ToString(v, CultureInfo.InvariantCulture));
        }

        static void WriteString(StringBuilder b, string s)
        {
            b.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': b.Append("\\\""); break;
                    case '\\': b.Append("\\\\"); break;
                    case '\n': b.Append("\\n"); break;
                    case '\r': b.Append("\\r"); break;
                    case '\t': b.Append("\\t"); break;
                    case '\b': b.Append("\\b"); break;
                    case '\f': b.Append("\\f"); break;
                    default:
                        if (c < 0x20) b.Append("\\u").Append(((int)c).ToString("x4"));
                        else b.Append(c);
                        break;
                }
            }
            b.Append('"');
        }

        sealed class Parser
        {
            readonly string s;
            int i;
            public Parser(string text) { s = text; i = 0; }

            public void SkipSpace()
            {
                while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
            }

            public object ReadValue()
            {
                SkipSpace();
                if (i >= s.Length) throw new FormatException("unexpected end");
                char c = s[i];
                if (c == '{') return ReadObject();
                if (c == '[') return ReadArray();
                if (c == '"') return ReadString();
                if (c == 't') { Expect("true"); return true; }
                if (c == 'f') { Expect("false"); return false; }
                if (c == 'n') { Expect("null"); return null; }
                return ReadNumber();
            }

            void Expect(string word)
            {
                if (i + word.Length > s.Length || s.Substring(i, word.Length) != word)
                    throw new FormatException("bad literal");
                i += word.Length;
            }

            Dictionary<string, object> ReadObject()
            {
                var map = new Dictionary<string, object>();
                i++; // {
                SkipSpace();
                if (i < s.Length && s[i] == '}') { i++; return map; }
                while (true)
                {
                    SkipSpace();
                    string key = ReadString();
                    SkipSpace();
                    if (i >= s.Length || s[i] != ':') throw new FormatException("expected :");
                    i++;
                    map[key] = ReadValue();
                    SkipSpace();
                    if (i >= s.Length) throw new FormatException("unexpected end");
                    char c = s[i++];
                    if (c == '}') return map;
                    if (c != ',') throw new FormatException("expected , or }");
                }
            }

            List<object> ReadArray()
            {
                var list = new List<object>();
                i++; // [
                SkipSpace();
                if (i < s.Length && s[i] == ']') { i++; return list; }
                while (true)
                {
                    list.Add(ReadValue());
                    SkipSpace();
                    if (i >= s.Length) throw new FormatException("unexpected end");
                    char c = s[i++];
                    if (c == ']') return list;
                    if (c != ',') throw new FormatException("expected , or ]");
                }
            }

            string ReadString()
            {
                if (i >= s.Length || s[i] != '"') throw new FormatException("expected string");
                i++;
                var b = new StringBuilder();
                while (i < s.Length)
                {
                    char c = s[i++];
                    if (c == '"') return b.ToString();
                    if (c != '\\') { b.Append(c); continue; }
                    if (i >= s.Length) break;
                    char e = s[i++];
                    switch (e)
                    {
                        case '"': b.Append('"'); break;
                        case '\\': b.Append('\\'); break;
                        case '/': b.Append('/'); break;
                        case 'b': b.Append('\b'); break;
                        case 'f': b.Append('\f'); break;
                        case 'n': b.Append('\n'); break;
                        case 'r': b.Append('\r'); break;
                        case 't': b.Append('\t'); break;
                        case 'u':
                            if (i + 4 > s.Length) throw new FormatException("bad \\u");
                            b.Append((char)int.Parse(s.Substring(i, 4), NumberStyles.HexNumber));
                            i += 4;
                            break;
                        default: throw new FormatException("bad escape");
                    }
                }
                throw new FormatException("unterminated string");
            }

            object ReadNumber()
            {
                int start = i;
                if (i < s.Length && (s[i] == '-' || s[i] == '+')) i++;
                bool real = false;
                while (i < s.Length)
                {
                    char c = s[i];
                    if (c >= '0' && c <= '9') { i++; continue; }
                    if (c == '.' || c == 'e' || c == 'E' || c == '+' || c == '-') { real = true; i++; continue; }
                    break;
                }
                string text = s.Substring(start, i - start);
                if (!real)
                {
                    long l;
                    if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out l)) return l;
                }
                return double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
            }
        }
    }

    // ---- Win32 ----
    static class Native
    {
        public delegate bool EnumProc(IntPtr h, IntPtr l);

        [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr p);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
        [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowTextLength(IntPtr h);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder b, int n);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder b, int n);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr h, uint flags);
        [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT p);
        [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] public static extern IntPtr GetWindowLongPtr(IntPtr h, int i);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)] public static extern IntPtr SetWindowLongPtr(IntPtr h, int i, IntPtr v);
        [DllImport("user32.dll", SetLastError = true)] public static extern bool SetLayeredWindowAttributes(IntPtr h, uint color, byte alpha, uint flags);
        [DllImport("user32.dll")] public static extern bool GetLayeredWindowAttributes(IntPtr h, out uint color, out byte alpha, out uint flags);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("kernel32.dll", SetLastError = true)] public static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern bool QueryFullProcessImageName(IntPtr p, uint flags, StringBuilder name, ref int size);
        [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll")] public static extern void SetLastError(uint error);
        [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int value, int size);

        [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X; public int Y; }
        [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

        // The faded look is a separate click-through overlay showing a DWM
        // thumbnail of the tracked window; the tracked window itself becomes
        // fully transparent (alpha 0), so the system routes clicks to the
        // window below and its controls (a layered alpha > 0 would only
        // activate it).
        [StructLayout(LayoutKind.Sequential)]
        public struct DWM_THUMBNAIL_PROPERTIES
        {
            public uint dwFlags; public RECT rcDestination; public RECT rcSource;
            public byte opacity; public bool fVisible; public bool fSourceClientAreaOnly;
        }
        [DllImport("dwmapi.dll")] public static extern int DwmRegisterThumbnail(IntPtr dest, IntPtr src, out IntPtr thumb);
        [DllImport("dwmapi.dll")] public static extern int DwmUnregisterThumbnail(IntPtr thumb);
        [DllImport("dwmapi.dll")] public static extern int DwmUpdateThumbnailProperties(IntPtr thumb, ref DWM_THUMBNAIL_PROPERTIES props);
        [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int attr, out RECT value, int size);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr CreateWindowExW(uint exStyle, string className, string windowName, uint style,
            int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
        [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr h);
        [DllImport("user32.dll")] public static extern bool PeekMessage(out MSG msg, IntPtr hWnd, uint min, uint max, uint remove);
        [DllImport("user32.dll")] public static extern bool TranslateMessage(ref MSG msg);
        [DllImport("user32.dll")] public static extern IntPtr DispatchMessage(ref MSG msg);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr GetModuleHandle(string name);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT rect);
        [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr context);
        [DllImport("shcore.dll")] public static extern int SetProcessDpiAwareness(int value);
        [StructLayout(LayoutKind.Sequential)]
        public struct MSG
        {
            public IntPtr hwnd; public uint message; public UIntPtr wParam; public IntPtr lParam;
            public uint time; public POINT pt; public uint lPrivate;
        }
    }

    sealed class CtWindow
    {
        public long Hwnd;
        public string Title = "";
        public string Process = "";
        public string Path = "";
        public bool Transparent;
        public bool Foreground;
        public bool Minimized;
    }

    sealed class CtApplied
    {
        public string Error = "";
        public int ErrorCode;
        public long Hwnd;
        public long ExStyle;
        public int OldAlpha = 255;
        public bool AlphaChanged;
        public int AppliedAlpha;
        public string Class = "";
        public string Title = "";
        public uint OwnerPid;
        public string Owner = "";
    }

    static class Ct
    {
        const int GWL_EXSTYLE = -20;
        const long WS_EX_TOPMOST = 0x8;
        const long WS_EX_TRANSPARENT = 0x20;
        const long WS_EX_TOOLWINDOW = 0x80;
        const long WS_EX_LAYERED = 0x80000;
        const uint LWA_ALPHA = 2;
        const uint SWP_NOSIZE = 1;
        const uint SWP_NOMOVE = 2;
        const uint SWP_NOACTIVATE = 0x10;
        const uint SWP_NOOWNERZORDER = 0x200;
        static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);

        public static long Style(IntPtr h) { return Native.GetWindowLongPtr(h, GWL_EXSTYLE).ToInt64(); }

        public static bool WindowExists(long hwnd) { return Native.IsWindow(new IntPtr(hwnd)); }
        public static string ClassName(long hwnd) { return ClassOf(new IntPtr(hwnd)); }
        public static string ProcessPath(uint pid) { return PathOf(pid); }

        static string ClassOf(IntPtr h)
        {
            var b = new StringBuilder(256);
            Native.GetClassName(h, b, b.Capacity);
            return b.ToString();
        }

        static string TitleOf(IntPtr h)
        {
            int n = Native.GetWindowTextLength(h);
            if (n <= 0) return "";
            var b = new StringBuilder(n + 1);
            Native.GetWindowText(h, b, b.Capacity);
            return b.ToString();
        }

        static string PathOf(uint pid)
        {
            IntPtr p = Native.OpenProcess(0x1000, false, pid);
            if (p == IntPtr.Zero) return "";
            try
            {
                var b = new StringBuilder(1024);
                int size = b.Capacity;
                return Native.QueryFullProcessImageName(p, 0, b, ref size) ? b.ToString() : "";
            }
            finally { Native.CloseHandle(p); }
        }

        static bool Cloaked(IntPtr h)
        {
            int v;
            return Native.DwmGetWindowAttribute(h, 14, out v, sizeof(int)) == 0 && v != 0;
        }

        public static CtWindow[] Snapshot()
        {
            var list = new List<CtWindow>();
            uint me = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
            IntPtr fg = Native.GetForegroundWindow();
            Native.EnumWindows(delegate(IntPtr h, IntPtr l)
            {
                if (!Native.IsWindowVisible(h)) return true;
                string cls = ClassOf(h);
                if (cls == "Progman" || cls == "WorkerW" || cls == "Shell_TrayWnd" || cls == "Shell_SecondaryTrayWnd") return true;
                if (Cloaked(h)) return true;
                string title = TitleOf(h);
                if (title.Length == 0) return true;
                long ex = Style(h);
                if ((ex & WS_EX_TOOLWINDOW) != 0) return true;
                uint pid;
                Native.GetWindowThreadProcessId(h, out pid);
                if (pid == me) return true;
                string path = PathOf(pid);
                string program = path.Length > 0 ? System.IO.Path.GetFileName(path) : "";
                if (string.Equals(program, "WinShun.exe", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(program, "WinShunPluginHost.exe", StringComparison.OrdinalIgnoreCase))
                    return true;
                var w = new CtWindow();
                w.Hwnd = h.ToInt64();
                w.Title = title;
                w.Process = program;
                w.Path = path;
                w.Transparent = (ex & WS_EX_TRANSPARENT) != 0;
                w.Foreground = h == fg;
                w.Minimized = Native.IsIconic(h);
                list.Add(w);
                return true;
            }, IntPtr.Zero);
            list.Sort(delegate(CtWindow a, CtWindow b)
            {
                if (a.Foreground != b.Foreground) return a.Foreground ? -1 : 1;
                return string.Compare(a.Title, b.Title, StringComparison.CurrentCultureIgnoreCase);
            });
            return list.ToArray();
        }

        // Adds WS_EX_TRANSPARENT|WS_EX_LAYERED, fades the window (unless it is
        // already see-through), and keeps it on top. CtApplied.Error is empty
        // on success; the saved fields are what Restore needs.
        public static CtApplied Apply(long hwndLong, int transparency)
        {
            var r = new CtApplied();
            IntPtr h = new IntPtr(hwndLong);
            if (!Native.IsWindow(h)) { r.Error = "window is gone"; return r; }
            IntPtr root = Native.GetAncestor(h, 2);
            if (root != IntPtr.Zero) h = root;

            long ex = Style(h);
            uint color;
            byte alpha = 255;
            uint flags = 0;
            bool haveAlpha = (ex & WS_EX_LAYERED) != 0
                && Native.GetLayeredWindowAttributes(h, out color, out alpha, out flags) && (flags & LWA_ALPHA) != 0;
            bool alreadyTransparent = haveAlpha && alpha != 255;

            r.Hwnd = h.ToInt64();
            r.ExStyle = ex;
            r.OldAlpha = haveAlpha ? alpha : 255;
            r.AlphaChanged = !alreadyTransparent;
            r.Class = ClassOf(h);
            r.Title = TitleOf(h);
            Native.GetWindowThreadProcessId(h, out r.OwnerPid);
            r.Owner = PathOf(r.OwnerPid);

            long newEx = ex | WS_EX_LAYERED | WS_EX_TRANSPARENT;
            int setError = 0;
            if (newEx != ex)
            {
                Native.SetLastError(0);
                Native.SetWindowLongPtr(h, GWL_EXSTYLE, new IntPtr(newEx));
                setError = Marshal.GetLastWin32Error();
            }

            int transp = Math.Max(0, Math.Min(100, transparency));
            int a = (255 * (100 - transp) + 50) / 100;
            r.AppliedAlpha = alreadyTransparent ? 0 : a;
            if (!alreadyTransparent) Native.SetLayeredWindowAttributes(h, 0, (byte)a, LWA_ALPHA);

            Native.SetWindowPos(h, HWND_TOPMOST, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);

            if ((Style(h) & WS_EX_TRANSPARENT) == 0)
            {
                // Refused (usually a window of another user, or elevated).
                Native.SetWindowLongPtr(h, GWL_EXSTYLE, new IntPtr(ex));
                if (!alreadyTransparent && haveAlpha) Native.SetLayeredWindowAttributes(h, 0, alpha, LWA_ALPHA);
                r.ErrorCode = setError;
                r.Error = "cannot change this window";
            }
            return r;
        }

        // Returns "" when done (or the window is gone / is not the one saved).
        public static string Restore(long hwndLong, long origExStyle, int oldAlpha, bool alphaChanged, string savedClass)
        {
            IntPtr h = new IntPtr(hwndLong);
            if (!Native.IsWindow(h)) return "";
            if (savedClass.Length > 0 && !string.Equals(ClassOf(h), savedClass, StringComparison.OrdinalIgnoreCase)) return "";
            long ex = Style(h);
            long newEx = ex & ~WS_EX_TRANSPARENT;
            if ((origExStyle & WS_EX_LAYERED) != 0) newEx |= WS_EX_LAYERED;
            else newEx &= ~WS_EX_LAYERED;
            if (newEx != ex) Native.SetWindowLongPtr(h, GWL_EXSTYLE, new IntPtr(newEx));
            if (alphaChanged && (origExStyle & WS_EX_LAYERED) != 0)
                Native.SetLayeredWindowAttributes(h, 0, (byte)oldAlpha, LWA_ALPHA);
            IntPtr after = (origExStyle & WS_EX_TOPMOST) != 0 ? HWND_TOPMOST : HWND_NOTOPMOST;
            Native.SetWindowPos(h, after, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
            return "";
        }

        public static long TargetHwnd(bool useCursor)
        {
            IntPtr h;
            if (useCursor)
            {
                Native.POINT p;
                if (!Native.GetCursorPos(out p)) return 0;
                h = Native.WindowFromPoint(p);
            }
            else h = Native.GetForegroundWindow();
            if (h == IntPtr.Zero) return 0;
            IntPtr root = Native.GetAncestor(h, 2);
            return (root != IntPtr.Zero ? root : h).ToInt64();
        }
    }

    // ---- the plugin ----
    // The semi-transparent visual: a native borderless, click-through,
    // topmost popup showing a live DWM thumbnail of the (now invisible)
    // tracked window. A native window has client size == window size, so the
    // thumbnail maps 1:1 on every window; no WinForms/DPI frame to offset it.
    sealed class NativeOverlay
    {
        readonly object m_lock = new object();
        long m_target;
        int m_alpha = 255;
        IntPtr m_hwnd;
        IntPtr m_thumb;
        long m_thumbTarget;
        bool m_failedLogged;
        int m_lastAlpha = -1;
        Thread m_thread;
        ManualResetEventSlim m_ready;

        // Returns true only when the thumbnail is actually registered: the
        // caller then makes the real window invisible. On failure the window
        // keeps its faded alpha, so it never ends up fully invisible.
        public bool Attach(long target, int alpha)
        {
            lock (m_lock) { m_target = target; m_alpha = Math.Max(0, Math.Min(255, alpha)); m_failedLogged = false; }
            if (!Ensure()) return false;
            for (int i = 0; i < 50; i++)
            {
                bool ok;
                lock (m_lock) ok = m_thumb != IntPtr.Zero && m_thumbTarget == target;
                if (ok) return true;
                if (!Native.IsWindow(new IntPtr(target)) || Native.IsIconic(new IntPtr(target))) break;
                Thread.Sleep(10);
            }
            Detach();
            return false;
        }

        public void SetAlpha(int alpha)
        {
            lock (m_lock) m_alpha = Math.Max(0, Math.Min(255, alpha));
        }

        public void Detach()
        {
            lock (m_lock) m_target = 0;
        }

        bool Ensure()
        {
            if (m_thread != null) return m_hwnd != IntPtr.Zero;
            m_ready = new ManualResetEventSlim(false);
            m_thread = new Thread(Run);
            m_thread.IsBackground = true;
            m_thread.Start();
            m_ready.Wait(2500);
            return m_hwnd != IntPtr.Zero;
        }

        void Run()
        {
            try
            {
                // The predefined STATIC class is enough for a borderless popup
                // that only shows a DWM thumbnail; no custom class/non-client
                // frame to misalign it.
                m_hwnd = Native.CreateWindowExW(0x080800A8u, "STATIC", "", 0x80000000u,
                    0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, Native.GetModuleHandle(null), IntPtr.Zero);
                if (m_hwnd == IntPtr.Zero)
                {
                    Console.Error.WriteLine("clickthrough: overlay window failed: " + Marshal.GetLastWin32Error());
                    m_ready.Set();
                    return;
                }
                Native.SetLayeredWindowAttributes(m_hwnd, 0, 0, 2);
                Native.ShowWindow(m_hwnd, 4); // SW_SHOWNOACTIVATE
                Update(m_hwnd);               // register the thumbnail before returning
                m_ready.Set();
                Native.MSG msg;
                while (true)
                {
                    while (Native.PeekMessage(out msg, IntPtr.Zero, 0, 0, 1))
                    {
                        Native.TranslateMessage(ref msg);
                        Native.DispatchMessage(ref msg);
                    }
                    Update(m_hwnd);
                    Thread.Sleep(25);
                }
            }
            catch (Exception e)
            {
                Console.Error.WriteLine("clickthrough: overlay thread failed: " + e.Message);
                m_ready.Set();
            }
        }

        void Update(IntPtr h)
        {
            long target;
            int alpha;
            lock (m_lock) { target = m_target; alpha = m_alpha; }
            IntPtr t = new IntPtr(target);
            if (target == 0 || !Native.IsWindow(t) || !Native.IsWindowVisible(t) || Native.IsIconic(t))
            {
                if (Native.IsWindowVisible(h)) Native.ShowWindow(h, 0); // SW_HIDE
                return;
            }
            int cloaked;
            if (Native.DwmGetWindowAttribute(t, 14, out cloaked, sizeof(int)) == 0 && cloaked != 0)
            {
                // On another virtual desktop: do not show its thumbnail here.
                if (Native.IsWindowVisible(h)) Native.ShowWindow(h, 0);
                return;
            }
            Native.RECT r;
            int size = Marshal.SizeOf(typeof(Native.RECT));
            if (Native.DwmGetWindowAttribute(t, 9, out r, size) != 0) // DWMWA_EXTENDED_FRAME_BOUNDS
            {
                if (!Native.GetWindowRect(t, out r)) return;
            }
            int w = r.Right - r.Left, ht = r.Bottom - r.Top;
            if (w <= 0 || ht <= 0)
            {
                if (Native.IsWindowVisible(h)) Native.ShowWindow(h, 0);
                return;
            }
            Native.SetWindowPos(h, new IntPtr(-1), r.Left, r.Top, w, ht, 0x10); // topmost, no activate

            IntPtr thumb;
            long thumbTarget;
            lock (m_lock) { thumb = m_thumb; thumbTarget = m_thumbTarget; }
            if (thumb != IntPtr.Zero && thumbTarget != target)
            {
                Native.DwmUnregisterThumbnail(thumb);
                lock (m_lock) { m_thumb = IntPtr.Zero; m_thumbTarget = 0; }
                thumb = IntPtr.Zero;
            }
            if (thumb == IntPtr.Zero)
            {
                int hr = Native.DwmRegisterThumbnail(h, t, out thumb);
                if (hr != 0)
                {
                    thumb = IntPtr.Zero;
                    if (!m_failedLogged)
                    {
                        Console.Error.WriteLine("clickthrough: DWM thumbnail failed: " + hr);
                        m_failedLogged = true;
                    }
                }
                lock (m_lock) { m_thumb = thumb; m_thumbTarget = thumb != IntPtr.Zero ? target : 0; }
            }
            if (thumb != IntPtr.Zero)
            {
                var p = new Native.DWM_THUMBNAIL_PROPERTIES();
                p.dwFlags = 0x1 | 0x4 | 0x8; // dest, opacity, visible
                p.rcDestination = new Native.RECT { Left = 0, Top = 0, Right = w, Bottom = ht };
                p.opacity = 255;
                p.fVisible = true;
                p.fSourceClientAreaOnly = false;
                Native.DwmUpdateThumbnailProperties(thumb, ref p);
            }
            if (alpha != m_lastAlpha)
            {
                Native.SetLayeredWindowAttributes(h, 0, (byte)alpha, 2);
                m_lastAlpha = alpha;
            }
            if (!Native.IsWindowVisible(h)) Native.ShowWindow(h, 4);
        }
    }

    sealed class Plugin
    {
        TextReader m_in;
        StreamWriter m_out;
        readonly Queue<string> m_queued = new Queue<string>();
        Dictionary<string, object> m_settings = new Dictionary<string, object>();
        Dictionary<string, object> m_tracked;
        string m_dataDir = "";
        string m_locale = "zh";
        bool m_hostHotkeys;
        string m_hotkey = "";
        string m_restoreKey = "";
        bool m_shuttingDown;
        int m_toolSeq = 1;

        NativeOverlay m_overlay;
        int m_lastHotkeyTick;

        static void Log(string text) { Console.Error.WriteLine("clickthrough: " + text); }

        public int Run()
        {
            m_in = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
            m_out = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false));
            m_out.AutoFlush = true;
            while (!m_shuttingDown)
            {
                string line = m_queued.Count > 0 ? m_queued.Dequeue() : m_in.ReadLine();
                if (line == null) break;
                if (line.Trim().Length == 0) continue;
                Dictionary<string, object> message;
                try { message = Json.Parse(line) as Dictionary<string, object>; }
                catch (Exception e) { Log("not JSON: " + e.Message); continue; }
                if (message != null) Handle(message);
            }
            return 0;
        }

        static string Str(object v) { return v == null ? "" : Convert.ToString(v, CultureInfo.InvariantCulture); }
        static Dictionary<string, object> Obj(object v) { return v as Dictionary<string, object>; }

        static long Long(object v)
        {
            if (v == null) return 0;
            try { return Convert.ToInt64(v, CultureInfo.InvariantCulture); }
            catch { return 0; }
        }

        static int Int(object v, int fallback)
        {
            if (v == null) return fallback;
            try { return Convert.ToInt32(v, CultureInfo.InvariantCulture); }
            catch { return fallback; }
        }

        static bool Bool(object v, bool fallback)
        {
            if (v == null) return fallback;
            if (v is bool) return (bool)v;
            return string.Equals(Str(v), "true", StringComparison.OrdinalIgnoreCase);
        }

        static Dictionary<string, object> Make(params object[] pairs)
        {
            var map = new Dictionary<string, object>();
            for (int i = 0; i + 1 < pairs.Length; i += 2) map[Str(pairs[i])] = pairs[i + 1];
            return map;
        }

        void Send(Dictionary<string, object> message)
        {
            m_out.WriteLine(Json.Stringify(message));
            m_out.Flush();
        }

        void Reply(object id, Dictionary<string, object> result)
        {
            Send(Make("jsonrpc", "2.0", "id", id, "result", result ?? new Dictionary<string, object>()));
        }

        void ReplyError(object id, int code, string text)
        {
            Send(Make("jsonrpc", "2.0", "id", id, "error", Make("code", code, "message", text)));
        }

        void Notify(string method, Dictionary<string, object> parameters)
        {
            Send(Make("jsonrpc", "2.0", "method", method, "params", parameters));
        }

        void DebugLog(string text)
        {
            Notify("winshun/log", Make("level", "info", "text", text));
        }

        void Handle(Dictionary<string, object> message)
        {
            string method = Str(message.ContainsKey("method") ? message["method"] : null);
            object id = message.ContainsKey("id") ? message["id"] : null;
            var p = Obj(message.ContainsKey("params") ? message["params"] : null) ?? new Dictionary<string, object>();
            switch (method)
            {
                case "initialize":
                {
                    m_settings = Obj(p.ContainsKey("settings") ? p["settings"] : null) ?? new Dictionary<string, object>();
                    m_locale = Str(p.ContainsKey("locale") ? p["locale"] : null);
                    if (m_locale.Length == 0) m_locale = "zh";
                    m_dataDir = Str(p.ContainsKey("dataDir") ? p["dataDir"] : null);
                    if (m_dataDir.Length == 0) m_dataDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data");
                    var caps = Obj(p.ContainsKey("capabilities") ? p["capabilities"] : null);
                    m_hostHotkeys = caps != null && Bool(caps.ContainsKey("hostHotkeys") ? caps["hostHotkeys"] : null, false);
                    Reply(id, Make("protocolVersion", "1", "name", "ClickThrough"));
                    RestoreTracked(true);
                    SyncHotkey();
                    break;
                }
                case "query":
                    DoQuery(id, p);
                    break;
                case "action":
                    DoAction(id, p);
                    break;
                case "settingsChanged":
                    m_settings = Obj(p.ContainsKey("settings") ? p["settings"] : null) ?? m_settings;
                    SyncHotkey();
                    if (m_tracked != null) UpdateOverlayAlpha(VisualAlpha());
                    break;
                case "hotkey":
                    DoHotkey(p);
                    break;
                case "cancel":
                    break;
                case "shutdown":
                    RestoreTracked(false);
                    Reply(id, new Dictionary<string, object>());
                    m_shuttingDown = true;
                    break;
                default:
                    if (id != null) ReplyError(id, -32601, "Method not found: " + method);
                    break;
            }
        }

        object CallTool(string name, Dictionary<string, object> arguments)
        {
            string id = "ct" + (m_toolSeq++).ToString(CultureInfo.InvariantCulture);
            Send(Make("jsonrpc", "2.0", "id", id, "method", "winshun/tool",
                "params", Make("name", name, "arguments", arguments)));
            while (true)
            {
                string line = m_in.ReadLine();
                if (line == null) return null;
                if (line.Trim().Length == 0) continue;
                Dictionary<string, object> message;
                try { message = Json.Parse(line) as Dictionary<string, object>; }
                catch { continue; }
                if (message == null) continue;
                if (!message.ContainsKey("method") && message.ContainsKey("id") && Str(message["id"]) == id)
                {
                    if (message.ContainsKey("error")) return Make("error", message["error"]);
                    return message.ContainsKey("result") ? message["result"] : new Dictionary<string, object>();
                }
                m_queued.Enqueue(line);
            }
        }

        void SyncHotkey()
        {
            string toggle = Str(m_settings.ContainsKey("toggleHotkey") ? m_settings["toggleHotkey"] : null).Trim();
            string restore = Str(m_settings.ContainsKey("restoreHotkey") ? m_settings["restoreHotkey"] : null).Trim();
            if (toggle == m_hotkey && restore == m_restoreKey) return;
            m_hotkey = toggle;
            m_restoreKey = restore;
            if (!m_hostHotkeys)
            {
                if (toggle.Length > 0 || restore.Length > 0)
                    Log("this WinShun does not register plugin hotkeys; use the ct command");
                return;
            }
            var keys = new List<object>();
            if (toggle.Length > 0) keys.Add(toggle);
            if (restore.Length > 0 && restore != toggle) keys.Add(restore);
            try
            {
                Notify("winshun/hotkeys", Make("keys", keys.ToArray()));
                object answer = CallTool("register_hotkeys", Make("keys", keys.ToArray()));
                var result = Obj(answer);
                string problem = null;
                if (result != null && result.ContainsKey("error"))
                {
                    var error = Obj(result["error"]);
                    problem = Str(error != null && error.ContainsKey("message") ? error["message"] : result["error"]);
                    Log("hotkey registration failed: " + problem);
                }
                else
                {
                    var errors = Obj(result != null && result.ContainsKey("errors") ? result["errors"] : null);
                    if (errors != null)
                    {
                        foreach (var pair in errors)
                        {
                            Log("hotkey not registered: " + pair.Key + "=" + Str(pair.Value));
                            problem = pair.Key + " (" + Str(pair.Value) + ")";
                        }
                    }
                }
                if (problem != null)
                    Notify("winshun/notify", Make("text",
                        L(m_locale == "en", "快捷键未注册：" + problem, "Hotkey not registered: " + problem)));
            }
            catch (Exception e) { Log("hotkey registration failed: " + e.Message); }
        }
        int VisualAlpha()
        {
            int transparency = Int(m_settings.ContainsKey("transparency") ? m_settings["transparency"] : null, 80);
            transparency = Math.Max(0, Math.Min(100, transparency));
            return (255 * (100 - transparency) + 50) / 100;
        }

        bool ShowOverlay(long hwnd, int visualAlpha)
        {
            if (m_overlay == null) m_overlay = new NativeOverlay();
            return m_overlay.Attach(hwnd, visualAlpha);
        }

        void UpdateOverlayAlpha(int visualAlpha) { if (m_overlay != null) m_overlay.SetAlpha(visualAlpha); }

        void HideOverlay() { if (m_overlay != null) m_overlay.Detach(); }

        bool HotkeyTargetEligible(long hwnd)
        {
            IntPtr h = new IntPtr(hwnd);
            uint pid;
            Native.GetWindowThreadProcessId(h, out pid);
            if (pid == (uint)System.Diagnostics.Process.GetCurrentProcess().Id) return false;
            string path = Ct.ProcessPath(pid);
            string program = path.Length > 0 ? Path.GetFileName(path) : "";
            if (string.Equals(program, "explorer.exe", StringComparison.OrdinalIgnoreCase)
                || string.Equals(program, "WinShun.exe", StringComparison.OrdinalIgnoreCase)
                || string.Equals(program, "WinShunPluginHost.exe", StringComparison.OrdinalIgnoreCase)
                || string.Equals(program, "ClickThrough.exe", StringComparison.OrdinalIgnoreCase))
                return false;
            string cls = Ct.ClassName(hwnd);
            if (cls == "Progman" || cls == "WorkerW" || cls == "Shell_TrayWnd" || cls == "Shell_SecondaryTrayWnd")
                return false;
            return true;
        }

        void DoHotkey(Dictionary<string, object> p)
        {
            string key = Str(p.ContainsKey("key") ? p["key"] : null);
            Log("hotkey event: [" + key + "] len=" + key.Length + " registered=[" + m_hotkey + "/" + m_restoreKey + "]");
            bool isToggle = key.Length > 0 && key == m_hotkey;
            bool isRestore = key.Length > 0 && key == m_restoreKey;
            if (!isToggle && !isRestore)
            {
                Log("hotkey ignored: not the registered key");
                return;
            }
            // Auto-repeat guard: repeated notifications of the same press
            // arrive tens of milliseconds apart; 60 ms is below perception
            // for normal presses and drops those repeats.
            int nowTick = Environment.TickCount;
            if (m_lastHotkeyTick != 0 && unchecked(nowTick - m_lastHotkeyTick) < 60)
            {
                Log("hotkey ignored: repeat");
                return;
            }
            m_lastHotkeyTick = nowTick;
            bool en = m_locale == "en";
            bool feedback = Bool(m_settings.ContainsKey("feedback") ? m_settings["feedback"] : null, true);

            // A restore-only hotkey never applies click-through somewhere else.
            if (isRestore && !isToggle)
            {
                LoadState();
                Log("hotkey state: " + (m_tracked == null ? "no click-through" : "tracked " + Str(m_tracked["title"])));
                if (m_tracked != null)
                {
                    long tracked = Long(m_tracked["hwnd"]);
                    string title = Str(m_tracked["title"]);
                    if (tracked != 0 && Ct.WindowExists(tracked))
                    {
                        RestoreTracked(false);
                        Log("hotkey restored: " + title);
                        if (feedback) Notify("winshun/notify", Make("text", L(en, "已恢复：" + title, "Restored: " + title)));
                        return;
                    }
                    ClearState();
                }
                Log("hotkey: no click-through to restore");
                if (feedback) Notify("winshun/notify", Make("text", L(en, "现在没有穿透的窗口", "No click-through window")));
                return;
            }

            // As AltSnap's toggle mode: a press restores the window we made
            // click-through, wherever the pointer is; it never applies to a
            // different window while one is still tracked.
            LoadState();
            Log("hotkey state: " + (m_tracked == null ? "no click-through" : "tracked " + Str(m_tracked["title"])));
            if (m_tracked != null)
            {
                long tracked = Long(m_tracked["hwnd"]);
                string title = Str(m_tracked["title"]);
                if (tracked != 0 && Ct.WindowExists(tracked))
                {
                    RestoreTracked(false);
                    Log("hotkey restored: " + title);
                    if (feedback) Notify("winshun/notify", Make("text", L(en, "已恢复：" + title, "Restored: " + title)));
                    return;
                }
                ClearState(); // the tracked window is gone
            }
            bool useCursor = Bool(m_settings.ContainsKey("useCursor") ? m_settings["useCursor"] : null, false);
            long hwnd = Ct.TargetHwnd(useCursor);
            if (hwnd == 0) { Log("hotkey: no target window"); return; }
            if (!HotkeyTargetEligible(hwnd))
            {
                Log("hotkey: skipped shell/host window class=" + Ct.ClassName(hwnd));
                return;
            }
            var r = ApplyTo(hwnd);
            if (!r.ok)
            {
                Log("hotkey failed: " + r.text + "; hwnd=0x" + hwnd.ToString("X") + " class=" + r.cls
                    + " owner=" + r.owner + " code=" + r.code + " title=" + r.title);
                if (feedback) Notify("winshun/notify", Make("text", L(en, "无法穿透该窗口：" + r.title, "Cannot make it click-through: " + r.title)));
            }
            else
            {
                Log("hotkey applied: " + r.title + " hwnd=0x" + r.hwnd.ToString("X") + " class=" + r.cls);
                if (feedback) Notify("winshun/notify", Make("text", L(en, "已穿透：" + r.title, "Click-through: " + r.title)));
            }
        }

        struct ToggleResult
        {
            public bool ok;
            public long hwnd;
            public string text;
            public string title;
            public string cls;
            public string owner;
            public int code;
        }

        static string L(bool en, string zh, string eg) { return en ? eg : zh; }

        // Apply click-through to `hwnd` and remember what to restore. The
        // caller decides whether to restore first; as AltSnap, only one
        // click-through window exists at a time and a toggle restores instead
        // of moving the effect to another window.
        ToggleResult ApplyTo(long hwnd)
        {
            var result = new ToggleResult();
            if (hwnd == 0) { result.text = "no window"; return result; }
            int transparency = Int(m_settings.ContainsKey("transparency") ? m_settings["transparency"] : null, 80);
            CtApplied applied;
            try { applied = Ct.Apply(hwnd, transparency); }
            catch (Exception e) { result.text = e.Message; return result; }
            if (applied.Error.Length > 0)
            {
                result.text = applied.Error;
                result.title = applied.Title;
                result.cls = applied.Class;
                result.owner = applied.Owner;
                result.code = applied.ErrorCode;
                return result;
            }
            m_tracked = Make(
                "hwnd", applied.Hwnd.ToString(CultureInfo.InvariantCulture),
                "exStyle", applied.ExStyle.ToString(CultureInfo.InvariantCulture),
                "oldAlpha", applied.OldAlpha,
                // The target ends at alpha 0 behind the overlay, so restore
                // always has to put the original alpha back.
                "alphaChanged", true,
                "appliedAlpha", applied.AppliedAlpha,
                "class", applied.Class,
                "title", applied.Title);
            SaveState();
            // Visual: an overlay showing a thumbnail at the configured
            // opacity. Input: the real window becomes fully transparent, so
            // clicks reach the window below's controls (address bar etc.).
            if (ShowOverlay(applied.Hwnd, VisualAlpha()))
                Native.SetLayeredWindowAttributes(new IntPtr(applied.Hwnd), 0, 0, 2);
            else
                Log("DWM thumbnail unavailable; leaving the window faded (clicks may only activate the window below)");
            result.ok = true;
            result.hwnd = applied.Hwnd;
            result.title = applied.Title;
            result.cls = applied.Class;
            return result;
        }

        string StatePath()
        {
            return Path.Combine(m_dataDir, "clickthrough.json");
        }

        void LoadState()
        {
            if (m_tracked != null) return;
            try
            {
                string file = StatePath();
                if (!File.Exists(file)) return;
                var parsed = Json.Parse(File.ReadAllText(file, Encoding.UTF8)) as Dictionary<string, object>;
                if (parsed == null || !parsed.ContainsKey("hwnd") || Long(parsed["hwnd"]) == 0) return;
                m_tracked = parsed;
            }
            catch (Exception e) { Log("state read failed: " + e.Message); }
        }

        void SaveState()
        {
            try
            {
                if (!Directory.Exists(m_dataDir)) Directory.CreateDirectory(m_dataDir);
                string path = StatePath();
                string temp = path + ".tmp";
                File.WriteAllText(temp, Json.Stringify(m_tracked), new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(temp, path, null);
                else File.Move(temp, path);
            }
            catch (Exception e) { Log("state write failed: " + e.Message); }
        }

        void ClearState()
        {
            HideOverlay();
            if (m_tracked != null) Log("state cleared: " + Str(m_tracked["title"]));
            m_tracked = null;
            try { if (File.Exists(StatePath())) File.Delete(StatePath()); }
            catch { }
        }

        bool RestoreTracked(bool load)
        {
            if (load) LoadState();
            if (m_tracked == null) return false;
            HideOverlay();
            long hwnd = Long(m_tracked["hwnd"]);
            if (hwnd == 0 || !Ct.WindowExists(hwnd)) { ClearState(); return false; }
            if (load)
            {
                // Only undo what still looks like our leftover, as AltSnap's
                // RecoverClickThrough does.
                string cls = Str(m_tracked["class"]);
                if (cls.Length > 0 && !string.Equals(Ct.ClassName(hwnd), cls, StringComparison.OrdinalIgnoreCase))
                {
                    Log("state: window class changed, not restoring");
                    ClearState();
                    return false;
                }
                bool achanged = Bool(m_tracked["alphaChanged"], false);
                int applied = Int(m_tracked["appliedAlpha"], 0);
                long ex = Ct.Style(new IntPtr(hwnd));
                bool ours = (ex & 0x20) != 0;
                if (!ours && achanged && applied != 0 && (ex & 0x80000) != 0)
                {
                    uint color; byte alpha; uint flags;
                    ours = Native.GetLayeredWindowAttributes(new IntPtr(hwnd), out color, out alpha, out flags)
                        && (flags & 2) != 0 && alpha == (byte)applied;
                }
                if (!ours) { Log("state: leftover no longer matches, not restoring"); ClearState(); return false; }
            }
            long origEx = 0;
            try
            {
                origEx = Long(m_tracked["exStyle"]);
                int oldAlpha = Int(m_tracked["oldAlpha"], 255);
                bool alphaChanged = Bool(m_tracked["alphaChanged"], false);
                string cls = Str(m_tracked["class"]);
                Ct.Restore(hwnd, origEx, oldAlpha, alphaChanged, cls);
            }
            catch (Exception e) { Log("restore failed: " + e.Message); }
            try
            {
                // After the click-through the window below may be the active
                // one (the user clicked its controls). Put the restored window
                // back on top and make it current again.
                IntPtr h = new IntPtr(hwnd);
                if (Native.IsIconic(h)) Native.ShowWindow(h, 9); // SW_RESTORE
                IntPtr after = (origEx & 0x8) != 0 ? new IntPtr(-1) : IntPtr.Zero; // HWND_TOPMOST / HWND_TOP
                Native.SetWindowPos(h, after, 0, 0, 0, 0, 0x1 | 0x2 | 0x40); // NOMOVE, NOSIZE, SHOWWINDOW
                Native.BringWindowToTop(h);
                Native.SetForegroundWindow(h);
            }
            catch { }
            ClearState();
            return true;
        }

        void DoQuery(object id, Dictionary<string, object> p)
        {
            var items = new List<object>();
            bool en = m_locale == "en";
            CtWindow[] windows;
            try { windows = Ct.Snapshot(); }
            catch (Exception e)
            {
                Log("snapshot failed: " + e.Message);
                items.Add(Make("id", "error", "title", L(en, "无法枚举窗口", "Cannot list windows"),
                    "subtitle", e.Message, "icon", "glyph:U+E783", "valid", false));
                Reply(id, Make("items", items.ToArray()));
                return;
            }
            string[] tokens = Str(p.ContainsKey("text") ? p["text"] : null).Trim()
                .Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            int max = Int(m_settings.ContainsKey("maxRows") ? m_settings["maxRows"] : null, 30);
            if (max <= 0) max = 30;
            LoadState();
            long tracked = m_tracked == null ? 0 : Long(m_tracked["hwnd"]);
            int n = 0;
            foreach (var w in windows)
            {
                if (tokens.Length > 0)
                {
                    string hay = w.Title + " " + w.Process;
                    bool match = true;
                    foreach (string token in tokens)
                    {
                        if (hay.IndexOf(token, StringComparison.OrdinalIgnoreCase) < 0) { match = false; break; }
                    }
                    if (!match) continue;
                }
                if (n >= max) break;
                bool marked = tracked != 0 && w.Hwnd == tracked;
                string stateText = marked ? L(en, "已穿透", "click-through")
                    : w.Transparent ? L(en, "已穿透（其他工具）", "click-through (another tool)")
                    : L(en, "可穿透", "ready");
                string sub = w.Process.Length > 0 ? w.Process + " · " + stateText : stateText;
                if (w.Minimized) sub += L(en, " · 已最小化", " · minimized");
                string icon = w.Path.Length > 0 ? "file:" + w.Path : "glyph:U+E7F4";
                var actions = new object[]
                {
                    Make("id", "toggle", "title", L(en, "穿透 / 恢复", "Toggle"), "key", "Enter"),
                    Make("id", "restore", "title", L(en, "恢复穿透的窗口", "Restore"), "key", "Ctrl+Enter")
                };
                items.Add(Make(
                    "id", "hwnd-" + w.Hwnd.ToString("x"),
                    "title", w.Title,
                    "subtitle", sub,
                    "icon", icon,
                    "score", 95 - n,
                    "match", w.Title + " " + w.Process,
                    "copy", w.Title,
                    "actions", actions,
                    "data", Make("hwnd", w.Hwnd)));
                n++;
            }
            if (items.Count == 0)
            {
                string text = Str(p.ContainsKey("text") ? p["text"] : null).Trim();
                string why = text.Length > 0 ? L(en, "没有匹配的窗口：" + text, "No window matches: " + text)
                    : L(en, "没有可穿透的窗口", "No window to make click-through");
                items.Add(Make("id", "none", "title", why,
                    "subtitle", L(en, "只列出有标题的普通窗口；管理员权限的窗口只有以管理员运行的插件才能修改",
                        "Only captioned, ordinary windows are listed; elevated windows need the elevated plugin"),
                    "icon", "glyph:U+E7F4", "valid", false));
            }
            Reply(id, Make("items", items.ToArray(), "rerun", 0, "cache", 0));
        }

        void DoAction(object id, Dictionary<string, object> p)
        {
            bool en = m_locale == "en";
            string action = Str(p.ContainsKey("action") ? p["action"] : null);
            if (action == "restore")
            {
                bool restored = RestoreTracked(true);
                Reply(id, Make("effects", new object[]
                {
                    Make("type", "notify", "text", restored ? L(en, "已恢复点击穿透的窗口", "Click-through restored")
                        : L(en, "现在没有穿透的窗口", "No click-through window")),
                    Make("type", "hide")
                }));
                return;
            }
            if (action != "toggle")
            {
                Reply(id, Make("effects", new object[]
                {
                    Make("type", "notify", "text", L(en, "未知操作", "Unknown action")),
                    Make("type", "keep")
                }));
                return;
            }
            long hwnd = 0;
            var item = Obj(p.ContainsKey("item") ? p["item"] : null);
            var data = item != null ? Obj(item.ContainsKey("data") ? item["data"] : null) : null;
            if (data != null && data.ContainsKey("hwnd")) hwnd = Long(data["hwnd"]);
            LoadState();
            if (m_tracked != null)
            {
                long tracked = Long(m_tracked["hwnd"]);
                if (tracked != 0 && Ct.WindowExists(tracked))
                {
                    // AltSnap: a listed action on the tracked window restores
                    // it; a request for another window is ignored while one is
                    // tracked. It never moves the effect to another window.
                    if (tracked == hwnd)
                    {
                        string title = Str(m_tracked["title"]);
                        RestoreTracked(false);
                        Log("action restored: " + title);
                        Reply(id, Make("effects", new object[]
                        {
                            Make("type", "notify", "text", L(en, "已恢复：" + title, "Restored: " + title)),
                            Make("type", "hide")
                        }));
                    }
                    else
                    {
                        Reply(id, Make("effects", new object[]
                        {
                            Make("type", "notify", "text", L(en, "已有穿透的窗口，请先恢复它", "One window is already click-through; restore it first")),
                            Make("type", "keep")
                        }));
                    }
                    return;
                }
                ClearState(); // the tracked window is gone
            }
            var r = ApplyTo(hwnd);
            if (r.ok)
            {
                Reply(id, Make("effects", new object[]
                {
                    Make("type", "notify", "text", L(en, "已穿透：" + r.title, "Click-through: " + r.title)),
                    Make("type", "hide")
                }));
                return;
            }
            string failure = r.text == "no window" ? L(en, "没有选中窗口", "No window selected")
                : L(en, "无法修改该窗口：" + r.title + "（可能以管理员运行）", "Cannot change " + r.title + " (elevated?)");
            Log("action failed: " + r.text + "; hwnd=0x" + hwnd.ToString("X") + " class=" + r.cls
                + " owner=" + r.owner + " code=" + r.code + " title=" + r.title);
            Reply(id, Make("effects", new object[]
            {
                Make("type", "notify", "text", failure),
                Make("type", "keep")
            }));
        }
    }

    static class Program
    {
        static int Main(string[] args)
        {
            // The overlay is native and uses physical pixels; keep the plugin
            // per-monitor DPI aware so GetWindowRect/DWM bounds agree. Older
            // Win10 builds only have SetProcessDpiAwareness (shcore).
            try
            {
                if (!Native.SetProcessDpiAwarenessContext(new IntPtr(-4)))
                    Native.SetProcessDpiAwareness(2); // PER_MONITOR_AWARE
            }
            catch
            {
                try { Native.SetProcessDpiAwareness(2); } catch { }
            }
            return new Plugin().Run();
        }
    }
}
