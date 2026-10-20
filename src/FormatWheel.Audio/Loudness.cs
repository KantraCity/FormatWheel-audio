using System;
using System.Collections.Generic;
using System.Threading;
using NAudio.Wave;

namespace FormatWheel.Audio;

// Громкость «на слух» по ITU-R BS.1770-4 (LUFS) — так её меряют YouTube, Spotify и т.п.
// Пик здесь бесполезен: почти у любого трека он и так у 0 dB, а звучит трек тихо.
internal static class Loudness
{
    public const double TargetLufs = -14; // как у стримингов

    // интегральная громкость: K-фильтр, блоки по 400 мс с шагом 100 мс, абсолютный и относительный гейт
    public static double Measure(ISampleProvider source, Action<double> progress, Func<double> position, CancellationToken ct)
    {
        int rate = source.WaveFormat.SampleRate, channels = source.WaveFormat.Channels;
        var filters = new KFilter[channels];
        for (int c = 0; c < channels; c++) filters[c] = new KFilter(rate);

        int step = rate / 10; // 100 мс
        var steps = new List<double>(); // сумма квадратов за каждые 100 мс (по всем каналам)
        double acc = 0;
        int inStep = 0;

        var buffer = new float[step * channels];
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            for (int i = 0; i < read; i += channels)
            {
                for (int c = 0; c < channels; c++)
                {
                    double y = filters[c].Process(buffer[i + c]);
                    acc += y * y;
                }
                if (++inStep == step) { steps.Add(acc); acc = 0; inStep = 0; }
            }
            progress(position());
        }
        if (inStep > 0) steps.Add(acc * step / inStep);

        // блоки по 4 шага (400 мс), для коротких файлов — один блок на всё
        var blocks = new List<double>();
        if (steps.Count < 4) blocks.Add(Sum(steps, 0, steps.Count) / Math.Max(1, steps.Count * step));
        else for (int i = 0; i + 4 <= steps.Count; i++) blocks.Add(Sum(steps, i, 4) / (4.0 * step));

        double absoluteGate = Energy(-70);
        var loud = blocks.FindAll(z => z > absoluteGate);
        if (loud.Count == 0) return double.NegativeInfinity; // тишина
        double relativeGate = Energy(Lufs(Average(loud)) - 10);
        var gated = loud.FindAll(z => z > relativeGate);
        return Lufs(Average(gated.Count > 0 ? gated : loud));
    }

    private static double Lufs(double z) => -0.691 + 10 * Math.Log10(z);
    private static double Energy(double lufs) => Math.Pow(10, (lufs + 0.691) / 10);

    private static double Sum(List<double> list, int from, int count)
    {
        double s = 0;
        for (int i = from; i < from + count; i++) s += list[i];
        return s;
    }

    private static double Average(List<double> list) => Sum(list, 0, list.Count) / list.Count;

    // два биквада K-взвешивания для любой частоты дискретизации (формулы как в libebur128)
    private sealed class KFilter
    {
        private readonly double b0, b1, b2, a1, a2, c0, c1, c2, d1, d2;
        private double x1, x2, y1, y2, u1, u2, v1, v2;

        public KFilter(int rate)
        {
            double f0 = 1681.974450955533, g = 3.999843853973347, q = 0.7071752369554196;
            double k = Math.Tan(Math.PI * f0 / rate);
            double vh = Math.Pow(10, g / 20), vb = Math.Pow(vh, 0.4996667741545416);
            double a0 = 1 + k / q + k * k;
            b0 = (vh + vb * k / q + k * k) / a0;
            b1 = 2 * (k * k - vh) / a0;
            b2 = (vh - vb * k / q + k * k) / a0;
            a1 = 2 * (k * k - 1) / a0;
            a2 = (1 - k / q + k * k) / a0;

            f0 = 38.13547087602444; q = 0.5003270373238773;
            k = Math.Tan(Math.PI * f0 / rate);
            double n = 1 + k / q + k * k;
            c0 = 1; c1 = -2; c2 = 1;
            d1 = 2 * (k * k - 1) / n;
            d2 = (1 - k / q + k * k) / n;
        }

        public double Process(double x)
        {
            double y = b0 * x + b1 * x1 + b2 * x2 - a1 * y1 - a2 * y2;
            x2 = x1; x1 = x; y2 = y1; y1 = y;
            double v = c0 * y + c1 * u1 + c2 * u2 - d1 * v1 - d2 * v2;
            u2 = u1; u1 = y; v2 = v1; v1 = v;
            return v;
        }
    }
}

// Усиление с лимитером: после +N dB пики не обрезаются с хрипом, а мягко прижимаются к −1 dB
// (запас нужен MP3/AAC/Opus: после сжатия пики подрастают). Атака мгновенная, отпускание ~150 мс.
internal sealed class GainLimiter : ISampleProvider
{
    private const float Ceiling = 0.891f; // −1 dBFS
    private readonly ISampleProvider _source;
    private readonly float _gain;
    private readonly float _release;
    private float _envelope;

    public GainLimiter(ISampleProvider source, float gain)
    {
        _source = source;
        _gain = gain;
        _release = (float)Math.Exp(-1.0 / (0.15 * source.WaveFormat.SampleRate));
    }

    public WaveFormat WaveFormat => _source.WaveFormat;

    public int Read(float[] buffer, int offset, int count)
    {
        int read = _source.Read(buffer, offset, count);
        int channels = WaveFormat.Channels;
        for (int i = offset; i < offset + read; i += channels)
        {
            float peak = 0;
            for (int c = 0; c < channels; c++) peak = Math.Max(peak, Math.Abs(buffer[i + c] * _gain));
            _envelope = Math.Max(peak, _envelope * _release);
            float reduce = _envelope > Ceiling ? Ceiling / _envelope : 1f;
            for (int c = 0; c < channels; c++) buffer[i + c] *= _gain * reduce;
        }
        return read;
    }
}
