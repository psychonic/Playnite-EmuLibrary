namespace EmuLibrary.Util.FileCopier
{
    // Byte-level progress for one copy operation. BytesTotal is the sum of all source file sizes for the copy
    // (0 when unknown, e.g. the Windows shell copier owns its own dialog); BytesDone is how much has been
    // written so far. A copier reports (0, total) up front and then a running BytesDone.
    public struct CopyProgress
    {
        public long BytesDone { get; }
        public long BytesTotal { get; }

        public CopyProgress(long bytesDone, long bytesTotal)
        {
            BytesDone = bytesDone;
            BytesTotal = bytesTotal;
        }
    }
}
