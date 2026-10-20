using System;
using System.IO;
using Concentus;
using Concentus.Oggfile;
using NAudio.Vorbis;
using NAudio.Wave;

namespace FormatWheel.Audio;

// Открытый аудиофайл: сэмплы, длительность и сколько уже прочитано (0..1, для прогресса).
internal sealed class AudioSource : IDisposable
{
    private readonly IDisposable _owner;

    private AudioSource(ISampleProvider samples, TimeSpan total, Func<double> position, IDisposable owner)
    {
        Samples = samples;
        Total = total;
        Position = position;
        _owner = owner;
    }

    public ISampleProvider Samples { get; }
    public TimeSpan Total { get; }
    public Func<double> Position { get; }

    public void Dispose() => _owner.Dispose();

    // OGG бывает и Vorbis, и Opus — пробуем по очереди. AIFF — ридером NAudio, остальное — кодеками Windows.
    public static AudioSource Open(string path)
    {
        switch (Path.GetExtension(path).ToLowerInvariant())
        {
            case ".opus": return OpenOpus(path);
            case ".ogg" or ".oga":
                try { return FromStream(new VorbisWaveReader(path)); }
                catch { return OpenOpus(path); }
            case ".aiff" or ".aif" or ".aifc": return FromStream(new AiffFileReader(path));
            default: return FromStream(new MediaFoundationReader(path));
        }
    }

    private static AudioSource FromStream(WaveStream stream) =>
        new(stream.ToSampleProvider(), stream.TotalTime, () => stream.Length > 0 ? (double)stream.Position / stream.Length : 0, stream);

    private static AudioSource OpenOpus(string path)
    {
        var file = File.OpenRead(path);
        try
        {
            // Opus всегда 48 кГц; моно-поток декодер сам разложит в стерео
            var ogg = new OpusOggReadStream(OpusCodecFactory.CreateDecoder(48000, 2), file);
            var samples = new OpusSamples(ogg);
            return new AudioSource(samples, ogg.TotalTime, () => file.Length > 0 ? (double)file.Position / file.Length : 0, file);
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    // пакеты Opus → поток float-сэмплов для NAudio
    private sealed class OpusSamples(OpusOggReadStream ogg) : ISampleProvider
    {
        private short[] _packet = Array.Empty<short>();
        private int _index;

        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);

        public int Read(float[] buffer, int offset, int count)
        {
            int written = 0;
            while (written < count)
            {
                if (_index >= _packet.Length)
                {
                    if (!ogg.HasNextPacket) break;
                    _packet = ogg.DecodeNextPacket() ?? Array.Empty<short>();
                    _index = 0;
                    continue;
                }
                int n = Math.Min(count - written, _packet.Length - _index);
                for (int i = 0; i < n; i++) buffer[offset + written + i] = _packet[_index + i] / 32768f;
                _index += n;
                written += n;
            }
            return written;
        }
    }
}
