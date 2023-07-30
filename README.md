# EmuLibrary

EmuLibrary is a library extension for [Playnite](https://www.playnite.link), an open source video game library manager, focused on emulator ROM management.

While Playnite has had built-in support for scanning paths for installed ROMs and adding them to the library since version 9, EmuLibrary provides alternate functionality.

EmuLibrary treats one or more folders of ROMs/Disc images as a library from which you can "install" games. It can be useful if you have a large collection of emulated games and limited storage where you play them versus where you store them (HTPC vs. NAS, for example). It also is useful for keeping the list of emulated games up to date, and for being able to filter via installed/uninstalled.

To set it up, you create mappings to combine one of each of the following:

* Emulator - either a built-in emulator or a custom emulator manually added
* Emulator Profile - either a built-in emulator profile or a custom one, out of those supported by the chosen emulator
* Platform - the ROM platform/console, out of those that the emulator profile supports
* RomType - See [Rom Types](#rom-types) below

## Paths

For source and destination, only valid Windows file paths are currently supported. The intended use case is for having the source be an SMB file share (either via UNC path or mapped drive), and the destination be a local path. However, any valid file path should work for either. This means that you can get creative with the source if you have a way to mount alternate remote storage at a Windows file path.

Additionally, for destination paths, relativity to the Playnite folder is preserved if you are using a portable installation of Playnite and your destination is below that folder hierarchically. This means that, for example, if your portable installation is at D:\playnite, and you choose `D:\playnite\rominstall` as your destination, it will be saved internally as `{PlayniteDir}\rominstall`.

## Rom Types

### SingleFile

SingleFile is the simplest type of ROM supported. This is for source folders in which each ROM is fully contained in a single file. It's commonly used for older, non-disc-based systems where the whole ROM consists of a single file. (Ex. .nes, .sfc, .md, etc.). Archive formats are supported as well if the emulator supports them directly. (Ex. .zip)

### MultiFile

With the MultiFile type, each subfolder directly within the source folder is scanned as a potential "ROM". This is for games that have multiple loose files. (Ex. one or more .bin/.cue, with optional .m3u). When installing a MultiFile game, the whole folder is copied. 

To determine which file is used as the one to tell the emulator to load, all files matching the configured emulator profile's supported extensions are considered. Precedence is by configured image extension list order, and then by alphabetical order. For example, if file names are the same except for `(Disc 1)` versus `(Disc 2)`, the first disc takes precedence. Similarly, if you have `.cue` in the extension list before `.m3u` (as some of the built-in profiles have at the time of writing), `.cue` would be chosen over `.m3u`, which may not be desired for multi-disc games.

### Yuzu (Beta)

The Yuzu type currently has a beta level quality of support. Some of it is still being reworked. As named, it is very hardcoded to Yuzu specifically, although Ryujinx support reusing most of the same logic will likely come in the future.

To add a functional mapping, make sure that the selected emulator is Yuzu. (It does not need to be the built-in emulator listing for Yuzu. Custom ones, including ones that point to Yuzu EA, etc. will also work). In the source path, loose XCI/NSP/XCZ/NSZ files in the root of the path are considered.

NSP/NSZ files can also be updates and DLC, rather than just games. Unlike with Tinfoil shares, files are not required to include the title id in the filename. Additionally, while destination path must point to a folder that exists, the setting is ignored. Games install into the NAND directory configured in the selected Yuzu emulator profile.

When a game is installed, the latest update and any DLC from the source will also be installed to the Yuzu NAND, in that order (Game, Update if available, each available DLC). Games already installed will be imported, whether or not they exist in the source folder, and will display as installed. As expected, uninstalling a game will remove the game from Yuzu's NAND. (While Yuzu does not support XCZ or NSZ files for launching or installing to NAND, this plugin installs directly to Yuzu's NAND, without relying on the emulator's built-in install functionality)

## Support

To get help, check out the #extension-support channel on the Playnite Discord, linked at the top of https://playnite.link/

The following files are generally useful for troubleshooting, relative to the folder where Playnite data is stored. For a portable installation, this is the same folder that Playnite is installed to. For non-portable installations, it is in AppData.

* playnite.log
* extensions.log
* library\emulators.db
* library\platforms.db