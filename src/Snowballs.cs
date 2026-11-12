namespace HNY;

/// <summary>
/// Снежки и снеговик. Оверлей обычно пропускает клики насквозь; когда курсор наведён
/// на сугроб или снежок, окно на это время начинает ловить мышь, чтобы клик достался нам.
/// </summary>
internal sealed unsafe partial class Engine
{
    public volatile bool Snowballs = true;
    public int BallCount;

    const int Rest = 0, Stacked = 1, Falling = 2, Dragged = 3;
    const int MaxBalls = 24;

    sealed class Ball
    {
        public float X, Y, R, Area, Angle, Vx, Vy;
        public int State;
        public IntPtr Surf;      // окно, на котором лежит
        public float RelX;       // смещение от левого края этого окна
        public Ball? Parent;     // снежок, на котором стоит
        public float OffX;
    }

    readonly List<Ball> _balls = [];
    readonly List<Ball> _drawOrder = [];
    Ball? _drag;
    float _grabDx, _grabDy;
    bool _prevDown, _hot;

    float MaxBallR => 28f * _scale;

    // ------------------------------------------------------------------ мышь

    void SetHot(bool hot)
    {
        if (hot == _hot) return;
        _hot = hot;
        int ex = Native.GetWindowLong(_hwnd, Native.GWL_EXSTYLE);
        ex = hot ? ex & ~Native.WS_EX_TRANSPARENT : ex | Native.WS_EX_TRANSPARENT;
        Native.SetWindowLong(_hwnd, Native.GWL_EXSTYLE, ex);
    }

    void StopInteraction()
    {
        if (_drag != null) { _drag.State = Falling; _drag = null; }
        _prevDown = false;
        SetHot(false);
    }

    void Interact()
    {
        if (!Snowballs) { StopInteraction(); return; }
        if (!Native.GetCursorPos(out POINT c)) return;
        bool down = (Native.GetAsyncKeyState(Native.VK_LBUTTON) & 0x8000) != 0;
        float cx = c.X, cy = c.Y;

        if (_drag != null)
        {
            if (down) DragTo(_drag, cx - _grabDx, cy - _grabDy);
            else { Release(_drag); _drag = null; }
        }

        if (_drag == null)
        {
            Ball? over = null;
            Surface? drift = null;
            if (!IsBlocked(cx, cy))
            {
                over = BallAt(cx, cy);
                if (over == null && Drifts) drift = DriftAt(c.X, c.Y);
            }
            bool want = over != null || drift != null;

            if (!down) SetHot(want);
            else if (!_prevDown && _hot && want)
            {
                if (over != null)
                {
                    _drag = over;
                    _grabDx = cx - over.X;
                    _grabDy = cy - over.Y;
                }
                else
                {
                    // Слепили новый снежок из сугроба.
                    float r = 7f * _scale;
                    Absorb(drift!, cx - r, cx + r);
                    var b = new Ball { X = cx, Y = drift!.Rect.Top - r, R = r, Area = MathF.PI * r * r };
                    if (_balls.Count >= MaxBalls) _balls.RemoveAt(0);
                    _balls.Add(b);
                    _drag = b;
                    _grabDx = 0;
                    _grabDy = cy - b.Y;
                }
                _drag.State = Dragged;
                _drag.Parent = null;
            }
        }
        _prevDown = down;
    }

    Ball? BallAt(float x, float y)
    {
        for (int i = _balls.Count - 1; i >= 0; i--)
        {
            var b = _balls[i];
            float dx = x - b.X, dy = y - b.Y, r = b.R + 1;
            if (dx * dx + dy * dy <= r * r) return b;
        }
        return null;
    }

    Surface? DriftAt(int x, int y)
    {
        float min = 4f * _scale;
        foreach (var s in _surfList)
        {
            if (x < s.Rect.Left || x >= s.Rect.Right || y > s.Rect.Top) continue;
            float h = s.H[x - s.Rect.Left];
            if (h >= min && y >= s.Rect.Top - h && IsVisible(s, x)) return s;
        }
        return null;
    }

    /// <summary>Забирает снег с участка сугроба, возвращает его «площадь».</summary>
    float Absorb(Surface s, float ax0, float ax1)
    {
        int x0 = Math.Max(0, (int)ax0 - s.Rect.Left), x1 = Math.Min(s.H.Length - 1, (int)ax1 - s.Rect.Left);
        float sum = 0;
        for (int x = x0; x <= x1; x++)
        {
            float take = s.H[x] * 0.85f;
            s.H[x] -= take;
            sum += take;
        }
        return sum;
    }

    void DragTo(Ball b, float tx, float ty)
    {
        float oldX = b.X;
        Surface? contact = null;
        int ix = (int)tx;
        foreach (var s in _surfList)
        {
            if (!IsVisible(s, ix)) continue;
            float lim = s.Rect.Top - b.R * 0.9f;
            // Снежок упирается в кромку окна; если утащить курсор далеко вниз — «проваливается».
            if (ty > lim && ty - lim < b.R * 1.6f) { ty = lim; contact = s; break; }
        }
        b.X = tx;
        b.Y = ty;

        if (contact == null) return;
        float dx = tx - oldX;
        if (dx == 0) return;
        b.Angle += dx / b.R;
        if (b.R >= MaxBallR) return;
        // Катим по сугробу — снежок растёт, за ним остаётся дорожка.
        float got = Absorb(contact, Math.Min(oldX, tx) - b.R * 0.5f, Math.Max(oldX, tx) + b.R * 0.5f);
        b.Area += got * 1.5f;
        b.R = Math.Min(MaxBallR, MathF.Sqrt(b.Area / MathF.PI));
    }

    void Release(Ball b)
    {
        b.State = Falling;
        b.Vx = b.Vy = 0;
        foreach (var o in _balls)
        {
            if (!CanCarry(o, b)) continue;
            if (Math.Abs(b.X - o.X) < o.R * 0.9f && b.Y < o.Y && b.Y + b.R > o.Y - o.R * 1.1f)
            {
                Stack(b, o);
                return;
            }
        }
    }

    bool CanCarry(Ball lower, Ball upper)
    {
        if (lower == upper || lower.State > Stacked || Depth(lower) >= 3) return false;
        foreach (var o in _balls)
            if (o.Parent == lower && o.State == Stacked) return false;
        return true;
    }

    static int Depth(Ball b)
    {
        int n = 1;
        while (b.State == Stacked && b.Parent != null && n < 8) { n++; b = b.Parent; }
        return n;
    }

    static void Stack(Ball b, Ball lower)
    {
        b.Parent = lower;
        b.OffX = Math.Clamp(b.X - lower.X, -lower.R * 0.3f, lower.R * 0.3f);
        b.State = Stacked;
        b.Vx = b.Vy = 0;
    }

    // ------------------------------------------------------------------ физика

    void UpdateBalls(float dt)
    {
        float g = 1500f * _scale;
        int bottom = _oy + _h;

        for (int i = _balls.Count - 1; i >= 0; i--)
        {
            var b = _balls[i];
            switch (b.State)
            {
                case Rest:
                    if (_surfaces.TryGetValue(b.Surf, out var s) && IsVisible(s, (int)(s.Rect.Left + b.RelX)))
                    {
                        b.X = s.Rect.Left + b.RelX;
                        b.Y = s.Rect.Top - b.R * 0.9f;
                    }
                    else { b.State = Falling; b.Vy = 0; }
                    break;

                case Stacked:
                    var p = b.Parent;
                    if (p != null && p.State <= Stacked && _balls.Contains(p))
                    {
                        b.X = p.X + b.OffX;
                        b.Y = p.Y - (p.R + b.R) * 0.86f;
                    }
                    else { b.State = Falling; b.Parent = null; b.Vy = 0; }
                    break;

                case Falling:
                    float prevBottom = b.Y + b.R;
                    b.Vy += g * dt;
                    b.X += b.Vx * dt;
                    b.Y += b.Vy * dt;
                    float newBottom = b.Y + b.R;
                    bool landed = false;

                    foreach (var o in _balls)
                    {
                        if (!CanCarry(o, b) || Math.Abs(b.X - o.X) > o.R * 0.8f) continue;
                        float level = o.Y - (o.R + b.R) * 0.86f + b.R;
                        if (prevBottom <= level + 2 && newBottom >= level) { Stack(b, o); landed = true; break; }
                    }
                    if (landed) break;

                    int ix = (int)b.X;
                    foreach (var sf in _surfList)
                    {
                        float level = sf.Rect.Top + b.R * 0.1f;
                        if (prevBottom > level + 2 || newBottom < level || !IsVisible(sf, ix)) continue;
                        b.State = Rest;
                        b.Surf = sf.Hwnd;
                        b.RelX = b.X - sf.Rect.Left;
                        b.Y = sf.Rect.Top - b.R * 0.9f;
                        b.Vx = b.Vy = 0;
                        landed = true;
                        break;
                    }
                    if (!landed && b.Y - b.R > bottom + 20) _balls.RemoveAt(i);
                    break;
            }
        }
        BallCount = _balls.Count;
    }

    // ------------------------------------------------------------------ отрисовка

    void DrawBalls()
    {
        if (_balls.Count == 0) return;
        _drawOrder.Clear();
        _drawOrder.AddRange(_balls);
        _drawOrder.Sort(static (a, b) => b.Y.CompareTo(a.Y)); // нижние сначала
        foreach (var b in _drawOrder)
            if (b != _drag && !IsBlocked(b.X, b.Y)) DrawBall(b);

        foreach (var head in _balls)
        {
            if (head.State != Stacked || Depth(head) != 3 || IsBlocked(head.X, head.Y)) continue;
            DrawSnowmanDetails(head, head.Parent!);
        }
        if (_drag != null) DrawBall(_drag);
    }

    void DrawBall(Ball b)
    {
        float cx = b.X - _ox, cy = b.Y - _oy, r = b.R;
        int x0 = Math.Max(0, (int)MathF.Floor(cx - r - 1)), x1 = Math.Min(_w - 1, (int)MathF.Ceiling(cx + r + 1));
        int y0 = Math.Max(0, (int)MathF.Floor(cy - r - 1)), y1 = Math.Min(_h - 1, (int)MathF.Ceiling(cy + r + 1));
        if (x1 < x0 || y1 < y0) return;
        float rim = 1.3f * _scale;

        for (int y = y0; y <= y1; y++)
        {
            float dy = y + 0.5f - cy;
            uint* row = _px + (long)y * _w;
            for (int x = x0; x <= x1; x++)
            {
                float dx = x + 0.5f - cx;
                float d = MathF.Sqrt(dx * dx + dy * dy);
                float cov = r + 0.5f - d;
                if (cov <= 0) continue;
                if (cov > 1) cov = 1;

                // Свет сверху-слева, голубоватая тень снизу-справа.
                float sh = Math.Clamp(0.5f * (dx * 0.6f + dy) / r + 0.45f, 0f, 1f);
                sh *= sh;
                float cr = 255 - 60 * sh, cg = 255 - 42 * sh, cb = 255 - 16 * sh;
                if (d > r - rim)
                {
                    float k = 0.5f * Math.Min(1f, (d - (r - rim)) / rim);
                    cr += (150 - cr) * k; cg += (172 - cg) * k; cb += (208 - cb) * k;
                }
                BlendColor(row + x, (int)cr, (int)cg, (int)cb, (int)(cov * 255));
            }
        }
        _dirty.Add(new RECT { Left = x0, Top = y0, Right = x1 + 1, Bottom = y1 + 1 });

        // Ямки, по которым видно, что снежок катится.
        for (int k = 0; k < 3; k++)
        {
            float a = b.Angle + k * 2.094f;
            ColorDisc(cx + MathF.Cos(a) * r * 0.55f, cy + MathF.Sin(a) * r * 0.55f, Math.Max(1f, r * 0.12f), 196, 212, 236, 170);
        }
    }

    void DrawSnowmanDetails(Ball head, Ball mid)
    {
        float hx = head.X - _ox, hy = head.Y - _oy, hr = head.R;
        float mx = mid.X - _ox, my = mid.Y - _oy, mr = mid.R;

        // руки-веточки
        float aw = Math.Max(1f, mr * 0.06f);
        Capsule(mx - mr * 0.8f, my - mr * 0.1f, mx - mr * 1.75f, my - mr * 0.8f, aw, aw, 112, 74, 42);
        Capsule(mx - mr * 1.45f, my - mr * 0.58f, mx - mr * 1.75f, my - mr * 0.4f, aw * 0.8f, aw * 0.8f, 112, 74, 42);
        Capsule(mx + mr * 0.8f, my - mr * 0.1f, mx + mr * 1.75f, my - mr * 0.8f, aw, aw, 112, 74, 42);
        Capsule(mx + mr * 1.45f, my - mr * 0.58f, mx + mr * 1.6f, my - mr * 1.0f, aw * 0.8f, aw * 0.8f, 112, 74, 42);

        // пуговицы
        float br = Math.Max(1.3f, mr * 0.09f);
        ColorDisc(mx, my - mr * 0.05f, br, 34, 36, 44, 255);
        ColorDisc(mx, my + mr * 0.4f, br, 34, 36, 44, 255);

        // шарф
        float sy = hy + hr * 0.82f, sr = Math.Max(1.5f, hr * 0.2f);
        Capsule(hx + hr * 0.4f, sy, hx + hr * 0.62f, sy + hr * 0.95f, sr * 0.85f, sr * 0.75f, 178, 28, 40);
        Capsule(hx - hr * 0.78f, sy, hx + hr * 0.78f, sy, sr, sr, 214, 40, 50);

        // глаза, улыбка, морковка
        float er = Math.Max(1.3f, hr * 0.11f);
        ColorDisc(hx - hr * 0.32f, hy - hr * 0.22f, er, 28, 30, 38, 255);
        ColorDisc(hx + hr * 0.32f, hy - hr * 0.22f, er, 28, 30, 38, 255);
        float dr = Math.Max(1f, hr * 0.055f);
        ColorDisc(hx - hr * 0.3f, hy + hr * 0.38f, dr, 28, 30, 38, 255);
        ColorDisc(hx - hr * 0.11f, hy + hr * 0.47f, dr, 28, 30, 38, 255);
        ColorDisc(hx + hr * 0.11f, hy + hr * 0.47f, dr, 28, 30, 38, 255);
        ColorDisc(hx + hr * 0.3f, hy + hr * 0.38f, dr, 28, 30, 38, 255);
        Capsule(hx, hy + hr * 0.06f, hx + hr * 0.8f, hy + hr * 0.2f, Math.Max(1.2f, hr * 0.15f), 0.3f, 242, 132, 32);
    }

    void ColorDisc(float cx, float cy, float r, int cr, int cg, int cb, int alpha)
        => Capsule(cx, cy, cx, cy, r, r, cr, cg, cb, alpha);

    /// <summary>Отрезок с закруглёнными концами и радиусом, меняющимся от r0 к r1.</summary>
    void Capsule(float ax, float ay, float bx, float by, float r0, float r1, int cr, int cg, int cb, int alpha = 255)
    {
        float rm = Math.Max(r0, r1) + 1;
        int x0 = Math.Max(0, (int)MathF.Floor(Math.Min(ax, bx) - rm)), x1 = Math.Min(_w - 1, (int)MathF.Ceiling(Math.Max(ax, bx) + rm));
        int y0 = Math.Max(0, (int)MathF.Floor(Math.Min(ay, by) - rm)), y1 = Math.Min(_h - 1, (int)MathF.Ceiling(Math.Max(ay, by) + rm));
        if (x1 < x0 || y1 < y0) return;
        float vx = bx - ax, vy = by - ay, len2 = vx * vx + vy * vy;

        for (int y = y0; y <= y1; y++)
        {
            float py = y + 0.5f - ay;
            uint* row = _px + (long)y * _w;
            for (int x = x0; x <= x1; x++)
            {
                float px = x + 0.5f - ax;
                float t = len2 > 0 ? Math.Clamp((px * vx + py * vy) / len2, 0f, 1f) : 0f;
                float dx = px - vx * t, dy = py - vy * t;
                float cov = r0 + (r1 - r0) * t + 0.5f - MathF.Sqrt(dx * dx + dy * dy);
                if (cov <= 0) continue;
                if (cov > 1) cov = 1;
                BlendColor(row + x, cr, cg, cb, (int)(cov * alpha));
            }
        }
        _dirty.Add(new RECT { Left = x0, Top = y0, Right = x1 + 1, Bottom = y1 + 1 });
    }

    static void BlendColor(uint* p, int r, int g, int b, int a)
    {
        if (a <= 0) return;
        if (a > 255) a = 255;
        uint d = *p;
        uint inv = (uint)(255 - a), ua = (uint)a;
        uint ob = ((uint)b * ua + (d & 0xFF) * inv + 127) / 255;
        uint og = ((uint)g * ua + ((d >> 8) & 0xFF) * inv + 127) / 255;
        uint or = ((uint)r * ua + ((d >> 16) & 0xFF) * inv + 127) / 255;
        uint oa = ua + ((d >> 24) * inv + 127) / 255;
        *p = ob | (og << 8) | (or << 16) | (oa << 24);
    }
}
