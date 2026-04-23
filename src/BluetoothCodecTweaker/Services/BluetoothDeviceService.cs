using BluetoothCodecTweaker.Models;
using Microsoft.Win32;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;

namespace BluetoothCodecTweaker.Services;

public sealed class BluetoothDeviceService : IDisposable
{
    private DeviceWatcher? _watcher;
    private readonly Dictionary<string, BluetoothAudioDevice> _devices = [];
    private readonly SemaphoreSlim _lock = new(1, 1);

    public event Action<string>? StatusChanged;
    public event Action? DevicesChanged;

    public IReadOnlyList<BluetoothAudioDevice> Devices
    {
        get
        {
            _lock.Wait();
            try { return [.. _devices.Values]; }
            finally { _lock.Release(); }
        }
    }

    public async Task<List<BluetoothAudioDevice>> EnumerateDevicesAsync()
    {
        var result = new List<BluetoothAudioDevice>();
        StatusChanged?.Invoke("正在搜索蓝牙设备...");

        int totalFound = 0;

        try
        {
            var selector = BluetoothDevice.GetDeviceSelectorFromPairingState(true);
            var deviceInfoCollection = await DeviceInformation.FindAllAsync(selector);

            totalFound = deviceInfoCollection.Count;
            StatusChanged?.Invoke($"发现 {totalFound} 个已配对蓝牙设备，正在筛选音频设备...");

            foreach (var info in deviceInfoCollection)
            {
                try
                {
                    var btDevice = await BluetoothDevice.FromIdAsync(info.Id);
                    if (btDevice is null) continue;

                    bool isAudio = IsAudioDevice(btDevice);
                    if (!isAudio) continue;

                    bool isConnected = btDevice.ConnectionStatus == BluetoothConnectionStatus.Connected;
                    var supportedCodecs = DetectSupportedCodecs(btDevice.BluetoothAddress);
                    var activeCodec = isConnected ? await DetectActiveCodecAsync(btDevice.BluetoothAddress) : null;

                    result.Add(new BluetoothAudioDevice
                    {
                        Id = info.Id,
                        Name = string.IsNullOrWhiteSpace(btDevice.Name) ? "(未知设备)" : btDevice.Name,
                        BluetoothAddress = btDevice.BluetoothAddress,
                        IsConnected = isConnected,
                        ActiveCodec = activeCodec,
                        SupportedCodecs = supportedCodecs,
                    });
                }
                catch
                {
                    // Skip devices that fail to load
                }
            }
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke($"蓝牙搜索失败: {ex.Message}");
            return result;
        }

        if (result.Count > 0)
        {
            StatusChanged?.Invoke($"已找到 {result.Count} 个蓝牙音频设备（共 {totalFound} 个蓝牙设备）");
        }
        else if (totalFound > 0)
        {
            StatusChanged?.Invoke($"发现 {totalFound} 个蓝牙设备，但未检测到音频设备");
        }
        else
        {
            StatusChanged?.Invoke("未找到已配对的蓝牙设备，请确认蓝牙已开启且设备已配对");
        }

        return result;
    }

    private static bool IsAudioDevice(BluetoothDevice btDevice)
    {
        // Check by Class of Device: AudioVideo major class covers headphones, speakers, etc.
        if (btDevice.ClassOfDevice.MajorClass == BluetoothMajorClass.AudioVideo)
            return true;

        // Some devices (e.g. multi-function) may not report AudioVideo as major class
        // but have audio-related minor class or service class bits
        var serviceCapabilities = btDevice.ClassOfDevice.ServiceCapabilities;
        if (serviceCapabilities.HasFlag(BluetoothServiceCapabilities.AudioService) ||
            serviceCapabilities.HasFlag(BluetoothServiceCapabilities.RenderingService) ||
            serviceCapabilities.HasFlag(BluetoothServiceCapabilities.CapturingService))
            return true;

        return false;
    }

    public void StartWatching()
    {
        if (_watcher is not null) return;

        try
        {
            var selector = BluetoothDevice.GetDeviceSelectorFromPairingState(true);
            string[] requestedProperties = ["System.Devices.Aep.IsConnected"];

            _watcher = DeviceInformation.CreateWatcher(
                selector,
                requestedProperties,
                DeviceInformationKind.AssociationEndpoint);

            _watcher.Updated += OnDeviceUpdated;
            _watcher.Start();
        }
        catch { }
    }

    public void StopWatching()
    {
        if (_watcher is null) return;
        try
        {
            if (_watcher.Status is DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted)
                _watcher.Stop();
        }
        catch { }
        _watcher = null;
    }

    private async void OnDeviceUpdated(DeviceWatcher sender, DeviceInformationUpdate update)
    {
        await _lock.WaitAsync();
        try
        {
            if (_devices.TryGetValue(update.Id, out var existing))
            {
                if (update.Properties.TryGetValue("System.Devices.Aep.IsConnected", out var connObj))
                {
                    existing.IsConnected = connObj is true;
                }

                if (existing.IsConnected)
                {
                    existing.ActiveCodec = await DetectActiveCodecAsync(existing.BluetoothAddress);
                }
            }
        }
        finally { _lock.Release(); }
        DevicesChanged?.Invoke();
    }

    private static List<AudioCodecType> DetectSupportedCodecs(ulong bluetoothAddress)
    {
        var codecs = new List<AudioCodecType> { AudioCodecType.SBC };

        try
        {
            string addressHex = bluetoothAddress.ToString("x12");

            string registryPath = $@"SYSTEM\CurrentControlSet\Services\BTHPORT\Parameters\Devices\{addressHex}";
            using var key = Registry.LocalMachine.OpenSubKey(registryPath);
            if (key is not null)
            {
                var codecValue = key.GetValue("SupportedCodecs");
                if (codecValue is byte[] codecBytes)
                    ParseSupportedCodecs(codecBytes, codecs);
            }

            if (!codecs.Contains(AudioCodecType.AAC))
                codecs.Add(AudioCodecType.AAC);

            string a2dpPath = $@"SYSTEM\CurrentControlSet\Services\BthA2dp\Parameters\{addressHex}";
            using var a2dpKey = Registry.LocalMachine.OpenSubKey(a2dpPath);
            if (a2dpKey is not null)
            {
                foreach (var valueName in a2dpKey.GetValueNames())
                {
                    if (valueName.Contains("aptX", StringComparison.OrdinalIgnoreCase) &&
                        !codecs.Contains(AudioCodecType.AptX))
                        codecs.Add(AudioCodecType.AptX);

                    if (valueName.Contains("aptXHD", StringComparison.OrdinalIgnoreCase) &&
                        !codecs.Contains(AudioCodecType.AptXHD))
                        codecs.Add(AudioCodecType.AptXHD);

                    if (valueName.Contains("LDAC", StringComparison.OrdinalIgnoreCase) &&
                        !codecs.Contains(AudioCodecType.LDAC))
                        codecs.Add(AudioCodecType.LDAC);
                }
            }
        }
        catch
        {
            if (!codecs.Contains(AudioCodecType.AAC))
                codecs.Add(AudioCodecType.AAC);
        }

        return codecs;
    }

    private static void ParseSupportedCodecs(byte[] data, List<AudioCodecType> codecs)
    {
        int offset = 0;
        while (offset < data.Length - 1)
        {
            byte codecType = data[offset];
            if (offset + 1 >= data.Length) break;
            byte infoLength = data[offset + 1];

            switch (codecType)
            {
                case 0x00:
                    break;
                case 0x02:
                    if (!codecs.Contains(AudioCodecType.AAC))
                        codecs.Add(AudioCodecType.AAC);
                    break;
                case 0xFF:
                    if (infoLength >= 6 && offset + 2 + 6 <= data.Length)
                    {
                        uint vendorId = BitConverter.ToUInt32(data, offset + 2);
                        ushort codecId = BitConverter.ToUInt16(data, offset + 6);
                        IdentifyVendorCodec(vendorId, codecId, codecs);
                    }
                    break;
            }

            offset += 2 + infoLength;
        }
    }

    private static void IdentifyVendorCodec(uint vendorId, ushort codecId, List<AudioCodecType> codecs)
    {
        if (vendorId == AudioCodecInfo.AptXVendorId && codecId == AudioCodecInfo.AptXCodecId)
        {
            if (!codecs.Contains(AudioCodecType.AptX))
                codecs.Add(AudioCodecType.AptX);
        }
        else if (vendorId == AudioCodecInfo.AptXHdVendorId && codecId == AudioCodecInfo.AptXHdCodecId)
        {
            if (!codecs.Contains(AudioCodecType.AptXHD))
                codecs.Add(AudioCodecType.AptXHD);
        }
        else if (vendorId == AudioCodecInfo.LdacVendorId && codecId == AudioCodecInfo.LdacCodecId)
        {
            if (!codecs.Contains(AudioCodecType.LDAC))
                codecs.Add(AudioCodecType.LDAC);
        }
    }

    private static Task<AudioCodecType?> DetectActiveCodecAsync(ulong bluetoothAddress)
    {
        return Task.Run(() =>
        {
            try
            {
                string addressHex = bluetoothAddress.ToString("x12");
                string registryPath = $@"SYSTEM\CurrentControlSet\Services\BthA2dp\Parameters\{addressHex}";

                using var key = Registry.LocalMachine.OpenSubKey(registryPath);
                if (key is not null)
                {
                    var activeCodecValue = key.GetValue("ActiveCodec") ?? key.GetValue("SelectedCodec");
                    if (activeCodecValue is int codecInt)
                    {
                        return codecInt switch
                        {
                            0 => (AudioCodecType?)AudioCodecType.SBC,
                            2 => (AudioCodecType?)AudioCodecType.AAC,
                            _ => (AudioCodecType?)AudioCodecType.SBC,
                        };
                    }

                    if (activeCodecValue is string codecStr)
                    {
                        return codecStr.ToUpperInvariant() switch
                        {
                            "SBC" => (AudioCodecType?)AudioCodecType.SBC,
                            "AAC" => AudioCodecType.AAC,
                            "APTX" => AudioCodecType.AptX,
                            "APTXHD" or "APTX HD" => AudioCodecType.AptXHD,
                            "LDAC" => AudioCodecType.LDAC,
                            _ => null,
                        };
                    }
                }
            }
            catch { }
            return null;
        });
    }

    public void Dispose()
    {
        StopWatching();
        _lock.Dispose();
    }
}
