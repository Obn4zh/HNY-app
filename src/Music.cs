using System.Runtime.InteropServices;
using System.Text;

namespace HNY;

/// <summary>
/// Фоновая музыка через MCI (winmm): играет mp3 по кругу, плавно появляется и затихает.
/// Все вызовы — из UI-потока, по таймеру.
/// </summary>
internal sealed class Music : IDisposable
{
    const string Alias = "hny_song";
    const int FadeStep = 18; // единиц громкости (из 1000) за тик в 50 мс

    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    static extern int mciSendStringW(string command, StringBuilder? ret, int retLength, IntPtr callback);

    readonly System.Windows.Forms.Timer _timer = new() { Interval = 50 };
    readonly StringBuilder _ret = new(128);
    readonly Func<bool> _shouldPlay;
    bool _open, _playing;
    int _volume;

    /// <summary>Громкость, к которой плавно стремится музыка (0..1000).</summary>
    public int TargetVolume { get; set; } = 250;
    public bool Available => _open;

    public Music(string path, Func<bool> shouldPlay)
    {
        _shouldPlay = shouldPlay;
        if (File.Exists(path))
            _open = Send($"open \"{path}\" type mpegvideo alias {Alias}") == 0;
        if (_open)
        {
            _timer.Tick += (_, _) => Tick();
            _timer.Start();
        }
    }

    void Tick()
    {
        bool want = _shouldPlay() && TargetVolume > 0;
        int target = want ? TargetVolume : 0;

        if (want && !_playing)
        {
            SetVolume(_volume);
            Send($"play {Alias}");
            _playing = true;
        }

        if (_volume != target)
        {
            _volume = _volume < target ? Math.Min(target, _volume + FadeStep) : Math.Max(target, _volume - FadeStep);
            SetVolume(_volume);
        }

        if (!_playing) return;
        if (!want && _volume == 0)
        {
            Send($"pause {Alias}");
            _playing = false;
            return;
        }
        // Трек закончился — начинаем сначала.
        if (Status("mode") == "stopped")
        {
            Send($"seek {Alias} to start");
            Send($"play {Alias}");
        }
    }

    public string State => !_open ? "no file" : $"{(_playing ? "playing" : "paused")} mode={Status("mode")} volume={_volume}";

    void SetVolume(int v) => Send($"setaudio {Alias} volume to {Math.Clamp(v, 0, 1000)}");

    string Status(string what)
    {
        _ret.Clear();
        return Send($"status {Alias} {what}", _ret) == 0 ? _ret.ToString() : "";
    }

    int Send(string command, StringBuilder? ret = null) => mciSendStringW(command, ret, ret?.Capacity ?? 0, IntPtr.Zero);

    public void Dispose()
    {
        _timer.Dispose();
        if (_open) Send($"close {Alias}");
        _open = false;
    }
}
