using System.Diagnostics;
using System.Runtime.InteropServices;

namespace HNY;

/// <summary>
/// Симуляция и отрисовка снега. Работает в собственном потоке, рисует в 32-битный
/// буфер с альфой и отдаёт его прозрачному окну через UpdateLayeredWindow.
/// Все координаты — физические пиксели виртуального экрана.
/// </summary>
internal sealed unsafe partial class Engine : IDisposable
{
    // ---- управление из UI-потока ----
    public volatile bool Paused;
    public volatile bool Drifts = true;
    public volatile bool ClearRequested;
    public volatile float Intensity = 1f;
    public volatile bool DemoRequested;
    public int ClumpsLanded;
    public int BlockedMonitors;

    // ---- статистика для --selftest ----
    public long Frames;
    public double WorkMs, PresentMs;
    public int PresentFailures, LastSurfaceCount, LastWindowCount;
    public bool TaskbarSeen;
    public float MaxPile;

    struct Flake
    {
        public float X, Y, Vy, R, Phase, SwaySpeed, SwayAmp, Depth;
        public int Alpha;
        public bool Lands;
    }

    struct Clump
    {
        public float X, Y, Vx, Vy, R;
        public IntPtr Skip; // окно, с которого комок стряхнули: обратно на него он не садится
    }

    sealed class Surface
    {
        public IntPtr Hwnd;
        public RECT Rect;
        public bool IsTaskbar;
        public float[] H = [];
        public float[] Cap = [];
        public float P1, P2, MaxH;
        public readonly List<(int A, int B)> Visible = [];
        public readonly List<(int A, int B)> Lights = []; // видимые участки полосы под кромкой (для гирлянды)
        public bool Seen, Touched;
    }

    struct WinInfo
    {
        public IntPtr Hwnd;
        public RECT Rect;
        public bool IsTaskbar;
    }

    const int MaxFlakes = 2400;
    const int MaxClumps = 4000;

    readonly IntPtr _hwnd;
    readonly Thread _thread;
    readonly Random _rng = new();
    readonly float _scale;
    volatile bool _run = true;

    // буфер
    IntPtr _memDc, _dib, _oldBmp;
    uint* _px;
    int _w, _h, _ox, _oy;
    readonly List<RECT> _dirty = new(4096);

    // мир
    readonly Flake[] _flakes = new Flake[MaxFlakes];
    int _baseFlakes = 300;
    readonly List<Clump> _clumps = new(1024);
    readonly Dictionary<IntPtr, Surface> _surfaces = [];
    readonly List<Surface> _surfList = [];
    readonly List<WinInfo> _wins = new(128);
    readonly List<RECT> _monitors = [];
    readonly List<RECT> _blocked = [];
    readonly List<IntPtr> _gone = [];
    readonly Native.EnumWindowsProc _enumWin;
    readonly Native.MonitorEnumProc _enumMon;
    double _time;

    public Engine(IntPtr overlayHwnd)
    {
        _hwnd = overlayHwnd;
        _scale = Math.Max(1f, Native.GetDpiForSystem() / 96f);
        _enumWin = OnEnumWindow;
        _enumMon = OnEnumMonitor;
        _thread = new Thread(Loop) { IsBackground = true, Name = "HNY.Render" };
    }

    public void Start() => _thread.Start();

    public void Dispose()
    {
        _run = false;
        if (_thread.IsAlive) _thread.Join(2000);
        FreeBuffer();
    }

    // ------------------------------------------------------------------ цикл

    void Loop()
    {
        var sw = Stopwatch.StartNew();
        double last = 0, lastScan = -1, lastTop = -1;
        bool hidden = false;

        Scan();
        InitFlakes();

        while (_run)
        {
            if (Paused)
            {
                if (!hidden && _px != null)
                {
                    new Span<uint>(_px, _w * _h).Clear();
                    _dirty.Clear();
                    Present();
                    hidden = true;
                }
                StopInteraction();
                Thread.Sleep(120);
                last = sw.Elapsed.TotalSeconds;
                continue;
            }
            hidden = false;

            double before = sw.Elapsed.TotalSeconds;
            Native.DwmFlush();
            double now = sw.Elapsed.TotalSeconds;
            if (now - before < 0.002) Thread.Sleep(8); // DWM не подождал кадр — не крутим цикл вхолостую

            now = sw.Elapsed.TotalSeconds;
            if (now - last < 1.0 / 75) continue; // на мониторах 120+ Гц рисуем не каждый кадр — бережём процессор
            float dt = (float)Math.Min(0.05, now - last);
            last = now;
            _time += dt;

            if (now - lastScan > 0.1) { Scan(); lastScan = now; }
            if (now - lastTop > 1.0)
            {
                Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0,
                    Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER | Native.SWP_NOSENDCHANGING);
                lastTop = now;
            }
            if (_px == null) { Thread.Sleep(100); continue; }

            if (ClearRequested)
            {
                ClearRequested = false;
                foreach (var s in _surfList) Shed(s, 1f, 1f);
                _balls.Clear();
                _drag = null;
            }
            if (DemoRequested) { DemoRequested = false; SpawnDemoSnowman(); }
            Interact();

            double t0 = sw.Elapsed.TotalMilliseconds;
            Step(dt);
            Render();
            double t1 = sw.Elapsed.TotalMilliseconds;
            Present();
            double t2 = sw.Elapsed.TotalMilliseconds;

            Frames++;
            WorkMs += t1 - t0;
            PresentMs += t2 - t1;
        }
    }

    /// <summary>Для --selftest: роняет три снежка на панель задач, чтобы проверить сборку снеговика.</summary>
    void SpawnDemoSnowman()
    {
        // Предпочитаем панель задач главного монитора (на нём точка 0,0).
        Surface? target = null;
        foreach (var s in _surfList)
        {
            if (s.Visible.Count == 0) continue;
            bool primaryTaskbar = s.IsTaskbar && s.Rect.Left <= 0 && s.Rect.Right > 0;
            if (target == null || primaryTaskbar || (s.IsTaskbar && !(target.IsTaskbar && target.Rect.Left <= 0 && target.Rect.Right > 0)))
                target = s;
        }
        if (target == null) return;
        var (a, b) = target.Visible[0];
        float x = a + (b - a) * 0.3f, top = target.Rect.Top;
        float[] radii = [32f, 23f, 16f];
        for (int i = 0; i < radii.Length; i++)
        {
            float r = radii[i] * _scale;
            _balls.Add(new Ball { X = x + i * 3, Y = top - 150 * _scale * (i + 1), R = r, Area = MathF.PI * r * r, State = Falling, Angle = i });
        }
    }

    // ------------------------------------------------------------------ буфер

    void EnsureBuffer(int ox, int oy, int w, int h)
    {
        if (w <= 0 || h <= 0) return;
        if (_px != null && ox == _ox && oy == _oy && w == _w && h == _h) return;

        FreeBuffer();
        var bmi = new BITMAPINFOHEADER
        {
            biSize = sizeof(BITMAPINFOHEADER),
            biWidth = w,
            biHeight = -h, // сверху вниз
            biPlanes = 1,
            biBitCount = 32,
        };
        _memDc = Native.CreateCompatibleDC(IntPtr.Zero);
        _dib = Native.CreateDIBSection(IntPtr.Zero, ref bmi, 0, out IntPtr bits, IntPtr.Zero, 0);
        if (_dib == IntPtr.Zero || bits == IntPtr.Zero)
        {
            Native.DeleteDC(_memDc);
            _memDc = IntPtr.Zero;
            return;
        }
        _oldBmp = Native.SelectObject(_memDc, _dib);
        _px = (uint*)bits;
        _ox = ox; _oy = oy; _w = w; _h = h;
        _dirty.Clear();

        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, ox, oy, w, h, Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER);

        long area = 0;
        foreach (var m in _monitors) area += (long)m.Width * m.Height;
        float density = area / (1920f * 1080f) / (_scale * _scale);
        _baseFlakes = Math.Clamp((int)(260 * density), 120, MaxFlakes / 2);
    }

    void FreeBuffer()
    {
        if (_memDc != IntPtr.Zero)
        {
            Native.SelectObject(_memDc, _oldBmp);
            Native.DeleteObject(_dib);
            Native.DeleteDC(_memDc);
        }
        _memDc = _dib = _oldBmp = IntPtr.Zero;
        _px = null;
    }

    void Present()
    {
        var dst = new POINT { X = _ox, Y = _oy };
        var size = new SIZE { Cx = _w, Cy = _h };
        var src = new POINT();
        var bf = new BLENDFUNCTION { SourceConstantAlpha = 255, AlphaFormat = 1 };
        if (!Native.UpdateLayeredWindow(_hwnd, IntPtr.Zero, ref dst, ref size, _memDc, ref src, 0, ref bf, Native.ULW_ALPHA))
            PresentFailures++;
    }

    // ------------------------------------------------------------------ сканирование окон

    bool OnEnumMonitor(IntPtr hMon, IntPtr hdc, ref RECT rc, IntPtr data)
    {
        _monitors.Add(rc);
        return true;
    }

    bool OnEnumWindow(IntPtr hwnd, IntPtr lParam)
    {
        if (hwnd == _hwnd || !Native.IsWindowVisible(hwnd) || Native.IsIconic(hwnd)) return true;
        if (Native.DwmGetWindowAttribute(hwnd, Native.DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return true;

        string cls = Native.GetClassName(hwnd);
        if (cls is "Progman" or "WorkerW") return true;
        bool taskbar = cls is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd";

        if (!taskbar)
        {
            int ex = Native.GetWindowLong(hwnd, Native.GWL_EXSTYLE);
            if ((ex & (Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_TRANSPARENT)) != 0) return true;
            if (!Native.HasTitle(hwnd)) return true;
        }

        if (Native.DwmGetWindowAttribute(hwnd, Native.DWMWA_EXTENDED_FRAME_BOUNDS, out RECT rc, sizeof(RECT)) != 0 || rc.Width <= 0)
        {
            if (!Native.GetWindowRect(hwnd, out rc)) return true;
        }
        if (rc.Width <= 0 || rc.Height <= 0) return true;

        _wins.Add(new WinInfo { Hwnd = hwnd, Rect = rc, IsTaskbar = taskbar });
        return true;
    }

    void Scan()
    {
        _monitors.Clear();
        Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, _enumMon, IntPtr.Zero);
        if (_monitors.Count == 0) return;

        int l = int.MaxValue, t = int.MaxValue, r = int.MinValue, b = int.MinValue;
        foreach (var m in _monitors)
        {
            l = Math.Min(l, m.Left); t = Math.Min(t, m.Top);
            r = Math.Max(r, m.Right); b = Math.Max(b, m.Bottom);
        }
        EnsureBuffer(l, t, r - l, b - t);

        _wins.Clear();
        Native.EnumWindows(_enumWin, IntPtr.Zero);
        LastWindowCount = _wins.Count;

        // Мониторы, на которых открыто полноэкранное окно (игра, видео) — там снег не рисуем.
        _blocked.Clear();
        IntPtr foreground = Native.GetForegroundWindow();
        for (int i = 0; i < _wins.Count; i++)
        {
            var w = _wins[i];
            if (w.IsTaskbar || !IsFullscreenStyle(w.Hwnd)) continue;
            foreach (var m in _monitors)
            {
                if (_blocked.Contains(m) || !(w.Rect.Left <= m.Left && w.Rect.Top <= m.Top && w.Rect.Right >= m.Right && w.Rect.Bottom >= m.Bottom))
                    continue;
                // Окно активно или ничем не перекрыто на этом мониторе — значит, его сейчас смотрят.
                if (w.Hwnd == foreground || !AnyWindowAbove(i, m)) _blocked.Add(m);
            }
        }
        BlockedMonitors = _blocked.Count;

        foreach (var s in _surfList) s.Seen = false;

        int minWidth = (int)(120 * _scale);
        for (int i = 0; i < _wins.Count; i++)
        {
            var w = _wins[i];
            var rc = w.Rect;
            if (rc.Width < minWidth) continue;

            // Верхняя кромка должна быть на экране и не у самого его верха.
            bool onScreen = false;
            int cx = (rc.Left + rc.Right) / 2;
            foreach (var m in _monitors)
                if (m.Contains(cx, rc.Top) && rc.Top > m.Top + 6) { onScreen = true; break; }
            if (!onScreen) continue;

            if (!_surfaces.TryGetValue(w.Hwnd, out var s))
            {
                s = new Surface
                {
                    Hwnd = w.Hwnd,
                    IsTaskbar = w.IsTaskbar,
                    Rect = rc,
                    P1 = (float)(_rng.NextDouble() * 6.28),
                    P2 = (float)(_rng.NextDouble() * 6.28),
                    MaxH = (w.IsTaskbar ? 15f : 11f) * _scale,
                };
                Resize(s, rc.Width);
                _surfaces[w.Hwnd] = s;
                _surfList.Add(s);
            }
            else
            {
                bool moved = rc.Left != s.Rect.Left || rc.Top != s.Rect.Top;
                bool resized = rc.Width != s.Rect.Width;
                if (moved || resized) Shed(s, moved ? 0.35f : 0.15f, 0.5f); // окно тряхнули — снег осыпается
                if (resized) Resize(s, rc.Width);
                s.Rect = rc;
            }
            s.Seen = true;

            // Видимые участки кромки: вычитаем окна, лежащие выше по Z-порядку.
            s.Visible.Clear();
            s.Visible.Add((rc.Left, rc.Right));
            int lineTop = rc.Top - (int)s.MaxH - 1, lineBottom = rc.Top + 1;
            for (int j = 0; j < i; j++)
            {
                var o = _wins[j].Rect;
                if (o.Top < lineBottom && o.Bottom > lineTop) Subtract(s.Visible, o.Left, o.Right);
            }
            foreach (var m in _blocked)
                if (rc.Top > m.Top && rc.Top <= m.Bottom) Subtract(s.Visible, m.Left, m.Right);

            // То же для полосы чуть ниже кромки, где висит гирлянда.
            s.Lights.Clear();
            if (!w.IsTaskbar)
            {
                s.Lights.Add((rc.Left, rc.Right));
                int bandBottom = rc.Top + (int)(26 * _scale);
                for (int j = 0; j < i; j++)
                {
                    var o = _wins[j].Rect;
                    if (o.Top < bandBottom && o.Bottom > rc.Top) Subtract(s.Lights, o.Left, o.Right);
                }
                foreach (var m in _blocked)
                    if (rc.Top >= m.Top && rc.Top < m.Bottom) Subtract(s.Lights, m.Left, m.Right);
            }
        }

        // Закрытые/свёрнутые окна: снег падает вниз.
        _gone.Clear();
        foreach (var s in _surfList)
            if (!s.Seen) { Shed(s, 1f, 1f); _gone.Add(s.Hwnd); }
        foreach (var h in _gone)
        {
            _surfList.Remove(_surfaces[h]);
            _surfaces.Remove(h);
        }

        // Сглаживание сугробов.
        bool taskbarSeen = false;
        foreach (var s in _surfList)
        {
            taskbarSeen |= s.IsTaskbar;
            if (!s.Touched) continue;
            s.Touched = false;
            var hgt = s.H;
            float prev = hgt[0];
            for (int x = 1; x < hgt.Length - 1; x++)
            {
                float cur = hgt[x];
                float v = cur + 0.22f * (prev + hgt[x + 1] - 2 * cur);
                prev = cur;
                hgt[x] = Math.Min(v, s.Cap[x]);
                if (v > MaxPile) MaxPile = v;
            }
        }
        TaskbarSeen = taskbarSeen;
        LastSurfaceCount = _surfList.Count;
    }

    /// <summary>
    /// Развёрнутое окно при скрытой панели задач тоже закрывает весь монитор, но у него есть заголовок.
    /// Полноэкранное (видео в браузере, игра) заголовка не имеет, даже если до этого было развёрнуто.
    /// </summary>
    static bool IsFullscreenStyle(IntPtr hwnd)
        => !Native.IsZoomed(hwnd) || (Native.GetWindowLong(hwnd, Native.GWL_STYLE) & Native.WS_CAPTION) != Native.WS_CAPTION;

    bool AnyWindowAbove(int index, RECT m)
    {
        for (int j = 0; j < index; j++)
        {
            var o = _wins[j];
            if (!o.IsTaskbar && o.Rect.Left < m.Right && o.Rect.Right > m.Left && o.Rect.Top < m.Bottom && o.Rect.Bottom > m.Top)
                return true;
        }
        return false;
    }

    static void Subtract(List<(int A, int B)> list, int a, int b)
    {
        for (int i = list.Count - 1; i >= 0; i--)
        {
            var (sa, sb) = list[i];
            if (b <= sa || a >= sb) continue;
            list.RemoveAt(i);
            if (a > sa) list.Add((sa, a));
            if (b < sb) list.Add((b, sb));
        }
    }

    void Resize(Surface s, int width)
    {
        var h = new float[width];
        Array.Copy(s.H, h, Math.Min(width, s.H.Length));
        s.H = h;
        s.Cap = new float[width];
        float edge = 14f * _scale;
        for (int x = 0; x < width; x++)
        {
            float wave = 0.66f + 0.2f * MathF.Sin(x * 0.021f / _scale + s.P1) + 0.14f * MathF.Sin(x * 0.057f / _scale + s.P2);
            float d = Math.Min(x, width - 1 - x) / edge;
            float taper = d >= 1 ? 1 : MathF.Sqrt(d);
            s.Cap[x] = s.MaxH * wave * taper;
            if (s.H[x] > s.Cap[x]) s.H[x] = s.Cap[x];
        }
    }

    /// <summary>Сбрасывает долю снега с поверхности, превращая её в падающие комки.</summary>
    void Shed(Surface s, float fraction, float spawnChance)
    {
        var h = s.H;
        int step = Math.Max(2, (int)(3 * _scale));
        foreach (var (a, b) in s.Visible)
        {
            for (int ax = a; ax < b; ax += step)
            {
                int x = ax - s.Rect.Left;
                if ((uint)x >= (uint)h.Length || h[x] < 1.5f) continue;
                if (_clumps.Count >= MaxClumps || _rng.NextDouble() > spawnChance) continue;
                _clumps.Add(new Clump
                {
                    X = ax,
                    Y = s.Rect.Top - h[x] * (float)_rng.NextDouble(),
                    Vx = ((float)_rng.NextDouble() - 0.5f) * 50f * _scale,
                    Vy = (float)_rng.NextDouble() * 60f * _scale,
                    R = (1.2f + (float)_rng.NextDouble() * 1.6f) * _scale,
                    Skip = s.Hwnd,
                });
            }
        }
        float keep = 1f - fraction;
        for (int x = 0; x < h.Length; x++) h[x] *= keep;
    }

    // ------------------------------------------------------------------ симуляция

    void InitFlakes()
    {
        for (int i = 0; i < MaxFlakes; i++)
        {
            Respawn(ref _flakes[i]);
            _flakes[i].Y = _oy + (float)_rng.NextDouble() * Math.Max(1, _h);
        }
    }

    void Respawn(ref Flake f)
    {
        float u = (float)_rng.NextDouble();
        float size = 0.8f + 2.3f * u * u; // мелких больше
        f.R = size * _scale;
        f.Depth = 0.5f + 0.5f * (size / 3.1f);
        f.X = _ox + (float)_rng.NextDouble() * Math.Max(1, _w);
        f.Y = _oy - 10 - (float)_rng.NextDouble() * 60 * _scale;
        f.Vy = (22f + 26f * size + (float)_rng.NextDouble() * 18f) * _scale;
        f.Phase = (float)(_rng.NextDouble() * 6.28);
        f.SwaySpeed = 0.6f + (float)_rng.NextDouble() * 1.4f;
        f.SwayAmp = (6f + (float)_rng.NextDouble() * 16f) * _scale;
        f.Alpha = (int)(255 * (0.45f + 0.5f * f.Depth * (0.7f + 0.3f * (float)_rng.NextDouble())));
        f.Lands = _rng.NextDouble() < 0.65;
    }

    int ActiveFlakes => Math.Clamp((int)(_baseFlakes * Intensity), 0, MaxFlakes);

    void Step(float dt)
    {
        float wind = (14f * MathF.Sin((float)_time * 0.13f) + 7f * MathF.Sin((float)_time * 0.31f + 1.7f)) * _scale;
        bool drifts = Drifts;
        int n = ActiveFlakes;
        int right = _ox + _w, bottom = _oy + _h;

        for (int i = 0; i < n; i++)
        {
            ref Flake f = ref _flakes[i];
            float py = f.Y;
            f.Phase += f.SwaySpeed * dt;
            f.Y += f.Vy * dt;
            f.X += (wind * f.Depth + MathF.Cos(f.Phase) * f.SwayAmp) * dt;
            if (f.X < _ox - 10) f.X += _w + 20;
            else if (f.X > right + 10) f.X -= _w + 20;

            if (f.Y > bottom + 10) { Respawn(ref f); continue; }
            if (!drifts || !f.Lands) continue;

            int ix = (int)f.X;
            foreach (var s in _surfList)
            {
                if (ix < s.Rect.Left || ix >= s.Rect.Right || py > s.Rect.Top) continue;
                int x = ix - s.Rect.Left;
                float sy = s.Rect.Top - s.H[x];
                if (f.Y < sy || py > sy || !IsVisible(s, ix)) continue;
                Deposit(s, x, f.R);
                Respawn(ref f);
                break;
            }
        }

        float g = 520f * _scale;
        for (int i = _clumps.Count - 1; i >= 0; i--)
        {
            var c = _clumps[i];
            float py = c.Y;
            c.Vy += g * dt;
            c.X += c.Vx * dt;
            c.Y += c.Vy * dt;

            // Стряхнутый снег ложится на первое окно или панель задач, встретившиеся по пути вниз.
            bool landed = false;
            if (drifts && c.Vy > 0)
            {
                int ix = (int)c.X;
                Surface? hit = null;
                foreach (var s in _surfList)
                {
                    if (s.Hwnd == c.Skip || ix < s.Rect.Left || ix >= s.Rect.Right || py > s.Rect.Top) continue;
                    if (c.Y < s.Rect.Top - s.H[ix - s.Rect.Left] || !IsVisible(s, ix)) continue;
                    if (hit == null || s.Rect.Top < hit.Rect.Top) hit = s; // ближайшая сверху кромка
                }
                if (hit != null)
                {
                    Deposit(hit, ix - hit.Rect.Left, c.R * 2.2f);
                    ClumpsLanded++;
                    landed = true;
                }
            }

            if (landed || c.Y > bottom + 10)
            {
                _clumps[i] = _clumps[^1];
                _clumps.RemoveAt(_clumps.Count - 1);
            }
            else _clumps[i] = c;
        }

        UpdatePlow(dt);
        UpdateBalls(dt);
    }

    static bool IsVisible(Surface s, int ax)
    {
        foreach (var (a, b) in s.Visible)
            if (ax >= a && ax < b) return true;
        return false;
    }

    void Deposit(Surface s, int x, float r)
    {
        // Каждая снежинка добавляет небольшой холмик; сглаживание разровняет его позже.
        int half = (int)(5 * _scale);
        float peak = 1.9f * r / _scale;
        var h = s.H;
        var cap = s.Cap;
        for (int i = -half; i <= half; i++)
        {
            int xi = x + i;
            if ((uint)xi >= (uint)h.Length) continue;
            float v = h[xi] + peak * (1f - Math.Abs(i) / (half + 1f));
            h[xi] = v < cap[xi] ? v : cap[xi];
        }
        s.Touched = true;
    }

    // ------------------------------------------------------------------ отрисовка

    void Render()
    {
        // Стираем только то, что рисовали в прошлом кадре.
        foreach (var d in _dirty)
        {
            int w = d.Right - d.Left;
            for (int y = d.Top; y < d.Bottom; y++)
                new Span<uint>(_px + (long)y * _w + d.Left, w).Clear();
        }
        _dirty.Clear();

        if (Garland)
            foreach (var s in _surfList) DrawGarland(s);
        if (Drifts)
            foreach (var s in _surfList) DrawDrift(s);
        DrawPlows();
        DrawBalls();

        int n = ActiveFlakes;
        for (int i = 0; i < n; i++)
        {
            ref Flake f = ref _flakes[i];
            if (IsBlocked(f.X, f.Y)) continue;
            Disc(f.X - _ox, f.Y - _oy, f.R, f.Alpha);
        }
        foreach (var c in _clumps)
        {
            if (IsBlocked(c.X, c.Y)) continue;
            Disc(c.X - _ox, c.Y - _oy, c.R, 245);
        }
    }

    bool IsBlocked(float x, float y)
    {
        foreach (var m in _blocked)
            if (m.Contains((int)x, (int)y)) return true;
        return false;
    }

    void DrawDrift(Surface s)
    {
        const uint White = 0xFFFFFFFF;
        const uint Shade = 0xFFDCE7F5; // чуть голубее у основания — даёт объём
        int top = s.Rect.Top - _oy;      // строка буфера, совпадающая с кромкой окна
        if (top < 1 || top >= _h) return;
        var h = s.H;
        int shadeRows = Math.Max(1, (int)(1.5f * _scale));

        foreach (var (a, b) in s.Visible)
        {
            int x0 = Math.Max(a - _ox, 0), x1 = Math.Min(b - _ox, _w);
            if (x1 <= x0) continue;
            int off = _ox - s.Rect.Left;
            bool any = false;
            for (int bx = x0; bx < x1; bx++)
            {
                int x = bx + off;
                if ((uint)x >= (uint)h.Length) continue;
                float hh = h[x];
                if (hh < 0.35f) continue;
                any = true;
                float yf = top - hh;
                int yi = (int)MathF.Floor(yf);
                if (yi < 0) yi = 0;
                int aa = (int)((1f - (yf - yi)) * 250f);
                uint* col = _px + (long)yi * _w + bx;
                Blend(col, aa);
                for (int y = yi + 1; y <= top; y++)
                {
                    col += _w;
                    *col = y > top - shadeRows ? Shade : White;
                }
            }
            if (any)
                _dirty.Add(new RECT { Left = x0, Right = x1, Top = Math.Max(0, top - (int)s.MaxH - 2), Bottom = Math.Min(_h, top + 1) });
        }
    }

    /// <summary>Мягкий белый кружок с премультиплицированной альфой.</summary>
    void Disc(float cx, float cy, float r, int alpha)
    {
        int x0 = (int)MathF.Floor(cx - r - 1), x1 = (int)MathF.Ceiling(cx + r + 1);
        int y0 = (int)MathF.Floor(cy - r - 1), y1 = (int)MathF.Ceiling(cy + r + 1);
        if (x0 < 0) x0 = 0;
        if (y0 < 0) y0 = 0;
        if (x1 >= _w) x1 = _w - 1;
        if (y1 >= _h) y1 = _h - 1;
        if (x1 < x0 || y1 < y0) return;

        for (int y = y0; y <= y1; y++)
        {
            float dy = y + 0.5f - cy;
            uint* row = _px + (long)y * _w;
            for (int x = x0; x <= x1; x++)
            {
                float dx = x + 0.5f - cx;
                float cov = r + 0.5f - MathF.Sqrt(dx * dx + dy * dy);
                if (cov <= 0) continue;
                if (cov > 1) cov = 1;
                Blend(row + x, (int)(cov * alpha));
            }
        }
        _dirty.Add(new RECT { Left = x0, Top = y0, Right = x1 + 1, Bottom = y1 + 1 });
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    static void Blend(uint* p, int a)
    {
        if (a <= 0) return;
        if (a >= 255) { *p = 0xFFFFFFFF; return; }
        uint d = *p;
        uint inv = (uint)(255 - a), ua = (uint)a;
        uint b = ua + ((d & 0xFF) * inv + 127) / 255;
        uint g = ua + (((d >> 8) & 0xFF) * inv + 127) / 255;
        uint r = ua + (((d >> 16) & 0xFF) * inv + 127) / 255;
        uint al = ua + ((d >> 24) * inv + 127) / 255;
        *p = b | (g << 8) | (r << 16) | (al << 24);
    }
}
