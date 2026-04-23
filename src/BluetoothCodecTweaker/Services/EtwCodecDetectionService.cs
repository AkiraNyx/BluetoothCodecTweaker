using BluetoothCodecTweaker.Models;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;

namespace BluetoothCodecTweaker.Services;

public sealed record DetectedCodecInfo(
    AudioCodecType? CodecType,
    byte StandardCodecId,
    int VendorId,
    int VendorCodecId)
{
    public string DisplayName => CodecType is { } ct
        ? AudioCodecInfo.FromType(ct).DisplayName
        : $"Unknown (0x{StandardCodecId:X2}, Vendor 0x{VendorId:X4}:0x{VendorCodecId:X4})";
}

public sealed class EtwCodecDetectionService : IDisposable
{
    private const string SessionName = "BluetoothCodecTweaker_BthA2dp";
    private const string ProviderName = "Microsoft.Windows.Bluetooth.BthA2dp";
    private const string EventName = "A2dpStreaming";
    private static readonly Guid ProviderGuid = Guid.Parse("8776ad1e-5022-4451-a566-f47e708b9075");

    private TraceEventSession? _session;
    private Thread? _processingThread;
    private volatile bool _isRunning;

    public event Action<DetectedCodecInfo>? CodecDetected;
    public event Action<string>? Error;

    public static bool IsElevated => TraceEventSession.IsElevated() == true;
    public bool IsRunning => _isRunning;

    public bool Start()
    {
        if (_isRunning) return true;

        if (!IsElevated)
        {
            Error?.Invoke("编码检测需要管理员权限，请以管理员身份运行应用");
            return false;
        }

        try
        {
            _session = new TraceEventSession(SessionName, TraceEventSessionOptions.Create);
            _session.StopOnDispose = true;

            _session.Source.Dynamic.AddCallbackForProviderEvent(
                ProviderName,
                EventName,
                OnA2dpStreamingEvent);

            _session.EnableProvider(ProviderGuid, TraceEventLevel.Verbose, matchAnyKeywords: 0);

            _isRunning = true;
            _processingThread = new Thread(ProcessEvents)
            {
                Name = "ETW-BthA2dp",
                IsBackground = true,
            };
            _processingThread.Start();

            return true;
        }
        catch (Exception ex)
        {
            Error?.Invoke($"ETW 会话启动失败: {ex.Message}");
            Cleanup();
            return false;
        }
    }

    public void Stop()
    {
        if (!_isRunning) return;
        _isRunning = false;
        _session?.Dispose();
        _session = null;
        _processingThread?.Join(TimeSpan.FromSeconds(3));
        _processingThread = null;
    }

    private void ProcessEvents()
    {
        try
        {
            _session?.Source.Process();
        }
        catch
        {
            // Session disposed (Stop called) or unexpected error
        }
        finally
        {
            _isRunning = false;
        }
    }

    private void OnA2dpStreamingEvent(TraceEvent e)
    {
        try
        {
            byte standardCodecId = (byte)e.PayloadValue(3);
            int vendorId = (int)e.PayloadValue(4);
            int vendorCodecId = (int)e.PayloadValue(5);

            var codecType = ResolveCodecType(standardCodecId, vendorId, vendorCodecId);
            CodecDetected?.Invoke(new DetectedCodecInfo(codecType, standardCodecId, vendorId, vendorCodecId));
        }
        catch { }
    }

    private static AudioCodecType? ResolveCodecType(byte standardCodecId, int vendorId, int vendorCodecId)
    {
        switch (standardCodecId)
        {
            case 0x00: return AudioCodecType.SBC;
            case 0x02: return AudioCodecType.AAC;
        }

        if (standardCodecId != 0xFF) return null;

        return (vendorId, vendorCodecId) switch
        {
            (0x004F, 0x0001) => AudioCodecType.AptX,
            (0x00D7, 0x0024) => AudioCodecType.AptXHD,
            (0x012D, 0x00AA) => AudioCodecType.LDAC,
            _ => null,
        };
    }

    private void Cleanup()
    {
        _isRunning = false;
        _session?.Dispose();
        _session = null;
    }

    public void Dispose() => Stop();
}
