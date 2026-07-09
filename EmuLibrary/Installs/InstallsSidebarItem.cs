using Playnite.SDK.Plugins;
using System;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace EmuLibrary.Installs
{
    // Desktop-mode sidebar entry that opens the Installs panel. The control is built once and cached (per
    // the SDK's Opened contract) so re-opening keeps scroll state and doesn't re-create bindings.
    internal sealed class InstallsSidebarItem : SidebarItem
    {
        private readonly InstallsViewModel _viewModel;
        private InstallsPanelControl _control;
        // Config-driven half of visibility, set by the plugin when settings are committed. Combined with the
        // empty-state gate (nothing to show) in RefreshVisibility so the two conditions can't clobber each other.
        private bool _configAllowsVisibility = true;

        public InstallsSidebarItem(IInstallProgressService service)
        {
            // Note the SDK's spelling: SiderbarItemType.
            Type = SiderbarItemType.View;
            Title = "EmuLibrary Installs";

            // Bubble the aggregate progress of in-flight installs onto the sidebar icon's built-in bar.
            _viewModel = service.ViewModel;
            _viewModel.PropertyChanged += OnViewModelChanged;
            UpdateProgress();
            // Hidden until there's something to show; the service marshals count changes onto the UI thread.
            RefreshVisibility();

            try
            {
                Icon = new Image
                {
                    Source = new BitmapImage(new Uri(ResolveIconPath())),
                    Width = 20,
                    Height = 20,
                    Stretch = Stretch.Uniform
                };
            }
            catch
            {
                // Icon is cosmetic; a missing/failed load must not break the sidebar entry.
            }

            Opened = () =>
            {
                if (_control == null)
                    _control = new InstallsPanelControl(service);
                return _control;
            };
        }

        // Prefer the user's configured override icon (if it points at a file that exists); otherwise fall back
        // to the bundled extension icon. Resolved once at construction — the item is cached for the session.
        // The stored override may use the {PlayniteDir} placeholder (portable installs), so expand it first.
        private static string ResolveIconPath()
        {
            var settings = Settings.Settings.Instance;
            var overridePath = settings?.SidebarIconPath;
            if (!string.IsNullOrWhiteSpace(overridePath))
            {
                var playnite = settings.PlayniteAPI;
                var resolved = Util.PortablePathResolver.Resolve(overridePath, playnite.Paths.IsPortable, playnite.Paths.ApplicationPath);
                if (System.IO.File.Exists(resolved))
                    return resolved;
            }

            return EmuLibrary.Icon;
        }

        // Called by the plugin when settings are committed. The final Visible state ANDs this with the
        // empty-state gate so a config change can't reveal an empty panel (and vice versa).
        public void SetConfigAllowsVisibility(bool allowed)
        {
            _configAllowsVisibility = allowed;
            RefreshVisibility();
        }

        private void OnViewModelChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(InstallsViewModel.AggregateProgressValue)
                || e.PropertyName == nameof(InstallsViewModel.AggregateProgressMaximum))
                UpdateProgress();
            else if (e.PropertyName == nameof(InstallsViewModel.IsEmpty))
                RefreshVisibility();
        }

        // Show the entry only when the config permits it AND there's at least one row to show — nothing started,
        // or everything cleared after finish/failure/cancel, leaves nothing worth a sidebar slot.
        private void RefreshVisibility()
        {
            Visible = _configAllowsVisibility && !_viewModel.IsEmpty;
        }

        private void UpdateProgress()
        {
            // Per the SDK, ProgressValue = 0 hides the bar — which is what we want when nothing measurable is
            // in flight (no active installs, or all still in an indeterminate stage).
            ProgressMaximum = _viewModel.AggregateProgressMaximum;
            ProgressValue = _viewModel.AggregateProgressMaximum > 0 ? _viewModel.AggregateProgressValue : 0;
        }
    }
}
