using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using StreamForge.App.ViewModels;

namespace StreamForge.App.Views;

public sealed partial class MainPage : Page
{
    public MainPage()
    {
        InitializeComponent();
        DataContext = App.Services.GetRequiredService<MainViewModel>();
    }
}
