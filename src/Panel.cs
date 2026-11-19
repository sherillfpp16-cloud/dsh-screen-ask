using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace DshScreenAskPanel
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            bool created;
            using (Mutex single = new Mutex(true, "Global\\DshScreenAskPanel", out created))
            {
                if (!created) return;
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                string dir = args.Length > 0 ? args[0] : Path.Combine(Path.GetTempPath(), "dsh-screen-ask");
                bool startExpanded = Array.IndexOf(args, "--expanded") >= 0;
                Application.Run(new PanelForm(dir, startExpanded));
            }
        }
    }

    internal sealed class PanelForm : Form
    {
        private const int CS_DROPSHADOW = 0x00020000;
        private const int WS_MINIMIZEBOX = 0x00020000;
        private const int ORB_SIZE = 58;
        private const int EM_SETCUEBANNER = 0x1501;

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        private static readonly Color Bg = Color.White;
        private static readonly Color Ink = Color.FromArgb(17, 24, 39);
        private static readonly Color Muted = Color.FromArgb(107, 114, 128);
        private static readonly Color Accent = Color.FromArgb(59, 125, 221);
        private static readonly Color Soft = Color.FromArgb(249, 250, 251);

        private readonly string _dir;
        private readonly Panel _header = new Panel();
        private readonly OrbButton _orb = new OrbButton();
        private readonly Label _title = new Label();
        private readonly Label _min = new Label();
        private readonly Label _close = new Label();
        private readonly Panel _previewBox = new Panel();
        private readonly PictureBox _pic = new PictureBox();
        private readonly Panel _inputBox = new Panel();
        private readonly TextBox _question = new TextBox();
        private readonly Label _hint = new Label();
        private readonly Button[] _efforts = new Button[3];
        private readonly Button _ask = new Button();
        private readonly RichTextBox _answer = new RichTextBox();
        private readonly Label _status = new Label();
        private readonly Label _grip = new Label();
        private readonly Panel _answerBox = new Panel();
        private readonly ContextMenuStrip _menu = new ContextMenuStrip();
        private readonly System.Windows.Forms.Timer _poll = new System.Windows.Forms.Timer();

        private string _capturePath = "";
        private string _pendingId = "";
        private bool _busy;
        private bool _expanded;
        private Size _expandedSize = new Size(440, 600);
        private Point _drag;
        private bool _dragging;
        private bool _resizing;
        private Point _resizeStart;
        private Size _resizeFrom;
        private bool _orbDragging;
        private bool _orbDragMoved;
        private Point _orbGrab;

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ClassStyle |= CS_DROPSHADOW;
                cp.Style |= WS_MINIMIZEBOX;
                return cp;
            }
        }

        public PanelForm(string dir, bool startExpanded = false)
        {
            _dir = dir;
            Directory.CreateDirectory(_dir);

            AutoScaleMode = AutoScaleMode.Dpi;
            Text = "屏幕问答";
            Font = new Font("Microsoft YaHei UI", 9f);
            BackColor = Bg;
            ForeColor = Ink;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            var area = Screen.PrimaryScreen.WorkingArea;
            Location = new Point(Math.Max(0, area.Right - ORB_SIZE - 24), Math.Max(0, area.Bottom - ORB_SIZE - 80));
            TopMost = true;
            ShowInTaskbar = false;
            Text = "屏幕问答";

            BuildOrb();
            BuildHeader();
            BuildBody();
            BuildMenu();

            Load += delegate {
                SetOrbMode();
                LoadLocation();

                try { File.AppendAllText(Path.Combine(_dir, "panel.log"), DateTime.Now.ToString("HH:mm:ss") + " load expanded=" + startExpanded + " size=" + Width + "x" + Height + "\r\n"); } catch { }
                if (startExpanded) Expand();
                try { File.AppendAllText(Path.Combine(_dir, "panel.log"), "after size=" + Width + "x" + Height + "\r\n"); } catch { }
            };
            Resize += delegate { ApplyRegion(); };
            _poll.Interval = 300;
            _poll.Tick += delegate { Poll(); };
            _poll.Start();
        }

        private void BuildOrb()
        {
            _orb.SetBounds(0, 0, ORB_SIZE, ORB_SIZE);
            _orb.Cursor = Cursors.Hand;
            _orb.DoubleClick += delegate { Expand(); };
            _orb.MouseDown += delegate(object s, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Right) { _menu.Show(this, e.Location); return; }
                _orbDragging = true;
                _orbGrab = e.Location;
                _orbDragMoved = false;
            };
            _orb.MouseMove += delegate(object s, MouseEventArgs e)
            {
                if (!_orbDragging) return;
                Point screen = PointToScreen(e.Location);
                if (Math.Abs(screen.X - PointToScreen(_orbGrab).X) > 2 || Math.Abs(screen.Y - PointToScreen(_orbGrab).Y) > 2)
                    _orbDragMoved = true;
                Location = new Point(screen.X - _orbGrab.X, screen.Y - _orbGrab.Y);
            };
            _orb.MouseUp += delegate { _orbDragging = false; SaveLocation(); };
            Controls.Add(_orb);
        }

        private void BuildMenu()
        {
            _menu.Items.Add("打开面板", null, delegate { Expand(); });
            _menu.Items.Add("退出", null, delegate { Close(); });
        }

        private void BuildHeader()
        {
            _header.SetBounds(0, 0, Width, 46);
            _header.BackColor = Bg;

            _title.Text = "屏幕问答";
            _title.Font = new Font("Microsoft YaHei UI", 11f, FontStyle.Bold);
            _title.ForeColor = Ink;
            _title.AutoSize = true;
            _title.Location = new Point(16, 15);

            _min.Text = "\u2014";
            _min.Font = new Font("Microsoft YaHei UI", 10f);
            _min.ForeColor = Muted;
            _min.TextAlign = ContentAlignment.MiddleCenter;
            _min.SetBounds(Width - 76, 8, 28, 28);
            _min.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _min.Cursor = Cursors.Hand;
            _min.Click += delegate { Collapse(); };
            _min.MouseEnter += delegate { _min.ForeColor = Ink; };
            _min.MouseLeave += delegate { _min.ForeColor = Muted; };

            _close.Text = "\u00D7";
            _close.Font = new Font("Microsoft YaHei UI", 11f);
            _close.ForeColor = Muted;
            _close.TextAlign = ContentAlignment.MiddleCenter;
            _close.SetBounds(Width - 42, 8, 28, 28);
            _close.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _close.Cursor = Cursors.Hand;
            _close.Click += delegate { Collapse(); };
            _close.MouseEnter += delegate { _close.ForeColor = Color.FromArgb(220, 38, 38); };
            _close.MouseLeave += delegate { _close.ForeColor = Muted; };

            foreach (Control c in new Control[] { _header, _title })
            {
                c.MouseDown += delegate(object s, MouseEventArgs e)
                {
                    if (e.Button != MouseButtons.Left) return;
                    _dragging = true;
                    _drag = e.Location;
                };
                c.MouseMove += delegate(object s, MouseEventArgs e)
                {
                    if (!_dragging) return;
                    Point screen = PointToScreen(e.Location);
                    Location = new Point(screen.X - _drag.X, screen.Y - _drag.Y);
                };
                c.MouseUp += delegate { _dragging = false; };
            }

            _header.Controls.Add(_title);
            _header.Controls.Add(_min);
            _header.Controls.Add(_close);
            Controls.Add(_header);
        }

        private void BuildBody()
        {
            _previewBox.SetBounds(16, 58, Width - 32, 170);
            _previewBox.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            _previewBox.BackColor = Soft;
            _pic.Dock = DockStyle.Fill;
            _pic.SizeMode = PictureBoxSizeMode.Zoom;
            _pic.BackColor = Soft;
            _previewBox.Controls.Add(_pic);

            _inputBox.SetBounds(16, 240, Width - 32, 62);
            _inputBox.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            _inputBox.BackColor = Color.White;
            _inputBox.BorderStyle = BorderStyle.FixedSingle;
            _question.Dock = DockStyle.Fill;
            _question.Multiline = true;
            _question.BorderStyle = BorderStyle.None;
            _question.Font = new Font("Microsoft YaHei UI", 9.5f);
            _question.ForeColor = Ink;
            _question.BackColor = Color.White;

            _inputBox.Controls.Add(_question);
            _hint.Text = "想问什么？留空则让它解释截图";
            _hint.ForeColor = Color.FromArgb(156, 163, 175);
            _hint.BackColor = Color.White;
            _hint.AutoSize = false;
            _hint.TextAlign = ContentAlignment.MiddleLeft;
            _hint.Dock = DockStyle.Fill;
            _hint.Cursor = Cursors.IBeam;
            _hint.Click += delegate { _question.Focus(); };
            _question.TextChanged += delegate { _hint.Visible = _question.Text.Length == 0; };
            _inputBox.Controls.Add(_hint);
            _hint.BringToFront();

            string[] labels = { "off", "high", "max" };
            for (int i = 0; i < 3; i++)
            {
                var b = new Button();
                b.Text = labels[i];
                b.Font = new Font("Microsoft YaHei UI", 9f);
                b.FlatStyle = FlatStyle.Flat;
                b.FlatAppearance.BorderSize = 0;
                b.SetBounds(16 + i * 62, 314, 58, 30);
                b.Cursor = Cursors.Hand;
                b.Tag = labels[i];
                b.Click += delegate(object s, EventArgs e) { SelectEffort((Button)s); };
                _efforts[i] = b;
                Controls.Add(b);
            }

            _ask.Text = "解答";
            _ask.Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold);
            _ask.FlatStyle = FlatStyle.Flat;
            _ask.FlatAppearance.BorderSize = 0;
            _ask.BackColor = Accent;
            _ask.ForeColor = Color.White;
            _ask.Cursor = Cursors.Hand;
            _ask.SetBounds(Width - 104, 312, 88, 34);
            _ask.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _ask.Click += OnAsk;

            _answerBox.SetBounds(16, 358, Width - 32, 200);
            _answerBox.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            _answerBox.BackColor = Soft;
            _answer.Dock = DockStyle.Fill;
            _answer.BorderStyle = BorderStyle.None;
            _answer.ReadOnly = true;
            _answer.BackColor = Soft;
            _answer.ForeColor = Ink;
            _answer.Font = new Font("Microsoft YaHei UI", 9.5f);
            _answerBox.Controls.Add(_answer);

            _status.SetBounds(16, Height - 28, Width - 32, 20);
            _status.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            _status.ForeColor = Muted;
            _status.Font = new Font("Microsoft YaHei UI", 8.5f);
            _status.Text = "按住 X 拖动鼠标框选区域";

            _grip.Text = "\u25E2";
            _grip.SetBounds(Width - 22, Height - 22, 16, 16);
            _grip.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            _grip.ForeColor = Color.FromArgb(209, 213, 219);
            _grip.Cursor = Cursors.SizeNWSE;
            _grip.MouseDown += delegate(object s, MouseEventArgs e)
            {
                if (e.Button != MouseButtons.Left) return;
                _resizing = true;
                _resizeStart = Cursor.Position;
                _resizeFrom = Size;
            };
            _grip.MouseMove += delegate
            {
                if (!_resizing) return;
                Point now = Cursor.Position;
                Size = new Size(
                    Math.Max(MinimumSize.Width, _resizeFrom.Width + (now.X - _resizeStart.X)),
                    Math.Max(MinimumSize.Height, _resizeFrom.Height + (now.Y - _resizeStart.Y)));
            };
            _grip.MouseUp += delegate { _resizing = false; };

            Controls.Add(_previewBox);
            Controls.Add(_inputBox);
            Controls.Add(_answerBox);
            Controls.Add(_status);
            Controls.Add(_grip);
            SelectEffort(_efforts[0]);
        }

        private static void SetCue(TextBox box, string text)
        {
            try { SendMessage(box.Handle, EM_SETCUEBANNER, (IntPtr)1, Marshal.StringToHGlobalUni(text)); }
            catch { }
        }

        private void SetOrbMode()
        {
            _expanded = false;
            MinimumSize = new Size(ORB_SIZE, ORB_SIZE);
            _header.Visible = false;
            _previewBox.Visible = false;
            _inputBox.Visible = false;
            _answerBox.Visible = false;
            _status.Visible = false;
            _grip.Visible = false;
            foreach (var b in _efforts) b.Visible = false;
            _ask.Visible = false;
            _orb.Visible = true;
            _orb.BringToFront();
            Size = new Size(ORB_SIZE, ORB_SIZE);
            Region = new Region(EllipsePath(ClientRectangle));
            Text = "屏幕问答";
        }

        private void Expand()
        {
            if (_expanded) { Activate(); return; }
            _expanded = true;
            MinimumSize = new Size(340, 420);
            _orb.Visible = false;
            _header.Visible = true;
            _previewBox.Visible = true;
            _inputBox.Visible = true;
            _answerBox.Visible = true;
            _status.Visible = true;
            _grip.Visible = true;
            foreach (var b in _efforts) b.Visible = true;
            _ask.Visible = true;
            Size = _expandedSize;
            var area = Screen.PrimaryScreen.WorkingArea;
            int x = Math.Min(Location.X, area.Right - Width - 12);
            int y = Math.Min(Location.Y, area.Bottom - Height - 12);
            Location = new Point(Math.Max(area.Left + 8, x), Math.Max(area.Top + 8, y));
            ApplyRegion();
            Activate();
        }

        private void Collapse()
        {
            if (!_expanded) return;
            _expandedSize = Size;
            SetOrbMode();
            SaveLocation();
        }

        private string LocationFile { get { return Path.Combine(_dir, "panel-location.txt"); } }

        private void LoadLocation()
        {
            try
            {
                if (!File.Exists(LocationFile)) return;
                string[] parts = File.ReadAllText(LocationFile).Trim().Split(',');
                int x, y;
                if (parts.Length != 2 || !int.TryParse(parts[0], out x) || !int.TryParse(parts[1], out y)) return;
                var area = Screen.PrimaryScreen.WorkingArea;
                if (x < area.Left - Width || y < area.Top - Height || x > area.Right || y > area.Bottom) return;
                Location = new Point(x, y);
            }
            catch { }
        }

        private void SaveLocation()
        {
            try { File.WriteAllText(LocationFile, Location.X + "," + Location.Y); } catch { }
        }

        private static GraphicsPath EllipsePath(Rectangle r)
        {
            var path = new GraphicsPath();
            path.AddEllipse(r);
            return path;
        }

        private void ApplyRegion()
        {
            if (!_expanded)
            {
                Region = new Region(EllipsePath(ClientRectangle));
                return;
            }
            using (var path = new GraphicsPath())
            {
                int radius = 12;
                var r = ClientRectangle;
                path.AddArc(r.X, r.Y, radius, radius, 180, 90);
                path.AddArc(r.Right - radius - 1, r.Y, radius, radius, 270, 90);
                path.AddArc(r.Right - radius - 1, r.Bottom - radius - 1, radius, radius, 0, 90);
                path.AddArc(r.X, r.Bottom - radius - 1, radius, radius, 90, 90);
                path.CloseFigure();
                Region = new Region(path);
            }
            RoundControl(_previewBox, 10);
            RoundControl(_inputBox, 8);
            RoundControl(_answerBox, 8);
        }

        private static void RoundControl(Control c, int radius)
        {
            if (c.Width <= 0 || c.Height <= 0) return;
            using (var path = new GraphicsPath())
            {
                var r = c.ClientRectangle;
                path.AddArc(r.X, r.Y, radius, radius, 180, 90);
                path.AddArc(r.Right - radius - 1, r.Y, radius, radius, 270, 90);
                path.AddArc(r.Right - radius - 1, r.Bottom - radius - 1, radius, radius, 0, 90);
                path.AddArc(r.X, r.Bottom - radius - 1, radius, radius, 90, 90);
                path.CloseFigure();
                c.Region = new Region(path);
            }
        }

        private void SelectEffort(Button button)
        {
            foreach (var b in _efforts)
            {
                bool on = b == button;
                b.BackColor = on ? Accent : Soft;
                b.ForeColor = on ? Color.White : Ink;
            }
        }

        private string SelectedEffort
        {
            get
            {
                foreach (var b in _efforts)
                    if (b.BackColor == Accent) return Convert.ToString(b.Tag);
                return "off";
            }
        }

        private void Poll()
        {
            try
            {
                RefreshCapture();
                RefreshAnswer();
                if (PluginGone()) Close();
            }
            catch { }
        }

        private bool PluginGone()
        {
            string beat = Path.Combine(_dir, "heartbeat");
            if (!File.Exists(beat)) return false;
            try
            {
                return (DateTime.UtcNow - File.GetLastWriteTimeUtc(beat)).TotalSeconds > 40;
            }
            catch { return false; }
        }

        private void RefreshCapture()
        {
            string file = Path.Combine(_dir, "last-capture.txt");
            if (!File.Exists(file)) return;
            string[] lines = File.ReadAllLines(file, Encoding.UTF8);
            if (lines.Length < 2) return;
            string path = lines[0].Trim();
            if (path.Length == 0 || path == _capturePath || !File.Exists(path)) return;
            string previous = _capturePath;
            _capturePath = path;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var loaded = Image.FromStream(fs))
            {
                var old = _pic.Image;
                _pic.Image = new Bitmap(loaded);
                if (old != null) old.Dispose();
            }
            _answer.Text = "";
            _status.Text = "截图就绪 · 输入问题后点「解答」";
            if (previous.Length > 0 && !_expanded) Expand();
        }

        private void RefreshAnswer()
        {
            if (_pendingId.Length == 0) return;
            string file = Path.Combine(_dir, "panel-answer.txt");
            if (!File.Exists(file)) return;
            string text = File.ReadAllText(file, Encoding.UTF8);
            int first = text.IndexOf('\n');
            if (first <= 0) return;
            if (text.Substring(0, first).Trim() != _pendingId) return;
            int second = text.IndexOf('\n', first + 1);
            string status = second < 0 ? text.Substring(first + 1).Trim() : text.Substring(first + 1, second - first - 1).Trim();
            string body = second < 0 ? "" : text.Substring(second + 1);
            _pendingId = "";
            _busy = false;
            _ask.Enabled = true;
            _answer.Text = body;
            _status.Text = status == "OK" ? "完成 · " + SelectedEffort : "出错";
        }

        private void OnAsk(object sender, EventArgs e)
        {
            if (_busy) return;
            if (_capturePath.Length == 0)
            {
                _status.Text = "还没有截图：按住 X 拖动鼠标框选";
                return;
            }
            string question = _question.Text.Replace("\r", " ").Replace("\n", " ").Trim();
            string effort = SelectedEffort;
            _pendingId = Guid.NewGuid().ToString("N");
            var sb = new StringBuilder();
            sb.AppendLine(_pendingId);
            sb.AppendLine(question);
            sb.AppendLine(_capturePath);
            sb.AppendLine(effort);
            File.WriteAllText(Path.Combine(_dir, "panel-request.txt"), sb.ToString(), Encoding.UTF8);
            _answer.Text = "";
            _busy = true;
            _ask.Enabled = false;
            _status.Text = "思考中…（" + effort + "）";
        }
    }
    internal sealed class OrbButton : Control
    {
        private bool _hover;
        private Image _icon;

        public OrbButton()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.UserPaint | ControlStyles.ResizeRedraw
                | ControlStyles.StandardDoubleClick | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            try
            {
                string file = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "orb-whale.png");
                if (File.Exists(file))
                {
                    using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
                    using (var img = Image.FromStream(fs))
                        _icon = new Bitmap(img);
                }
            }
            catch { }
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            _hover = true;
            Invalidate();
            base.OnMouseEnter(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            _hover = false;
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            var r = new Rectangle(1, 1, Width - 3, Height - 3);
            Color top = _hover ? Color.FromArgb(246, 250, 255) : Color.White;
            Color bottom = _hover ? Color.FromArgb(226, 236, 251) : Color.FromArgb(238, 243, 251);
            using (var brush = new System.Drawing.Drawing2D.LinearGradientBrush(r, top, bottom, 60f))
                g.FillEllipse(brush, r);
            using (var ring = new Pen(_hover ? Color.FromArgb(90, 59, 125, 221) : Color.FromArgb(28, 0, 0, 0), 1.4f))
                g.DrawEllipse(ring, r);

            if (_icon != null)
            {
                float side = Math.Min(Width, Height) * 0.62f;
                float x = (Width - side) / 2f;
                float y = (Height - side) / 2f;
                g.DrawImage(_icon, new RectangleF(x, y, side, side));
            }
            else
            {
                float w = Width, h = Height;
                using (var blue = new SolidBrush(Color.FromArgb(54, 101, 196)))
                {
                    var body = new RectangleF(w * 0.24f, h * 0.33f, w * 0.52f, h * 0.36f);
                    using (var path = RoundedRect(body, Math.Min(body.Width, body.Height) * 0.22f))
                        g.FillPath(blue, path);
                    g.FillEllipse(blue, w * 0.40f, h * 0.39f, w * 0.20f, h * 0.20f);
                }
            }
        }

        private static System.Drawing.Drawing2D.GraphicsPath RoundedRect(RectangleF r, float radius)
        {
            var path = new System.Drawing.Drawing2D.GraphicsPath();
            float d = radius * 2;
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }
}
