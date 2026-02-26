const { app, BrowserWindow, clipboard, ipcMain } = require("electron");
const { spawn } = require("child_process");
const fs = require("fs");
const http = require("http");
const path = require("path");

const MAX_LOG_LINES = 80;
const DEFAULTS = {
  backendPort: 3000,
  ngrokAuthtoken: "",
  ngrokRegion: "",
  autoStartNgrok: true,
  ngrokProfiles: [],
  activeNgrokProfileId: null,
};

function toBoolean(value, fallback) {
  if (typeof value === "boolean") {
    return value;
  }
  if (typeof value === "string") {
    const normalized = value.trim().toLowerCase();
    if (["1", "true", "yes", "on"].includes(normalized)) {
      return true;
    }
    if (["0", "false", "no", "off"].includes(normalized)) {
      return false;
    }
  }

  return fallback;
}

function sanitizeText(value) {
  return typeof value === "string" ? value.trim() : "";
}

function generateProfileId() {
  const random = Math.random().toString(36).slice(2, 9);
  return `profile_${Date.now().toString(36)}_${random}`;
}

function maskToken(token) {
  const raw = sanitizeText(token);
  if (!raw) {
    return "";
  }

  if (raw.length <= 10) {
    return `${raw.slice(0, 2)}****${raw.slice(-2)}`;
  }

  return `${raw.slice(0, 4)}...${raw.slice(-4)}`;
}

function toProfileLabel(name, index) {
  const cleaned = sanitizeText(name);
  if (cleaned) {
    return cleaned;
  }

  return `Token ${index + 1}`;
}

function normalizeProfiles(rawProfiles, legacyToken) {
  const source = Array.isArray(rawProfiles) ? rawProfiles : [];
  const profiles = [];

  for (const item of source) {
    if (!item || typeof item !== "object") {
      continue;
    }

    const token = sanitizeText(item.token);
    if (!token) {
      continue;
    }

    profiles.push({
      id: sanitizeText(item.id) || generateProfileId(),
      name: sanitizeText(item.name),
      token,
    });
  }

  const legacy = sanitizeText(legacyToken);
  if (legacy && profiles.length === 0) {
    profiles.push({
      id: generateProfileId(),
      name: "Default Token",
      token: legacy,
    });
  }

  return profiles;
}

function getBundledConfigPath() {
  return path.join(__dirname, "app-config.json");
}

function getUserConfigPath() {
  try {
    return path.join(app.getPath("userData"), "app-config.json");
  } catch (_err) {
    return path.join(__dirname, "app-config.user.json");
  }
}

function readJsonFile(filePath) {
  try {
    if (!fs.existsSync(filePath)) {
      return null;
    }

    const raw = fs.readFileSync(filePath, "utf8");
    return JSON.parse(raw);
  } catch (_err) {
    return null;
  }
}

function normalizeAppConfig(raw) {
  const value = raw && typeof raw === "object" ? raw : {};

  const profiles = normalizeProfiles(value.ngrokProfiles, value.ngrokAuthtoken);
  const requestedActiveId = sanitizeText(value.activeNgrokProfileId);
  const hasRequestedActive = profiles.some((profile) => profile.id === requestedActiveId);
  const activeNgrokProfileId = hasRequestedActive
    ? requestedActiveId
    : profiles[0]?.id || null;

  return {
    backendPort: Number(value.backendPort) || DEFAULTS.backendPort,
    ngrokAuthtoken: sanitizeText(value.ngrokAuthtoken),
    ngrokRegion: sanitizeText(value.ngrokRegion),
    autoStartNgrok: toBoolean(value.autoStartNgrok, DEFAULTS.autoStartNgrok),
    ngrokProfiles: profiles,
    activeNgrokProfileId,
  };
}

function writeConfigFile(filePath, config) {
  fs.mkdirSync(path.dirname(filePath), { recursive: true });
  fs.writeFileSync(filePath, `${JSON.stringify(config, null, 2)}\n`, "utf8");
}

function loadInitialConfig() {
  const userPath = getUserConfigPath();
  const bundledPath = getBundledConfigPath();

  const userRaw = readJsonFile(userPath);
  const bundledRaw = readJsonFile(bundledPath);

  const fromSource = userRaw || bundledRaw || DEFAULTS;
  const normalized = normalizeAppConfig(fromSource);

  try {
    writeConfigFile(userPath, normalized);
  } catch (_err) {
    // ignore write failures and continue with in-memory config
  }

  return {
    config: normalized,
    configPath: userPath,
  };
}

const loaded = loadInitialConfig();
let appConfig = loaded.config;
const configPath = loaded.configPath;

function saveAppConfig(nextConfig) {
  const normalized = normalizeAppConfig(nextConfig);
  appConfig = normalized;
  writeConfigFile(configPath, normalized);
  return normalized;
}

function getProfilesForUi() {
  return appConfig.ngrokProfiles.map((profile, index) => ({
    id: profile.id,
    name: toProfileLabel(profile.name, index),
    tokenPreview: maskToken(profile.token),
  }));
}

function getActiveProfile() {
  return (
    appConfig.ngrokProfiles.find(
      (profile) => profile.id === appConfig.activeNgrokProfileId,
    ) || null
  );
}

function addNgrokProfile({ name, token }) {
  const tokenValue = sanitizeText(token);
  if (!tokenValue) {
    throw new Error("Token không được để trống.");
  }

  const nextProfiles = [...appConfig.ngrokProfiles];
  const duplicate = nextProfiles.find((profile) => profile.token === tokenValue);
  if (duplicate) {
    throw new Error("Token này đã tồn tại trong danh sách.");
  }

  const profile = {
    id: generateProfileId(),
    name: sanitizeText(name),
    token: tokenValue,
  };

  nextProfiles.push(profile);

  saveAppConfig({
    ...appConfig,
    ngrokProfiles: nextProfiles,
    activeNgrokProfileId: profile.id,
  });

  return profile;
}

function removeNgrokProfile(profileId) {
  const targetId = sanitizeText(profileId);
  if (!targetId) {
    throw new Error("Thiếu profileId để xóa.");
  }

  const nextProfiles = appConfig.ngrokProfiles.filter(
    (profile) => profile.id !== targetId,
  );

  if (nextProfiles.length === appConfig.ngrokProfiles.length) {
    throw new Error("Không tìm thấy token để xóa.");
  }

  const activeNgrokProfileId =
    appConfig.activeNgrokProfileId === targetId
      ? nextProfiles[0]?.id || null
      : appConfig.activeNgrokProfileId;

  saveAppConfig({
    ...appConfig,
    ngrokProfiles: nextProfiles,
    activeNgrokProfileId,
  });
}

function selectNgrokProfile(profileId) {
  const targetId = sanitizeText(profileId);
  const target = appConfig.ngrokProfiles.find((profile) => profile.id === targetId);

  if (!target) {
    throw new Error("Không tìm thấy token được chọn.");
  }

  saveAppConfig({
    ...appConfig,
    activeNgrokProfileId: target.id,
  });

  return target;
}

const BACKEND_PORT =
  Number(process.env.BACKEND_PORT || process.env.PORT) || appConfig.backendPort;

const BACKEND_ENTRY = path.join(__dirname, "backend", "index.js");
const BACKEND_CWD = path.join(__dirname, "backend");
const NGROK_API_PORT = Number(process.env.NGROK_API_PORT) || 4040;

function resolveNgrokCommand() {
  if (process.env.NGROK_CMD && process.env.NGROK_CMD.trim()) {
    return process.env.NGROK_CMD.trim();
  }

  const binaryName = process.platform === "win32" ? "ngrok.exe" : "ngrok";
  const baseDir = app.isPackaged
    ? path.join(process.resourcesPath, "bin")
    : path.join(__dirname, "bin");

  const candidates = [
    path.join(baseDir, `${process.platform}-${process.arch}`, binaryName),
    path.join(baseDir, binaryName),
  ];

  for (const candidate of candidates) {
    try {
      if (fs.existsSync(candidate)) {
        return candidate;
      }
    } catch (_err) {
      // ignore and continue
    }
  }

  return "ngrok";
}

function getNgrokAuthToken() {
  if (process.env.NGROK_AUTHTOKEN && process.env.NGROK_AUTHTOKEN.trim()) {
    return process.env.NGROK_AUTHTOKEN.trim();
  }

  const activeProfile = getActiveProfile();
  if (activeProfile?.token) {
    return activeProfile.token;
  }

  return appConfig.ngrokAuthtoken || "";
}

function getNgrokRegion() {
  if (process.env.NGROK_REGION && process.env.NGROK_REGION.trim()) {
    return process.env.NGROK_REGION.trim();
  }

  return appConfig.ngrokRegion || "";
}

const state = {
  backend: {
    process: null,
    logs: [],
    lastError: null,
  },
  ngrok: {
    process: null,
    logs: [],
    lastError: null,
    tunnel: null,
    command: resolveNgrokCommand(),
    expectedStop: false,
  },
};

function isRunning(child) {
  return Boolean(child && child.exitCode === null && !child.killed);
}

function pushLog(target, chunk) {
  const text = String(chunk || "")
    .split(/\r?\n/)
    .map((line) => line.trim())
    .filter(Boolean);

  if (!text.length) {
    return;
  }

  target.push(...text);
  if (target.length > MAX_LOG_LINES) {
    target.splice(0, target.length - MAX_LOG_LINES);
  }
}

function delay(ms) {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

function getJson({ host, port, route, timeoutMs = 2000 }) {
  return new Promise((resolve, reject) => {
    const req = http.request(
      {
        host,
        port,
        path: route,
        method: "GET",
        timeout: timeoutMs,
      },
      (res) => {
        let body = "";
        res.setEncoding("utf8");
        res.on("data", (chunk) => {
          body += chunk;
        });
        res.on("end", () => {
          if (res.statusCode < 200 || res.statusCode >= 300) {
            reject(new Error(`HTTP ${res.statusCode}: ${body.slice(0, 200)}`));
            return;
          }

          try {
            resolve(JSON.parse(body));
          } catch (err) {
            reject(new Error(`Invalid JSON response: ${err.message}`));
          }
        });
      },
    );

    req.on("error", reject);
    req.on("timeout", () => {
      req.destroy(new Error("Request timed out"));
    });
    req.end();
  });
}

async function getBackendHealth() {
  try {
    const response = await getJson({
      host: "127.0.0.1",
      port: BACKEND_PORT,
      route: "/health",
      timeoutMs: 1200,
    });

    return {
      healthy: response && response.success === true,
      payload: response,
    };
  } catch (_err) {
    return {
      healthy: false,
      payload: null,
    };
  }
}

async function getNgrokTunnel() {
  try {
    const data = await getJson({
      host: "127.0.0.1",
      port: NGROK_API_PORT,
      route: "/api/tunnels",
      timeoutMs: 1200,
    });

    const tunnels = Array.isArray(data?.tunnels) ? data.tunnels : [];
    const selected =
      tunnels.find((item) => String(item?.public_url || "").startsWith("https://")) ||
      tunnels.find((item) => String(item?.public_url || "").startsWith("http://")) ||
      null;

    if (!selected) {
      return null;
    }

    return {
      name: selected.name || null,
      proto: selected.proto || null,
      publicUrl: selected.public_url,
      stampUrl: `${selected.public_url}/api/pdf/stamp`,
      healthUrl: `${selected.public_url}/health`,
    };
  } catch (_err) {
    return null;
  }
}

function waitForExit(child, timeoutMs = 3000) {
  return new Promise((resolve) => {
    if (!isRunning(child)) {
      resolve();
      return;
    }

    let done = false;

    const finish = () => {
      if (done) {
        return;
      }
      done = true;
      resolve();
    };

    child.once("exit", finish);

    try {
      child.kill("SIGTERM");
    } catch (_err) {
      finish();
      return;
    }

    setTimeout(() => {
      if (done) {
        return;
      }

      try {
        child.kill("SIGKILL");
      } catch (_err) {
        // ignore kill errors
      }

      finish();
    }, timeoutMs);
  });
}

async function waitUntil(check, timeoutMs, intervalMs) {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    const result = await check();
    if (result) {
      return result;
    }

    await delay(intervalMs);
  }

  return null;
}

async function startBackend() {
  if (isRunning(state.backend.process)) {
    return;
  }

  const existingHealth = await getBackendHealth();
  if (existingHealth.healthy) {
    state.backend.lastError = null;
    return;
  }

  state.backend.lastError = null;

  const child = spawn(process.execPath, [BACKEND_ENTRY], {
    cwd: BACKEND_CWD,
    env: {
      ...process.env,
      ELECTRON_RUN_AS_NODE: "1",
      PORT: String(BACKEND_PORT),
    },
    stdio: ["ignore", "pipe", "pipe"],
  });

  state.backend.process = child;

  child.stdout.on("data", (chunk) => {
    pushLog(state.backend.logs, chunk);
  });

  child.stderr.on("data", (chunk) => {
    pushLog(state.backend.logs, chunk);
    const text = String(chunk || "");
    if (text.includes("EADDRINUSE")) {
      state.backend.lastError = `Port ${BACKEND_PORT} đang bận.`;
    }
  });

  child.on("error", (err) => {
    state.backend.lastError = `Không khởi động backend: ${err.message}`;
  });

  child.on("exit", (code, signal) => {
    if (code && code !== 0) {
      state.backend.lastError = `Backend dừng (code ${code}${signal ? `, signal ${signal}` : ""}).`;
    }

    if (state.backend.process === child) {
      state.backend.process = null;
    }
  });

  const healthy = await waitUntil(async () => {
    const result = await getBackendHealth();
    return result.healthy ? result : null;
  }, 10000, 350);

  if (!healthy) {
    if (!state.backend.lastError) {
      state.backend.lastError = "Backend không phản hồi /health sau khi khởi động.";
    }

    if (!isRunning(state.backend.process)) {
      throw new Error(state.backend.lastError);
    }
  }
}

async function stopBackend() {
  if (!isRunning(state.backend.process)) {
    state.backend.process = null;
    return;
  }

  const target = state.backend.process;
  await waitForExit(target);

  if (state.backend.process === target) {
    state.backend.process = null;
  }
}

function buildNgrokArgs() {
  const args = ["http", String(BACKEND_PORT), "--log=stdout"];
  const token = getNgrokAuthToken();
  const region = getNgrokRegion();

  if (token) {
    args.push("--authtoken", token);
  }
  if (region) {
    args.push("--region", region);
  }

  return args;
}

async function waitForNgrokTunnel(timeoutMs = 15000) {
  const tunnel = await waitUntil(async () => {
    const info = await getNgrokTunnel();
    return info || null;
  }, timeoutMs, 400);

  state.ngrok.tunnel = tunnel;
  if (tunnel) {
    state.ngrok.lastError = null;
  }
  return tunnel;
}

async function startNgrok({ restart = true } = {}) {
  if (restart) {
    await stopNgrok();
  }

  if (isRunning(state.ngrok.process)) {
    const tunnel = await waitForNgrokTunnel(2500);
    if (tunnel) {
      return;
    }
  }

  state.ngrok.lastError = null;
  state.ngrok.expectedStop = false;
  state.ngrok.command = resolveNgrokCommand();

  const child = spawn(state.ngrok.command, buildNgrokArgs(), {
    cwd: BACKEND_CWD,
    env: process.env,
    stdio: ["ignore", "pipe", "pipe"],
  });

  state.ngrok.process = child;

  child.stdout.on("data", (chunk) => {
    pushLog(state.ngrok.logs, chunk);
  });

  child.stderr.on("data", (chunk) => {
    pushLog(state.ngrok.logs, chunk);
  });

  child.on("error", (err) => {
    state.ngrok.lastError = `Không chạy được ngrok (${state.ngrok.command}): ${err.message}`;
  });

  child.on("exit", (code, signal) => {
    const wasExpectedStop = state.ngrok.expectedStop || signal === "SIGTERM";
    if (code && code !== 0 && !wasExpectedStop) {
      state.ngrok.lastError = `ngrok d???ng (code ${code}${signal ? `, signal ${signal}` : ""}).`;
    }

    if (state.ngrok.process === child) {
      state.ngrok.process = null;
      state.ngrok.tunnel = null;
    }
    state.ngrok.expectedStop = false;
  });

  const tunnel = await waitForNgrokTunnel();
  if (!tunnel) {
    if (!state.ngrok.lastError) {
      state.ngrok.lastError =
        "Không lấy được tunnel ngrok. Kiểm tra binary, quota hoặc auth token.";
    }

    if (!isRunning(state.ngrok.process)) {
      throw new Error(state.ngrok.lastError);
    }
  }
}

async function stopNgrok() {
  if (!isRunning(state.ngrok.process)) {
    state.ngrok.process = null;
    state.ngrok.tunnel = null;
    state.ngrok.expectedStop = false;
    state.ngrok.lastError = null;
    return;
  }

  const target = state.ngrok.process;
  state.ngrok.expectedStop = true;
  state.ngrok.lastError = null;
  await waitForExit(target);

  if (state.ngrok.process === target) {
    state.ngrok.process = null;
    state.ngrok.tunnel = null;
  }
  state.ngrok.expectedStop = false;
}

async function collectStatus() {
  const backendHealth = await getBackendHealth();
  const detectedTunnel = await getNgrokTunnel();
  const activeProfile = getActiveProfile();

  if (detectedTunnel) {
    state.ngrok.tunnel = detectedTunnel;
    state.ngrok.lastError = null;
  } else if (!isRunning(state.ngrok.process)) {
    state.ngrok.tunnel = null;
  }

  return {
    backend: {
      port: BACKEND_PORT,
      healthUrl: `http://127.0.0.1:${BACKEND_PORT}/health`,
      stampUrl: `http://127.0.0.1:${BACKEND_PORT}/api/pdf/stamp`,
      managedRunning: isRunning(state.backend.process),
      pid: isRunning(state.backend.process) ? state.backend.process.pid : null,
      healthy: backendHealth.healthy,
      lastError: state.backend.lastError,
      logTail: state.backend.logs.slice(-10),
    },
    ngrok: {
      managedRunning: isRunning(state.ngrok.process),
      pid: isRunning(state.ngrok.process) ? state.ngrok.process.pid : null,
      command: state.ngrok.command,
      tunnel: state.ngrok.tunnel,
      publicUrl: state.ngrok.tunnel?.publicUrl || null,
      stampUrl: state.ngrok.tunnel?.stampUrl || null,
      healthUrl: state.ngrok.tunnel?.healthUrl || null,
      lastError: state.ngrok.lastError,
      logTail: state.ngrok.logs.slice(-10),
    },
    app: {
      autoStartNgrok: toBoolean(process.env.AUTO_START_NGROK, appConfig.autoStartNgrok),
      configSource: configPath,
      ngrokRegion: getNgrokRegion() || null,
      hasNgrokToken: Boolean(getNgrokAuthToken()),
      ngrokProfiles: getProfilesForUi(),
      activeNgrokProfileId: activeProfile?.id || null,
      activeNgrokProfileName: activeProfile
        ? toProfileLabel(activeProfile.name, 0)
        : null,
    },
  };
}

async function handleAction(action) {
  try {
    await action();
    return {
      ok: true,
      status: await collectStatus(),
    };
  } catch (err) {
    return {
      ok: false,
      error: err.message || "Unknown error",
      status: await collectStatus(),
    };
  }
}

function createWindow() {
  const win = new BrowserWindow({
    width: 980,
    height: 760,
    minWidth: 900,
    minHeight: 700,
    autoHideMenuBar: true,
    webPreferences: {
      preload: path.join(__dirname, "preload.js"),
      contextIsolation: true,
      nodeIntegration: false,
    },
  });

  win.loadFile(path.join(__dirname, "renderer", "index.html"));
}

ipcMain.handle("status:get", async () => collectStatus());

ipcMain.handle("backend:start", async () =>
  handleAction(async () => {
    await startBackend();
  }),
);

ipcMain.handle("backend:stop", async () =>
  handleAction(async () => {
    await stopBackend();
  }),
);

ipcMain.handle("ngrok:start", async (_event, payload) =>
  handleAction(async () => {
    await startBackend();
    await startNgrok({ restart: payload?.restart !== false });
  }),
);

ipcMain.handle("ngrok:stop", async () =>
  handleAction(async () => {
    await stopNgrok();
  }),
);

ipcMain.handle("config:add-token", async (_event, payload) =>
  handleAction(async () => {
    addNgrokProfile({
      name: payload?.name,
      token: payload?.token,
    });

    await startBackend();
    await startNgrok({ restart: true });
  }),
);

ipcMain.handle("config:remove-token", async (_event, payload) =>
  handleAction(async () => {
    removeNgrokProfile(payload?.profileId);
    if (isRunning(state.ngrok.process)) {
      await startNgrok({ restart: true });
    }
  }),
);

ipcMain.handle("config:select-token", async (_event, payload) =>
  handleAction(async () => {
    selectNgrokProfile(payload?.profileId);

    if (payload?.restartNgrok !== false) {
      await startBackend();
      await startNgrok({ restart: true });
    }
  }),
);

ipcMain.handle("clipboard:copy", async (_event, text) => {
  clipboard.writeText(String(text || ""));
  return true;
});

let quitting = false;

app.on("before-quit", () => {
  if (quitting) {
    return;
  }

  quitting = true;
  if (isRunning(state.ngrok.process)) {
    try {
      state.ngrok.process.kill("SIGTERM");
    } catch (_err) {
      // ignore
    }
  }

  if (isRunning(state.backend.process)) {
    try {
      state.backend.process.kill("SIGTERM");
    } catch (_err) {
      // ignore
    }
  }
});

app.whenReady().then(async () => {
  createWindow();

  try {
    await startBackend();
  } catch (err) {
    state.backend.lastError = err.message;
  }

  const shouldAutoStartNgrok = toBoolean(
    process.env.AUTO_START_NGROK,
    appConfig.autoStartNgrok,
  );

  if (shouldAutoStartNgrok) {
    try {
      await startNgrok({ restart: true });
    } catch (err) {
      state.ngrok.lastError = err.message;
    }
  }

  app.on("activate", () => {
    if (BrowserWindow.getAllWindows().length === 0) {
      createWindow();
    }
  });
});

app.on("window-all-closed", () => {
  if (process.platform !== "darwin") {
    app.quit();
  }
});
