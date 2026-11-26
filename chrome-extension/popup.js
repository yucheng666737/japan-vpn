const STATE_KEY = "jpvpn_state";

const mainView = document.getElementById("main-view");
const serversView = document.getElementById("servers-view");
const logModal = document.getElementById("log-modal");

const btnPower = document.getElementById("btn-power");
const powerHalo = document.getElementById("power-halo");
const statusTitle = document.getElementById("status-title");
const statusDetail = document.getElementById("status-detail");
const timerEl = document.getElementById("timer");
const selectedServerMeta = document.getElementById("selected-server-meta");
const filterSummary = document.getElementById("filter-summary");
const refreshSub = document.getElementById("refresh-sub");

const chkFailover = document.getElementById("chk-failover");
const chkAutoRefresh = document.getElementById("chk-autorefresh");
const chkHealth = document.getElementById("chk-health");

const btnSwitchNext = document.getElementById("btn-switch-next");
const btnRefreshMain = document.getElementById("btn-refresh-main");
const btnRefreshList = document.getElementById("btn-refresh-list");
const btnOpenServers = document.getElementById("btn-open-servers");
const btnBack = document.getElementById("btn-back");
const btnTheme = document.getElementById("btn-theme");
const btnLogs = document.getElementById("btn-logs");
const btnCloseLogs = document.getElementById("btn-close-logs");
const btnClearLogs = document.getElementById("btn-clear-logs");

const searchInput = document.getElementById("search-input");
const serverListEl = document.getElementById("server-list");
const pillAvail = document.getElementById("pill-avail");
const pillExcluded = document.getElementById("pill-excluded");
const logContent = document.getElementById("log-content");

let currentState = null;

function formatDuration(totalSeconds) {
  const s = Math.max(0, Math.floor(totalSeconds));
  const hh = String(Math.floor(s / 3600)).padStart(2, "0");
  const mm = String(Math.floor((s % 3600) / 60)).padStart(2, "0");
  const ss = String(s % 60).padStart(2, "0");
  return `${hh}:${mm}:${ss}`;
}

function formatClock(ts) {
  if (!ts) return "尚未更新";
  const d = new Date(ts);
  const hh = String(d.getHours()).padStart(2, "0");
  const mm = String(d.getMinutes()).padStart(2, "0");
  const ss = String(d.getSeconds()).padStart(2, "0");
  return `${hh}:${mm}:${ss}`;
}

function buildSignalSvg(bars) {
  const color = bars >= 4 ? "#22C55E" : bars >= 2 ? "#F59E0B" : "#EF4444";
  const muted = "rgba(156,163,175,0.35)";
  const heights = [6, 9, 12, 16, 20];
  const rects = heights
    .map((h, i) => {
      const x = i * 6;
      const y = 20 - h;
      return `<rect x="${x}" y="${y}" width="4" height="${h}" rx="1.5" fill="${i < bars ? color : muted}"/>`;
    })
    .join("");
  return `<svg width="28" height="20" viewBox="0 0 28 20">${rects}</svg>`;
}

function updateTimerOnly() {
  if (currentState?.phase === "CONNECTED" && currentState.connectedAt > 0) {
    const secs = (Date.now() - currentState.connectedAt) / 1000;
    timerEl.textContent = formatDuration(secs);
  } else {
    timerEl.textContent = "00:00:00";
  }
}

function render(state) {
  currentState = state;
  if (!state) return;

  document.body.classList.toggle("dark", Boolean(state.darkTheme));

  const isConnected = state.phase === "CONNECTED";
  const isBusy =
    state.phase === "FETCHING" ||
    state.phase === "CONNECTING" ||
    state.phase === "SWITCHING";

  btnPower.classList.toggle("connected", isConnected);
  btnPower.classList.toggle("busy", isBusy);
  powerHalo.classList.toggle("connected", isConnected);
  powerHalo.classList.toggle("busy", isBusy);

  statusTitle.classList.toggle("connected", isConnected);
  statusTitle.classList.toggle("busy", isBusy);
  statusTitle.textContent = state.statusTitle || "未連線";

  const detail = state.attemptProgress
    ? `${state.statusDetail} (${state.attemptProgress})`
    : state.statusDetail;
  statusDetail.textContent = detail || "";

  btnRefreshMain.classList.toggle("spinning", Boolean(state.isRefreshing));
  btnRefreshList.classList.toggle("spinning", Boolean(state.isRefreshing));

  const servers = state.servers || [];
  const selected =
    state.activeServer ||
    servers.find((s) => s.key === state.selectedKey) ||
    servers[0] ||
    null;

  if (selected) {
    selectedServerMeta.textContent = `${selected.ip}:${selected.port} • ${selected.scheme.toUpperCase()} • ${selected.city}`;
  } else {
    selectedServerMeta.textContent = state.isRefreshing
      ? "正在抓取日本節點..."
      : "點擊重新抓取日本代理";
  }

  filterSummary.textContent = `已自動排除 ${state.excluded219Count || 0} 個 219 開頭 • 可用 ${servers.length} 個日本代理`;
  refreshSub.textContent = `每 5 分鐘自動更新（最後更新：${formatClock(state.lastFetchedAt)}）`;

  chkFailover.checked = Boolean(state.autoFailoverEnabled);
  chkAutoRefresh.checked = Boolean(state.autoRefreshEnabled);
  chkHealth.checked = Boolean(state.healthCheckEnabled);

  btnSwitchNext.classList.toggle("hidden", !(isConnected || isBusy));

  updateTimerOnly();
  renderServerList(state);
  renderLogs(state);
}

function renderServerList(state) {
  const servers = state.servers || [];
  const query = (searchInput.value || "").trim().toLowerCase();
  const filtered = query
    ? servers.filter(
        (s) =>
          s.ip.toLowerCase().includes(query) ||
          s.scheme.toLowerCase().includes(query) ||
          s.city.toLowerCase().includes(query)
      )
    : servers;

  pillAvail.textContent = `日本免費代理 (${filtered.length})`;
  pillExcluded.textContent = `已排除 219 開頭 (${state.excluded219Count || 0})`;

  serverListEl.innerHTML = "";
  if (filtered.length === 0) {
    const empty = document.createElement("div");
    empty.className = "status-detail";
    empty.style.padding = "28px 0";
    empty.style.textAlign = "center";
    empty.textContent = state.isRefreshing
      ? "正在抓取日本代理節點..."
      : "找不到符合的日本節點，請按右上角重新整理";
    serverListEl.appendChild(empty);
    return;
  }

  for (const s of filtered) {
    const isCurrent =
      state.activeServer?.key === s.key && state.phase !== "DISCONNECTED";
    const isConnected = isCurrent && state.phase === "CONNECTED";
    const isFailed = Boolean(state.failedMap?.[s.key]);

    const btn = document.createElement("button");
    btn.className = `server-item${isCurrent ? " current" : ""}${isConnected ? " connected" : ""}`;
    btn.innerHTML = `
      <div>
        <div class="server-item-top">
          <strong>Japan</strong>
          <span class="badge">${s.scheme.toUpperCase()}</span>
          ${isConnected ? '<span class="badge ok">已連線</span>' : isFailed ? '<span class="badge fail">近期失敗</span>' : ""}
        </div>
        <div class="server-ip">${s.ip}:${s.port}</div>
        <div class="server-meta">${s.city} • ${s.latency}ms • 穩定度 ${s.uptime}%</div>
      </div>
      ${buildSignalSvg(s.signalBars)}
    `;
    btn.addEventListener("click", () => {
      serversView.classList.add("hidden");
      mainView.classList.remove("hidden");
      chrome.runtime.sendMessage({ type: "CONNECT_SERVER", server: s });
    });
    serverListEl.appendChild(btn);
  }
}

function renderLogs(state) {
  const logs = state.logs || [];
  if (logs.length === 0) {
    logContent.textContent = "尚無連線紀錄";
    return;
  }
  logContent.textContent = [...logs].reverse().join("\n");
}

btnPower.addEventListener("click", () => {
  if (!currentState) return;
  if (currentState.phase === "DISCONNECTED") {
    chrome.runtime.sendMessage({ type: "CONNECT_AUTO" });
  } else {
    chrome.runtime.sendMessage({ type: "DISCONNECT" });
  }
});

btnSwitchNext.addEventListener("click", () => {
  chrome.runtime.sendMessage({ type: "SWITCH_NEXT" });
});

btnRefreshMain.addEventListener("click", () => {
  chrome.runtime.sendMessage({ type: "REFRESH_SERVERS" });
});

btnRefreshList.addEventListener("click", () => {
  chrome.runtime.sendMessage({ type: "REFRESH_SERVERS" });
});

btnOpenServers.addEventListener("click", () => {
  mainView.classList.add("hidden");
  serversView.classList.remove("hidden");
});

btnBack.addEventListener("click", () => {
  serversView.classList.add("hidden");
  mainView.classList.remove("hidden");
});

btnTheme.addEventListener("click", () => {
  chrome.runtime.sendMessage({
    type: "UPDATE_SETTINGS",
    darkTheme: !currentState?.darkTheme
  });
});

chkFailover.addEventListener("change", () => {
  chrome.runtime.sendMessage({
    type: "UPDATE_SETTINGS",
    autoFailoverEnabled: chkFailover.checked
  });
});

chkAutoRefresh.addEventListener("change", () => {
  chrome.runtime.sendMessage({
    type: "UPDATE_SETTINGS",
    autoRefreshEnabled: chkAutoRefresh.checked
  });
});

chkHealth.addEventListener("change", () => {
  chrome.runtime.sendMessage({
    type: "UPDATE_SETTINGS",
    healthCheckEnabled: chkHealth.checked
  });
});

searchInput.addEventListener("input", () => {
  if (currentState) renderServerList(currentState);
});

btnLogs.addEventListener("click", () => {
  logModal.classList.remove("hidden");
});

btnCloseLogs.addEventListener("click", () => {
  logModal.classList.add("hidden");
});

btnClearLogs.addEventListener("click", () => {
  chrome.runtime.sendMessage({ type: "CLEAR_LOGS" });
});

chrome.storage.onChanged.addListener((changes, areaName) => {
  if (areaName === "local" && changes[STATE_KEY]?.newValue) {
    render(changes[STATE_KEY].newValue);
  }
});

chrome.runtime.sendMessage({ type: "GET_STATE" }, (res) => {
  if (res?.ok && res.state) {
    render(res.state);
  }
});

setInterval(updateTimerOnly, 1000);
