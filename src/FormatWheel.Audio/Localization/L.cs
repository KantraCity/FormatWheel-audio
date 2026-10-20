using System;
using System.Globalization;

namespace FormatWheel.Audio;

// Маленькая локализация расширения. Код языка берём так же, как приложение.
internal static class L
{
    private static readonly bool Ru =
        (Environment.GetEnvironmentVariable("FORMATWHEEL_LANG") ?? CultureInfo.CurrentUICulture.TwoLetterISOLanguageName)
        .StartsWith("ru", StringComparison.OrdinalIgnoreCase);

    private static string T(string en, string ru) => Ru ? ru : en;

    public static string Name => T("Audio", "Аудио");
    public static string Universal => T("universal", "везде");
    public static string Apple => T("AAC, Apple", "AAC, Apple");
    public static string Windows => T("Windows Media", "Windows Media");
    public static string Uncompressed => T("uncompressed", "без сжатия");
    public static string TinySize => T("tiny size", "мало весит");
    public static string Lossless => T("lossless", "без потерь");
    public static string LosslessApple => T("lossless, Apple", "без потерь, Apple");
    public static string NoCodec => T("This Windows has no encoder for this format", "В этой Windows нет кодека для этого формата");

    public static string Standard => T("standard", "обычно");
    public static string Best => T("best", "лучшее");
    public static string High => T("high", "высокое");
    public static string Smaller => T("smaller", "полегче");
    public static string Voice => T("voice", "голос");

    public static string AsIs => T("As is", "Как есть");
    public static string Channels => T("channels", "каналы");
    public static string Mono => T("Mono", "Моно");
    public static string OneChannel => T("one channel", "один канал");
    public static string Stereo => T("Stereo", "Стерео");
    public static string TwoChannels => T("two channels", "два канала");

    public static string Volume => T("volume", "громкость");
    public static string Normalize => T("Normalize", "Выровнять");
    public static string ToLufs => T("even loudness, −14 LUFS", "ровно громко, −14 LUFS");
    public static string Louder => T("louder", "громче");
    public static string Quieter => T("quieter", "тише");
}
