using System.Collections.ObjectModel;
using System.Reflection;
using System.Windows.Controls;
using System.Windows.Data;
using optimizerDuck.Domain.Abstractions;
using optimizerDuck.Domain.Attributes;
using optimizerDuck.Services.Optimization;
using Wpf.Ui.Controls;

namespace optimizerDuck.UI.ViewModels.Pages;

public class OptimizeViewModel(OptimizationRegistry optimizationService) : ViewModel
{
    public ObservableCollection<NavigationViewItem> OptimizationCategories { get; } = [];

    public event Action? OptimizationsLoaded;

    protected override async Task InitializeOnceAsync()
    {
        await optimizationService.EnsurePreloadedAsync().ConfigureAwait(true);
        ReloadCategories();
        OptimizationsLoaded?.Invoke();
    }

    private void ReloadCategories()
    {
        OptimizationCategories.Clear();

        // Redesign (19/11/2026): the first tab is the summary with the findings and the packs.
        var overview = new NavigationViewItem
        {
            TargetPageType = typeof(UI.Pages.OptimizeOverviewPage),
        };
        overview.SetBinding(
            ContentControl.ContentProperty,
            new Binding("[Optimize.Overview.Tab]")
            {
                Source = Services.Configuration.Loc.Instance,
                Mode = BindingMode.OneWay,
            }
        );
        OptimizationCategories.Add(overview);

        foreach (var category in optimizationService.OptimizationCategories)
        {
            var item = new NavigationViewItem
            {
                TargetPageType = category
                    .GetType()
                    .GetCustomAttribute<OptimizationCategoryAttribute>()!
                    .PageType,
            };

            // Bind Content instead of assigning it: categories are LocalizedObjects,
            // so the label re-resolves automatically on culture change.
            item.SetBinding(
                ContentControl.ContentProperty,
                new Binding(nameof(IOptimizationCategory.Name))
                {
                    Source = category,
                    Mode = BindingMode.OneWay,
                }
            );

            OptimizationCategories.Add(item);
        }
    }
}
