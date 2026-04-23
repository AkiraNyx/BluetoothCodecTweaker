using BluetoothCodecTweaker.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace BluetoothCodecTweaker;

public sealed partial class MainPage : Page
{
    public MainViewModel ViewModel { get; }

    public MainPage()
    {
        ViewModel = new MainViewModel(DispatcherQueue);
        InitializeComponent();
        ViewModel.InitializeCommand.Execute(null);
    }
}
