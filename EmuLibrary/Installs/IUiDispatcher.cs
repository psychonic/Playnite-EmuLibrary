using System;

namespace EmuLibrary.Installs
{
    // Marshals work onto the UI thread. Production wraps Playnite's MainView.UIDispatcher; unit tests
    // inject a synchronous implementation so InstallProgressService can be exercised without a live WPF
    // dispatcher. All ObservableCollection mutations and item PropertyChanged raises go through this.
    internal interface IUiDispatcher
    {
        void Invoke(Action action);
    }
}
