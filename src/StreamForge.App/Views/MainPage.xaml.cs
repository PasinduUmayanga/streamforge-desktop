using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using StreamForge.App.ViewModels;
using System.Collections.Specialized;

namespace StreamForge.App.Views;

public sealed partial class MainPage : Page
{
    private readonly MainViewModel _viewModel;
    private bool _networkSectionRevealed;

    public MainPage()
    {
        InitializeComponent();
        _viewModel = App.Services.GetRequiredService<MainViewModel>();
        DataContext = _viewModel;
        _viewModel.NetworkActivities.CollectionChanged += OnNetworkActivitiesChanged;
        Unloaded += (_, _) => _viewModel.NetworkActivities.CollectionChanged -= OnNetworkActivitiesChanged;
    }

    private void OnNetworkActivitiesChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        if (args.Action == NotifyCollectionChangedAction.Reset)
        {
            _networkSectionRevealed = false;
            return;
        }

        if (args.Action != NotifyCollectionChangedAction.Add || args.NewItems is null || args.NewItems.Count == 0)
        {
            return;
        }

        var latest = args.NewItems[^1];
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!_networkSectionRevealed)
            {
                NetworkSection.StartBringIntoView();
                _networkSectionRevealed = true;
            }

            NetworkActivityList.ScrollIntoView(latest);
        });
    }
}
