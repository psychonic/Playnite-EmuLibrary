using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

namespace ROMManager
{
    public partial class ROMInstallerSettingsView : UserControl
    {
        private ROMInstallerSettings.ROMInstallerEmulatorMapping rowBeingEdited = null;
        private bool InManualCellCommit = false;

        public ROMInstallerSettingsView()
        {
            InitializeComponent();
        }

        private void Click_BrowseSource(object sender, RoutedEventArgs e)
        {
            var mapping = ((FrameworkElement)sender).DataContext as ROMInstallerSettings.ROMInstallerEmulatorMapping;

            var dlg = new Ookii.Dialogs.Wpf.VistaFolderBrowserDialog();
            dlg.SelectedPath = mapping.SourcePath;

            Nullable<bool> result = dlg.ShowDialog();
            if (result == true)
            {
                string filename = dlg.SelectedPath;
                mapping.SourcePath = filename;
            }
        }

        private void Click_BrowseDestination(object sender, RoutedEventArgs e)
        {
            var mapping = ((FrameworkElement)sender).DataContext as ROMInstallerSettings.ROMInstallerEmulatorMapping;

            var dlg = new Ookii.Dialogs.Wpf.VistaFolderBrowserDialog();
            dlg.SelectedPath = mapping.SourcePath;

            bool? result = dlg.ShowDialog();
            if (result == true)
            {
                string filename = dlg.SelectedPath;
                mapping.SourcePath = filename;
            }
        }

        private void DataGrid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
        {
            if (!InManualCellCommit)
            {
                InManualCellCommit = true;
                var grid = (DataGrid)sender;
                // HACK!!!!
                if (e.Column.Header.ToString() == "Emulator")
                {
                    grid.CommitEdit(DataGridEditingUnit.Row, true);
                }
                //rowBeingEdited = e.Row.Item as ROMInstallerSettings.ROMInstallerEmulatorMapping;
                InManualCellCommit = false;
            }

        }

        private void DataGrid_CurrentCellChanged(object sender, EventArgs e)
        {
            
        }
    }
}