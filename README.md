# Japan VPN（Android / Windows / Chrome 擴充功能）

專為連線**日本節點**設計的免費開源跨平台代理與 VPN 工具（零廣告、無追蹤）。

---

## 三大版本比較與選擇

| 版本 | 適用情境 | 運作協定 | 檔案下載（Releases） |
|---|---|---|---|
| **Android 手機版** | 手機遊戲（如賽馬娘日版）、手機瀏覽日本網頁 | VPN Gate（OpenVPN 全機通道） | `JapanVPN-v1.2.2.apk` |
| **Windows 電腦版** | 電腦版遊戲（DMM Game Player 等桌面軟體）＋全電腦上網 | VPN Gate（OpenVPN 全機通道） | `JapanVPN-Windows-v1.0.0.exe` |
| **Chrome 插件版** | 僅在 Chrome 瀏覽器中逛日本網站（免管理員權限） | Chrome Proxy API（日本 SOCKS5 / HTTPS / HTTP 代理） | `JapanVPN-Chrome-v1.0.0.zip` |

> ⚠️ **技術差異說明**：Chrome 擴充功能受限於瀏覽器沙盒，無法建立系統虛擬網卡或執行 OpenVPN 協定，因此**僅代理 Chrome 瀏覽器內的網頁流量**；若您需要加速 **DMM 等電腦遊戲或桌面軟體**，請使用 **Windows 桌面版**（`JapanVPN-Windows-v1.0.0.exe`）。

---

## 核心共同特色

- **全自動抓取最新日本節點**：使用 HTTPS 來源自動抓取代號 `JP` 的日本伺服器並依品質排序，背景定時更新不中斷現有連線。
- **強制排除 `219.` 開頭 IP**：嚴格過濾所有 `219.x.x.x` 網段（含設定檔內的實際連線主機）。
- **失敗／斷線／假連線自動切換**：
  - 連線前快速連通檢測，跳過無回應節點。
  - 逾時、驗證失敗、斷線或主動健康檢查連續無回應時，自動切換至下一個可用日本節點。
  - 手動點擊「切換」或「斷開」具最高優先權，立即搶佔並停止背景重連。

---

## 安裝與使用方式

請前往本專案的 **[Releases](../../releases)** 頁面下載對應檔案：

### 1. Android 手機版（`JapanVPN-v1.2.2.apk`）
1. 下載 APK 至 Android 7.0+ 手機並安裝。
2. 首次開啟依提示允許 VPN 權限、通知權限與「電池無限制」保護。
3. 連線中下拉通知列可直接點擊「**換下一個節點**」或「**中斷連線**」（從系統 VPN 狀態欄斷開亦不會誤觸自動重連）。

### 2. Windows 桌面版（`JapanVPN-Windows-v1.0.0.exe`）
1. 下載 `JapanVPN-Windows-v1.0.0.exe` 並執行（獨立單一執行檔，免安裝 .NET；啟動時需同意系統管理員權限以建立 VPN 虛擬網卡）。
2. 若電腦尚未安裝 OpenVPN 核心，視窗上方會自動顯示提示，點擊「**一鍵安裝 OpenVPN**」即可自動安裝。
3. 關閉視窗會自動縮小至右下角系統匣繼續連線，右鍵系統匣圖示可一鍵「**換下一個日本節點**」或「**結束程式**」。

### 3. Chrome 擴充功能版（`JapanVPN-Chrome-v1.0.0.zip`）
1. 下載並解壓縮 `JapanVPN-Chrome-v1.0.0.zip` 到任意資料夾（資料夾內需直接包含 `manifest.json`）。
2. 在 Chrome 網址列開啟 `chrome://extensions/`，打開右上角「**開發人員模式**」。
3. 點選「**載入未封裝項目**」，選擇剛剛解壓縮的資料夾即可使用。

---

## 自行編譯

- **Android**：使用 JDK 17 與 Android SDK（API 35），設定 `local.properties` 後執行 `./gradlew :app:assembleRelease`。
- **Windows**：安裝 .NET 10 SDK，於 `windows-app/` 目錄執行：
  ```powershell
  dotnet publish JapanVPN.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
  ```
- **Chrome 擴充功能**：直接使用 `chrome-extension/` 目錄載入 Chrome 開發人員模式即可。

---

## 授權聲明

本專案內之 Android OpenVPN 核心元件（`vpnLib` 模組）衍生自 Arne Schwabe 之 [ics-openvpn](https://github.com/schwabe/ics-openvpn)，遵循 **GNU General Public License v2 (GPLv2)** 授權條款。
