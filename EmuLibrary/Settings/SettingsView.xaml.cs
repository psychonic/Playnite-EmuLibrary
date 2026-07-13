using System;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace EmuLibrary.Settings
{
    public partial class SettingsView : UserControl
    {
        public SettingsView()
        {
            InitializeComponent();
        }

        private void Click_AddMapping(object sender, RoutedEventArgs e)
        {
            Settings.Instance.Mappings.Add(new EmulatorMapping { Enabled = true });
        }

        private void Click_Delete(object sender, RoutedEventArgs e)
        {
            if (((FrameworkElement)sender).DataContext is EmulatorMapping mapping)
            {
                var details = mapping.GetDescriptionLines().Aggregate((a, b) => $"{a}{Environment.NewLine}{b}");

                // A game stores its mapping's Id inside its GameId, so deleting the mapping orphans every
                // game imported under it (their mapping can no longer be resolved). Warn with the count.
                var gameCount = MappingEditReconciler.GamesForMapping(Settings.Instance.PlayniteAPI, mapping.MappingId).Count();
                var warning = gameCount > 0
                    ? $"{Environment.NewLine}{Environment.NewLine}WARNING: {gameCount} imported game(s) reference this mapping. Deleting it will orphan them - they can no longer be installed/uninstalled through EmuLibrary unless the mapping is re-created."
                    : "";

                var res = Settings.Instance.PlayniteAPI.Dialogs.ShowMessage(string.Format("Delete this mapping?\r\n\r\n{0}{1}", details, warning), "Confirm delete", MessageBoxButton.YesNo);
                if (res == MessageBoxResult.Yes)
                {
                    Settings.Instance.Mappings.Remove(mapping);
                }
            }
        }

        private void Click_BrowseSource(object sender, RoutedEventArgs e)
        {
            var mapping = ((FrameworkElement)sender).DataContext as EmulatorMapping;
            string path;
            if ((path = GetSelectedFolderPath()) != null)
            {
                mapping.SourcePath = path;
            }
        }

        private void Click_BrowseDestination(object sender, RoutedEventArgs e)
        {
            var mapping = ((FrameworkElement)sender).DataContext as EmulatorMapping;
            if (mapping == null || !mapping.SupportsDestinationPath)
            {
                return;
            }

            string path;
            if ((path = GetSelectedFolderPath()) != null)
            {
                var playnite = Settings.Instance.PlayniteAPI;
                if (playnite.Paths.IsPortable)
                {
                    path = path.Replace(playnite.Paths.ApplicationPath, Playnite.SDK.ExpandableVariables.PlayniteDirectory);
                }

                mapping.DestinationPath = path;
            }
        }

        private static string GetSelectedFolderPath()
        {
            return Settings.Instance.PlayniteAPI.Dialogs.SelectFolder();
        }

        private void Click_BrowseSidebarIcon(object sender, RoutedEventArgs e)
        {
            var playnite = Settings.Instance.PlayniteAPI;
            var path = playnite.Dialogs.SelectIconFile();
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            // Store paths under a portable Playnite install using the {PlayniteDir} placeholder so the
            // library stays relocatable (mirrors Click_BrowseDestination).
            if (playnite.Paths.IsPortable)
            {
                path = path.Replace(playnite.Paths.ApplicationPath, Playnite.SDK.ExpandableVariables.PlayniteDirectory);
            }

            Settings.Instance.SidebarIconPath = path;
        }

        private void Click_ClearSidebarIcon(object sender, RoutedEventArgs e)
        {
            Settings.Instance.SidebarIconPath = null;
        }

        private void Hyperlink_RequestNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
        {
            Process.Start(e.Uri.AbsoluteUri);
            e.Handled = true;
        }
    }
}