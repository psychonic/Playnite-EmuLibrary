using EmuLibrary.Util.FileCopier;
using System;

namespace EmuLibrary.Installs
{
    // Forwards byte-level copy progress from an IFileCopier to an install handle. The handle throttles (by
    // bytes and time) and marshals onto the UI thread, so this adapter can report straight from a tight copy
    // loop. Lets the copy-based RomTypes show a real percentage bar instead of an indeterminate one.
    internal sealed class CopyProgressReporter : IProgress<CopyProgress>
    {
        private readonly IInstallProgressHandle _handle;

        public CopyProgressReporter(IInstallProgressHandle handle)
        {
            _handle = handle;
        }

        public void Report(CopyProgress value) => _handle.SetBytes(value.BytesDone, value.BytesTotal);
    }
}
