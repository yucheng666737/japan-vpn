# Japan VPN (Android)

專為連線**日本節點**設計的免費開源 Android OpenVPN 應用程式（零廣告、無追蹤）。

## 主要功能

- **自動抓取日本 VPN 節點**：透過 HTTPS 連線 VPN Gate 官方公開 API（強制 `usesCleartextTraffic="false"`），自動篩選日本（`JP`）節點並依速度、延遲與評分排序。
- **強制排除 `219.` 開頭 IP**：同時過濾清單 IP 與 OpenVPN `remote` 設定，避開常被遊戲封鎖的 `219.x.x.x` 網段。
- **無法連線／斷線自動切換其他節點**：
  - 連線前 2.5 秒 TCP 快速連通預檢，自動跳過無回應節點。
  - 15 秒連線逾時、驗證失敗、伺服器中斷或 `RECONNECTING` 超過 5 秒即自動切換下一個可用日本節點。
  - 失敗節點自動進入 10 分鐘冷卻，若清單耗盡自動重新抓取最新節點。
- **主動防假連線／斷流健康檢查**：連線後每 10 秒透過雙 HTTPS 端點輕量檢測，連續 2 次無回應自動換線；手動操作即時搶佔不衝突。
- **通知列一鍵換下一個節點**：不用離開遊戲畫面，下拉通知列即可點選「換下一個節點」或「中斷連線」。
- **背景隨時自動抓取新節點**：每 3 分鐘背景自動刷新備用節點池（不中斷現有 VPN），切回 App 自動檢查更新。
- **首次開啟電池無限制引導提示**：協助一鍵設定略過電池最佳化，避免背景自動換線被系統省電中斷。

## 下載安裝

請前往本專案的 **[Releases](../../releases)** 頁面下載最新版 `JapanVPN-v*.apk` 直接安裝於 Android 7.0+ 手機。

## 自行編譯

1. 安裝 JDK 17 與 Android SDK（API 35）。
2. 設定 `local.properties` 指向您的 Android SDK 路徑。
3. 執行 Gradle 建置：
   ```bash
   ./gradlew :app:assembleRelease
   ```
   *(若未提供 `keystore.properties`，將自動以 debug 簽章建置。)*

## 授權聲明

本專案內含衍生自 Arne Schwabe 之 [ics-openvpn](https://github.com/schwabe/ics-openvpn)（透過 `vpnLib` 模組）的 OpenVPN 核心元件，遵循 **GNU General Public License v2 (GPLv2)** 授權條款。
