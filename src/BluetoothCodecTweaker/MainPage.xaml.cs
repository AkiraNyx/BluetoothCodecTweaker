using BluetoothCodecTweaker.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace BluetoothCodecTweaker;

public sealed partial class MainPage : Page
{
    public MainViewModel ViewModel { get; }

    public MainPage()
    {
        ViewModel = new MainViewModel(DispatcherQueue);
        InitializeComponent();
        DeviceSelector.ItemsSource = ViewModel.Devices;
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await ViewModel.InitializeCommand.ExecuteAsync(null);
    }

    private void OnCodecCardTapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: CodecOption option } && option.IsEnabled)
        {
            ViewModel.SelectedCodec = option.Info;
        }
    }
}
