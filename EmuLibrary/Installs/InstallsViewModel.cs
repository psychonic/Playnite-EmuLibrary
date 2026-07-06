using Playnite.SDK;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace EmuLibrary.Installs
{
    // Read model bound by InstallsPanelControl. Three collections keep the XAML simple: Active (in flight),
    // Finished (completed/canceled since launch), and Failed. InstallProgressService owns all mutation and
    // marshals it onto the UI thread; the panel only observes. This model also rolls up a title-level aggregate
    // (summed bytes across the determinate active installs) that the sidebar item surfaces as its progress bar.
    public class InstallsViewModel : ObservableObject
    {
        public InstallsViewModel()
        {
            // Track active-install progress so AggregateProgress* stays current. Item mutations and collection
            // changes are all raised on the UI thread by the service, so these handlers run there too.
            Active.CollectionChanged += OnActiveCollectionChanged;
        }

        public ObservableCollection<InstallProgressItem> Active { get; } = new ObservableCollection<InstallProgressItem>();
        public ObservableCollection<InstallProgressItem> Finished { get; } = new ObservableCollection<InstallProgressItem>();
        // Failed installs live in their own section (kept apart from completed/canceled) so errors stand out.
        public ObservableCollection<InstallProgressItem> Failed { get; } = new ObservableCollection<InstallProgressItem>();

        public bool HasActive => Active.Count > 0;
        public bool HasFinished => Finished.Count > 0;
        public bool HasFailed => Failed.Count > 0;
        // True when there's any terminal row to clear (drives the "Clear all" footer).
        public bool HasClearable => Finished.Count > 0 || Failed.Count > 0;
        public bool IsEmpty => Active.Count == 0 && Finished.Count == 0 && Failed.Count == 0;

        // Summed progress across the determinate active installs (indeterminate ones — "Preparing…", etc. — have
        // no byte total and are excluded). Maximum is 0 when nothing measurable is in flight, which the sidebar
        // item treats as "hide the bar". These are raw byte sums; only the value/maximum ratio matters.
        public double AggregateProgressValue { get; private set; }
        public double AggregateProgressMaximum { get; private set; }

        // Raised by the service after mutating the collections so header/empty-state/"Clear all" toggles refresh.
        internal void RaiseCountsChanged()
        {
            OnPropertyChanged(nameof(HasActive));
            OnPropertyChanged(nameof(HasFinished));
            OnPropertyChanged(nameof(HasFailed));
            OnPropertyChanged(nameof(HasClearable));
            OnPropertyChanged(nameof(IsEmpty));
        }

        private void OnActiveCollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.OldItems != null)
                foreach (InstallProgressItem item in e.OldItems)
                    item.PropertyChanged -= OnActiveItemChanged;
            if (e.NewItems != null)
                foreach (InstallProgressItem item in e.NewItems)
                    item.PropertyChanged += OnActiveItemChanged;

            RecomputeAggregate();
        }

        private void OnActiveItemChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(InstallProgressItem.ProgressValue)
                || e.PropertyName == nameof(InstallProgressItem.ProgressMaximum)
                || e.PropertyName == nameof(InstallProgressItem.IsIndeterminate))
                RecomputeAggregate();
        }

        private void RecomputeAggregate()
        {
            double value = 0, maximum = 0;
            foreach (var item in Active)
            {
                if (item.IsIndeterminate || item.ProgressMaximum <= 0)
                    continue;
                value += item.ProgressValue;
                maximum += item.ProgressMaximum;
            }

            AggregateProgressValue = value;
            AggregateProgressMaximum = maximum;
            OnPropertyChanged(nameof(AggregateProgressValue));
            OnPropertyChanged(nameof(AggregateProgressMaximum));
        }
    }
}
