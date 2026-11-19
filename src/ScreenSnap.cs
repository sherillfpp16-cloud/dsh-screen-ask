using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace DshScreenSnap
{
    internal static class Program
    {
        private const int WH_MOUSE_LL = 14;
        private const int WH_KEYBOARD_LL = 13;
        private const int WM_XBUTTONDOWN = 0x020B;
        private const int WM_XBUTTONUP = 0x020C;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_KEYUP = 0x0101;

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X; public int Y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct MSLLHOOKSTRUCT
        {
            public POINT pt;
            public uint mouseData;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KBDLLHOOKSTRUCT
        {
            public uint vkCode;
            public uint scanCode;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);
        [DllImport("user32.dll")]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);
        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();
        [DllImport("user32.dll")]
        private static extern IntPtr GetConsoleWindow();
        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();
        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool GetTokenInformation(IntPtr token, int cls, IntPtr info, int len, out int ret);
        [DllImport("advapi32.dll")]
        private static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);
        [DllImport("advapi32.dll")]
        private static extern IntPtr GetSidSubAuthority(IntPtr sid, int index);

        private static LowLevelMouseProc _proc;
        private static IntPtr _hook = IntPtr.Zero;
        private static IntPtr _kbHook = IntPtr.Zero;
        private static string _outDir;
        private static string _heartbeat;
        private static SelectionForm _overlay;
        private static Form _hidden;
        private static bool _capturing;
        private static System.Windows.Forms.Timer _armTimer;
        private static System.Windows.Forms.Timer _hotkeyTimer;
        private static Mutex _single;

        // hotkey state
        private static string _hotkeySpec = "x";
        private static uint _hotkeyVK = 'X';
        private static int _hotkeyMouseSide = 0;
        private static bool _needCtrl, _needAlt, _needShift, _needWin;
        private static bool _ctrlDown, _altDown, _shiftDown, _winDown;
        private static bool _mainDown;
        private static DateTime _hotkeyStamp = DateTime.MinValue;

        private static string EventsPath { get { return Path.Combine(_outDir, "events.jsonl"); } }

        private static void Log(string text)
        {
            try { File.AppendAllText(Path.Combine(_outDir, "helper.log"), DateTime.Now.ToString("HH:mm:ss.fff") + " " + text + "\r\n", Encoding.UTF8); }
            catch { }
        }

        private static string Esc(string s)
        {
            if (s == null) s = "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "").Replace("\n", " | ");
        }

        private static void Emit(string json)
        {
            try { File.AppendAllText(EventsPath, json + "\r\n", Encoding.UTF8); }
            catch (Exception ex) { Log("emit failed: " + ex.Message); }
        }

        private static string IntegrityLevel()
        {
            try
            {
                IntPtr token;
                if (!OpenProcessToken(GetCurrentProcess(), 0x0008, out token)) return "token-failed";
                int len;
                GetTokenInformation(token, 25, IntPtr.Zero, 0, out len);
                IntPtr buf = Marshal.AllocHGlobal(len);
                string result = "?";
                if (GetTokenInformation(token, 25, buf, len, out len))
                {
                    IntPtr sid = Marshal.ReadIntPtr(buf);
                    int count = Marshal.ReadByte(GetSidSubAuthorityCount(sid));
                    int rid = Marshal.ReadInt32(GetSidSubAuthority(sid, count - 1));
                    result = "S-1-16-" + rid;
                }
                Marshal.FreeHGlobal(buf);
                return result;
            }
            catch { return "error"; }
        }

        // ---- hotkey parsing -------------------------------------------------

        private static bool ParseHotkey(string spec)
        {
            if (string.IsNullOrWhiteSpace(spec)) return false;
            string text = spec.Trim().ToLowerInvariant();
            string[] parts = text.Split(new[] { '+' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return false;

            bool ctrl = false, alt = false, shift = false, win = false;
            string key = null;
            foreach (string raw in parts)
            {
                string token = raw.Trim();
                if (token.Length == 0) continue;
                if (token == "ctrl" || token == "control") ctrl = true;
                else if (token == "alt") alt = true;
                else if (token == "shift") shift = true;
                else if (token == "win" || token == "windows" || token == "meta") win = true;
                else key = token;
            }
            if (key == null) return false;

            uint vk = 0;
            int mouseSide = 0;

            if (key == "side" || key == "mouse4" || key == "xbutton1") mouseSide = 1;
            else if (key == "mouse5" || key == "xbutton2") mouseSide = 2;
            else if (key.Length == 1)
            {
                char ch = key[0];
                if (ch >= 'a' && ch <= 'z') vk = (uint)char.ToUpperInvariant(ch);
                else if (ch >= '0' && ch <= '9') vk = ch;
                else if (ch == '`' || ch == '~') vk = 0xC0;
                else if (ch == '-') vk = 0xBD;
                else if (ch == '=') vk = 0xBB;
                else if (ch == '[') vk = 0xDB;
                else if (ch == ']') vk = 0xDD;
                else if (ch == ';') vk = 0xBA;
                else if (ch == '\'') vk = 0xDE;
                else if (ch == ',') vk = 0xBC;
                else if (ch == '.') vk = 0xBE;
                else if (ch == '/') vk = 0xBF;
                else if (ch == '\\') vk = 0xDC;
            }
            else if (key.StartsWith("f") && key.Length <= 3)
            {
                int n;
                if (int.TryParse(key.Substring(1), out n) && n >= 1 && n <= 24) vk = (uint)(0x70 + n - 1);
            }
            else if (key == "space") vk = 0x20;
            else if (key == "tab") vk = 0x09;
            else if (key == "enter" || key == "return") vk = 0x0D;
            else if (key == "esc" || key == "escape") vk = 0x1B;
            else if (key == "backspace") vk = 0x08;
            else if (key == "insert") vk = 0x2D;
            else if (key == "delete" || key == "del") vk = 0x2E;
            else if (key == "home") vk = 0x24;
            else if (key == "end") vk = 0x23;
            else if (key == "pageup" || key == "pgup") vk = 0x21;
            else if (key == "pagedown" || key == "pgdn") vk = 0x22;
            else if (key == "up") vk = 0x26;
            else if (key == "down") vk = 0x28;
            else if (key == "left") vk = 0x25;
            else if (key == "right") vk = 0x27;

            if (vk == 0 && mouseSide == 0) return false;

            _hotkeySpec = text;
            _hotkeyVK = vk;
            _hotkeyMouseSide = mouseSide;
            _needCtrl = ctrl;
            _needAlt = alt;
            _needShift = shift;
            _needWin = win;
            return true;
        }

        private static void ReloadHotkey()
        {
            string file = Path.Combine(_outDir, "hotkey.txt");
            try
            {
                if (!File.Exists(file)) return;
                DateTime stamp = File.GetLastWriteTimeUtc(file);
                if (stamp == _hotkeyStamp) return;
                _hotkeyStamp = stamp;
                string spec = File.ReadAllText(file, Encoding.UTF8).Trim();
                if (spec.Length == 0 || spec == _hotkeySpec) return;
                if (ParseHotkey(spec))
                {
                    Log("hotkey reloaded: " + _hotkeySpec);
                    Emit("{\"type\":\"hotkey\",\"hotkey\":\"" + Esc(_hotkeySpec) + "\"}");
                }
                else Log("hotkey invalid: " + spec);
            }
            catch (Exception ex) { Log("hotkey reload failed: " + ex.Message); }
        }

        // ---------------------------------------------------------------------

        [STAThread]
        private static void Main(string[] args)
        {
            bool created;
            _single = new Mutex(true, "Global\\DshScreenSnapHelper", out created);
            if (!created) return;
            try { _outDir = Path.Combine(Path.GetTempPath(), "dsh-screen-snap"); Run(args); }
            catch (Exception ex)
            {
                try { File.AppendAllText(Path.Combine(_outDir ?? Path.GetTempPath(), "fatal.log"), ex.ToString()); } catch { }
            }
        }

        private static void Run(string[] args)
        {
            string mode = "";
            string spec = "";
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--out" && i + 1 < args.Length) { _outDir = args[i + 1]; i++; }
                else if (args[i] == "--hotkey" && i + 1 < args.Length) { _hotkeySpec = args[i + 1]; i++; }
                else if (args[i] == "--hold" && i + 1 < args.Length) { _hotkeySpec = (args[i + 1] ?? "x").ToLowerInvariant(); i++; }
                else if (args[i] == "--heartbeat" && i + 1 < args.Length) { _heartbeat = args[i + 1]; i++; }
                else if (args[i] == "--capture-region" && i + 1 < args.Length) { mode = "capture"; spec = args[i + 1]; i++; }
            }

            Directory.CreateDirectory(_outDir);
            ParseHotkey(_hotkeySpec);
            Log("start mode=" + mode + " hotkey=" + _hotkeySpec + " out=" + _outDir + " il=" + IntegrityLevel());

            try
            {
                IntPtr console = GetConsoleWindow();
                if (console != IntPtr.Zero) ShowWindow(console, 0);
            }
            catch { }

            if (_heartbeat != null && _heartbeat.Length > 0)
            {
                System.Threading.Timer heartbeat = null;
                heartbeat = new System.Threading.Timer(delegate(object state)
                {
                    try
                    {
                        DateTime stamp = File.GetLastWriteTimeUtc(_heartbeat);
                        if (stamp == DateTime.MinValue || (DateTime.UtcNow - stamp).TotalSeconds > 25)
                        {
                            Log("heartbeat stale, exiting");
                            Environment.Exit(0);
                        }
                    }
                    catch { Log("heartbeat check error, exiting"); Environment.Exit(0); }
                }, null, 3000, 3000);
                GC.KeepAlive(heartbeat);
            }

            if (mode == "capture") { CaptureRegionCommand(spec); return; }

            SetProcessDPIAware();
            Application.EnableVisualStyles();
            Application.ThreadException += delegate(object sender, ThreadExceptionEventArgs e) { Log("thread exception: " + e.Exception); };

            _hidden = new Form();
            _hidden.ShowInTaskbar = false;
            _hidden.WindowState = FormWindowState.Minimized;
            _hidden.FormBorderStyle = FormBorderStyle.None;
            _hidden.Opacity = 0;
            _hidden.Show();

            _armTimer = new System.Windows.Forms.Timer();
            _armTimer.Interval = 220;
            _armTimer.Tick += delegate(object sender, EventArgs e)
            {
                _armTimer.Stop();
                if (_mainDown) BeginSelection();
            };

            _hotkeyTimer = new System.Windows.Forms.Timer();
            _hotkeyTimer.Interval = 1000;
            _hotkeyTimer.Tick += delegate { ReloadHotkey(); };
            _hotkeyTimer.Start();
            ReloadHotkey();

            _proc = HookCallback;
            _hook = SetWindowsHookEx(WH_MOUSE_LL, _proc, IntPtr.Zero, 0);
            _kbHook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, IntPtr.Zero, 0);
            Log("mouse hook=" + _hook.ToInt64() + " kb hook=" + _kbHook.ToInt64());
            Emit("{\"type\":\"ready\",\"hotkey\":\"" + Esc(_hotkeySpec) + "\"}");
            Application.Run(_hidden);
        }

        private static void CaptureRegionCommand(string spec)
        {
            string[] parts = spec.Split(',');
            Rectangle rect = new Rectangle(int.Parse(parts[0]), int.Parse(parts[1]), int.Parse(parts[2]), int.Parse(parts[3]));
            string path = parts.Length >= 5 && parts[4].Length > 0 ? parts[4] : Path.Combine(_outDir, "snap.png");
            using (Bitmap bmp = new Bitmap(rect.Width, rect.Height, PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                    g.CopyFromScreen(rect.Left, rect.Top, 0, 0, rect.Size, CopyPixelOperation.SourceCopy);
                bmp.Save(path, ImageFormat.Png);
            }
            Emit("{\"type\":\"capture\",\"path\":\"" + Esc(path) + "\"}");
        }

        private static bool ModifiersSatisfied()
        {
            if (_needCtrl && !_ctrlDown) return false;
            if (_needAlt && !_altDown) return false;
            if (_needShift && !_shiftDown) return false;
            if (_needWin && !_winDown) return false;
            if (!_needCtrl && !_needAlt && !_needShift && !_needWin)
                return !_ctrlDown && !_altDown && !_shiftDown && !_winDown;
            return true;
        }

        private static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                if (nCode >= 0)
                {
                    int msg = (int)wParam;
                    if (msg == WM_XBUTTONDOWN || msg == WM_XBUTTONUP)
                    {
                        if (_hotkeyMouseSide != 0)
                        {
                            MSLLHOOKSTRUCT info = (MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(MSLLHOOKSTRUCT));
                            int button = (int)((info.mouseData >> 16) & 0xFFFF);
                            if (button == _hotkeyMouseSide)
                            {
                                if (msg == WM_XBUTTONDOWN) BeginSelection();
                                else CancelSelection("side-released");
                                return (IntPtr)1;
                            }
                        }
                    }
                    else if (msg == WM_KEYDOWN || msg == WM_KEYUP)
                    {
                        KBDLLHOOKSTRUCT key = (KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(KBDLLHOOKSTRUCT));
                        uint vk = key.vkCode;
                        bool down = msg == WM_KEYDOWN;

                        // track modifier state
                        if (vk == 0x11) _ctrlDown = down;
                        else if (vk == 0x12) _altDown = down;
                        else if (vk == 0x10) _shiftDown = down;
                        else if (vk == 0x5B || vk == 0x5C) _winDown = down;

                        if (_hotkeyVK != 0 && vk == _hotkeyVK)
                        {
                            if (down)
                            {
                                if (!_mainDown && ModifiersSatisfied())
                                {
                                    _mainDown = true;
                                    Log("hotkey down: " + _hotkeySpec);
                                    if (_needCtrl || _needAlt || _needShift || _needWin) BeginSelection();
                                    else _armTimer.Start();
                                }
                            }
                            else
                            {
                                _mainDown = false;
                                _armTimer.Stop();
                                if (_capturing) CancelSelection("hotkey-released");
                            }
                        }
                        else if (!down && _capturing)
                        {
                            // releasing a required modifier aborts the selection
                            bool wasModifier =
                                (vk == 0x11 && _needCtrl) || (vk == 0x12 && _needAlt) ||
                                (vk == 0x10 && _needShift) || ((vk == 0x5B || vk == 0x5C) && _needWin);
                            if (wasModifier) CancelSelection("modifier-released");
                        }
                    }
                }
            }
            catch (Exception ex) { Log("hook error " + ex.Message); }
            return CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        private static void BeginSelection()
        {
            if (_capturing || _overlay != null) return;
            _capturing = true;
            try
            {
                _hidden.BeginInvoke((MethodInvoker)delegate
                {
                    try
                    {
                        Log("overlay show");
                        _overlay = new SelectionForm();
                        _overlay.OnDone = OnSelectionDone;
                        _overlay.OnCancel = delegate(string reason) { CancelSelection(reason); };
                        _overlay.Show();
                        _overlay.Activate();
                        _overlay.BringToFront();
                    }
                    catch (Exception ex) { Log("overlay fail " + ex); CancelSelection("overlay-failed"); }
                });
            }
            catch (Exception ex) { Log("invoke fail " + ex.Message); CancelSelection("invoke-failed"); }
        }

        private static void CancelSelection(string reason)
        {
            try
            {
                _hidden.BeginInvoke((MethodInvoker)delegate
                {
                    bool had = _overlay != null;
                    if (_overlay != null)
                    {
                        _overlay.Close();
                        _overlay.Dispose();
                        _overlay = null;
                    }
                    _capturing = false;
                    if (had) Emit("{\"type\":\"cancelled\",\"reason\":\"" + reason + "\"}");
                });
            }
            catch (Exception ex) { Log("cancel fail " + ex.Message); }
        }

        private static void OnSelectionDone(Rectangle rect)
        {
            try
            {
                Log("done " + rect);
                if (_overlay != null) _overlay.Hide();
                Thread.Sleep(120);
                string path = Path.Combine(_outDir, "snap-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + ".png");
                using (Bitmap bmp = new Bitmap(rect.Width, rect.Height, PixelFormat.Format32bppArgb))
                {
                    using (Graphics g = Graphics.FromImage(bmp))
                        g.CopyFromScreen(rect.Left, rect.Top, 0, 0, rect.Size, CopyPixelOperation.SourceCopy);
                    bmp.Save(path, ImageFormat.Png);
                }
                Log("saved " + path);
                try
                {
                    File.WriteAllText(Path.Combine(_outDir, "last-capture.txt"),
                        path + "\n" + DateTime.Now.ToString("o") + "\n" + rect.Width + "\n" + rect.Height, Encoding.UTF8);
                    StartPanel();
                }
                catch (Exception panelEx) { Log("panel start fail " + panelEx.Message); }
                Emit("{\"type\":\"capture\",\"path\":\"" + Esc(path) + "\",\"w\":" + rect.Width + ",\"h\":" + rect.Height + "}");
            }
            catch (Exception ex) { Log("capture fail " + ex.Message); Emit("{\"type\":\"error\",\"message\":\"" + Esc(ex.Message) + "\"}"); }
            finally { CancelSelection("done"); }
        }

        private static void StartPanel()
        {
            try
            {
                string dir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
                string exe = Path.Combine(dir, "Panel.exe");
                if (!File.Exists(exe)) { Log("panel missing"); return; }
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = "\"" + _outDir + "\"",
                    UseShellExecute = true,
                });
            }
            catch (Exception ex) { Log("panel start error " + ex.Message); }
        }
    }

    internal sealed class SelectionForm : Form
    {
        public Action<Rectangle> OnDone;
        public Action<string> OnCancel;

        private Point _start;
        private Point _current;
        private bool _dragging;

        public SelectionForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            Bounds = SystemInformation.VirtualScreen;
            TopMost = true;
            ShowInTaskbar = false;
            BackColor = Color.Black;
            Opacity = 0.35;
            Cursor = Cursors.Cross;
            DoubleBuffered = true;
            KeyPreview = true;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Rectangle r = Normalize();
            if (_dragging && r.Width > 0 && r.Height > 0)
            {
                using (Brush fill = new SolidBrush(Color.FromArgb(50, 120, 200, 255)))
                    e.Graphics.FillRectangle(fill, r);
                using (Pen pen = new Pen(Color.FromArgb(235, 120, 200, 255), 2f))
                    e.Graphics.DrawRectangle(pen, r);
            }
            using (Font f = new Font("Microsoft YaHei UI", 14f))
            using (Brush text = new SolidBrush(Color.FromArgb(235, 255, 255, 255)))
                e.Graphics.DrawString("拖动鼠标框选区域，松开快捷键或右键/Esc 取消", f, text, new PointF(24f, 20f));
        }

        private Rectangle Normalize()
        {
            int x = Math.Min(_start.X, _current.X);
            int y = Math.Min(_start.Y, _current.Y);
            int w = Math.Abs(_current.X - _start.X);
            int h = Math.Abs(_current.Y - _start.Y);
            return new Rectangle(x, y, w, h);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left)
            {
                _start = new Point(e.X + Left, e.Y + Top);
                _current = _start;
                _dragging = true;
                Invalidate();
            }
            else if (e.Button == MouseButtons.Right && OnCancel != null) OnCancel("right-click");
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_dragging) { _current = new Point(e.X + Left, e.Y + Top); Invalidate(); }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left || !_dragging) return;
            _dragging = false;
            Rectangle r = Normalize();
            if (r.Width < 8 || r.Height < 8) { if (OnCancel != null) OnCancel("too-small"); return; }
            if (OnDone != null) OnDone(r);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Escape && OnCancel != null) OnCancel("esc");
        }
    }
}
