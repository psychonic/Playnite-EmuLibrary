using Playnite.SDK.Plugins;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;

namespace EmuLibrary.Installs
{
    // Desktop top-panel badge showing how many installs are in progress. Hidden when none are active. Purely
    // informational — Playnite exposes no API to open a sidebar view from a top-panel click, so activation is a
    // no-op; the badge just tells the user something is running without opening the Installs panel. TopPanelItem
    // is an ObservableObject, so updating Title/Visible re-renders live; the count glyph is a UIElement whose
    // text is updated in place. Wired to the ViewModel's Active collection, which is mutated on the UI thread,
    // so CollectionChanged (and this handler) run on the UI thread too.
    internal sealed class InstallsTopPanelItem : TopPanelItem
    {
        private readonly InstallsViewModel _viewModel;
        private readonly TextBlock _countText;

        public InstallsTopPanelItem(InstallsViewModel viewModel)
        {
            _viewModel = viewModel;

            var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            panel.Children.Add(new TextBlock
            {
                Text = "↓", // downwards arrow — a "downloading" cue that renders in any font
                FontSize = 16,
                VerticalAlignment = VerticalAlignment.Center
            });
            _countText = new TextBlock
            {
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(3, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            panel.Children.Add(_countText);
            Icon = panel;

            _viewModel.Active.CollectionChanged += OnActiveChanged;
            Update(_viewModel.Active.Count);
        }

        private void OnActiveChanged(object sender, NotifyCollectionChangedEventArgs e) => Update(_viewModel.Active.Count);

        private void Update(int count)
        {
            Visible = count > 0;
            _countText.Text = count.ToString();
            Title = count == 1 ? "1 install in progress" : $"{count} installs in progress";
        }
    }
}
