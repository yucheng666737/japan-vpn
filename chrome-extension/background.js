const STATE_KEY = "jpvpn_state";
const ALARM_HEALTH_CHECK = "jpvpn_health_check";
const ALARM_AUTO_REFRESH = "jpvpn_auto_refresh";

const FAILED_COOLDOWN_MS = 10 * 60 * 1000;
const STALE_REFRESH_MS = 5 * 60 * 1000;
const PROBE_CONNECT_TIMEOUT_MS = 5500;
const PROBE_QUICK_TIMEOUT_MS = 4000;
const MAX_LOGS = 120;

const BYPASS_LIST = [
  "<local>",
  "localhost",
  "127.0.0.1",
  "::1",
  "api.proxyscrape.com",
  "proxylist.geonode.com"
];

const PROBE_URLS = [
  "https://connectivitycheck.gstatic.com/generate_204",
  "https://cp.cloudflare.com/generate_204"
];

const DEFAULT_STATE = {
  phase: "DISCONNECTED",
  statusTitle: "未連線",
  statusDetail: "點擊電源鍵自動連線最佳日本代理",
  attemptProgress: "",
  userWantsConnected: false,
  sessionId: 0,
  servers: [],
  selectedKey: null,
  activeServer: null,
  totalJapanCount: 0,
  excluded219Count: 0,
  failedMap: {},
  lastFetchedAt: 0,
  connectedAt: 0,
  isRefreshing: false,
  autoFailoverEnabled: true,
  autoRefreshEnabled: true,
  healthCheckEnabled: true,
  darkTheme: false,
  logs: []
};

let cachedState = null;
let stateWriteChain = Promise.resolve();
let activeRunSessionId = 0;
let lastProxyErrorCheckAt = 0;
let proxyErrorCheckInFlight = false;

function isBlocked219Ip(ip) {
  return String(ip || "").trim().startsWith("219.");
}

function isValidIpv4(ip) {
  const parts = String(ip || "").trim().split(".");
  if (parts.length !== 4) return false;
  return parts.every((p) => {
    if (!/^\d{1,3}$/.test(p)) return false;
    const n = Number(p);
    return n >= 0 && n <= 255;
  });
}

function formatTimeNow() {
  const d = new Date();
  const hh = String(d.getHours()).padStart(2, "0");
  const mm = String(d.getMinutes()).padStart(2, "0");
  const ss = String(d.getSeconds()).padStart(2, "0");
  return `${hh}:${mm}:${ss}`;
}

function computeSignalBars(latency, uptime) {
  let pts = 0;
  if (latency > 0 && latency <= 600) pts += 3;
  else if (latency <= 1800) pts += 2;
  else if (latency <= 3500) pts += 1;

  if (uptime >= 80) pts += 2;
  else if (uptime >= 50) pts += 1;

  return Math.max(1, Math.min(5, pts));
}

function pruneFailedMap(failedMap) {
  const now = Date.now();
  const next = {};
  for (const [k, ts] of Object.entries(failedMap || {})) {
    if (now - ts < FAILED_COOLDOWN_MS) {
      next[k] = ts;
    }
  }
  return next;
}

async function loadState() {
  if (cachedState) return cachedState;
  const res = await chrome.storage.local.get(STATE_KEY);
  cachedState = { ...DEFAULT_STATE, ...(res[STATE_KEY] || {}) };
  cachedState.failedMap = pruneFailedMap(cachedState.failedMap);
  activeRunSessionId = cachedState.sessionId || 0;
  return cachedState;
}

async function mutateState(mutator) {
  const run = stateWriteChain.then(async () => {
    const current = await loadState();
    const draft = { ...current };
    mutator(draft);
    cachedState = draft;
    await chrome.storage.local.set({ [STATE_KEY]: draft });
    return draft;
  });
  stateWriteChain = run.catch(() => {});
  return run;
}

function appendLogInDraft(draft, message) {
  const line = `[${formatTimeNow()}] ${message}`;
  const logs = [...(draft.logs || []), line];
  draft.logs = logs.slice(-MAX_LOGS);
}

async function addLog(message) {
  return mutateState((draft) => {
    appendLogInDraft(draft, message);
  });
}

async function updateBadge(phase) {
  try {
    if (phase === "CONNECTED") {
      await chrome.action.setBadgeText({ text: "JP" });
      await chrome.action.setBadgeBackgroundColor({ color: "#16A34A" });
    } else if (phase === "CONNECTING" || phase === "SWITCHING" || phase === "FETCHING") {
      await chrome.action.setBadgeText({ text: "..." });
      await chrome.action.setBadgeBackgroundColor({ color: "#EA580C" });
    } else {
      await chrome.action.setBadgeText({ text: "" });
    }
  } catch {
    // Ignore badge errors
  }
}

async function fetchJsonWithTimeout(url, timeoutMs = 12000) {
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), timeoutMs);
  try {
    const resp = await fetch(url, {
      method: "GET",
      cache: "no-store",
      signal: controller.signal
    });
    if (!resp.ok) throw new Error(`HTTP ${resp.status}`);
    return await resp.json();
  } finally {
    clearTimeout(timer);
  }
}

async function fetchTextWithTimeout(url, timeoutMs = 12000) {
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), timeoutMs);
  try {
    const resp = await fetch(url, {
      method: "GET",
      cache: "no-store",
      signal: controller.signal
    });
    if (!resp.ok) throw new Error(`HTTP ${resp.status}`);
    return await resp.text();
  } finally {
    clearTimeout(timer);
  }
}

function pickPreferredScheme(protocols) {
  const list = (protocols || []).map((p) => String(p).toLowerCase());
  for (const pref of ["socks5", "https", "http", "socks4"]) {
    if (list.includes(pref)) return pref;
  }
  return null;
}

function normalizeServer(raw) {
  const scheme = String(raw.scheme || "").toLowerCase();
  if (!["socks5", "https", "http", "socks4"].includes(scheme)) return null;
  const ip = String(raw.ip || "").trim();
  const port = Number(raw.port);
  if (!isValidIpv4(ip) || !Number.isInteger(port) || port < 1 || port > 65535) return null;

  const latency = Math.max(1, Math.round(Number(raw.latency) || 999));
  const uptime = Math.max(0, Math.min(100, Math.round(Number(raw.uptime) || 0)));
  const city = String(raw.city || "Japan").trim() || "Japan";
  const isp = String(raw.isp || "").trim();
  const key = `${scheme}://${ip}:${port}`;

  return {
    key,
    scheme,
    ip,
    port,
    latency,
    uptime,
    city,
    isp,
    signalBars: computeSignalBars(latency, uptime),
    source: raw.source || "Proxy"
  };
}

async function fetchJapanProxies(failedMap = {}) {
  let totalJapanCount = 0;
  let excluded219Count = 0;
  const candidates = [];

  // Source 1: ProxyScrape v4 JSON
  try {
    const json = await fetchJsonWithTimeout(
      "https://api.proxyscrape.com/v4/free-proxy-list/get?request=display_proxies&country=jp&proxy_format=protocolipport&format=json"
    );
    const list = Array.isArray(json?.proxies) ? json.proxies : [];
    for (const item of list) {
      if (item?.alive === false) continue;
      const ip = String(item?.ip || "").trim();
      if (!isValidIpv4(ip)) continue;
      totalJapanCount++;
      if (isBlocked219Ip(ip)) {
        excluded219Count++;
        continue;
      }
      const normalized = normalizeServer({
        scheme: item?.protocol,
        ip,
        port: item?.port,
        latency: item?.timeout || item?.average_timeout || 999,
        uptime: item?.uptime || 0,
        city: item?.ip_data?.city || "Japan",
        isp: item?.ip_data?.isp || item?.ip_data?.org || "",
        source: "ProxyScrape"
      });
      if (normalized) candidates.push(normalized);
    }
  } catch {
    // Fallback to next source
  }

  // Source 2: GeoNode JSON
  try {
    const json = await fetchJsonWithTimeout(
      "https://proxylist.geonode.com/api/proxy-list?country=JP&limit=50&page=1&sort_by=lastChecked&sort_type=desc"
    );
    const list = Array.isArray(json?.data) ? json.data : [];
    for (const item of list) {
      const ip = String(item?.ip || "").trim();
      if (!isValidIpv4(ip)) continue;
      totalJapanCount++;
      if (isBlocked219Ip(ip)) {
        excluded219Count++;
        continue;
      }
      const scheme = pickPreferredScheme(item?.protocols);
      if (!scheme) continue;
      const normalized = normalizeServer({
        scheme,
        ip,
        port: parseInt(item?.port, 10),
        latency: item?.responseTime || item?.latency || 999,
        uptime: item?.upTime || 0,
        city: item?.city || "Japan",
        isp: item?.org || item?.isp || "",
        source: "GeoNode"
      });
      if (normalized) candidates.push(normalized);
    }
  } catch {
    // Continue to fallback if needed
  }

  // Source 3: Plain-text fallback only if JSON sources returned 0 valid proxies
  if (candidates.length === 0) {
    const fallbacks = [
      {
        scheme: "socks5",
        url: "https://api.proxyscrape.com/v2/?request=displayproxies&protocol=socks5&timeout=10000&country=JP"
      },
      {
        scheme: "http",
        url: "https://api.proxyscrape.com/v2/?request=displayproxies&protocol=http&timeout=10000&country=JP&ssl=all&anonymity=all"
      }
    ];
    for (const fb of fallbacks) {
      try {
        const text = await fetchTextWithTimeout(fb.url);
        for (const rawLine of text.split(/\r?\n/)) {
          const line = rawLine.trim();
          if (!line.includes(":")) continue;
          const [ip, portStr] = line.split(":");
          if (!isValidIpv4(ip)) continue;
          totalJapanCount++;
          if (isBlocked219Ip(ip)) {
            excluded219Count++;
            continue;
          }
          const normalized = normalizeServer({
            scheme: fb.scheme,
            ip,
            port: parseInt(portStr, 10),
            latency: 1500,
            uptime: 50,
            city: "Japan",
            isp: "",
            source: "Fallback"
          });
          if (normalized) candidates.push(normalized);
        }
      } catch {
        // Ignore single fallback failure
      }
    }
  }

  // Deduplicate by scheme://ip:port keeping better latency/uptime
  const deduped = new Map();
  for (const s of candidates) {
    if (isBlocked219Ip(s.ip)) continue;
    const prev = deduped.get(s.key);
    if (!prev || s.uptime > prev.uptime || (s.uptime === prev.uptime && s.latency < prev.latency)) {
      deduped.set(s.key, s);
    }
  }

  const prunedFailed = pruneFailedMap(failedMap);
  const sorted = Array.from(deduped.values()).sort((a, b) => {
    const scoreOf = (s) => {
      const schemeBonus = s.scheme === "socks5" ? 35 : s.scheme === "https" ? 25 : s.scheme === "socks4" ? 15 : 0;
      const uptimeFactor = s.uptime * 1.4;
      const latencyPenalty = Math.min(s.latency, 6000) * 0.025;
      const failedPenalty = prunedFailed[s.key] ? 1000 : 0;
      return uptimeFactor + schemeBonus - latencyPenalty - failedPenalty;
    };
    return scoreOf(b) - scoreOf(a);
  });

  return {
    servers: sorted,
    totalJapanCount,
    excluded219Count,
    fetchedAt: Date.now()
  };
}

async function refreshServers(silent = false) {
  const current = await loadState();
  if (current.isRefreshing) return current;

  await mutateState((draft) => {
    draft.isRefreshing = true;
    if (!silent) {
      appendLogInDraft(draft, "正在從日本代理來源抓取最新節點清單...");
    }
  });

  try {
    const st = await loadState();
    const fetched = await fetchJapanProxies(st.failedMap);

    const updated = await mutateState((draft) => {
      draft.isRefreshing = false;
      draft.failedMap = pruneFailedMap(draft.failedMap);
      if (fetched.servers.length > 0 || draft.servers.length === 0) {
        draft.servers = fetched.servers;
        draft.totalJapanCount = fetched.totalJapanCount;
        draft.excluded219Count = fetched.excluded219Count;
        draft.lastFetchedAt = fetched.fetchedAt;
        const hasSelected = fetched.servers.some((s) => s.key === draft.selectedKey);
        if (!hasSelected) {
          const nextPick =
            fetched.servers.find((s) => !draft.failedMap[s.key]) || fetched.servers[0] || null;
          draft.selectedKey = nextPick ? nextPick.key : null;
        }
      }
      appendLogInDraft(
        draft,
        `已更新日本代理：共 ${fetched.totalJapanCount} 個，已排除 ${fetched.excluded219Count} 個 219 開頭，可用 ${fetched.servers.length} 個`
      );
    });
    return updated;
  } catch (err) {
    const updated = await mutateState((draft) => {
      draft.isRefreshing = false;
      if (!silent) {
        appendLogInDraft(draft, `抓取代理清單失敗：${err?.message || "網路錯誤"}`);
      }
    });
    return updated;
  }
}

async function applyProxy(server) {
  return new Promise((resolve, reject) => {
    chrome.proxy.settings.set(
      {
        value: {
          mode: "fixed_servers",
          rules: {
            singleProxy: {
              scheme: server.scheme,
              host: server.ip,
              port: server.port
            },
            bypassList: BYPASS_LIST
          }
        },
        scope: "regular"
      },
      () => {
        if (chrome.runtime.lastError) {
          reject(new Error(chrome.runtime.lastError.message));
        } else {
          resolve();
        }
      }
    );
  });
}

async function clearProxy() {
  return new Promise((resolve) => {
    chrome.proxy.settings.clear({ scope: "regular" }, () => resolve());
  });
}

async function probeThroughCurrentProxy(timeoutMs = PROBE_CONNECT_TIMEOUT_MS) {
  for (const url of PROBE_URLS) {
    const controller = new AbortController();
    const timer = setTimeout(() => controller.abort(), timeoutMs);
    try {
      const resp = await fetch(url, {
        method: "GET",
        cache: "no-store",
        signal: controller.signal
      });
      clearTimeout(timer);
      if (resp && (resp.ok || (resp.status >= 200 && resp.status < 400))) {
        return true;
      }
    } catch {
      clearTimeout(timer);
    }
  }
  return false;
}

function isSessionStillValid(sessionId) {
  return activeRunSessionId === sessionId && cachedState?.userWantsConnected === true;
}

async function disconnect(reason = "使用者已手動中斷連線") {
  activeRunSessionId += 1;
  const newSessionId = activeRunSessionId;

  await chrome.alarms.clear(ALARM_HEALTH_CHECK);
  await clearProxy();
  await updateBadge("DISCONNECTED");

  return mutateState((draft) => {
    draft.sessionId = newSessionId;
    draft.userWantsConnected = false;
    draft.phase = "DISCONNECTED";
    draft.statusTitle = "未連線";
    draft.statusDetail = reason;
    draft.attemptProgress = "";
    draft.activeServer = null;
    draft.connectedAt = 0;
    appendLogInDraft(draft, reason);
  });
}

async function connectPipeline(preferredKey = null) {
  activeRunSessionId += 1;
  const sessionId = activeRunSessionId;

  await chrome.alarms.clear(ALARM_HEALTH_CHECK);
  await updateBadge("CONNECTING");

  await mutateState((draft) => {
    draft.sessionId = sessionId;
    draft.userWantsConnected = true;
    draft.phase = "FETCHING";
    draft.statusTitle = "準備日本代理中...";
    draft.statusDetail = "篩選非 219 開頭的可用日本節點";
    draft.attemptProgress = "";
    draft.connectedAt = 0;
  });

  // Ensure we have a fresh server list (use bypassList so fetching doesn't rely on dead proxy)
  let st = await loadState();
  const isStale = !st.servers.length || Date.now() - st.lastFetchedAt > STALE_REFRESH_MS;
  if (isStale) {
    await clearProxy();
    st = await refreshServers(false);
    if (!isSessionStillValid(sessionId)) return;
  }

  let refreshedDuringLoop = false;
  let attemptIndex = 0;
  let nextPreferredKey = preferredKey;

  while (isSessionStillValid(sessionId)) {
    st = await loadState();
    const prunedFailed = pruneFailedMap(st.failedMap);
    if (Object.keys(prunedFailed).length !== Object.keys(st.failedMap || {}).length) {
      await mutateState((draft) => {
        draft.failedMap = prunedFailed;
      });
    }

    const availableList = (st.servers || []).filter((s) => !isBlocked219Ip(s.ip));
    if (availableList.length === 0) {
      await clearProxy();
      await updateBadge("DISCONNECTED");
      await mutateState((draft) => {
        if (draft.sessionId !== sessionId) return;
        draft.userWantsConnected = false;
        draft.phase = "DISCONNECTED";
        draft.statusTitle = "無可用日本代理";
        draft.statusDetail = "請稍後點擊重新整理再試";
        draft.activeServer = null;
        draft.attemptProgress = "";
      });
      return;
    }

    let candidate = null;
    if (nextPreferredKey) {
      candidate = availableList.find((s) => s.key === nextPreferredKey) || null;
      nextPreferredKey = null;
    }
    if (!candidate) {
      candidate = availableList.find((s) => !prunedFailed[s.key]) || null;
    }

    if (!candidate) {
      if (!refreshedDuringLoop) {
        refreshedDuringLoop = true;
        await clearProxy();
        await mutateState((draft) => {
          if (draft.sessionId !== sessionId) return;
          draft.phase = "FETCHING";
          draft.statusTitle = "重新抓取日本代理...";
          draft.statusDetail = "清單節點皆無回應，正在更新最新日本節點";
          draft.failedMap = {};
          appendLogInDraft(draft, "目前清單節點皆失敗，重新抓取最新日本代理...");
        });
        await refreshServers(false);
        if (!isSessionStillValid(sessionId)) return;
        continue;
      }

      await clearProxy();
      await updateBadge("DISCONNECTED");
      await mutateState((draft) => {
        if (draft.sessionId !== sessionId) return;
        draft.userWantsConnected = false;
        draft.phase = "DISCONNECTED";
        draft.statusTitle = "暫無可連通的日本代理";
        draft.statusDetail = "已嘗試全部節點，請稍後再試";
        draft.activeServer = null;
        draft.attemptProgress = "";
        appendLogInDraft(draft, "已嘗試所有可用日本代理皆無法連通");
      });
      return;
    }

    attemptIndex += 1;
    const isSwitch = attemptIndex > 1;
    const progressText = `節點 ${attemptIndex} / ${availableList.length}`;

    await mutateState((draft) => {
      if (draft.sessionId !== sessionId) return;
      draft.phase = isSwitch ? "SWITCHING" : "CONNECTING";
      draft.statusTitle = isSwitch ? "自動切換日本代理..." : "正在連線日本代理...";
      draft.statusDetail = `${candidate.ip}:${candidate.port} (${candidate.scheme.toUpperCase()})`;
      draft.attemptProgress = progressText;
      draft.selectedKey = candidate.key;
      draft.activeServer = candidate;
      appendLogInDraft(draft, `嘗試連線：${candidate.ip}:${candidate.port} (${candidate.scheme.toUpperCase()}, ${candidate.city})`);
    });

    try {
      await applyProxy(candidate);
    } catch (err) {
      await mutateState((draft) => {
        draft.failedMap = { ...draft.failedMap, [candidate.key]: Date.now() };
        appendLogInDraft(draft, `套用代理設定失敗 (${candidate.ip}:${candidate.port}): ${err?.message}`);
      });
      continue;
    }

    if (!isSessionStillValid(sessionId)) {
      await clearProxy();
      return;
    }

    // Brief pause for Chrome proxy pipeline to settle before probing
    await new Promise((r) => setTimeout(r, 250));
    if (!isSessionStillValid(sessionId)) {
      await clearProxy();
      return;
    }

    const ok = await probeThroughCurrentProxy(PROBE_CONNECT_TIMEOUT_MS);
    if (!isSessionStillValid(sessionId)) {
      if (cachedState?.phase === "DISCONNECTED") {
        await clearProxy();
      }
      return;
    }

    if (ok) {
      await updateBadge("CONNECTED");
      await mutateState((draft) => {
        if (draft.sessionId !== sessionId) return;
        draft.phase = "CONNECTED";
        draft.statusTitle = "已連線日本代理";
        draft.statusDetail = `${candidate.ip}:${candidate.port} • ${candidate.scheme.toUpperCase()} • ${candidate.city}`;
        draft.attemptProgress = "";
        draft.activeServer = candidate;
        draft.selectedKey = candidate.key;
        draft.connectedAt = Date.now();
        appendLogInDraft(draft, `連線成功：${candidate.ip}:${candidate.port} (${candidate.scheme.toUpperCase()})`);
      });
      await chrome.alarms.create(ALARM_HEALTH_CHECK, { periodInMinutes: 0.5 });
      return;
    }

    // Candidate failed probe
    await mutateState((draft) => {
      if (draft.sessionId !== sessionId) return;
      draft.failedMap = { ...draft.failedMap, [candidate.key]: Date.now() };
      appendLogInDraft(draft, `節點 ${candidate.ip}:${candidate.port} 無法連通，自動略過`);
    });

    const afterFail = await loadState();
    if (!afterFail.autoFailoverEnabled) {
      await clearProxy();
      await updateBadge("DISCONNECTED");
      await mutateState((draft) => {
        if (draft.sessionId !== sessionId) return;
        draft.userWantsConnected = false;
        draft.phase = "DISCONNECTED";
        draft.statusTitle = "代理連線失敗";
        draft.statusDetail = `${candidate.ip}:${candidate.port} 無回應`;
        draft.activeServer = null;
        draft.attemptProgress = "";
      });
      return;
    }
  }
}

async function switchToNextServer() {
  const st = await loadState();
  const currentKey = st.activeServer?.key || st.selectedKey;
  if (currentKey) {
    await mutateState((draft) => {
      draft.failedMap = { ...draft.failedMap, [currentKey]: Date.now() };
      appendLogInDraft(draft, `手動切換節點，暫時略過 ${currentKey}`);
    });
  }
  return connectPipeline(null);
}

// Listen for proxy connection failures reported by Chrome
chrome.proxy.onProxyError.addListener(async (details) => {
  const st = await loadState();
  if (
    st.phase !== "CONNECTED" ||
    !st.userWantsConnected ||
    !st.autoFailoverEnabled ||
    proxyErrorCheckInFlight
  ) {
    return;
  }

  const now = Date.now();
  if (now - lastProxyErrorCheckAt < 4000) return;
  lastProxyErrorCheckAt = now;
  proxyErrorCheckInFlight = true;

  const sessionId = st.sessionId;
  try {
    const ok = await probeThroughCurrentProxy(PROBE_QUICK_TIMEOUT_MS);
    if (!ok && isSessionStillValid(sessionId)) {
      const latest = await loadState();
      if (latest.sessionId === sessionId && latest.phase === "CONNECTED" && latest.activeServer) {
        const failedKey = latest.activeServer.key;
        await mutateState((draft) => {
          draft.failedMap = { ...draft.failedMap, [failedKey]: Date.now() };
          appendLogInDraft(
            draft,
            `偵測到代理錯誤 (${details?.error || "Proxy Error"})，自動切換下一個日本節點`
          );
        });
        connectPipeline(null);
      }
    }
  } finally {
    proxyErrorCheckInFlight = false;
  }
});

// Periodic health check & background proxy refresh alarms
chrome.alarms.onAlarm.addListener(async (alarm) => {
  if (alarm.name === ALARM_AUTO_REFRESH) {
    const st = await loadState();
    if (st.autoRefreshEnabled) {
      await refreshServers(true);
    }
    return;
  }

  if (alarm.name === ALARM_HEALTH_CHECK) {
    const st = await loadState();
    if (
      st.phase !== "CONNECTED" ||
      !st.userWantsConnected ||
      !st.healthCheckEnabled ||
      !st.autoFailoverEnabled
    ) {
      return;
    }

    const sessionId = st.sessionId;
    const firstOk = await probeThroughCurrentProxy(PROBE_QUICK_TIMEOUT_MS);
    if (firstOk || !isSessionStillValid(sessionId)) return;

    await new Promise((r) => setTimeout(r, 3000));
    if (!isSessionStillValid(sessionId)) return;

    const secondOk = await probeThroughCurrentProxy(PROBE_QUICK_TIMEOUT_MS);
    if (!secondOk && isSessionStillValid(sessionId)) {
      const latest = await loadState();
      if (latest.sessionId === sessionId && latest.phase === "CONNECTED" && latest.activeServer) {
        const failedKey = latest.activeServer.key;
        await mutateState((draft) => {
          draft.failedMap = { ...draft.failedMap, [failedKey]: Date.now() };
          appendLogInDraft(draft, `健康檢查連續 2 次無回應 (${latest.activeServer.ip})，自動切換下一個日本代理`);
        });
        connectPipeline(null);
      }
    }
  }
});

chrome.runtime.onInstalled.addListener(async () => {
  await loadState();
  await chrome.alarms.create(ALARM_AUTO_REFRESH, { periodInMinutes: 5 });
  await clearProxy();
  await updateBadge("DISCONNECTED");
  await refreshServers(true);
});

chrome.runtime.onStartup.addListener(async () => {
  const st = await loadState();
  await chrome.alarms.create(ALARM_AUTO_REFRESH, { periodInMinutes: 5 });
  if (st.userWantsConnected && st.activeServer) {
    await updateBadge("CONNECTING");
    connectPipeline(st.activeServer.key);
  } else {
    await clearProxy();
    await updateBadge("DISCONNECTED");
    await refreshServers(true);
  }
});

chrome.runtime.onMessage.addListener((message, _sender, sendResponse) => {
  (async () => {
    const st = await loadState();
    switch (message?.type) {
      case "GET_STATE":
        sendResponse({ ok: true, state: st });
        break;
      case "CONNECT_AUTO":
        connectPipeline(st.selectedKey);
        sendResponse({ ok: true });
        break;
      case "CONNECT_SERVER":
        if (message.server && !isBlocked219Ip(message.server.ip)) {
          await mutateState((draft) => {
            const copy = { ...draft.failedMap };
            delete copy[message.server.key];
            draft.failedMap = copy;
            draft.selectedKey = message.server.key;
          });
          connectPipeline(message.server.key);
        }
        sendResponse({ ok: true });
        break;
      case "SWITCH_NEXT":
        switchToNextServer();
        sendResponse({ ok: true });
        break;
      case "DISCONNECT":
        await disconnect("使用者已手動斷開代理");
        sendResponse({ ok: true });
        break;
      case "REFRESH_SERVERS":
        await refreshServers(false);
        sendResponse({ ok: true });
        break;
      case "UPDATE_SETTINGS":
        await mutateState((draft) => {
          if (typeof message.autoFailoverEnabled === "boolean") {
            draft.autoFailoverEnabled = message.autoFailoverEnabled;
          }
          if (typeof message.autoRefreshEnabled === "boolean") {
            draft.autoRefreshEnabled = message.autoRefreshEnabled;
          }
          if (typeof message.healthCheckEnabled === "boolean") {
            draft.healthCheckEnabled = message.healthCheckEnabled;
          }
          if (typeof message.darkTheme === "boolean") {
            draft.darkTheme = message.darkTheme;
          }
        });
        sendResponse({ ok: true });
        break;
      case "CLEAR_LOGS":
        await mutateState((draft) => {
          draft.logs = [];
        });
        sendResponse({ ok: true });
        break;
      default:
        sendResponse({ ok: false, error: "Unknown message type" });
    }
  })();
  return true;
});
