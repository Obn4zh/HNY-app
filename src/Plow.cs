namespace HNY;

/// <summary>Снегоуборщик: когда на панели задач наметает много снега, проезжает по ней и счищает сугроб.</summary>
internal sealed unsafe partial class Engine
{
    public volatile bool Plow = true;
    public volatile bool PlowRequested;
    public int PlowRuns;

    sealed class PlowState
    {
        public IntPtr Surf;
        public float X, Dir, From, To, Carried, Alpha;
        public bool Done;
    }

    readonly List<PlowState> _plows = [];
    double _plowNextCheck = 20;

    void UpdatePlow(float dt)
    {
        if (PlowRequested)
        {
            PlowRequested = false;
            StartPlows(force: true);
        }
        else if (Plow && Drifts && _time >= _plowNextCheck)
        {
            _plowNextCheck = _time + 20;
            StartPlows(force: false);
        }

        foreach (var p in _plows) StepPlow(p, dt);
        _plows.RemoveAll(static p => p.Done);
    }

    void StepPlow(PlowState p, float dt)
    {
        if (!_surfaces.TryGetValue(p.Surf, out var s)) { p.Done = true; return; }

        float sc = _scale;
        float prev = p.X;
        p.X += p.Dir * 110f * sc * dt;
        float blade = 13f * sc;

        // Счищаем снег под отвалом.
        float a = Math.Min(prev, p.X) + p.Dir * blade, b = Math.Max(prev, p.X) + p.Dir * blade;
        int x0 = Math.Max(0, (int)a - s.Rect.Left), x1 = Math.Min(s.H.Length - 1, (int)b - s.Rect.Left);
        for (int x = x0; x <= x1; x++)
        {
            p.Carried += s.H[x];
            s.H[x] = 0;
        }

        // Лишний снег летит из-под отвала вперёд.
        float top = s.Rect.Top;
        while (p.Carried > 40f * sc && _clumps.Count < MaxClumps)
        {
            p.Carried -= 5f * sc;
            _clumps.Add(new Clump
            {
                X = p.X + p.Dir * (blade + 3f * sc),
                Y = top - (4f + 8f * (float)_rng.NextDouble()) * sc,
                Vx = p.Dir * (70f + 130f * (float)_rng.NextDouble()) * sc,
                Vy = -(90f + 150f * (float)_rng.NextDouble()) * sc,
                R = (1.1f + 1.3f * (float)_rng.NextDouble()) * sc,
            });
        }

        // Снежки на пути отбрасывает вперёд.
        foreach (var ball in _balls)
        {
            if (ball.State != Rest || ball.Surf != p.Surf) continue;
            float gap = (ball.X - p.X) * p.Dir;
            if (gap < -blade || gap > blade + ball.R) continue;
            ball.State = Falling;
            ball.Vx = p.Dir * 190f * sc;
            ball.Vy = -330f * sc;
            ball.Y -= 3f * sc;
        }

        float edge = Math.Min(Math.Abs(p.X - p.From), Math.Abs(p.To - p.X));
        p.Alpha = Math.Clamp(edge / (26f * sc), 0f, 1f);

        if ((p.To - p.X) * p.Dir <= 0)
        {
            p.Done = true;
            // Остатки из-под отвала ссыпаются.
            for (int i = 0; i < 14 && _clumps.Count < MaxClumps; i++)
                _clumps.Add(new Clump
                {
                    X = p.X + p.Dir * blade,
                    Y = top - 10f * sc * (float)_rng.NextDouble(),
                    Vx = p.Dir * 120f * sc * (float)_rng.NextDouble(),
                    Vy = -160f * sc * (float)_rng.NextDouble(),
                    R = (1.2f + 1.4f * (float)_rng.NextDouble()) * sc,
                });
        }
    }

    /// <summary>Запускает по трактору на каждой панели задач (на каждом мониторе своя).</summary>
    void StartPlows(bool force)
    {
        foreach (var s in _surfList)
        {
            if (!s.IsTaskbar || s.Visible.Count == 0) continue;
            bool busy = false;
            foreach (var p in _plows)
                if (p.Surf == s.Hwnd) { busy = true; break; }
            if (busy) continue;

            var seg = s.Visible[0];
            foreach (var v in s.Visible)
                if (v.B - v.A > seg.B - seg.A) seg = v;
            if (seg.B - seg.A < 200 * _scale) continue;

            if (!force)
            {
                float have = 0, cap = 0;
                for (int ax = seg.A; ax < seg.B; ax++)
                {
                    int x = ax - s.Rect.Left;
                    if ((uint)x >= (uint)s.H.Length) continue;
                    have += s.H[x];
                    cap += s.Cap[x];
                }
                if (cap <= 0 || have / cap < 0.8f) continue;

                // Не ломаем то, что человек слепил: с постройками на панели сам не выезжает.
                bool occupied = false;
                foreach (var ball in _balls)
                    if (ball.State == Rest && ball.Surf == s.Hwnd) { occupied = true; break; }
                if (occupied) continue;
            }

            float pad = 22f * _scale;
            float dir = _rng.Next(2) == 0 ? 1f : -1f;
            float from = dir > 0 ? seg.A + pad : seg.B - pad;
            _plows.Add(new PlowState
            {
                Surf = s.Hwnd,
                Dir = dir,
                From = from,
                To = dir > 0 ? seg.B - pad : seg.A + pad,
                X = from,
            });
            PlowRuns++;
        }
    }

    void DrawPlows()
    {
        foreach (var p in _plows) DrawPlow(p);
    }

    void DrawPlow(PlowState p)
    {
        if (p.Done || p.Alpha <= 0.02f || !_surfaces.TryGetValue(p.Surf, out var s)) return;
        if (IsBlocked(p.X, s.Rect.Top - 2)) return;

        float sc = _scale, d = p.Dir;
        float ox = p.X - _ox;
        float oy = s.Rect.Top - _oy + MathF.Sin((float)_time * 22f) * 0.35f * sc; // лёгкая тряска на ходу
        int al = (int)(255 * p.Alpha);

        // Локальные координаты: x — вперёд по ходу, y — вверх от кромки панели.
        void Cap(float x0, float y0, float x1, float y1, float r0, float r1, int r, int g, int b, int alpha = 255)
            => Capsule(ox + d * x0 * sc, oy - y0 * sc, ox + d * x1 * sc, oy - y1 * sc, r0 * sc, r1 * sc, r, g, b, alpha * al / 255);

        // снег перед отвалом
        float heap = Math.Clamp(p.Carried / (40f * sc), 0f, 1f);
        if (heap > 0.05f)
        {
            Cap(15.5f, 2f + 3.5f * heap, 15.5f + 5f * heap, 1f + 1.5f * heap, 2.5f + 3.5f * heap, 1.5f + 1.5f * heap, 255, 255, 255, 245);
            Cap(14.5f, 5f * heap, 16f, 3f * heap, 1.5f + 2.5f * heap, 1.5f, 240, 246, 255, 245);
        }

        // мигалка
        float blink = 0.5f + 0.5f * MathF.Sin((float)_time * 9f);
        Glow(ox + d * -6f * sc, oy - 20.5f * sc, 9f * sc, 255, 150, 30, (int)(130 * blink * p.Alpha));

        // гусеница и катки
        Cap(-13f, 3.4f, 7f, 3.4f, 3.4f, 3.4f, 38, 40, 46);
        float roll = p.X / sc * 0.25f * d;
        for (int i = 0; i < 3; i++)
        {
            float wx = -12f + i * 9f;
            Cap(wx, 3.4f, wx, 3.4f, 1.9f, 1.9f, 120, 124, 132);
            Cap(wx + MathF.Cos(roll) * 1.1f, 3.4f + MathF.Sin(roll) * 1.1f, wx + MathF.Cos(roll) * 1.1f, 3.4f + MathF.Sin(roll) * 1.1f, 0.6f, 0.6f, 50, 52, 58);
        }

        // корпус, кабина, окно
        Cap(-11.5f, 9.5f, 6f, 9.5f, 3.6f, 3.6f, 250, 196, 30);
        Cap(-9.5f, 14.5f, -3f, 14.5f, 4.3f, 4.3f, 240, 180, 22);
        Cap(-6.2f, 15f, -2.4f, 15f, 2.3f, 2.3f, 176, 220, 246);
        Cap(-11.5f, 7.2f, 6f, 7.2f, 0.7f, 0.7f, 60, 50, 20);            // полоса вдоль борта
        Cap(-6f, 19.6f, -6f, 20.4f, 1.5f, 1.5f, 255, (int)(110 + 110 * blink), 30);

        // рычаг и отвал
        Cap(6f, 6f, 11.5f, 5f, 0.9f, 0.9f, 60, 62, 70);
        Cap(11.5f, 1.4f, 13.5f, 11.5f, 1.5f, 1.3f, 206, 212, 222);
        Cap(12.6f, 2f, 14.2f, 10.5f, 0.6f, 0.6f, 150, 158, 172);
    }
}
