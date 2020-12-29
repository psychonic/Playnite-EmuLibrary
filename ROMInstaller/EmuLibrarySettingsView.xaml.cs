using System;
using System.Windows;
using System.Windows.Controls;

namespace EmuLibrary
{
    public partial class EmuLibrarySettingsView : UserControl
    {
        private bool InManualCellCommit = false;

        public EmuLibrarySettingsView()
        {
            InitializeComponent();
        }

        private void Click_Delete(object sender, RoutedEventArgs e)
        {
            // TODO Refactor
            var mapping = ((FrameworkElement)sender).DataContext as EmuLibrarySettings.ROMInstallerEmulatorMapping;
            if (mapping != null)
            {
                var res = EmuLibrarySettings.Instance.PlayniteAPI.Dialogs.ShowMessage(string.Format("Delete this mapping?\r\n\r\n{0}", mapping.GetDescription()), "Confirm delete", MessageBoxButton.YesNo);
                if (res == MessageBoxResult.Yes)
                {
                    EmuLibrarySettings.Instance.Mappings.Remove(mapping);
                }
            }
        }

        private void Click_BrowseSource(object sender, RoutedEventArgs e)
        {
            var mapping = ((FrameworkElement)sender).DataContext as EmuLibrarySettings.ROMInstallerEmulatorMapping;
            string path;
            if ((path = GetSelectedFolderPath(mapping.SourcePath)) != null)
            {
                mapping.SourcePath = path;
            }
        }

        private void Click_BrowseDestination(object sender, RoutedEventArgs e)
        {
            var mapping = ((FrameworkElement)sender).DataContext as EmuLibrarySettings.ROMInstallerEmulatorMapping;
            string path;
            if ((path = GetSelectedFolderPath(mapping.DestinationPath)) != null)
            {
                mapping.DestinationPath = path;
            }
        }

        private string GetSelectedFolderPath(string startingFolder)
        {
#if true
            return EmuLibrarySettings.Instance.PlayniteAPI.Dialogs.SelectFolder();
#else
            var dlg = new Ookii.Dialogs.Wpf.VistaFolderBrowserDialog();
            dlg.SelectedPath = startingFolder;

            bool? result = dlg.ShowDialog();
            if (result == true)
            {
                return dlg.SelectedPath;
            }

            return null;
#endif
        }

        private void DataGrid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
        {
            if (!InManualCellCommit)
            {
                InManualCellCommit = true;
                var grid = (DataGrid)sender;
                // HACK!!!!
                // Alternate approach 1: try to find new value here and store that somewhere as the currently selected emu
                // Alternate approach 2: the "right" way(?) https://stackoverflow.com/a/34332709
                if (e.Column.Header.ToString() == "Emulator" || e.Column.Header.ToString() == "Profile")
                {
                    grid.CommitEdit(DataGridEditingUnit.Row, true);
                }
                InManualCellCommit = false;
            }

        }

        private void DataGrid_CurrentCellChanged(object sender, EventArgs e)
        {
            
        }
    }
}