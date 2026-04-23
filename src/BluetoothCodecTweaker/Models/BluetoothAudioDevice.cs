namespace BluetoothCodecTweaker.Models;

public sealed class BluetoothAudioDevice
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public ulong BluetoothAddress { get; init; }
    public bool IsConnected { get; set; }
    public AudioCodecType? ActiveCodec { get; set; }
    public List<AudioCodecType> SupportedCodecs { get; init; } = [];

    public string FormattedAddress
    {
        get
        {
            var addr = BluetoothAddress;
            return string.Format("{0:X2}:{1:X2}:{2:X2}:{3:X2}:{4:X2}:{5:X2}",
                (addr >> 40) & 0xFF, (addr >> 32) & 0xFF, (addr >> 24) & 0xFF,
                (addr >> 16) & 0xFF, (addr >> 8) & 0xFF, addr & 0xFF);
        }
    }

    public string ActiveCodecDisplay => ActiveCodec is { } codec
        ? AudioCodecInfo.FromType(codec).DisplayName
        : "正在检测（播放音频后自动识别）";

    public string ConnectionStatusDisplay => IsConnected ? "已连接" : "未连接";

    public bool SupportsCodec(AudioCodecType codec) => SupportedCodecs.Contains(codec);
}
