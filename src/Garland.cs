namespace HNY;

/// <summary>Гирлянда: провод с провисанием и мигающие лампочки вдоль верхней кромки окон.</summary>
internal sealed unsafe partial class Engine
{
    public volatile bool Garland = true;
    public int BulbsDrawn;

    static readonly (int R, int G, int B)[] BulbColors =
    [
        (255, 64, 54),   // красный
        (255, 190, 48),  // янтарный
        (70, 224, 100),  // зелёный
        (76, 150, 255),  // синий
        (236, 92, 224),  // розовый
    ];

    void DrawGarland(Surface s)
    {
        if (s.IsTaskbar || s.Lights.Count == 0) return;

        float sc = _scale;
        int left = s.Rect.Left, width = s.Rect.Width;
        float margin = 14f * sc;            // отступ от скруглённых углов окна
        float span = 96f * sc;              // длина одного «провиса»
        float sag = 6f * sc;
        float baseY = s.Rect.Top - _oy + 3f * sc;
        float usable = width - 2 * margin;
        if (usable < span) return;
        // Подгоняем длину провиса, чтобы на окно легло целое число дуг.
        int arcs = Math.Max(1, (int)MathF.Round(usable / span));
        span = usable / arcs;

        float WireY(float rel)
        {
            float u = (rel - margin) / span;
            u -= MathF.Floor(u);
            return baseY + sag * 4f * u * (1f - u);
        }

        // --- провод
        int bandTop = Math.Max(0, (int)baseY - 1), bandBottom = Math.Min(_h, (int)(baseY + sag + 16f * sc));
        foreach (var (a, b) in s.Lights)
        {
            int ax0 = Math.Max(a, left + (int)margin), ax1 = Math.Min(b, left + width - (int)margin);
            int x0 = Math.Max(ax0 - _ox, 0), x1 = Math.Min(ax1 - _ox, _w);
            if (x1 <= x0) continue;
            for (int bx = x0; bx < x1; bx++)
            {
                float y = WireY(bx + _ox - left);
                int yi = (int)y;
                if (yi < 0 || yi + 2 >= _h) continue;
                float f = y - yi;
                uint* p = _px + (long)yi * _w + bx;
                BlendColor(p, 26, 58, 36, (int)(230 * (1 - f)));
                BlendColor(p + _w, 26, 58, 36, 230);
                BlendColor(p + 2 * _w, 26, 58, 36, (int)(230 * f));
            }
            _dirty.Add(new RECT { Left = x0, Right = x1, Top = bandTop, Bottom = bandBottom });
        }

        // --- лампочки
        float step = span / 4f;
        float t = (float)_time;
        int mode = (int)(t / 14f) % 3;
        float glowR = 10f * sc;
        int count = (int)(usable / step);
        for (int i = 0; i <= count; i++)
        {
            float rel = margin + step * (i + 0.5f);
            if (rel > width - margin) break;
            int ax = left + (int)rel;
            if (!InSegments(s.Lights, ax - (int)glowR) || !InSegments(s.Lights, ax + (int)glowR)) continue;

            int c = i % BulbColors.Length;
            float bright = mode switch
            {
                0 => 0.5f + 0.5f * MathF.Sin(t * 2.4f + c * 1.2566f),                 // цвета по очереди
                1 => MathF.Pow(Math.Max(0f, MathF.Sin(t * 4.5f - i * 0.75f)), 2f),    // бегущая волна
                _ => 0.5f + 0.5f * MathF.Sin(t * 1.4f + MathF.Sin(i * 12.9898f) * 3f), // вразнобой
            };
            bright = 0.22f + 0.78f * Math.Clamp(bright, 0f, 1f);

            var (r, g, b) = BulbColors[c];
            float x = ax - _ox + 0.5f;
            float y = WireY(rel) + 1.5f * sc;

            Glow(x, y + 4.5f * sc, glowR, r, g, b, (int)(120 * bright * bright));
            Capsule(x, y, x, y + 2f * sc, 1.4f * sc, 1.4f * sc, 40, 44, 48);            // цоколь
            float lit = 0.38f + 0.62f * bright;
            Capsule(x, y + 3.5f * sc, x, y + 7f * sc, 2.8f * sc, 2.1f * sc, (int)(r * lit), (int)(g * lit), (int)(b * lit));
            ColorDisc(x - 0.8f * sc, y + 3.6f * sc, 1f * sc, 255, 255, 255, (int)(170 * bright)); // блик
            BulbsDrawn++;
        }
    }

    static bool InSegments(List<(int A, int B)> list, int ax)
    {
        foreach (var (a, b) in list)
            if (ax >= a && ax < b) return true;
        return false;
    }

    /// <summary>Мягкое свечение: прозрачность спадает к краю.</summary>
    void Glow(float cx, float cy, float r, int cr, int cg, int cb, int alpha)
    {
        if (alpha <= 0) return;
        int x0 = Math.Max(0, (int)MathF.Floor(cx - r)), x1 = Math.Min(_w - 1, (int)MathF.Ceiling(cx + r));
        int y0 = Math.Max(0, (int)MathF.Floor(cy - r)), y1 = Math.Min(_h - 1, (int)MathF.Ceiling(cy + r));
        if (x1 < x0 || y1 < y0) return;
        float inv = 1f / r;
        for (int y = y0; y <= y1; y++)
        {
            float dy = y + 0.5f - cy;
            uint* row = _px + (long)y * _w;
            for (int x = x0; x <= x1; x++)
            {
                float dx = x + 0.5f - cx;
                float k = 1f - MathF.Sqrt(dx * dx + dy * dy) * inv;
                if (k <= 0) continue;
                BlendColor(row + x, cr, cg, cb, (int)(alpha * k * k));
            }
        }
        _dirty.Add(new RECT { Left = x0, Top = y0, Right = x1 + 1, Bottom = y1 + 1 });
    }
}
