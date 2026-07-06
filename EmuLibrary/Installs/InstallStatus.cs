namespace EmuLibrary.Installs
{
    // Lifecycle of a single install as shown in the Installs panel. Queued/Installing are "active";
    // Completed/Failed/Canceled are "finished" (movable to the finished section, clearable).
    public enum InstallStatus
    {
        Queued,
        Installing,
        Completed,
        Failed,
        Canceled
    }
}
