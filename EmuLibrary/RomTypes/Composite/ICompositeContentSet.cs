using System.Collections.Generic;

namespace EmuLibrary.RomTypes
{
    // The composite shape both content families produce for a single title: a base plus updates, DLC and
    // (Family B only) license files. The item type is per-platform — Yuzu uses source file paths, PS3 uses
    // Ps3FileInfo — so this is generic; it does not force a shared item type. Update selection/ordering is
    // applied by CompositeContent.SelectUpdatesToInstall, not by this interface. Licenses is empty for the
    // content-store family (Family A).
    internal interface ICompositeContentSet<TItem>
    {
        TItem Base { get; }
        IReadOnlyList<TItem> Updates { get; }
        IReadOnlyList<TItem> Dlc { get; }
        IReadOnlyList<TItem> Licenses { get; }
    }
}
