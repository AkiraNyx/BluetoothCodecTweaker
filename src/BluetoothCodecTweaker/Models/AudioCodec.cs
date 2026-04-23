namespace BluetoothCodecTweaker.Models;

public enum AudioCodecType
{
    SBC,
    AAC,
    AptX,
    AptXHD,
    LDAC,
}

public sealed class AudioCodecInfo
{
    public AudioCodecType Type { get; }
    public string DisplayName { get; }
    public string Description { get; }
    public int MaxBitrateKbps { get; }
    public int SampleRateHz { get; }
    public int BitDepth { get; }
    public bool IsNativelySupported { get; }

    private AudioCodecInfo(AudioCodecType type, string displayName, string description,
        int maxBitrateKbps, int sampleRateHz, int bitDepth, bool isNativelySupported)
    {
        Type = type;
        DisplayName = displayName;
        Description = description;
        MaxBitrateKbps = maxBitrateKbps;
        SampleRateHz = sampleRateHz;
        BitDepth = bitDepth;
        IsNativelySupported = isNativelySupported;
    }

    public string BitrateDisplay => $"{MaxBitrateKbps} kbps";
    public string SampleRateDisplay => SampleRateHz >= 1000 ? $"{SampleRateHz / 1000.0:0.#} kHz" : $"{SampleRateHz} Hz";
    public string QualityDisplay => $"{SampleRateDisplay} / {BitDepth}-bit";

    public static readonly AudioCodecInfo SBC = new(
        AudioCodecType.SBC, "SBC", "Sub-Band Coding\n蓝牙 A2DP 标准基础编码，兼容性最广",
        328, 44100, 16, true);

    public static readonly AudioCodecInfo AAC = new(
        AudioCodecType.AAC, "AAC", "Advanced Audio Coding\n高质量音频编码，Apple 设备首选",
        256, 44100, 16, true);

    public static readonly AudioCodecInfo AptX = new(
        AudioCodecType.AptX, "aptX", "Qualcomm aptX\n低延迟高质量编码",
        384, 44100, 16, true);

    public static readonly AudioCodecInfo AptXHD = new(
        AudioCodecType.AptXHD, "aptX HD", "Qualcomm aptX HD\n高解析度低延迟编码",
        576, 48000, 24, true);

    public static readonly AudioCodecInfo LDAC = new(
        AudioCodecType.LDAC, "LDAC", "Sony LDAC\n超高解析度蓝牙音频编码，最高支持 Hi-Res 音频",
        990, 96000, 24, false);

    public static IReadOnlyList<AudioCodecInfo> All { get; } = [SBC, AAC, AptX, AptXHD, LDAC];

    public static AudioCodecInfo FromType(AudioCodecType type) => type switch
    {
        AudioCodecType.SBC => SBC,
        AudioCodecType.AAC => AAC,
        AudioCodecType.AptX => AptX,
        AudioCodecType.AptXHD => AptXHD,
        AudioCodecType.LDAC => LDAC,
        _ => SBC,
    };

    // A2DP Vendor-specific codec identifiers
    // LDAC: Sony Vendor ID = 0x054C, Codec ID = 0x00AA
    public static readonly uint LdacVendorId = 0x054C;
    public static readonly ushort LdacCodecId = 0x00AA;

    // aptX: Qualcomm Vendor ID = 0x004F (CSR), Codec ID = 0x0001
    public static readonly uint AptXVendorId = 0x004F;
    public static readonly ushort AptXCodecId = 0x0001;

    // aptX HD: Qualcomm Vendor ID = 0x00D7, Codec ID = 0x0024
    public static readonly uint AptXHdVendorId = 0x00D7;
    public static readonly ushort AptXHdCodecId = 0x0024;
}
