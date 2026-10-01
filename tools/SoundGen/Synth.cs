namespace IdleSword.SoundGen;

/// <summary>
/// 8bit 合成原语与 WAV 读写。输出 16bit PCM 单声道 22050Hz——与 NES 时代同量级的采样率，
/// 文件小，音色也自带那种粗糙感。素材是占位性质，不是最终音频设计。
/// </summary>
internal static class Synth
{
    public const int Rate = 22050;
    // 五声音阶（宫商角徵羽）的相邻半音数，主旋律与音效都基于它，避免半音带来的西洋味。
    private static readonly int[] PentaSteps = [0, 2, 4, 7, 9];

    public enum Wave { Square, Triangle, Noise }

    /// <summary>五声音阶级数转频率，0 为宫（C4）；负级数向下八度，用于贝斯。</summary>
    public static double Penta(double degree)
    {
        int d = (int)Math.Floor(degree);
        int octave = (int)Math.Floor(d / 5.0);
        int step = PentaSteps[((d % 5) + 5) % 5];
        return 261.6256 * Math.Pow(2, (step + 12 * octave) / 12.0);
    }

    public static double[] Buffer(double seconds) => new double[(int)Math.Round(seconds * Rate)];

    /// <summary>
    /// 渲染一个音。f0→f1 线性扫频，扫频 zap 与鼓点的音高下滑都靠它；attack 内的线性起音
    /// 保证从 0 开始，避免波形突然跳变产生咔哒声。
    /// </summary>
    /// <param name="duty">方波占空比，12.5% 更尖、50% 更厚。</param>
    /// <param name="curve">衰减幂次，越大衰减越快。</param>
    public static void Tone(double[] buf, double start, double duration, double f0, double f1, Wave wave,
        double amp, double duty = .5, double attack = .004, double curve = 2)
    {
        int begin = (int)(start * Rate), count = (int)(duration * Rate);
        double phase = 0, clock = 0;
        ushort lfsr = 0x7FFF;
        for (int i = 0; i < count; i++)
        {
            int index = begin + i;
            if (index < 0 || index >= buf.Length) continue;
            double t = (double)i / Rate;
            double f = f0 + (f1 - f0) * (t / duration);
            double value;
            switch (wave)
            {
                case Wave.Square:
                    phase += f / Rate;
                    value = phase % 1.0 < duty ? 1 : -1;
                    break;
                case Wave.Triangle:
                    // 量化成 16 级，还原 NES 三角波那种台阶感。
                    phase += f / Rate;
                    value = Math.Floor(phase % 1.0 * 16) / 16.0 * 2 - 1;
                    break;
                default:
                    // 噪声也用 f 当时钟，于是"敲"出来的噪声是带音高的，可以直接做鼓和爆音。
                    clock += f / Rate;
                    if (clock >= 1)
                    {
                        clock -= 1;
                        ushort bit = (ushort)((lfsr ^ (lfsr >> 1)) & 1);
                        lfsr = (ushort)((lfsr >> 1) | (bit << 14));
                    }
                    value = (lfsr & 1) == 0 ? 1 : -1;
                    break;
            }
            buf[index] += value * amp * Envelope(t, duration, attack, curve);
        }
    }

    /// <summary>起音线性、其余按幂次衰减。</summary>
    private static double Envelope(double t, double duration, double attack, double curve)
    {
        if (attack > 0 && t < attack) return t / attack;
        double k = (t - attack) / Math.Max(1e-6, duration - attack);
        return k >= 1 ? 0 : Math.Pow(1 - k, curve);
    }

    /// <summary>
    /// 首尾各做一次极短淡入淡出。循环曲若在接缝处残留非零振幅，每圈都会"啪"一声；
    /// 3ms 的斜坡听不出来，但足以消掉跳变。
    /// </summary>
    public static void FadeEdges(double[] buf, double seconds = .003)
    {
        int n = Math.Min((int)(seconds * Rate), buf.Length / 2);
        for (int i = 0; i < n; i++)
        {
            double k = (double)i / n;
            buf[i] *= k;
            buf[buf.Length - 1 - i] *= k;
        }
    }

    public static void Write(string path, double[] buffer)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var writer = new BinaryWriter(File.Create(path));
        int dataBytes = buffer.Length * 2;
        writer.Write("RIFF"u8); writer.Write(36 + dataBytes); writer.Write("WAVE"u8);
        writer.Write("fmt "u8); writer.Write(16); writer.Write((short)1); writer.Write((short)1);
        writer.Write(Rate); writer.Write(Rate * 2); writer.Write((short)2); writer.Write((short)16);
        writer.Write("data"u8); writer.Write(dataBytes);
        // 用 tanh 软限幅而不是归一化：保留各音效之间的相对响度（重击本来就该比普通命中响）。
        foreach (double sample in buffer) writer.Write((short)(Math.Tanh(sample) * 32000));
    }

    public readonly record struct Inspection(int Frames, double Peak, double Head, double Tail);

    /// <summary>回读校验：确认文件真的写成了预期的 PCM 格式，并检查首尾电平（循环接缝是否干净）。</summary>
    public static Inspection Inspect(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        if (bytes.Length < 44) throw new InvalidDataException($"{path}: 文件过短，不是合法 WAV");
        if (System.Text.Encoding.ASCII.GetString(bytes, 0, 4) != "RIFF" ||
            System.Text.Encoding.ASCII.GetString(bytes, 8, 4) != "WAVE")
            throw new InvalidDataException($"{path}: 缺少 RIFF/WAVE 头");
        int channels = BitConverter.ToInt16(bytes, 22);
        int bits = BitConverter.ToInt16(bytes, 34);
        int rate = BitConverter.ToInt32(bytes, 24);
        if (channels != 1 || bits != 16 || rate != Rate)
            throw new InvalidDataException($"{path}: 期望单声道 16bit {Rate}Hz，实际 {channels}ch {bits}bit {rate}Hz");
        int frames = (bytes.Length - 44) / 2;
        double peak = 0;
        for (int i = 0; i < frames; i++)
            peak = Math.Max(peak, Math.Abs(BitConverter.ToInt16(bytes, 44 + i * 2) / 32000.0));
        // 首尾各取单个样本。循环接缝的跳变就是 |首 - 尾|：只要两者相等，回绕就是连续的；
        // 淡入淡出把两者都压到 0，于是跳变也接近 0。不能用"前若干帧的最大值"——淡入本来就会把它抬起来。
        double head = frames > 0 ? Math.Abs(BitConverter.ToInt16(bytes, 44) / 32000.0) : 0;
        double tail = frames > 0 ? Math.Abs(BitConverter.ToInt16(bytes, 44 + (frames - 1) * 2) / 32000.0) : 0;
        return new Inspection(frames, peak, head, tail);
    }
}
