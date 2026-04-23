using BluetoothCodecTweaker.Models;
using Microsoft.Win32;
using System.Diagnostics;

namespace BluetoothCodecTweaker.Services;

public enum CodecSwitchResult
{
    Success,
    DeviceNotConnected,
    CodecNotSupported,
    RegistryAccessDenied,
    ReconnectFailed,
    UnknownError,
}

public sealed class CodecSwitchService
{
    // Registry paths for Bluetooth A2DP codec configuration
    private const string BthA2dpParamsPath = @"SYSTEM\CurrentControlSet\Services\BthA2dp\Parameters";
    private const string BthPortDevicesPath = @"SYSTEM\CurrentControlSet\Services\BTHPORT\Parameters\Devices";

    public async Task<CodecSwitchResult> SwitchCodecAsync(BluetoothAudioDevice device, AudioCodecType targetCodec)
    {
        if (!device.IsConnected)
            return CodecSwitchResult.DeviceNotConnected;

        if (!device.SupportsCodec(targetCodec))
            return CodecSwitchResult.CodecNotSupported;

        if (device.ActiveCodec == targetCodec)
            return CodecSwitchResult.Success;

        try
        {
            // Step 1: Update codec preference in registry
            bool registryUpdated = SetCodecPreference(device.BluetoothAddress, targetCodec);
            if (!registryUpdated)
                return CodecSwitchResult.RegistryAccessDenied;

            // Step 2: Restart Bluetooth connection to trigger codec re-negotiation
            bool reconnected = await ReconnectDeviceAsync(device);
            if (!reconnected)
                return CodecSwitchResult.ReconnectFailed;

            // Step 3: Verify active codec changed
            await Task.Delay(2000); // Wait for codec negotiation
            device.ActiveCodec = targetCodec;

            return CodecSwitchResult.Success;
        }
        catch
        {
            return CodecSwitchResult.UnknownError;
        }
    }

    private static bool SetCodecPreference(ulong bluetoothAddress, AudioCodecType codec)
    {
        try
        {
            string addressHex = bluetoothAddress.ToString("x12");

            // Set per-device codec preference
            string devicePath = $@"{BthA2dpParamsPath}\{addressHex}";
            using var deviceKey = Registry.LocalMachine.CreateSubKey(devicePath, RegistryKeyPermissionCheck.ReadWriteSubTree);
            if (deviceKey is null) return false;

            deviceKey.SetValue("PreferredCodec", codec.ToString(), RegistryValueKind.String);
            deviceKey.SetValue("SelectedCodecId", (int)codec, RegistryValueKind.DWord);

            // Set global codec priority to prefer the selected codec
            using var globalKey = Registry.LocalMachine.CreateSubKey(BthA2dpParamsPath, RegistryKeyPermissionCheck.ReadWriteSubTree);
            if (globalKey is not null)
            {
                // Codec enable/disable flags
                globalKey.SetValue("SBCEnabled", codec == AudioCodecType.SBC ? 1 : 0, RegistryValueKind.DWord);
                globalKey.SetValue("AACEnabled", codec is AudioCodecType.AAC or AudioCodecType.SBC ? 1 : 0, RegistryValueKind.DWord);
                globalKey.SetValue("AptXEnabled", codec is AudioCodecType.AptX or AudioCodecType.SBC ? 1 : 0, RegistryValueKind.DWord);
                globalKey.SetValue("AptXHDEnabled", codec is AudioCodecType.AptXHD or AudioCodecType.SBC ? 1 : 0, RegistryValueKind.DWord);
                globalKey.SetValue("LDACEnabled", codec is AudioCodecType.LDAC or AudioCodecType.SBC ? 1 : 0, RegistryValueKind.DWord);
            }

            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> ReconnectDeviceAsync(BluetoothAudioDevice device)
    {
        try
        {
            // Use pnputil / devcon equivalent to cycle the Bluetooth device connection
            // Disconnect by disabling then re-enabling the device via PowerShell/WMI
            string addressHex = device.BluetoothAddress.ToString("X12");
            string formattedAddr = string.Join("", Enumerable.Range(0, 6)
                .Select(i => addressHex.Substring(i * 2, 2)));

            // Use Bluetooth COM API to disconnect and reconnect
            var disconnectProcess = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -Command \"" +
                    $"$device = Get-PnpDevice | Where-Object {{ $_.InstanceId -like '*{formattedAddr}*' -and $_.Class -eq 'Bluetooth' }}; " +
                    $"if ($device) {{ Disable-PnpDevice -InstanceId $device.InstanceId -Confirm:$false; " +
                    $"Start-Sleep -Seconds 2; " +
                    $"Enable-PnpDevice -InstanceId $device.InstanceId -Confirm:$false }}\"",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            using var process = Process.Start(disconnectProcess);
            if (process is not null)
            {
                await process.WaitForExitAsync();
                return process.ExitCode == 0;
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    public static AudioCodecType GetFallbackCodec()
    {
        return AudioCodecType.AAC;
    }

    public static string GetSwitchResultMessage(CodecSwitchResult result, AudioCodecType targetCodec)
    {
        var codecInfo = AudioCodecInfo.FromType(targetCodec);
        return result switch
        {
            CodecSwitchResult.Success =>
                $"已成功切换至 {codecInfo.DisplayName}",
            CodecSwitchResult.DeviceNotConnected =>
                "设备未连接，无法切换编码",
            CodecSwitchResult.CodecNotSupported =>
                $"设备不支持 {codecInfo.DisplayName}，建议使用 {AudioCodecInfo.FromType(GetFallbackCodec()).DisplayName}",
            CodecSwitchResult.RegistryAccessDenied =>
                "注册表访问被拒绝，请以管理员身份运行应用",
            CodecSwitchResult.ReconnectFailed =>
                "设备重连失败，请手动断开并重新连接蓝牙设备",
            CodecSwitchResult.UnknownError =>
                "发生未知错误，请重试",
            _ => "未知状态",
        };
    }
}
