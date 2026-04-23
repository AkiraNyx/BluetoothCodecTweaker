using BluetoothCodecTweaker.Models;
using BluetoothCodecTweaker.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;

namespace BluetoothCodecTweaker.ViewModels;

public partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly BluetoothDeviceService _deviceService;
    private readonly CodecSwitchService _codecService;
    private readonly Microsoft.UI.Dispatching.DispatcherQueue _dispatcherQueue;

    public ObservableCollection<BluetoothAudioDevice> Devices { get; } = [];
    public ObservableCollection<CodecOption> CodecOptions { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedDeviceCodecDisplay))]
    [NotifyPropertyChangedFor(nameof(SelectedDeviceConnectionDisplay))]
    [NotifyPropertyChangedFor(nameof(HasSelectedDevice))]
    [NotifyCanExecuteChangedFor(nameof(SwitchCodecCommand))]
    public partial BluetoothAudioDevice? SelectedDevice { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SwitchCodecCommand))]
    public partial AudioCodecInfo? SelectedCodec { get; set; }

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial bool IsSwitching { get; set; }

    [ObservableProperty]
    public partial string StatusMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial InfoBarState InfoBarState { get; set; } = InfoBarState.Hidden;

    [ObservableProperty]
    public partial string InfoBarTitle { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string InfoBarMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool ShowFallbackButton { get; set; }

    // Safe non-chained properties for x:Bind (no SelectedDevice.XXX chains)
    public string SelectedDeviceCodecDisplay => SelectedDevice?.ActiveCodecDisplay ?? "";
    public string SelectedDeviceConnectionDisplay => SelectedDevice?.ConnectionStatusDisplay ?? "";
    public bool HasSelectedDevice => SelectedDevice is not null;

    public MainViewModel(Microsoft.UI.Dispatching.DispatcherQueue dispatcherQueue)
    {
        _dispatcherQueue = dispatcherQueue;
        _deviceService = new BluetoothDeviceService();
        _codecService = new CodecSwitchService();

        _deviceService.StatusChanged += msg =>
        {
            if (_dispatcherQueue is not null)
                _dispatcherQueue.TryEnqueue(() => StatusMessage = msg);
        };
    }

    [RelayCommand]
    private async Task InitializeAsync()
    {
        IsLoading = true;
        StatusMessage = "正在初始化蓝牙搜索...";
        try
        {
            var devices = await _deviceService.EnumerateDevicesAsync();
            Devices.Clear();
            foreach (var d in devices)
                Devices.Add(d);
            _deviceService.StartWatching();
        }
        catch (Exception ex)
        {
            ShowInfoBar("初始化失败", $"{ex.GetType().Name}: {ex.Message}", InfoBarState.Error);
        }
        IsLoading = false;
    }

    [RelayCommand]
    private async Task RefreshDevicesAsync()
    {
        IsLoading = true;
        HideInfoBar();
        StatusMessage = "正在刷新...";
        try
        {
            _deviceService.StopWatching();
            var devices = await _deviceService.EnumerateDevicesAsync();
            var previousSelectedId = SelectedDevice?.Id;
            SelectedDevice = null;
            Devices.Clear();
            foreach (var d in devices)
                Devices.Add(d);
            _deviceService.StartWatching();

            if (previousSelectedId is not null)
                SelectedDevice = Devices.FirstOrDefault(d => d.Id == previousSelectedId);

            if (Devices.Count > 0)
                ShowInfoBar("刷新完成", $"已找到 {Devices.Count} 个蓝牙音频设备", InfoBarState.Success);
            else
                ShowInfoBar("未找到设备", StatusMessage, InfoBarState.Warning);
        }
        catch (Exception ex)
        {
            ShowInfoBar("刷新失败", $"{ex.GetType().Name}: {ex.Message}", InfoBarState.Error);
        }
        IsLoading = false;
    }

    partial void OnSelectedDeviceChanged(BluetoothAudioDevice? value)
    {
        UpdateCodecOptions();
        HideInfoBar();
    }

    private void UpdateCodecOptions()
    {
        CodecOptions.Clear();
        if (SelectedDevice is null) return;

        foreach (var codecInfo in AudioCodecInfo.All)
        {
            bool isSupported = SelectedDevice.SupportsCodec(codecInfo.Type);
            bool isActive = SelectedDevice.ActiveCodec == codecInfo.Type;

            CodecOptions.Add(new CodecOption(
                codecInfo,
                isSupported,
                isActive,
                isSupported && SelectedDevice.IsConnected));
        }

        var activeOption = CodecOptions.FirstOrDefault(c => c.IsActive);
        if (activeOption is not null)
            SelectedCodec = activeOption.Info;
        else
            SelectedCodec = CodecOptions.FirstOrDefault(c => c.IsEnabled)?.Info;
    }

    private bool CanSwitchCodec() =>
        SelectedDevice is not null &&
        SelectedCodec is not null &&
        !IsSwitching;

    [RelayCommand(CanExecute = nameof(CanSwitchCodec))]
    private async Task SwitchCodecAsync()
    {
        if (SelectedDevice is null || SelectedCodec is null) return;

        var targetCodec = SelectedCodec.Type;

        if (!SelectedDevice.SupportsCodec(targetCodec))
        {
            var fallback = CodecSwitchService.GetFallbackCodec();
            ShowInfoBar(
                $"设备不支持 {SelectedCodec.DisplayName}",
                $"当前设备不支持 {SelectedCodec.DisplayName} 编码。建议切换至 {AudioCodecInfo.FromType(fallback).DisplayName}。",
                InfoBarState.Warning);
            ShowFallbackButton = true;
            return;
        }

        if (SelectedDevice.ActiveCodec == targetCodec)
        {
            ShowInfoBar("无需切换", $"设备已在使用 {SelectedCodec.DisplayName} 编码。", InfoBarState.Info);
            return;
        }

        IsSwitching = true;
        SwitchCodecCommand.NotifyCanExecuteChanged();
        ShowInfoBar("正在切换", $"正在切换至 {SelectedCodec.DisplayName}...", InfoBarState.Info);

        var result = await _codecService.SwitchCodecAsync(SelectedDevice, targetCodec);
        var message = CodecSwitchService.GetSwitchResultMessage(result, targetCodec);

        switch (result)
        {
            case CodecSwitchResult.Success:
                ShowInfoBar("切换成功", message, InfoBarState.Success);
                UpdateCodecOptions();
                break;
            case CodecSwitchResult.CodecNotSupported:
                ShowInfoBar("不支持", message, InfoBarState.Warning);
                ShowFallbackButton = true;
                break;
            case CodecSwitchResult.RegistryAccessDenied:
                ShowInfoBar("权限不足", message, InfoBarState.Error);
                break;
            default:
                ShowInfoBar("切换失败", message, InfoBarState.Error);
                break;
        }

        IsSwitching = false;
        SwitchCodecCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private async Task FallbackToAacAsync()
    {
        if (SelectedDevice is null) return;
        ShowFallbackButton = false;
        SelectedCodec = AudioCodecInfo.AAC;
        await SwitchCodecAsync();
    }

    private void ShowInfoBar(string title, string message, InfoBarState state)
    {
        InfoBarTitle = title;
        InfoBarMessage = message;
        InfoBarState = state;
        ShowFallbackButton = false;
    }

    private void HideInfoBar()
    {
        InfoBarState = InfoBarState.Hidden;
        ShowFallbackButton = false;
    }

    public void Dispose()
    {
        _deviceService.Dispose();
    }
}

public enum InfoBarState
{
    Hidden,
    Info,
    Success,
    Warning,
    Error,
}

// Simple record-like class for codec options displayed in the list
public sealed class CodecOption
{
    public AudioCodecInfo Info { get; }
    public string DisplayName => Info.DisplayName;
    public string Description => Info.Description;
    public string BitrateDisplay => Info.BitrateDisplay;
    public string QualityDisplay => Info.QualityDisplay;
    public bool IsNativelySupported => Info.IsNativelySupported;
    public bool IsSupported { get; }
    public bool IsActive { get; }
    public bool IsEnabled { get; }

    public CodecOption(AudioCodecInfo info, bool isSupported, bool isActive, bool isEnabled)
    {
        Info = info;
        IsSupported = isSupported;
        IsActive = isActive;
        IsEnabled = isEnabled;
    }
}
