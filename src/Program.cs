using System.Drawing.Drawing2D;
using System.Globalization;

namespace HNY;

internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        using var mutex = new Mutex(true, "HNY.SingleInstance", out bool first);
        if (!first) return;

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayContext(args));
    }
}

/// <summary>Прозрачное окно поверх всех, пропускающее клики насквозь.</summary>
internal sealed class OverlayWindow : NativeWindow
{
    public Action? CloseRequested;

    public OverlayWindow()
    {
        CreateHandle(new CreateParams
        {
            Caption = "HNY",
            Style = Native.WS_POPUP,
            ExStyle = Native.WS_EX_LAYERED | Native.WS_EX_TRANSPARENT | Native.WS_EX_TOPMOST
                      | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE,
            X = 0, Y = 0, Width = 1, Height = 1,
        });
        Native.ShowWindow(Handle, Native.SW_SHOWNOACTIVATE);
    }

    protected override void WndProc(ref Message m)
    {
        const int WM_MOUSEACTIVATE = 0x21, MA_NOACTIVATE = 3, WM_CLOSE = 0x10;
        if (m.Msg == WM_CLOSE) { CloseRequested?.Invoke(); return; } // установщик просит закрыться перед обновлением/удалением
        if (m.Msg == WM_MOUSEACTIVATE) { m.Result = MA_NOACTIVATE; return; } // клик по снежку не забирает фокус
        base.WndProc(ref m);
    }
}

internal sealed class TrayContext : ApplicationContext
{
    readonly OverlayWindow _overlay = new();
    readonly Engine _engine;
    readonly NotifyIcon _tray;
    readonly Icon _icon;
    readonly ToolStripMenuItem _pauseItem;
    readonly ToolStripMenuItem[] _intensityItems;
    readonly string? _selfTestLog;

    static readonly (string Name, float Value)[] Levels =
    [
        ("Лёгкий снежок", 0.45f),
        ("Снегопад", 1f),
        ("Метель", 2.2f),
    ];

    public TrayContext(string[] args)
    {
        _engine = new Engine(_overlay.Handle);
        _overlay.CloseRequested = ExitThread;
        _icon = CreateIcon();

        _pauseItem = new ToolStripMenuItem("Пауза", null, (_, _) => TogglePause());
        _intensityItems = new ToolStripMenuItem[Levels.Length];
        var intensity = new ToolStripMenuItem("Сила снегопада");
        for (int i = 0; i < Levels.Length; i++)
        {
            int index = i;
            _intensityItems[i] = new ToolStripMenuItem(Levels[i].Name, null, (_, _) => SetIntensity(index)) { Checked = i == 1 };
            intensity.DropDownItems.Add(_intensityItems[i]);
        }
        var drifts = new ToolStripMenuItem("Сугробы на окнах") { Checked = true, CheckOnClick = true };
        drifts.CheckedChanged += (_, _) => _engine.Drifts = drifts.Checked;

        var menu = new ContextMenuStrip();
        menu.Items.Add(_pauseItem);
        menu.Items.Add(intensity);
        menu.Items.Add(drifts);
        var balls = new ToolStripMenuItem("Лепить снежки мышью") { Checked = true, CheckOnClick = true };
        balls.CheckedChanged += (_, _) => _engine.Snowballs = balls.Checked;
        menu.Items.Add(balls);
        var garland = new ToolStripMenuItem("Гирлянда на окнах") { Checked = true, CheckOnClick = true };
        garland.CheckedChanged += (_, _) => _engine.Garland = garland.Checked;
        menu.Items.Add(garland);
        var plow = new ToolStripMenuItem("Снегоуборщик выезжает сам") { Checked = true, CheckOnClick = true };
        plow.CheckedChanged += (_, _) => _engine.Plow = plow.Checked;
        menu.Items.Add(plow);
        menu.Items.Add("Вызвать снегоуборщик", null, (_, _) => _engine.PlowRequested = true);
        menu.Items.Add("Стряхнуть снег", null, (_, _) => _engine.ClearRequested = true);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Выход", null, (_, _) => ExitThread());

        _tray = new NotifyIcon
        {
            Icon = _icon,
            Text = "HNY — Happy New Year",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _tray.DoubleClick += (_, _) => TogglePause();

        _engine.Start();
        if (Array.IndexOf(args, "--demo") >= 0) _engine.DemoRequested = true;
        if (Array.IndexOf(args, "--plow") >= 0) _engine.PlowRequested = true;

        // Для постановочных кадров: --intensity <множитель>, --plow-after <сек>.
        if (ArgValue(args, "--intensity") is double level) _engine.Intensity = (float)level;
        if (ArgValue(args, "--plow-after") is double plowAfter)
        {
            var plowTimer = new System.Windows.Forms.Timer { Interval = Math.Max(1, (int)(plowAfter * 1000)) };
            plowTimer.Tick += (_, _) => { plowTimer.Stop(); _engine.PlowRequested = true; };
            plowTimer.Start();
        }
        if (Array.IndexOf(args, "--shed") >= 0)
        {
            // для самопроверки: через 6 секунд стряхнуть снег со всех окон
            var shed = new System.Windows.Forms.Timer { Interval = 6000 };
            shed.Tick += (_, _) => { shed.Stop(); _engine.ClearRequested = true; };
            shed.Start();
        }

        // --selftest <сек> [лог]: поработать N секунд, записать статистику и выйти.
        int st = Array.IndexOf(args, "--selftest");
        if (st >= 0)
        {
            double seconds = st + 1 < args.Length && double.TryParse(args[st + 1], CultureInfo.InvariantCulture, out var v) ? v : 5;
            _selfTestLog = st + 2 < args.Length ? args[st + 2] : Path.Combine(Path.GetTempPath(), "hny-selftest.log");
            var timer = new System.Windows.Forms.Timer { Interval = (int)(seconds * 1000) };
            timer.Tick += (_, _) => { timer.Stop(); ExitThread(); };
            timer.Start();
        }
    }

    static double? ArgValue(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length && double.TryParse(args[i + 1], CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    void TogglePause()
    {
        _engine.Paused = !_engine.Paused;
        _pauseItem.Text = _engine.Paused ? "Продолжить" : "Пауза";
    }

    void SetIntensity(int index)
    {
        _engine.Intensity = Levels[index].Value;
        for (int i = 0; i < _intensityItems.Length; i++) _intensityItems[i].Checked = i == index;
    }

    protected override void ExitThreadCore()
    {
        _tray.Visible = false;
        _engine.Dispose();
        if (_selfTestLog != null) WriteSelfTest(_selfTestLog);
        _tray.Dispose();
        _overlay.DestroyHandle();
        _icon.Dispose();
        base.ExitThreadCore();
    }

    void WriteSelfTest(string path)
    {
        var e = _engine;
        long n = Math.Max(1, e.Frames);
        File.WriteAllText(path, string.Create(CultureInfo.InvariantCulture, $"""
            frames={e.Frames}
            sim+render ms/frame={e.WorkMs / n:F3}
            present ms/frame={e.PresentMs / n:F3}
            present failures={e.PresentFailures}
            windows={e.LastWindowCount}
            surfaces={e.LastSurfaceCount}
            taskbar={e.TaskbarSeen}
            max pile px={e.MaxPile:F2}
            balls={e.BallCount}
            bulbs drawn total={e.BulbsDrawn}
            plow runs={e.PlowRuns}
            clumps landed={e.ClumpsLanded}

            """));
    }

    static Icon CreateIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var bg = new SolidBrush(Color.FromArgb(255, 38, 92, 190));
            g.FillEllipse(bg, 1, 1, 30, 30);
            using var pen = new Pen(Color.White, 2.2f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            const float c = 16f, len = 10.5f, twig = 3.6f;
            for (int i = 0; i < 6; i++)
            {
                double ang = Math.PI / 3 * i + Math.PI / 6;
                float dx = (float)Math.Cos(ang), dy = (float)Math.Sin(ang);
                g.DrawLine(pen, c, c, c + dx * len, c + dy * len);
                float mx = c + dx * len * 0.62f, my = c + dy * len * 0.62f;
                foreach (double off in new[] { 0.9, -0.9 })
                {
                    float tx = (float)Math.Cos(ang + off), ty = (float)Math.Sin(ang + off);
                    g.DrawLine(pen, mx, my, mx + tx * twig, my + ty * twig);
                }
            }
        }
        IntPtr h = bmp.GetHicon();
        try { return (Icon)Icon.FromHandle(h).Clone(); }
        finally { Native.DestroyIcon(h); }
    }
}
