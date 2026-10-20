using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Concentus;
using Concentus.Enums;
using Concentus.Oggfile;
using FormatWheel.Sdk;
using NAudio.MediaFoundation;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace FormatWheel.Audio;

// Аудио без FFmpeg: декодирование и кодирование встроенными кодеками Windows (Media Foundation),
// Opus — управляемым Concentus, обработка — NAudio. Кроме формата умеет битрейт, моно/стерео и громкость.
public sealed class AudioConverter : IConverter
{
    private const string FormatAxis = "audio.format";
    private const string BitrateAxis = "audio.bitrate";
    private const string ChannelsAxis = "audio.channels";
    private const string VolumeAxis = "audio.volume";
    private const int DefaultBitrate = 192_000;

    // кодеки Windows, которых нет в NAudio.AudioSubtypes
    private static readonly Guid FlacSubtype = new("0000F1AC-0000-0010-8000-00AA00389B71");
    private static readonly Guid AlacSubtype = new("00006C61-0000-0010-8000-00AA00389B71");

    private static readonly HashSet<string> Inputs = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".wav", ".flac", ".m4a", ".m4b", ".aac", ".wma", ".aiff", ".aif", ".aifc",
        ".ogg", ".oga", ".opus", ".mp2" // AC3 не берём: декодер Dolby из новых Windows убрали
    };

    private static readonly Lazy<bool> MfStarted = new(() => { MediaFoundationApi.Startup(); return true; });

    public string Name => L.Name;
    public bool IsSlow => true; // длинный трек кодируется секунды, отдаём когда готов
    public int MaxParallel => Math.Max(1, Environment.ProcessorCount / 2);

    public bool CanHandle(string path) => Inputs.Contains(Path.GetExtension(path));

    public IReadOnlyList<WheelOption> MenuFor(WheelSlot slot, IReadOnlyList<string> files) => slot switch
    {
        WheelSlot.Primary => new[]
        {
            new WheelOption(FormatAxis, "mp3", "MP3", L.Universal),
            new WheelOption(FormatAxis, "m4a", "M4A", L.Apple),
            new WheelOption(FormatAxis, "opus", "OPUS", L.TinySize),
            new WheelOption(FormatAxis, "flac", "FLAC", L.Lossless),
            new WheelOption(FormatAxis, "alac", "ALAC", L.LosslessApple),
            new WheelOption(FormatAxis, "wma", "WMA", L.Windows),
            new WheelOption(FormatAxis, "wav", "WAV", L.Uncompressed),
            new WheelOption(FormatAxis, "aiff", "AIFF", "Mac"),
        },
        // битрейт для сжатых форматов; у M4A кодек Windows умеет максимум 192k, Opus берёт половину — ему хватает
        WheelSlot.Ctrl => new[]
        {
            WheelOption.Reset(BitrateAxis, "192k", L.Standard),
            new WheelOption(BitrateAxis, 320_000, "320k", L.Best),
            new WheelOption(BitrateAxis, 256_000, "256k", L.High),
            new WheelOption(BitrateAxis, 128_000, "128k", L.Smaller),
            new WheelOption(BitrateAxis, 64_000, "64k", L.Voice),
        },
        WheelSlot.Alt => new[]
        {
            WheelOption.Reset(ChannelsAxis, L.AsIs, L.Channels),
            new WheelOption(ChannelsAxis, "mono", L.Mono, L.OneChannel),
            new WheelOption(ChannelsAxis, "stereo", L.Stereo, L.TwoChannels),
        },
        WheelSlot.CtrlAlt => new[]
        {
            WheelOption.Reset(VolumeAxis, L.AsIs, L.Volume),
            new WheelOption(VolumeAxis, "normalize", L.Normalize, L.ToLufs),
            new WheelOption(VolumeAxis, "+6", "+6 dB", L.Louder),
            new WheelOption(VolumeAxis, "-6", "−6 dB", L.Quieter),
        },
        _ => Array.Empty<WheelOption>()
    };

    public string Convert(string source, Recipe recipe, Action<double> report, CancellationToken ct)
    {
        _ = MfStarted.Value;
        // по умолчанию MP3, а сам MP3 — в M4A
        string format = recipe.Get<string>(FormatAxis)
            ?? (Path.GetExtension(source).Equals(".mp3", StringComparison.OrdinalIgnoreCase) ? "m4a" : "mp3");
        int bitrate = recipe.GetRaw(BitrateAxis) is int b ? b : DefaultBitrate;
        string? channels = recipe.Get<string>(ChannelsAxis);
        string? volume = recipe.Get<string>(VolumeAxis);

        // выравнивание — отдельные проходы: меряем громкость в LUFS и подтягиваем к −14.
        // Лимитер срезает пики и чуть «съедает» громкость, поэтому меряем ещё раз уже с ним и добираем разницу.
        double encodeStart = 0;
        float gain = volume switch { "+6" => 1.995f, "-6" => 0.501f, _ => 1f };
        if (volume == "normalize")
        {
            double db = 0;
            for (int pass = 0; pass < 2; pass++)
            {
                using var scan = AudioSource.Open(source);
                ISampleProvider measured = db == 0 ? scan.Samples : new GainLimiter(scan.Samples, Db(db));
                double lufs = Loudness.Measure(measured, p => report((pass + p) * 0.2), scan.Position, ct);
                if (double.IsInfinity(lufs)) break; // тишина — нечего выравнивать
                double missing = Loudness.TargetLufs - lufs;
                db = Math.Clamp(db + missing, -24, 24); // больше +24 dB — это уже раздувание шума
                if (Math.Abs(missing) < 0.3) break;
            }
            gain = Db(db);
            encodeStart = 0.4;
        }

        using var input = AudioSource.Open(source);
        ISampleProvider s = input.Samples;
        // кодеки Windows понимают максимум стерео: 5.1 и прочее сводим к первым двум каналам
        if (s.WaveFormat.Channels > 2) s = new MultiplexingSampleProvider(new[] { s }, 2);
        if (channels == "mono" && s.WaveFormat.Channels == 2) s = new StereoToMonoSampleProvider(s);
        if (channels == "stereo" && s.WaveFormat.Channels == 1) s = new MonoToStereoSampleProvider(s);
        if (gain != 1f) s = new GainLimiter(s, gain);
        // кодеки Windows кодируют только из 44.1 или 48 кГц
        if (format is not ("wav" or "aiff" or "opus") && s.WaveFormat.SampleRate is not (44100 or 48000))
            s = new WdlResamplingSampleProvider(s, s.WaveFormat.SampleRate < 44100 ? 44100 : 48000);
        // WMA-кодек капризнее: надёжно работает на 44.1 кГц, а моно есть не на всех битрейтах
        if (format == "wma")
        {
            if (s.WaveFormat.SampleRate != 44100) s = new WdlResamplingSampleProvider(s, 44100);
            if (s.WaveFormat.Channels == 1 && MediaFoundationEncoder.SelectMediaType(AudioSubtypes.MFAudioFormat_WMAudioV8,
                    new WaveFormat(44100, 16, 1), bitrate) == null)
                s = new MonoToStereoSampleProvider(s);
        }

        Action<double> progress = p => report(encodeStart + p * (1 - encodeStart));
        string output = OutputPath.New(source, format == "alac" ? "m4a" : format);
        try
        {
            if (format == "opus")
                EncodeOpus(new ProgressSamples(s, input.Total, progress, ct), output, bitrate);
            else
            {
                // 16 бит, плюс прогресс и отмена прямо в чтении
                var pcm = new ProgressProvider(new SampleToWaveProvider16(s), input.Total, progress, ct);
                switch (format)
                {
                    case "mp3": MediaFoundationEncoder.EncodeToMp3(pcm, output, bitrate); break;
                    case "m4a": MediaFoundationEncoder.EncodeToAac(pcm, output, Math.Min(bitrate, 192_000)); break;
                    case "wma": MediaFoundationEncoder.EncodeToWma(pcm, output, bitrate); break;
                    case "flac": EncodeLossless(FlacSubtype, pcm, output); break;
                    case "alac": EncodeLossless(AlacSubtype, pcm, output); break;
                    case "aiff": WriteAiff(pcm, output); break;
                    default: WaveFileWriter.CreateWaveFile(output, pcm); break;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ct.IsCancellationRequested)
        {
            // отмену из Read кодек может завернуть в своё исключение
            throw new OperationCanceledException(ct);
        }
        ct.ThrowIfCancellationRequested();

        report(1);
        return output;
    }

    private static float Db(double db) => (float)Math.Pow(10, db / 20);

    // FLAC и ALAC — кодеки Windows 10/11, нужен вариант ровно под наш PCM (частота, каналы, 16 бит)
    private static void EncodeLossless(Guid subtype, IWaveProvider pcm, string output)
    {
        var format = pcm.WaveFormat;
        var type = MediaFoundationEncoder.GetOutputMediaTypes(subtype)
            .FirstOrDefault(t => t.SampleRate == format.SampleRate && t.ChannelCount == format.Channels && t.BitsPerSample == 16)
            ?? throw new InvalidOperationException(L.NoCodec);
        using var encoder = new MediaFoundationEncoder(type);
        encoder.Encode(output, pcm);
    }

    // AIFF хранит сэмплы big-endian — AiffFileWriter переворачивает их сам
    private static void WriteAiff(IWaveProvider pcm, string output)
    {
        using var writer = new AiffFileWriter(output, pcm.WaveFormat);
        var buffer = new byte[pcm.WaveFormat.AverageBytesPerSecond];
        int read;
        while ((read = pcm.Read(buffer, 0, buffer.Length)) > 0) writer.Write(buffer, 0, read);
    }

    // Opus: Concentus сам пересэмплирует в 48 кГц и пакует в OGG
    private static void EncodeOpus(ISampleProvider samples, string output, int bitrate)
    {
        int channels = samples.WaveFormat.Channels;
        var encoder = OpusCodecFactory.CreateEncoder(48000, channels, OpusApplication.OPUS_APPLICATION_AUDIO);
        encoder.Bitrate = Math.Clamp(bitrate / 2, 24_000, 256_000);
        encoder.Complexity = 8;

        using var file = File.Create(output);
        var ogg = new OpusOggWriteStream(encoder, file, null, samples.WaveFormat.SampleRate, 5, false);
        var buffer = new float[samples.WaveFormat.SampleRate / 10 * channels];
        int read;
        while ((read = samples.Read(buffer, 0, buffer.Length)) > 0)
        {
            // обрезаем на всякий случай: после усиления float может выйти за ±1
            for (int i = 0; i < read; i++) buffer[i] = Math.Clamp(buffer[i], -1f, 1f);
            ogg.WriteSamples(buffer, 0, read);
        }
        ogg.Finish();
    }

    // считает прочитанное, чтобы показать прогресс, и прерывает кодирование при отмене
    private sealed class ProgressProvider(IWaveProvider inner, TimeSpan total, Action<double> report, CancellationToken ct) : IWaveProvider
    {
        private readonly double _totalBytes = total.TotalSeconds * inner.WaveFormat.AverageBytesPerSecond;
        private long _done;

        public WaveFormat WaveFormat => inner.WaveFormat;

        public int Read(byte[] buffer, int offset, int count)
        {
            ct.ThrowIfCancellationRequested();
            int read = inner.Read(buffer, offset, count);
            _done += read;
            if (_totalBytes > 0) report(Math.Clamp(_done / _totalBytes, 0, 0.99));
            return read;
        }
    }

    private sealed class ProgressSamples(ISampleProvider inner, TimeSpan total, Action<double> report, CancellationToken ct) : ISampleProvider
    {
        private readonly double _totalSamples = total.TotalSeconds * inner.WaveFormat.SampleRate * inner.WaveFormat.Channels;
        private long _done;

        public WaveFormat WaveFormat => inner.WaveFormat;

        public int Read(float[] buffer, int offset, int count)
        {
            ct.ThrowIfCancellationRequested();
            int read = inner.Read(buffer, offset, count);
            _done += read;
            if (_totalSamples > 0) report(Math.Clamp(_done / _totalSamples, 0, 0.99));
            return read;
        }
    }
}
