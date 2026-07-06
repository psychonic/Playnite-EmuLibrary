using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace EmuLibrary.Installs
{
    // The Installs sidebar view. Observes InstallProgressService.ViewModel and routes Cancel/Play/Clear buttons
    // (and row clicks) back to the service; it never triggers installs itself.
    public partial class InstallsPanelControl : UserControl
    {
        private readonly IInstallProgressService _service;

        internal InstallsPanelControl(IInstallProgressService service)
        {
            InitializeComponent();
            _service = service;
            DataContext = service.ViewModel;
        }

        private void CancelClicked(object sender, RoutedEventArgs e)
        {
            if ((sender as Button)?.Tag is InstallProgressItem item)
                _service.Cancel(item.GameId);
        }

        private void PlayClicked(object sender, RoutedEventArgs e)
        {
            if ((sender as Button)?.Tag is InstallProgressItem item)
                _service.PlayGame(item.GameId);
        }

        private void ClearClicked(object sender, RoutedEventArgs e)
        {
            if ((sender as Button)?.Tag is InstallProgressItem item)
                _service.ClearFinished(item.GameId);
        }

        private void ClearAllClicked(object sender, RoutedEventArgs e)
        {
            _service.ClearAllFinished();
        }

        // Clicking anywhere on a row (other than its buttons) jumps to the game in the library. ButtonBase
        // handles its own clicks, but guard anyway so a click that started on a button never also navigates.
        private void RowClicked(object sender, MouseButtonEventArgs e)
        {
            if (IsWithinButton(e.OriginalSource as DependencyObject))
                return;
            if ((sender as FrameworkElement)?.Tag is InstallProgressItem item)
                _service.NavigateToGame(item.GameId);
        }

        private static bool IsWithinButton(DependencyObject source)
        {
            for (var node = source; node != null; node = VisualTreeHelper.GetParent(node))
            {
                if (node is ButtonBase)
                    return true;
            }
            return false;
        }
    }
}
