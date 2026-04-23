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
    private const string BthA2dpParamsPath = @"SYSTEM\CurrentControlSet\Services\BthA2dp\Parameters";

    public async Task<CodecSwitchResult> SwitchCodecAsync(BluetoothAudioDevice device, AudioCodecType targetCodec)
    {
        if (!device.IsConnected)
            return CodecSwitchResult.DeviceNotConnected;

        try
        {
            bool registryUpdated = SetCodecPreference(device.BluetoothAddress, targetCodec);
            if (!registryUpdated)
                return CodecSwitchResult.RegistryAccessDenied;

            bool reconnected = await ReconnectBluetoothAsync(device.BluetoothAddress);
            if (!reconnected)
                return CodecSwitchResult.ReconnectFailed;

            // Don't fake ActiveCodec — ETW will detect the real codec after reconnection
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
            // Set global codec enable/disable flags
            // SBC is mandatory (A2DP spec), always keep enabled
            using var globalKey = Registry.LocalMachine.CreateSubKey(BthA2dpParamsPath, RegistryKeyPermissionCheck.ReadWriteSubTree);
            if (globalKey is null) return false;

            globalKey.SetValue("SBCEnabled", 1, RegistryValueKind.DWord); // Always on
            globalKey.SetValue("AACEnabled", codec == AudioCodecType.AAC ? 1 : 0, RegistryValueKind.DWord);
            globalKey.SetValue("AptXEnabled", codec == AudioCodecType.AptX ? 1 : 0, RegistryValueKind.DWord);
            globalKey.SetValue("AptXHDEnabled", codec == AudioCodecType.AptXHD ? 1 : 0, RegistryValueKind.DWord);
            globalKey.SetValue("LDACEnabled", codec == AudioCodecType.LDAC ? 1 : 0, RegistryValueKind.DWord);

            // If target is SBC, disable all others to force SBC
            if (codec == AudioCodecType.SBC)
            {
                globalKey.SetValue("AACEnabled", 0, RegistryValueKind.DWord);
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

    private static async Task<bool> ReconnectBluetoothAsync(ulong bluetoothAddress)
    {
        try
        {
            string addressHex = bluetoothAddress.ToString("x12");

            string script =
                "$addr = '" + addressHex + "'; " +
                "$devices = Get-PnpDevice -ErrorAction SilentlyContinue | " +
                "Where-Object { $_.InstanceId -like '*BTHENUM*' -and ($_.InstanceId -replace '[^0-9a-fA-F]','') -match $addr }; " +
                "if (-not $devices) { " +
                "  $radio = Get-PnpDevice -Class Bluetooth -ErrorAction SilentlyContinue | " +
                "  Where-Object { $_.InstanceId -notlike '*BTHENUM*' -and $_.Status -eq 'OK' } | Select-Object -First 1; " +
                "  if ($radio) { " +
                "    Disable-PnpDevice -InstanceId $radio.InstanceId -Confirm:$false; " +
                "    Start-Sleep -Seconds 3; " +
                "    Enable-PnpDevice -InstanceId $radio.InstanceId -Confirm:$false; " +
                "    Write-Host 'OK' } " +
                "  else { Write-Host 'NOTFOUND' } " +
                "} else { " +
                "  foreach ($dev in $devices) { Disable-PnpDevice -InstanceId $dev.InstanceId -Confirm:$false -ErrorAction SilentlyContinue } " +
                "  Start-Sleep -Seconds 3; " +
                "  foreach ($dev in $devices) { Enable-PnpDevice -InstanceId $dev.InstanceId -Confirm:$false -ErrorAction SilentlyContinue } " +
                "  Write-Host 'OK' }";

            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -Command \"{script}\"",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            using var process = Process.Start(psi);
            if (process is null) return false;

            string output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();

            return output.Contains("OK");
        }
        catch
        {
            return false;
        }
    }

    public static AudioCodecType GetFallbackCodec() => AudioCodecType.AAC;

    public static string GetSwitchResultMessage(CodecSwitchResult result, AudioCodecType targetCodec)
    {
        var codecInfo = AudioCodecInfo.FromType(targetCodec);
        return result switch
        {
            CodecSwitchResult.Success => $"已设置偏好编码为 {codecInfo.DisplayName}，蓝牙正在重连中，播放音频后可确认实际编码",
            CodecSwitchResult.DeviceNotConnected => "设备未连接，无法切换编码",
            CodecSwitchResult.CodecNotSupported => $"设备不支持 {codecInfo.DisplayName}，建议使用 {AudioCodecInfo.FromType(GetFallbackCodec()).DisplayName}",
            CodecSwitchResult.RegistryAccessDenied => "注册表访问被拒绝，请以管理员身份运行应用",
            CodecSwitchResult.ReconnectFailed => "蓝牙重连失败，请手动在系统设置中断开并重连蓝牙设备",
            CodecSwitchResult.UnknownError => "发生未知错误，请重试",
            _ => "未知状态",
        };
    }
}
