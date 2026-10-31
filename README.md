# 磁碟清理

[![build](https://github.com/wuling63/DiskSweeper/actions/workflows/build.yml/badge.svg)](https://github.com/wuling63/DiskSweeper/actions/workflows/build.yml)

中文 | [English](README.en.md)

C# / .NET Framework 4.8 WinForms 桌面程式，掃描並刪除系統暫存、快取、錯誤報告等可以安全移除的檔案。
版面仿一般的磁碟清理工具：上方綠色橫幅顯示今天／總計清理量與掃描結果，左邊是可勾選的工作清單，右邊是檔案明細。

## 建置與執行

```
dotnet build -c Release
bin\Release\net48\DiskSweeper.exe
```

執行檔叫 DiskSweeper.exe，不叫 DiskCleaner.exe，免得跟 Glary Utilities 的同名程式混淆。
平常以一般權限執行；系統項目（標盾牌圖示）要完整清理時，從「檔案 → 以系統管理員身分重新啟動」提權。

## 使用方式

1. 左邊勾選要清的項目（父節點可整組勾選，部分勾選會顯示半勾）
2. 按「掃瞄」：只列出檔案，不會刪；各項目後面會附上容量
3. 點左邊項目看右邊明細；不想刪的檔案選起來按「排除此檔案」（加入忽略清單）
4. 按「清理」：跳出確認視窗（寫明數量與大小、無法復原）後才刪除

| 位置 | 功能 |
|------|------|
| 檢視歷史記錄 | 每次清理的時間、檔案數、釋放空間 |
| 全部選取／全不選取／預設選取 | 左邊清單的勾選 |
| 自訂義檔案和資料夾 | 加入自己要清的資料夾與檔名樣式（如 `*.log;*.bak`），勾選＝含子資料夾 |
| 開啟資料夾／屬性 | 對右邊選中的檔案（雙擊也會開資料夾） |
| 排除此檔案 | 加進忽略清單，這次結果也拿掉 |
| 忽略清單 | 管理掃描時跳過的檔案與資料夾 |
| 搜尋路徑／點欄位標題 | 過濾與排序右邊清單；右鍵可開啟資料夾、屬性、複製路徑、排除 |
| 檔案 → 匯出掃描結果 | 全部結果存成 CSV（Excel 可直接開） |
| 工具 → 大檔案分析 | 資料夾大小樹＋樹狀圖（面積＝大小，點了往下鑽）＋最大的檔案 |
| 工具 → 重複檔案 | 內容完全相同（比雜湊）的檔案分組；可每組留最舊／最新一份，至少留一份才給刪 |
| 工具 → 空資料夾 | 底下完全沒檔案的資料夾（整棵空的只列最上層），預設不勾 |
| 工具 → 已安裝的程式 | 名稱、發行者、版本、安裝日期、安裝路徑、大小；可解除安裝、修改／修復、開資料夾（見下節） |
| 設定 → 選項 | 暫存保留時數、送進資源回收筒、開發專案資料夾、排程 |

工具視窗裡的刪除一律送進資源回收筒。系統磁碟剩不到 10% 時，主視窗橫幅下方會出現黃色提醒。

橫幅下方是**所有磁碟的卡片**（本機、抽取式、網路磁碟）：代號、名稱、可用／總容量與使用量長條，用量 80% 以上琥珀色、90% 以上紅色；每 30 秒與清理後自動更新，卡片多時可左右捲動（滾輪或兩端箭頭），點卡片會用「大檔案分析」分析那個磁碟。

## 工具選單

| 子選單 | 工具 | 說明 |
|------|------|------|
| （最上面） | 電腦健康檢查 | 一次檢查系統磁碟與其他磁碟空間、可清理的垃圾、資源回收筒、開機項目、磁碟健康、休眠檔、分頁檔、還原點、WSL／Docker 虛擬磁碟、安裝殘留，給 0～100 分（注意扣 6、需要處理扣 15），每項旁邊有按鈕直接開對應的工具。只讀，不會自己動任何東西 |
| 磁碟分析 | 大檔案分析／重複檔案／空資料夾 | 見上表 |
| | WSL／Docker 虛擬磁碟 | 找出 WSL 發行版（登錄 `Lxss`）與 Docker Desktop 的 `.vhdx`。Linux 裡刪檔不會讓 vhdx 變小，這裡先（可選）在 Linux 裡跑 `fstrim`、`wsl --shutdown`，再用 `diskpart` 的 `compact vdisk` 壓縮（Windows 家用版沒有 `Optimize-VHD` 也能用）。需要系統管理員；Docker Desktop 還開著時不給壓 |
| | 安裝殘留 | Program Files、ProgramData、AppData 第一層裡，已安裝程式（名稱、發行者、安裝路徑、名稱第一個字）、服務、啟動項目、開始選單捷徑、執行中程式都對不上，而且 180 天沒變動、大於 1 MB 的資料夾。名稱像快取／建置／更新程式的跳過。這是推測：預設不勾、刪除送資源回收筒 |
| | 磁碟空間趨勢 | 每天記一筆各固定磁碟的可用空間（開程式、排程清理、每日空間檢查時記，保留一年，存在 `space.json`），畫成折線圖，有清理的日子標實心點 |
| | 壓縮資料夾 | 用 `compact.exe` 做 NTFS 壓縮（標準／XPRESS 8K／XPRESS 16K／LZX），顯示壓縮前後的磁碟佔用；系統與使用者資料夾本身不給壓 |
| 系統 | 已安裝的程式 | 見下節 |
| | 開機啟動項目 | 登錄 Run（目前使用者／所有使用者 64、32 位元）、啟動資料夾、登入或開機時執行的排程工作（`\Microsoft` 底下的不列）。啟用／停用寫 `StartupApproved`，跟工作管理員同一套，不刪項目；指向程式已不存在的可以移除（登錄先備份成 .reg） |
| | 背景服務 | 預設只列非 Microsoft 的服務；系統管理員可以改啟動類型（`sc config`）、啟動／停止，每次都要確認，Microsoft 服務另外警告 |
| | 資源佔用 | 每 2 秒取樣，依 CPU 或記憶體（私有工作集）排序，可以結束處理程序 |
| | 磁碟健康（SMART） | 每顆實體磁碟的型號、類型、介面、容量、對應的磁碟代號與健康狀態（`MSFT_PhysicalDisk`）；溫度、SSD 已用壽命、開機時數、讀寫錯誤（`MSFT_StorageReliabilityCounter`）要系統管理員才讀得到 |
| | 系統還原點 | 列出還原點（WMI `SystemRestore`）與陰影複製佔用（`Win32_ShadowStorage`），可「只保留最新一個」或刪除選取（`SRRemoveRestorePoint`）；需要系統管理員 |
| 安全 | 檔案粉碎機 | 亂數覆寫 1／3／7 次、改亂碼檔名再刪；可拖放。SSD 因為損耗平均與 TRIM，覆寫不保證寫到原本的位置 |
| | 清理備份 | 列出清理前備份的 zip，可還原到原位（不覆蓋已存在的檔案）或刪除 |

## 外觀、語言、系統匣、備份（設定 → 選項）

- **外觀**：跟隨系統／淺色／深色，立即套用到所有開著的視窗；深色模式連標題列、捲軸、清單表頭都換成深色。
  跟隨系統時，Windows 切換淺色／深色會自動跟著換。
- **語言**：中文（繁體）／中文（简体）／日本語／한국어／English，重新啟動後生效；介面字型跟著換成各語言的 Windows UI 字型
  （正黑體／雅黑／Yu Gothic UI／Malgun Gothic）。原始碼裡的字串都包在 `Lang.T("…")`，各語言字典是內嵌的
  `en.txt`、`zh-CN.txt`、`ja.txt`、`ko.txt`（每行「繁體中文<Tab>譯文」，`\r \n \\` 跳脫、行尾空白寫成 `\s`）。
  格式字串先翻再填值，所以動態訊息也是整句翻好的。新增字串時要同步加進四個字典（測試會檢查四份句子一致），
  日文、韓文缺的句子先顯示英文。
- **系統匣**：最小化或關閉視窗時縮到系統匣；右鍵圖示可以「快速清理」（範圍同每週自動清理）、開回視窗、結束。
- **清理前備份**：要刪的檔案先壓成 `%LOCALAPPDATA%\DiskCleaner\Backup\yyyyMMdd_HHmmss.zip`，單檔超過 200 MB、
  或整批超過 2 GB 的部分不備份；保留天數可設（預設 7 天）。送進資源回收筒時不另外備份。

## 已安裝的程式

讀登錄裡的解除安裝清單（HKLM 64 位元、HKLM 32 位元 WOW6432Node、HKCU），跟「設定 → 應用程式」同一份來源。

| 欄位／操作 | 說明 |
|------|------|
| 大小（登錄） | 安裝程式自己填的 EstimatedSize，常常不準或沒填 |
| 資料夾大小 | 開視窗後在背景實際加總安裝資料夾（同一個資料夾只算一次） |
| 安裝路徑 | 登錄的 InstallLocation；沒寫時從圖示或解除安裝程式的位置推，標「（推測）」 |
| 解除安裝 | 確認後執行程式自己的解除安裝程式（需要時會跳 UAC），結束後自動重新整理。MSI 一律用 `msiexec /X{GUID}`（很多 MSI 登錄寫的是 `/I`，那是修改） |
| 修改／修復 | 有 ModifyPath 用它；MSI 開 `msiexec /I{GUID}` 維護模式 |
| 移除殘留項目 | 只對「解除安裝程式已經不在」的項目（清單裡灰字）。只刪登錄那一筆、不刪檔案，刪之前先 `reg export` 備份到 `%LOCALAPPDATA%\DiskCleaner\RegistryBackup`，雙擊 .reg 就能還原；所有使用者的項目要系統管理員 |
| 其他 | 搜尋（名稱／發行者／路徑）、點欄位排序、開啟安裝資料夾、發行者網站、複製資訊、匯出 CSV |

系統元件、Windows 更新與修補程式預設隱藏，勾「顯示系統元件與更新」才列出。
判斷殘留時，RunDll32 類的解除安裝命令看它載入的 DLL 在不在；判斷不出來一律當成正常，寧可漏掉也不誤標。
Microsoft Store 的 App 不在這份清單裡。

## 選項與排程

- **送進資源回收筒**：清理時不直接刪，刪錯可以還原（空間要等清空回收筒才釋放）。
  資源回收筒、Windows.old、元件存放區、執行紀錄不適用，一律直接清。
- **開發專案資料夾**：「Python __pycache__」「.mypy_cache／.pytest_cache／.ruff_cache」只在這些資料夾裡找，
  會跳過 `.git`、`node_modules`；刪空的快取資料夾本身也會收掉，但不會往上動到專案資料夾。
- **每週自動清理**：在 Windows 工作排程器建立 `DiskSweeper\AutoClean`，以 `DiskSweeper.exe /auto` 執行。
  用左邊勾選的項目，但**不清空資源回收筒、不碰隱私類、不跑 DISM 與 Windows.old**；一般權限執行。
  結果記進歷史，另寫一行到 `%LOCALAPPDATA%\DiskCleaner\auto.log`，並在系統匣跳通知。
- **每天檢查磁碟空間**：建立 `DiskSweeper\LowSpaceCheck`，每天 09:00 以 `/lowspace` 執行，
  系統磁碟剩不到 10% 才跳通知（點通知開啟本程式），空間夠就安靜結束。
- 排程用 `schtasks.exe` 以目前使用者建立，不需要系統管理員；取消勾選就刪掉對應的工作。

## 清理項目

🛡＝需要系統管理員權限才掃得完整（一般權限時只列得出可讀的部分）；⚠＝勾選時會跳出副作用說明。
「全部選取」不會勾 ⚠ 與隱私項目，那些要自己逐項勾。

| 分類 | 項目 | 位置 | 預設 |
|------|------|------|------|
| Windows | 系統暫存 🛡 | `%WINDIR%\Temp` | ✔ |
| | 使用者暫存 | `%TEMP%` | ✔ |
| | 略圖快取 | `Explorer\thumbcache_*.db`、`iconcache_*.db` | ✔ |
| | 記憶體傾印檔案 🛡 | `MEMORY.DMP`、`Minidump`、`LiveKernelReports`、`CrashDumps` | ✔ |
| | 錯誤報告檔案 | WER 的 ReportArchive／ReportQueue | ✔ |
| | 系統日誌檔案 🛡 | `%WINDIR%\Logs` 的 .log／.cab、`%WINDIR%\*.log` | ✔ |
| | 遠端桌面快取 | `Terminal Server Client\Cache` | |
| | 資源回收筒 | 所有磁碟（Shell API 清空） | ✔ |
| | 開始選單無效的捷徑 | 使用者與所有使用者的開始選單 | ✔ |
| | 桌面無效的捷徑 | 使用者與公用桌面 | |
| | IIS 日誌檔案 🛡 | `inetpub\logs\LogFiles` | ✔ |
| | 舊的預讀取數據 🛡 | `%WINDIR%\Prefetch` 超過 30 天的 .pf | ✔ |
| | 字型快取 🛡 | FontCache 服務資料 | |
| | 下載的程式 | `Downloaded Program Files` | ✔ |
| | Windows 更新 🛡 | `SoftwareDistribution\Download` | ✔ |
| | 傳遞最佳化檔案 🛡 | `DeliveryOptimization\Cache` | ✔ |
| | Windows 安裝程式暫存檔 🛡 | `%WINDIR%\Installer` 最上層的 .tmp | ✔ |
| | 舊版 Windows 🛡⚠ | `Windows.old`（takeown＋icacls 後整個刪，刪了不能退回舊版） | |
| | Windows 元件存放區 🛡⚠ | `DISM /Online /Cleanup-Image /StartComponentCleanup` | |
| | 休眠檔 🛡⚠ | `hiberfil.sys`（大小約記憶體的四成）；清理＝`powercfg /hibernate off`，同時關掉休眠與快速啟動 | |
| 瀏覽器 | Internet Explorer | `INetCache` | ✔ |
| | Edge／Chrome／Brave 快取 | 各設定檔的 `Cache`、`Code Cache`、`GPUCache` | ✔ |
| | Firefox 快取 | 各設定檔的 `cache2` | ✔ |
| 應用程式 | VS Code 快取 | `Code\Cache`、`CachedData`、`CachedExtensionVSIXs`、`logs`… | ✔ |
| | Visual Studio 元件快取 | `VisualStudio\*\ComponentModelCache` | ✔ |
| | Microsoft Teams 快取 | 傳統版與新版的網頁快取 | ✔ |
| | Discord／LINE 快取 | 網頁與圖片快取（不影響聊天紀錄） | ✔ |
| | Spotify 快取 | `Spotify\Storage`、`Data`（離線下載的歌也在這） | |
| | 顯示卡著色器快取 | `D3DSCache`、NVIDIA／AMD／Intel 的著色器快取 | ✔ |
| 開發者 | pip 快取 | `%LOCALAPPDATA%\pip\Cache` | ✔ |
| | NuGet HTTP 快取 | `NuGet\v3-cache`、`plugins-cache` | ✔ |
| | NuGet 全域套件 ⚠ | `~\.nuget\packages`（建置時重新下載，離線無法建置） | |
| | npm／Yarn 快取 | `npm-cache\_cacache`、`Yarn\Cache` | ✔ |
| | conda 安裝包 | `anaconda3／miniconda3…\pkgs` 的 .conda／.tar.bz2（同 `conda clean --tarballs`） | ✔ |
| | Python __pycache__ | 開發專案資料夾底下的 `__pycache__` | ✔ |
| | .mypy_cache／.pytest_cache／.ruff_cache | 開發專案資料夾底下 | ✔ |
| 各磁碟 | 磁碟檢查留下的檔案片段 🛡 | 各本機磁碟根目錄 `FOUND.*\*.CHK` | |
| | Windows 升級暫存 🛡⚠ | 各本機磁碟根目錄的 `$WINDOWS.~BT`、`$Windows.~WS`、`$GetCurrent`（清了不能退回舊版） | |
| | Steam 著色器快取與暫存 | 各遊戲庫（`libraryfolders.vdf` 登記的，加上各磁碟的 `SteamLibrary`／`Steam`）的 `steamapps\shadercache`、`temp` | ✔ |
| | Steam 未完成的下載 ⚠ | 各遊戲庫的 `steamapps\downloading` | |
| | 隨身碟上的 macOS 隱藏檔 | 抽取式磁碟根目錄的 `._*`、`.DS_Store`、`.Spotlight-V100`、`.fseventsd`、`.Trashes` | |
| 隱私 | 檔案總管搜尋與網址列紀錄 ⚠ | `Explorer\WordWheelQuery`、`TypedPaths` | |
| | Office 最近使用的檔案 ⚠ | 各版本、各程式的 `File MRU`／`Place MRU`，以及 `Office\Recent` 捷徑 | |
| | 剪貼簿 ⚠ | 目前複製的內容（Win+V 歷程記錄要在 Windows 設定裡清） | |
| | 瀏覽器歷史紀錄 ⚠ | Edge／Chrome／Brave 的 History、Top Sites、Shortcuts（書籤另存不受影響；Firefox 歷史與書籤同檔，不清） | |
| | 瀏覽器 Cookie ⚠ | Edge／Chrome／Brave／Firefox（清了要重新登入） | |
| | 最近使用的檔案 ⚠ | `Recent` 與跳躍清單（原檔不受影響） | |
| | 執行紀錄（Win+R）⚠ | `HKCU\…\Explorer\RunMRU` | |

分頁檔（`pagefile.sys`）不列成清理項目，只在健康檢查裡顯示大小與設定位置：關掉分頁檔可能讓程式在記憶體不足時直接當掉。

## 社群清理規則（winapp2.ini）

「設定 → 社群清理規則」可以從 GitHub 下載或從檔案匯入 [winapp2.ini](https://github.com/MoscaDotTo/Winapp2)
（CCleaner／BleachBit 共用的社群規則，數千個程式的快取位置），匯入的項目放在「社群規則」分類。為了安全只收一小部分：

- 只收名稱是快取、日誌、暫存、當機傾印、著色器、縮圖、更新／安裝檔類的規則；名稱帶歷史、Cookie、密碼、登入、設定、
  書籤、存檔、雲端同步、郵件、聊天等字樣的一律不收
- 只收偵測得到（`Detect`／`DetectFile`）的程式；`RegKey`（刪登錄）完全不執行
- 規則指到磁碟根目錄、Windows、Program Files、使用者資料夾本身、事件記錄（`System32\winevt`）、
  `Windows\Installer`（.tmp 以外）的一律略過
- 匯入的項目預設不勾、只清 24 小時前的檔案，排程自動清理、系統匣快速清理、命令列預設範圍都不碰

以 2026 年版的 winapp2.ini 測試：4,068 條規則裡收進本機有安裝的 23 條。winapp2.ini 以 CC-BY-SA 4.0 授權，程式不內附，使用時才下載。

## 命令列

```
DiskSweeper.exe /list                          列出所有項目代號
DiskSweeper.exe /scan [/items:a,b]             掃描並列出每項可清理的數量與大小
DiskSweeper.exe /clean [/items:a,b] /yes       清理；沒有 /yes 只預演，不刪任何東西
  /recycle    送進資源回收筒      /backup    先備份成 zip      /out:檔案   結果另存成文字檔
```

- 沒給 `/items` 時，用「排程自動清理」的範圍（左邊勾選的檔案類項目，不含隱私與社群規則）。不認得的代號直接報錯，不會猜。
- 結束代碼：0 成功、1 有錯誤、2 參數錯誤。
- 程式本身是視窗程式，輸出接到呼叫者的主控台：cmd 裡請用 `start /wait DiskSweeper.exe /scan`；
  PowerShell 要放在管線裡（`DiskSweeper.exe /scan | Out-String`），否則 PowerShell 不會等它結束。

## 安全設計

- **掃描與刪除分開**：掃描只讀；清理前一定確認，刪的就是畫面上列出的那些檔案
- 瀏覽器預設**只清快取**；歷史紀錄與 Cookie 在「隱私」分類，預設不勾、勾選要確認；密碼與書籤一律不碰
- 不做登錄檔清理：省不了空間、對速度沒幫助，卻可能讓程式壞掉
- 走目錄時**不跟進 junction／符號連結**，不會繞到資料夾外面去刪
- 暫存類預設只清 24 小時前的檔案，避免刪到執行中程式剛寫的暫存
- 「各磁碟」項目每顆磁碟只看固定幾個位置，不整顆磁碟遞迴搜尋；收空資料夾時不處理磁碟根目錄
- 使用中、沒權限的檔案直接略過並計數；檔名是 `nul` 這類保留字時改用 `\\?\` 路徑刪
- 無效捷徑只認「目標在本機固定磁碟、而且確定不存在」；沒有路徑的捷徑（MSI 宣告式、殼層項目）、網路磁碟、隨身碟上的一律當有效
- 自訂資料夾不能選磁碟根目錄、Windows、Program Files、使用者資料夾本身
- 以 64 位元執行（`Prefer32Bit=false`），避免 32 位元程式看到被轉向的 Program Files／System32 而誤判

## 檔案

```
DiskCleaner\
├── DiskCleaner.csproj   # net48 WinExe
├── app.manifest         # 一般權限、Per-Monitor V2 DPI
├── app.ico              # 程式圖示（16～256，執行檔、視窗、系統匣共用）
├── Program.cs           # 進入點、/auto 與 /lowspace 排程模式（共用 SafeClean）、程式圖示
├── Cli.cs               # 命令列模式（/list /scan /clean）
├── HealthCheck.cs       # 電腦健康檢查：檢查項目、分數、儀表與清單視窗
├── DiskHealth.cs        # 磁碟健康（SMART）
├── VhdxForm.cs          # WSL／Docker 虛擬磁碟的搜尋與壓縮
├── Winapp2.cs           # winapp2.ini 解析、安全過濾、下載與管理視窗
├── MainForm.cs          # 主視窗：選單、橫幅、工作清單（三態勾選）、檔案清單、掃描與清理流程
├── HeaderPanel.cs       # 橫幅：漸層、跳動計數、今天／總計膠囊、系統磁碟卡片、忙碌流光
├── Theme.cs             # 配色、字型、選單配色（ModernRenderer）、卡片（CardPanel）
├── RoundButton.cs       # 圓角膠囊按鈕（漸層、陰影、滑過漸變）
├── OverviewPanel.cs     # 掃描結果總覽：分類環圈圖＋前五大項目長條
├── DrivesPanel.cs       # 主頁的磁碟卡片列（所有磁碟的空間）
├── Widgets.cs           # 圓角搜尋框、警示卡、無水平捲軸的樹
├── MessageDialog.cs     # 取代系統 MessageBox 的訊息視窗（彩色圖示、膠囊按鈕）
├── ToolWindow.cs        # 工具視窗共用外框（漸層標題列、內容、狀態列）
├── Lang.cs              # 介面語言與字型
├── en.txt / zh-CN.txt / ja.txt / ko.txt  # 英文、簡中、日文、韓文字典
├── Backup.cs            # 清理前備份與還原、備份管理視窗
├── Startup.cs           # 開機啟動項目
├── ServicesForm.cs      # 背景服務
├── ProcessForm.cs       # 資源佔用
├── Leftovers.cs         # 安裝殘留
├── SpaceTrend.cs        # 磁碟空間紀錄與趨勢圖
├── RestorePoints.cs     # 系統還原點
├── CompressForm.cs      # NTFS 壓縮資料夾
├── Shredder.cs          # 檔案粉碎機
├── Cleaner.cs           # 掃描與刪除的引擎
├── CleanItems.cs        # 清理項目定義（Windows／瀏覽器／應用程式／開發者／各磁碟／隱私／自訂）
├── Analysis.cs          # 資料夾大小樹、重複檔案、空資料夾的搜尋
├── AnalyzerForm.cs      # 磁碟分析工具視窗
├── TreemapPanel.cs      # 樹狀圖（squarified）
├── InstalledApps.cs     # 已安裝程式：讀登錄、拆解除安裝命令、移除殘留項目、資料夾大小
├── InstalledAppsForm.cs # 已安裝的程式視窗
├── Scheduler.cs         # 工作排程器（schtasks）
├── NativeMethods.cs     # 資源回收筒（查詢、清空、送入）、捷徑目標、系統圖示、內容對話框
├── Dialogs.cs           # 對話框外框（漸層標題列＋白卡片）、忽略清單、自訂資料夾、歷史記錄、選項
├── Settings.cs          # 設定與歷史（%LOCALAPPDATA%\DiskCleaner）
├── Tests\DiskSweeper.Tests\  # xUnit 測試
└── .github\workflows\build.yml  # GitHub Actions：建置、測試、上傳執行檔
```

## 測試與 CI

```
dotnet test Tests\DiskSweeper.Tests\DiskSweeper.Tests.csproj -c Release
```

測試都在暫存資料夾裡建檔再刪，不碰真實的系統資料夾。涵蓋：路徑萬用字元與磁碟代號展開、副檔名嚴格比對、
**不跟進 junction**（junction 指到的外部檔案必須還在）、唯讀檔、忽略清單、受保護資料夾、自訂資料夾、
自動清理範圍、winapp2.ini 的過濾規則、英文字典（格式字串、欄位規格、行尾空白）、解除安裝命令拆解、
容量格式、健康分數、備份與還原。

每次推送到 main 或開 PR，GitHub Actions 會在 Windows 上建置、跑測試，並把 `bin/Release/net48` 打包成 artifact 供下載。

## 授權

MIT License，見 [LICENSE](LICENSE)。本程式會刪除檔案，請先看清楚掃描結果再清理；作者不對資料遺失負責。
