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

    public void StartWatching()
    {
        if (_watcher is not null) return;

        try
        {
            string aqsFilter = BluetoothDevice.GetDeviceSelectorFromPairingState(true);
            string[] requestedProperties =
            [
                "System.Devices.Aep.IsConnected",
                "System.Devices.Aep.DeviceAddress",
            ];

            _watcher = DeviceInformation.CreateWatcher(
                aqsFilter,
                requestedProperties,
                DeviceInformationKind.AssociationEndpoint);

            _watcher.Added += OnDeviceAdded;
            _watcher.Updated += OnDeviceUpdated;
            _watcher.Removed += OnDeviceRemoved;
            _watcher.EnumerationCompleted += (_, _) => DevicesChanged?.Invoke();
            _watcher.Start();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[BluetoothDeviceService] StartWatching failed: {ex.Message}");
            DevicesChanged?.Invoke();
        }
    }

    public void StopWatching()
    {
        if (_watcher is null) return;
        try
        {
            if (_watcher.Status is DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted)
            {
                _watcher.Stop();
            }
        }
        catch { }
        _watcher = null;
    }

    private async void OnDeviceAdded(DeviceWatcher sender, DeviceInformation info)
    {
        var device = await CreateDeviceFromInfoAsync(info);
        if (device is null) return;

        await _lock.WaitAsync();
        try { _devices[info.Id] = device; }
        finally { _lock.Release(); }
        DevicesChanged?.Invoke();
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

    private async void OnDeviceRemoved(DeviceWatcher sender, DeviceInformationUpdate update)
    {
        await _lock.WaitAsync();
        try { _devices.Remove(update.Id); }
        finally { _lock.Release(); }
        DevicesChanged?.Invoke();
    }

    private static async Task<BluetoothAudioDevice?> CreateDeviceFromInfoAsync(DeviceInformation info)
    {
        try
        {
            var btDevice = await BluetoothDevice.FromIdAsync(info.Id);
            if (btDevice is null) return null;

            // Filter: only keep audio devices (AudioVideo major class)
            if (btDevice.ClassOfDevice.MajorClass != BluetoothMajorClass.AudioVideo)
                return null;

            bool isConnected = false;
            if (info.Properties.TryGetValue("System.Devices.Aep.IsConnected", out var connObj))
            {
                isConnected = connObj is true;
            }

            var supportedCodecs = DetectSupportedCodecs(btDevice.BluetoothAddress);
            var activeCodec = isConnected ? await DetectActiveCodecAsync(btDevice.BluetoothAddress) : null;

            return new BluetoothAudioDevice
            {
                Id = info.Id,
                Name = btDevice.Name,
                BluetoothAddress = btDevice.BluetoothAddress,
                IsConnected = isConnected,
                ActiveCodec = activeCodec,
                SupportedCodecs = supportedCodecs,
            };
        }
        catch
        {
            return null;
        }
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
                {
                    ParseSupportedCodecs(codecBytes, codecs);
                }
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
                case 0x00: // SBC
                    break;
                case 0x02: // AAC
                    if (!codecs.Contains(AudioCodecType.AAC))
                        codecs.Add(AudioCodecType.AAC);
                    break;
                case 0xFF: // Vendor-specific
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
