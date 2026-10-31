# DiskSweeper

[![build](https://github.com/wuling63/DiskSweeper/actions/workflows/build.yml/badge.svg)](https://github.com/wuling63/DiskSweeper/actions/workflows/build.yml)

[中文](README.md) | English

A C# / .NET Framework 4.8 WinForms desktop app that scans for and deletes files that are safe to remove: system temp files, caches, error reports and so on.
The layout follows the usual disk-cleaner pattern: a green banner at the top shows how much was cleaned today and in total plus the scan result, a checkable task list on the left, and file details on the right.

The UI is available in Traditional Chinese, Simplified Chinese, Japanese, Korean and English (**Settings → Options → Language**, takes effect after a restart).

## Build and run

```
dotnet build -c Release
bin\Release\net48\DiskSweeper.exe
```

The executable is named DiskSweeper.exe rather than DiskCleaner.exe so it isn't confused with the Glary Utilities program of the same name.
It normally runs with standard privileges; to fully clean system items (marked with a shield icon), elevate with **File → Restart as administrator**.

## Usage

1. Check the items to clean on the left (a parent node checks the whole group; a partially checked group shows a mixed state)
2. Click **Scan**: files are only listed, nothing is deleted; each item shows its size
3. Click an item on the left to see its files on the right; select files you want to keep and click **Exclude this file** (adds them to the ignore list)
4. Click **Clean**: a confirmation dialog (with the file count, total size and a "cannot be undone" note) appears before anything is deleted

| Where | What it does |
|------|------|
| View history | Time, file count and space freed for each cleanup |
| Select all / Select none / Default selection | Check state of the list on the left |
| Custom files and folders | Add your own folders and file name patterns (e.g. `*.log;*.bak`); checked = include subfolders |
| Open folder / Properties | For the file selected on the right (double-click also opens the folder) |
| Exclude this file | Adds it to the ignore list and removes it from the current result |
| Ignore list | Manage files and folders skipped while scanning |
| Search box / column headers | Filter and sort the list on the right; right-click to open the folder, show properties, copy the path or exclude |
| File → Export scan results | Save all results as CSV (opens directly in Excel) |
| Tools → Large files | Folder size tree + treemap (area = size, click to drill down) + largest files |
| Tools → Duplicate files | Groups of files with identical content (by hash); keep the oldest or newest in each group — at least one copy must remain |
| Tools → Empty folders | Folders with no files anywhere beneath them (only the top of an empty tree is listed), unchecked by default |
| Tools → Installed programs | Name, publisher, version, install date, install location, size; uninstall, modify/repair, open folder (see below) |
| Settings → Options | Temp file age, send to Recycle Bin, development project folders, schedules |

Deletions from tool windows always go to the Recycle Bin. When the system drive has less than 10% free, a yellow warning appears under the banner.

Below the banner are **cards for every drive** (local, removable and network): letter, label, free/total space and a usage bar — amber at 80% and red at 90% or more. They refresh every 30 seconds and after each cleanup, scroll sideways when there are many (mouse wheel or the arrows at either end), and clicking a card opens **Large files** on that drive.

## Tools menu

| Submenu | Tool | Description |
|------|------|------|
| (top) | PC health check | Checks system and other drive space, cleanable junk, Recycle Bin, startup programs, disk health, hibernation file, page file, restore points, WSL / Docker virtual disks and install leftovers in one pass, and gives a 0–100 score (−6 for "attention", −15 for "needs action"). Each row has a button that opens the matching tool. Read-only — it never changes anything by itself |
| Disk analysis | Large files / Duplicate files / Empty folders | See the table above |
| | WSL / Docker virtual disks | Finds WSL distributions (registry `Lxss`) and Docker Desktop `.vhdx` files. Deleting files inside Linux doesn't shrink the vhdx, so this optionally runs `fstrim` inside Linux, then `wsl --shutdown`, then compacts with `diskpart`'s `compact vdisk` (works on Windows Home, which lacks `Optimize-VHD`). Requires administrator; refuses while Docker Desktop is running |
| | Install leftovers | Top-level folders in Program Files, ProgramData and AppData that match no installed program (name, publisher, install location, first word of the name), service, startup entry, Start menu shortcut or running process, haven't changed in 180 days and are larger than 1 MB. Names that look like caches / builds / updaters are skipped. This is a guess: unchecked by default, and deletions go to the Recycle Bin |
| | Disk space trend | Records the free space of each fixed drive once a day (when the app starts, after scheduled cleanups and during the daily space check; kept for one year in `space.json`) and draws a line chart, with filled dots on days a cleanup ran |
| | Compress folder | NTFS compression via `compact.exe` (Standard / XPRESS 8K / XPRESS 16K / LZX), showing disk usage before and after; system and user folders themselves can't be compressed |
| System | Installed programs | See below |
| | Startup programs | Registry Run keys (current user / all users, 64- and 32-bit), Startup folders, and scheduled tasks that run at logon or boot (tasks under `\Microsoft` aren't listed). Enable / disable writes `StartupApproved` — the same mechanism Task Manager uses — without deleting the entry; entries pointing to programs that no longer exist can be removed (registry backed up to .reg first) |
| | Background services | Lists only non-Microsoft services by default; administrators can change the startup type (`sc config`) and start / stop services, with a confirmation each time and an extra warning for Microsoft services |
| | Resource usage | Samples every 2 seconds, sorted by CPU or memory (private working set); processes can be ended |
| | Disk health (SMART) | Model, type, bus, capacity, drive letters and health status of each physical disk (`MSFT_PhysicalDisk`); temperature, SSD wear, power-on hours and read/write errors (`MSFT_StorageReliabilityCounter`) require administrator |
| | System restore points | Lists restore points (WMI `SystemRestore`) and shadow copy usage (`Win32_ShadowStorage`); can "keep only the newest" or delete selected points (`SRRemoveRestorePoint`); requires administrator |
| Security | File shredder | Overwrites with random data 1 / 3 / 7 times and renames to random characters before deleting; supports drag and drop. On SSDs, wear levelling and TRIM mean overwriting can't guarantee the original blocks are hit |
| | Cleanup backups | Lists the zip files made before cleanups; restore to the original location (never overwriting existing files) or delete |

## Appearance, language, tray and backups (Settings → Options)

- **Appearance**: Follow system / Light / Dark, applied immediately to every open window; dark mode also darkens title bars, scroll bars and list headers.
  With "Follow system", the app switches when Windows switches between light and dark.
- **Language**: Traditional Chinese / Simplified Chinese / Japanese / Korean / English, takes effect after a restart; the UI font switches to each language's Windows UI font
  (JhengHei / YaHei / Yu Gothic UI / Malgun Gothic). Every UI string in the source is wrapped in `Lang.T("…")`, and the dictionaries are the embedded
  `en.txt`, `zh-CN.txt`, `ja.txt` and `ko.txt` (one `Traditional Chinese<Tab>translation` pair per line, with `\r \n \\` escapes and trailing spaces written as `\s`).
  Format strings are translated before values are filled in, so dynamic messages are fully translated sentences too. New strings must be added to all four dictionaries
  (a test checks they contain the same keys); strings missing from Japanese or Korean fall back to English.
- **System tray**: minimizing or closing the window sends it to the tray; right-click the icon for **Quick clean** (same scope as the weekly automatic cleanup), reopen the window or exit.
- **Back up before cleaning**: files to be deleted are first zipped into `%LOCALAPPDATA%\DiskCleaner\Backup\yyyyMMdd_HHmmss.zip`. Single files over 200 MB,
  and anything beyond 2 GB per batch, aren't backed up; the retention period is configurable (7 days by default). Nothing extra is backed up when sending to the Recycle Bin.

## Installed programs

Reads the uninstall lists in the registry (HKLM 64-bit, HKLM 32-bit WOW6432Node, HKCU) — the same source as **Settings → Apps** in Windows.

| Column / action | Description |
|------|------|
| Size (registry) | EstimatedSize as filled in by the installer — often inaccurate or missing |
| Folder size | Actual total of the install folder, computed in the background after the window opens (each folder counted once) |
| Install location | InstallLocation from the registry; when missing, inferred from the icon or uninstaller location and marked "(guessed)" |
| Uninstall | After confirmation, runs the program's own uninstaller (with a UAC prompt when needed) and refreshes when it finishes. MSI packages always use `msiexec /X{GUID}` (many MSI entries register `/I`, which means modify) |
| Modify / repair | Uses ModifyPath when present; MSI packages open `msiexec /I{GUID}` maintenance mode |
| Remove leftover entry | Only for entries whose uninstaller no longer exists (shown greyed out). Deletes just that registry entry, never files, after backing it up with `reg export` to `%LOCALAPPDATA%\DiskCleaner\RegistryBackup` — double-click the .reg to restore. All-users entries require administrator |
| Other | Search (name / publisher / path), sort by column, open install folder, publisher website, copy details, export CSV |

System components, Windows updates and patches are hidden by default; check **Show system components and updates** to list them.
When deciding whether an entry is a leftover, RunDll32-style uninstall commands are judged by whether the DLL they load exists; anything undecidable is treated as fine — better to miss one than to mislabel one.
Microsoft Store apps aren't in this list.

## Options and schedules

- **Send to Recycle Bin**: cleaned files go to the Recycle Bin instead of being deleted, so mistakes can be undone (space is freed only when the Recycle Bin is emptied).
  Doesn't apply to the Recycle Bin, Windows.old, the component store or Run history — those are always cleaned directly.
- **Development project folders**: "Python __pycache__" and ".mypy_cache / .pytest_cache / .ruff_cache" are searched only inside these folders,
  skipping `.git` and `node_modules`; emptied cache folders are removed too, but nothing above them in the project is touched.
- **Weekly automatic cleanup**: creates `DiskSweeper\AutoClean` in Windows Task Scheduler, running `DiskSweeper.exe /auto`.
  Uses the items checked on the left, but **never empties the Recycle Bin, never touches privacy items, and never runs DISM or Windows.old**; runs with standard privileges.
  Results go into the history, a line is appended to `%LOCALAPPDATA%\DiskCleaner\auto.log`, and a tray notification is shown.
- **Daily disk space check**: creates `DiskSweeper\LowSpaceCheck`, running `/lowspace` every day at 09:00.
  It only notifies when the system drive has less than 10% free (clicking the notification opens the app) and exits quietly otherwise.
- Tasks are created with `schtasks.exe` as the current user, without administrator rights; unchecking an option deletes its task.

## Cleanup items

🛡 = requires administrator to scan fully (with standard privileges only readable parts are listed); ⚠ = shows a side-effect warning when checked.
**Select all** doesn't check ⚠ or privacy items; those must be checked one by one.

| Category | Item | Location | Default |
|------|------|------|------|
| Windows | System temp files 🛡 | `%WINDIR%\Temp` | ✔ |
| | User temp files | `%TEMP%` | ✔ |
| | Thumbnail cache | `Explorer\thumbcache_*.db`, `iconcache_*.db` | ✔ |
| | Memory dump files 🛡 | `MEMORY.DMP`, `Minidump`, `LiveKernelReports`, `CrashDumps` | ✔ |
| | Error report files | WER ReportArchive / ReportQueue | ✔ |
| | System log files 🛡 | .log / .cab in `%WINDIR%\Logs`, `%WINDIR%\*.log` | ✔ |
| | Remote Desktop cache | `Terminal Server Client\Cache` | |
| | Recycle Bin | All drives (emptied through the Shell API) | ✔ |
| | Broken Start menu shortcuts | Current-user and all-users Start menu | ✔ |
| | Broken desktop shortcuts | User and public desktop | |
| | IIS log files 🛡 | `inetpub\logs\LogFiles` | ✔ |
| | Old Prefetch data 🛡 | .pf files in `%WINDIR%\Prefetch` older than 30 days | ✔ |
| | Font cache 🛡 | FontCache service data | |
| | Downloaded program files | `Downloaded Program Files` | ✔ |
| | Windows Update 🛡 | `SoftwareDistribution\Download` | ✔ |
| | Delivery Optimization files 🛡 | `DeliveryOptimization\Cache` | ✔ |
| | Windows Installer temp files 🛡 | Top-level .tmp files in `%WINDIR%\Installer` | ✔ |
| | Previous Windows 🛡⚠ | `Windows.old` (deleted whole after takeown + icacls; you can no longer roll back) | |
| | Windows component store 🛡⚠ | `DISM /Online /Cleanup-Image /StartComponentCleanup` | |
| | Hibernation file 🛡⚠ | `hiberfil.sys` (about 40% of RAM); cleaning = `powercfg /hibernate off`, which also disables hibernation and Fast Startup | |
| Browsers | Internet Explorer | `INetCache` | ✔ |
| | Edge / Chrome / Brave cache | `Cache`, `Code Cache`, `GPUCache` of every profile | ✔ |
| | Firefox cache | `cache2` of every profile | ✔ |
| Applications | VS Code cache | `Code\Cache`, `CachedData`, `CachedExtensionVSIXs`, `logs`… | ✔ |
| | Visual Studio component cache | `VisualStudio\*\ComponentModelCache` | ✔ |
| | Microsoft Teams cache | Web cache of classic and new Teams | ✔ |
| | Discord / LINE cache | Web and image caches (chat history unaffected) | ✔ |
| | Spotify cache | `Spotify\Storage`, `Data` (songs downloaded for offline listening live here too) | |
| | GPU shader cache | `D3DSCache` and NVIDIA / AMD / Intel shader caches | ✔ |
| Developer | pip cache | `%LOCALAPPDATA%\pip\Cache` | ✔ |
| | NuGet HTTP cache | `NuGet\v3-cache`, `plugins-cache` | ✔ |
| | NuGet global packages ⚠ | `~\.nuget\packages` (re-downloaded on build; offline builds fail) | |
| | npm / Yarn cache | `npm-cache\_cacache`, `Yarn\Cache` | ✔ |
| | conda packages | .conda / .tar.bz2 in `anaconda3 / miniconda3…\pkgs` (same as `conda clean --tarballs`) | ✔ |
| | Python __pycache__ | `__pycache__` under the development project folders | ✔ |
| | .mypy_cache / .pytest_cache / .ruff_cache | Under the development project folders | ✔ |
| All drives | CHKDSK file fragments 🛡 | `FOUND.*\*.CHK` at the root of each local drive | |
| | Windows upgrade leftovers 🛡⚠ | `$WINDOWS.~BT`, `$Windows.~WS`, `$GetCurrent` at the root of each local drive (you can no longer roll back once removed) | |
| | Steam shader cache and temp | `steamapps\shadercache` and `temp` in every library (those registered in `libraryfolders.vdf`, plus `SteamLibrary` / `Steam` on each drive) | ✔ |
| | Unfinished Steam downloads ⚠ | `steamapps\downloading` in every library | |
| | macOS hidden files on removable drives | `._*`, `.DS_Store`, `.Spotlight-V100`, `.fseventsd`, `.Trashes` at the root of removable drives | |
| Privacy | File Explorer search and address history ⚠ | `Explorer\WordWheelQuery`, `TypedPaths` | |
| | Office recent files ⚠ | `File MRU` / `Place MRU` of every version and app, plus `Office\Recent` shortcuts | |
| | Clipboard ⚠ | Current clipboard content (Win+V history must be cleared in Windows Settings) | |
| | Browser history ⚠ | History, Top Sites, Shortcuts of Edge / Chrome / Brave (bookmarks are stored separately and unaffected; Firefox keeps history and bookmarks in one file, so it isn't cleaned) | |
| | Browser cookies ⚠ | Edge / Chrome / Brave / Firefox (you'll have to sign in again) | |
| | Recent files ⚠ | `Recent` and jump lists (the original files are unaffected) | |
| | Run history (Win+R) ⚠ | `HKCU\…\Explorer\RunMRU` | |

The page file (`pagefile.sys`) isn't a cleanup item; the health check only shows its size and where to configure it — turning it off can make programs crash outright when memory runs low.

## Community cleaning rules (winapp2.ini)

**Settings → Community cleaning rules** can download from GitHub or import from a file [winapp2.ini](https://github.com/MoscaDotTo/Winapp2)
(community rules shared by CCleaner / BleachBit, covering cache locations of thousands of programs). Imported items go into the "Community rules" category. For safety only a small subset is accepted:

- Only rules whose names indicate caches, logs, temp files, crash dumps, shaders, thumbnails or update / installer files; anything mentioning history, cookies, passwords, logins, settings,
  bookmarks, saves, cloud sync, mail, chat and similar is rejected
- Only programs that are detected (`Detect` / `DetectFile`); `RegKey` (registry deletion) is never executed
- Rules pointing at a drive root, Windows, Program Files, a user folder itself, event logs (`System32\winevt`)
  or `Windows\Installer` (other than .tmp) are skipped
- Imported items are unchecked by default, only clean files older than 24 hours, and are never touched by the scheduled cleanup, tray Quick clean or the command line's default scope

Tested against the 2026 winapp2.ini: of 4,068 rules, the 23 matching locally installed programs were accepted. winapp2.ini is licensed CC-BY-SA 4.0; it isn't bundled and is downloaded only when used.

## Command line

```
DiskSweeper.exe /list                          List all item IDs
DiskSweeper.exe /scan [/items:a,b]             Scan and show the count and size for each item
DiskSweeper.exe /clean [/items:a,b] /yes       Clean; without /yes it's a dry run that deletes nothing
  /recycle    send to Recycle Bin      /backup    back up to zip first      /out:file   also save results to a text file
```

- Without `/items`, the scope of the scheduled automatic cleanup is used (checked file-type items, excluding privacy and community rules). Unknown IDs are an error — nothing is guessed.
- Exit codes: 0 success, 1 errors occurred, 2 invalid arguments.
- The program is a GUI app that attaches to the caller's console: in cmd use `start /wait DiskSweeper.exe /scan`;
  in PowerShell put it in a pipeline (`DiskSweeper.exe /scan | Out-String`), otherwise PowerShell won't wait for it to finish.

## Safety design

- **Scanning and deleting are separate**: scanning is read-only; cleaning always asks first and deletes exactly the files listed on screen
- Browsers are **cache-only** by default; history and cookies are in the "Privacy" category, unchecked by default and confirmed when checked; passwords and bookmarks are never touched
- No registry cleaning: it saves no space and doesn't speed anything up, but can break programs
- Directory walks **never follow junctions or symbolic links**, so nothing outside the folder can be deleted
- Temp items only clean files older than 24 hours by default, to avoid deleting temp files a running program just wrote
- Per-drive items only look at a few fixed locations on each drive — never a recursive search of a whole drive — and empty-folder removal never runs at a drive root
- Files in use or without permission are skipped and counted; reserved names such as `nul` are deleted via `\\?\` paths
- A shortcut is broken only if its target is on a local fixed drive and definitely missing; shortcuts without a path (MSI advertised, shell items), and those on network or removable drives, are always treated as valid
- Custom folders can't be a drive root, Windows, Program Files or a user folder itself
- Runs as 64-bit (`Prefer32Bit=false`), so it doesn't see the redirected Program Files / System32 a 32-bit process would and misjudge them

## Files

```
DiskCleaner\
├── DiskCleaner.csproj   # net48 WinExe
├── app.manifest         # standard privileges, Per-Monitor V2 DPI
├── app.ico              # app icon (16–256, shared by exe, windows and tray)
├── Program.cs           # entry point, /auto and /lowspace scheduled modes (shared SafeClean), app icon
├── Cli.cs               # command-line mode (/list /scan /clean)
├── HealthCheck.cs       # PC health check: checks, score, gauge and list window
├── DiskHealth.cs        # disk health (SMART)
├── VhdxForm.cs          # finding and compacting WSL / Docker virtual disks
├── Winapp2.cs           # winapp2.ini parsing, safety filter, download and management window
├── MainForm.cs          # main window: menus, banner, task list (tri-state checks), file list, scan and clean flow
├── HeaderPanel.cs       # banner: gradient, animated counters, today/total pills, system drive card, busy shimmer
├── Theme.cs             # colors, fonts, menu renderer (ModernRenderer), cards (CardPanel)
├── RoundButton.cs       # rounded pill button (gradient, shadow, hover fade)
├── OverviewPanel.cs     # scan overview: category donut + top five items bars
├── DrivesPanel.cs       # drive cards on the main page (space on every drive)
├── Widgets.cs           # rounded search box, alert card, tree without horizontal scroll bar
├── MessageDialog.cs     # replacement for the system MessageBox (colored icon, pill buttons)
├── ToolWindow.cs        # shared tool window frame (gradient title bar, content, status bar)
├── Lang.cs              # UI language and font
├── en.txt / zh-CN.txt / ja.txt / ko.txt  # English, Simplified Chinese, Japanese and Korean dictionaries
├── Backup.cs            # backup before cleaning and restore, backup manager window
├── Startup.cs           # startup programs
├── ServicesForm.cs      # background services
├── ProcessForm.cs       # resource usage
├── Leftovers.cs         # install leftovers
├── SpaceTrend.cs        # disk space log and trend chart
├── RestorePoints.cs     # system restore points
├── CompressForm.cs      # NTFS folder compression
├── Shredder.cs          # file shredder
├── Cleaner.cs           # scanning and deletion engine
├── CleanItems.cs        # cleanup item definitions (Windows / Browsers / Applications / Developer / All drives / Privacy / Custom)
├── Analysis.cs          # folder size tree, duplicate files, empty folder search
├── AnalyzerForm.cs      # disk analysis tool window
├── TreemapPanel.cs      # treemap (squarified)
├── InstalledApps.cs     # installed programs: registry reading, uninstall command parsing, leftover entries, folder sizes
├── InstalledAppsForm.cs # installed programs window
├── Scheduler.cs         # Task Scheduler (schtasks)
├── NativeMethods.cs     # Recycle Bin (query, empty, send), shortcut targets, system icons, properties dialog
├── Dialogs.cs           # dialog frame (gradient title bar + white card), ignore list, custom folders, history, options
├── Settings.cs          # settings and history (%LOCALAPPDATA%\DiskCleaner)
├── Tests\DiskSweeper.Tests\  # xUnit tests
└── .github\workflows\build.yml  # GitHub Actions: build, test, upload executable
```

## Tests and CI

```
dotnet test Tests\DiskSweeper.Tests\DiskSweeper.Tests.csproj -c Release
```

Tests create and delete files in a temp folder and never touch real system folders. They cover: path wildcard expansion and per-drive tokens, strict extension matching,
**not following junctions** (files outside the junction must survive), read-only files, the ignore list, protected folders, custom folders,
automatic cleanup scope, winapp2.ini filtering, the English dictionary (format strings, column specs, trailing spaces), uninstall command parsing,
size formatting, health score, and backup and restore.

Every push to main or pull request runs GitHub Actions on Windows to build, run the tests and package `bin/Release/net48` as a downloadable artifact.

## License

MIT License, see [LICENSE](LICENSE). This program deletes files — review the scan results carefully before cleaning; the author isn't responsible for data loss.
