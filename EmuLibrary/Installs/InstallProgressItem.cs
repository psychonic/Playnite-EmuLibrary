using Playnite.SDK;
using System;
using System.Collections.Generic;

namespace EmuLibrary.Installs
{
    // One row in the Installs panel: an install in flight or finished-since-launch. Identity fields are
    // immutable; the status/progress fields raise change notifications. All mutation is expected to happen
    // on the UI thread (InstallProgressService marshals via IUiDispatcher).
    public class InstallProgressItem : ObservableObject
    {
        public Guid GameId { get; }
        public string GameName { get; }
        // Resolved full path to the game icon (via Playnite.Database.GetFullFilePath), or null.
        public string IconPath { get; }
        public DateTime StartedOn { get; }

        public InstallProgressItem(Guid gameId, string gameName, string iconPath)
        {
            GameId = gameId;
            GameName = gameName;
            IconPath = iconPath;
            StartedOn = DateTime.Now;
            _status = InstallStatus.Queued;
            _statusText = "Queued…";
            _isIndeterminate = true;
            _progressMaximum = 1;
        }

        private InstallStatus _status;
        public InstallStatus Status
        {
            get => _status;
            set
            {
                if (_status == value)
                    return;
                _status = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsActive));
                OnPropertyChanged(nameof(IsFinished));
                OnPropertyChanged(nameof(IsCompleted));
            }
        }

        private string _statusText;
        public string StatusText
        {
            get => _statusText;
            set => SetValue(ref _statusText, value, nameof(StatusText));
        }

        private bool _isIndeterminate;
        public bool IsIndeterminate
        {
            get => _isIndeterminate;
            set => SetValue(ref _isIndeterminate, value, nameof(IsIndeterminate));
        }

        private double _progressValue;
        public double ProgressValue
        {
            get => _progressValue;
            set => SetValue(ref _progressValue, value, nameof(ProgressValue));
        }

        private double _progressMaximum;
        public double ProgressMaximum
        {
            get => _progressMaximum;
            set => SetValue(ref _progressMaximum, value < 1 ? 1 : value, nameof(ProgressMaximum));
        }

        private DateTime? _finishedOn;
        public DateTime? FinishedOn
        {
            get => _finishedOn;
            set => SetValue(ref _finishedOn, value, nameof(FinishedOn));
        }

        public bool IsActive => _status == InstallStatus.Queued || _status == InstallStatus.Installing;
        public bool IsFinished => !IsActive;
        // Successfully installed — the row offers a Play button.
        public bool IsCompleted => _status == InstallStatus.Completed;
    }
}
