const express = require("express");
const http = require("http");
const https = require("https");
const fs = require("fs");
const os = require("os");
const path = require("path");
const multer = require("multer");
const { stampPdf, toPdfBytes } = require("./pdfStamp");

const app = express();
app.use(express.json({ limit: "50mb" }));

const PORT = Number(process.env.PORT) || 3000;
const MAX_FILE_SIZE = Number(process.env.MAX_FILE_SIZE_BYTES) || 20 * 1024 * 1024;
const GOOGLE_SHEET_SYNC_URL = trimValue(process.env.GOOGLE_SHEET_SYNC_URL);
const GOOGLE_SHEET_SYNC_API_KEY = trimValue(process.env.GOOGLE_SHEET_SYNC_API_KEY);
const GOOGLE_SHEET_SYNC_TIMEOUT_MS =
  Number(process.env.GOOGLE_SHEET_SYNC_TIMEOUT_MS) || 25000;
const GOOGLE_SHEET_SYNC_MAX_REDIRECTS =
  Number(process.env.GOOGLE_SHEET_SYNC_MAX_REDIRECTS) > 0
    ? Number(process.env.GOOGLE_SHEET_SYNC_MAX_REDIRECTS)
    : 4;
const TEMP_SIGFIELDS_PREFIX = "pdf-sigfields-";
const TEMP_MAX_AGE_MS =
  Number(process.env.TEMP_FILE_MAX_AGE_MS) > 0
    ? Number(process.env.TEMP_FILE_MAX_AGE_MS)
    : 30 * 60 * 1000;
const TEMP_CLEANUP_INTERVAL_MS =
  Number(process.env.TEMP_CLEANUP_INTERVAL_MS) > 0
    ? Number(process.env.TEMP_CLEANUP_INTERVAL_MS)
    : 5 * 60 * 1000;
const upload = multer({
  storage: multer.memoryStorage(),
  limits: { fileSize: MAX_FILE_SIZE },
});

function sanitizePdfFileName(name, fallback) {
  const raw = (name || fallback || "document")
    .replace(/\.pdf$/i, "")
    .replace(/[^\w.-]+/g, "_");
  return `${raw || "document"}.pdf`;
}

function isPdfBuffer(buffer) {
  if (!Buffer.isBuffer(buffer) || buffer.length < 5) {
    return false;
  }

  return buffer.slice(0, 5).toString("ascii") === "%PDF-";
}

function createError(status, errorCode, message) {
  const err = new Error(message);
  err.status = status;
  err.errorCode = errorCode;
  return err;
}

function trimValue(value) {
  return typeof value === "string" ? value.trim() : "";
}

function normalizeProfileSelection(profileName, profile) {
  const candidate = trimValue(profileName) || trimValue(profile);
  if (!candidate) {
    return { profileHint: "auto", profileOverride: undefined };
  }

  const normalized = candidate.toLowerCase();
  if (
    normalized === "auto" ||
    normalized === "default" ||
    normalized === "classifier" ||
    normalized === "none"
  ) {
    return { profileHint: "auto", profileOverride: undefined };
  }

  return { profileHint: candidate, profileOverride: candidate };
}

function generateRequestId() {
  return `${Date.now().toString(36)}${Math.random().toString(36).slice(2, 8)}`;
}

function maskValue(value, visible = 4) {
  const raw = trimValue(value);
  if (!raw) {
    return "";
  }

  if (raw.length <= visible * 2) {
    return `${raw.slice(0, 1)}***${raw.slice(-1)}`;
  }

  return `${raw.slice(0, visible)}...${raw.slice(-visible)}`;
}

function getHostOnly(urlValue) {
  try {
    return new URL(trimValue(urlValue)).host;
  } catch (_err) {
    return "";
  }
}

function cleanupOrphanTempDirs(prefix, maxAgeMs) {
  if (!prefix || maxAgeMs <= 0) {
    return 0;
  }

  const tempRoot = os.tmpdir();
  const now = Date.now();
  let removed = 0;
  let entries = [];

  try {
    entries = fs.readdirSync(tempRoot, { withFileTypes: true });
  } catch (_err) {
    return 0;
  }

  for (const entry of entries) {
    if (!entry?.isDirectory?.() || !entry.name.startsWith(prefix)) {
      continue;
    }

    const fullPath = path.join(tempRoot, entry.name);
    try {
      const stat = fs.statSync(fullPath);
      if (!Number.isFinite(stat.mtimeMs) || now - stat.mtimeMs < maxAgeMs) {
        continue;
      }

      fs.rmSync(fullPath, { recursive: true, force: true });
      removed += 1;
    } catch (_err) {
      // best-effort cleanup
    }
  }

  return removed;
}

function startTempCleanupScheduler() {
  const runCleanup = () => {
    const removed = cleanupOrphanTempDirs(TEMP_SIGFIELDS_PREFIX, TEMP_MAX_AGE_MS);
    if (removed > 0) {
      console.info(`[temp-cleanup] removed ${removed} stale temp folder(s)`);
    }
  };

  runCleanup();

  if (TEMP_CLEANUP_INTERVAL_MS > 0) {
    const timer = setInterval(runCleanup, TEMP_CLEANUP_INTERVAL_MS);
    if (typeof timer.unref === "function") {
      timer.unref();
    }
  }
}

function parseOptionalJson(value) {
  if (!value) {
    return {};
  }

  if (typeof value === "object") {
    return value;
  }

  if (typeof value !== "string") {
    return {};
  }

  const trimmed = value.trim();
  if (!trimmed) {
    return {};
  }

  try {
    return JSON.parse(trimmed);
  } catch (_err) {
    return {};
  }
}

function parseOptionalBoolean(value) {
  if (typeof value === "boolean") {
    return value;
  }

  if (typeof value !== "string") {
    return undefined;
  }

  const normalized = value.trim().toLowerCase();
  if (["1", "true", "yes", "on"].includes(normalized)) {
    return true;
  }
  if (["0", "false", "no", "off"].includes(normalized)) {
    return false;
  }

  return undefined;
}

function normalizeGoogleSheetId(value) {
  const trimmed = trimValue(value);
  if (!trimmed) {
    return "";
  }

  const marker = "/spreadsheets/d/";
  const markerIndex = trimmed.toLowerCase().indexOf(marker);
  if (markerIndex < 0) {
    return trimmed;
  }

  const start = markerIndex + marker.length;
  if (start >= trimmed.length) {
    return trimmed;
  }

  const slashIndex = trimmed.indexOf("/", start);
  return (slashIndex > -1 ? trimmed.slice(start, slashIndex) : trimmed.slice(start)).trim();
}

function normalizeTargetA1(value) {
  const trimmed = trimValue(value);
  if (!trimmed) {
    return "CONFIG!B32";
  }

  return trimmed.includes("!") ? trimmed : `CONFIG!${trimmed}`;
}

function tryParseJson(raw) {
  if (!raw || typeof raw !== "string") {
    return null;
  }

  try {
    return JSON.parse(raw);
  } catch (_err) {
    return null;
  }
}

function isRedirectStatus(statusCode) {
  return statusCode === 301 || statusCode === 302 || statusCode === 303 || statusCode === 307 || statusCode === 308;
}

function resolveRedirectUrl(baseUrl, locationValue) {
  const location = trimValue(locationValue);
  if (!location) {
    return "";
  }

  try {
    return new URL(location, baseUrl).toString();
  } catch (_err) {
    return "";
  }
}

function postJson(
  targetUrl,
  payload,
  headers = {},
  timeoutMs = 12000,
  maxRedirects = GOOGLE_SHEET_SYNC_MAX_REDIRECTS,
) {
  return new Promise((resolve, reject) => {
    let parsedUrl;
    try {
      parsedUrl = new URL(targetUrl);
    } catch (err) {
      reject(err);
      return;
    }

    const body = JSON.stringify(payload || {});
    const isHttps = parsedUrl.protocol === "https:";
    const transport = isHttps ? https : http;

    const req = transport.request(
      {
        protocol: parsedUrl.protocol,
        hostname: parsedUrl.hostname,
        port: parsedUrl.port || (isHttps ? 443 : 80),
        path: `${parsedUrl.pathname}${parsedUrl.search}`,
        method: "POST",
        headers: {
          "Content-Type": "application/json",
          "Content-Length": Buffer.byteLength(body),
          ...headers,
        },
      },
      (res) => {
        const chunks = [];
        res.on("data", (chunk) => chunks.push(chunk));
        res.on("end", () => {
          const statusCode = Number(res.statusCode) || 0;
          const bodyText = Buffer.concat(chunks).toString("utf8");
          const responseHeaders = res.headers || {};

          if (isRedirectStatus(statusCode)) {
            const nextUrl = resolveRedirectUrl(targetUrl, responseHeaders.location);
            if (nextUrl && maxRedirects > 0) {
              postJson(nextUrl, payload, headers, timeoutMs, maxRedirects - 1)
                .then(resolve)
                .catch(reject);
              return;
            }
          }

          resolve({
            statusCode,
            body: bodyText,
            headers: responseHeaders,
            finalUrl: targetUrl,
          });
        });
      },
    );

    req.setTimeout(timeoutMs, () => {
      req.destroy(new Error("upstream timeout"));
    });

    req.on("error", reject);
    req.write(body);
    req.end();
  });
}

function validatePdfBuffer(buffer) {
  if (!buffer || !Buffer.isBuffer(buffer) || buffer.length === 0) {
    throw createError(400, "MISSING_FILE", "No PDF payload was provided.");
  }

  if (buffer.length > MAX_FILE_SIZE) {
    throw createError(
      413,
      "PDF_TOO_LARGE",
      `PDF exceeds ${Math.round(MAX_FILE_SIZE / 1024 / 1024)}MB limit.`,
    );
  }

  if (!isPdfBuffer(buffer)) {
    throw createError(400, "INVALID_PDF", "The payload is not a valid PDF.");
  }
}

app.get("/health", (_req, res) => {
  res.json({
    success: true,
    service: "pdf-certification-backend",
    mode: "stamp-only",
    timestamp: new Date().toISOString(),
  });
});

app.post("/api/pdf/stamp", upload.single("file"), async (req, res, next) => {
  const requestId = trimValue(req.headers["x-request-id"]) || generateRequestId();
  const startAt = performance.now?.() ?? Date.now();
  res.setHeader("x-request-id", requestId);

  try {
    const {
      contentBase64,
      certificationNumber,
      certificationBookNumber,
      certificationDate,
      certificationText,
      notaryTitle,
      copyStampText,
      images,
      fonts,
      imageLayout,
      textLayout,
      signatureFields,
      profileName,
      profile,
      logTiming,
      outputFileName,
    } = req.body || {};
    const { profileHint, profileOverride } = normalizeProfileSelection(
      profileName,
      profile,
    );

    const certNo = trimValue(certificationNumber);
    const certDate = trimValue(certificationDate);
    const sourceBuffer = req.file?.buffer || toPdfBytes(contentBase64);

    validatePdfBuffer(sourceBuffer);

    if (!certNo) {
      throw createError(
        400,
        "MISSING_CERTIFICATION_NUMBER",
        "certificationNumber is required.",
      );
    }

    if (!certDate) {
      throw createError(400, "MISSING_CERTIFICATION_DATE", "certificationDate is required.");
    }

    const stampedBytes = await stampPdf(sourceBuffer, {
      certificationNumber: certNo,
      certificationBookNumber: trimValue(certificationBookNumber) || undefined,
      certificationDate: certDate,
      certificationText: trimValue(certificationText) || undefined,
      notaryTitle: trimValue(notaryTitle) || undefined,
      copyStampText:
        copyStampText === null ? null : trimValue(copyStampText) || undefined,
      images: parseOptionalJson(images),
      fonts: parseOptionalJson(fonts),
      imageLayout: parseOptionalJson(imageLayout),
      textLayout: parseOptionalJson(textLayout),
      signatureFields: parseOptionalJson(signatureFields),
      profileName: profileOverride,
      logTiming: parseOptionalBoolean(logTiming),
      requestId,
    });

    const finalName = sanitizePdfFileName(
      outputFileName || req.file?.originalname,
      "stamped",
    );

    res.setHeader("Content-Type", "application/pdf");
    res.setHeader("Content-Disposition", `attachment; filename="${finalName}"`);
    res.send(Buffer.from(stampedBytes));
    const elapsedMs = (performance.now?.() ?? Date.now()) - startAt;
    console.info("[stamp] success", {
      requestId,
      profileHint,
      durationMs: Math.round(elapsedMs),
    });
  } catch (err) {
    err.requestId = requestId;
    next(err);
  }
});

app.post("/api/google-sheet/set-endpoint", async (req, res, next) => {
  try {
    const sheetId = normalizeGoogleSheetId(req.body?.sheetId);
    const targetA1 = normalizeTargetA1(req.body?.targetA1);
    const webhookUrl = trimValue(req.body?.webhookUrl) || GOOGLE_SHEET_SYNC_URL;
    const endpoint = trimValue(req.body?.endpoint);
    const webhookHost = getHostOnly(webhookUrl) || "invalid-webhook-url";
    const endpointHost = getHostOnly(endpoint) || "invalid-endpoint";

    console.info("[sheet-sync] request", {
      sheetIdMasked: maskValue(sheetId),
      targetA1,
      webhookHost,
      endpointHost,
    });

    if (!sheetId) {
      throw createError(400, "MISSING_SHEET_ID", "sheetId is required.");
    }

    if (!endpoint) {
      throw createError(400, "MISSING_ENDPOINT", "endpoint is required.");
    }

    if (!webhookUrl) {
      throw createError(
        503,
        "GOOGLE_SHEET_SYNC_NOT_CONFIGURED",
        "Google Sheet sync API URL is not configured on backend.",
      );
    }

    const upstreamHeaders = {};
    if (GOOGLE_SHEET_SYNC_API_KEY) {
      upstreamHeaders["x-api-key"] = GOOGLE_SHEET_SYNC_API_KEY;
    }

    let upstream;
    try {
      upstream = await postJson(
        webhookUrl,
        {
          sheetId,
          targetA1,
          endpoint,
        },
        upstreamHeaders,
        GOOGLE_SHEET_SYNC_TIMEOUT_MS,
      );
    } catch (upstreamErr) {
      const upstreamMessage = String(upstreamErr?.message || "Upstream request failed.");
      const isTimeout = upstreamMessage.toLowerCase().includes("timeout");
      throw createError(
        isTimeout ? 504 : 502,
        "GOOGLE_SHEET_SYNC_FAILED",
        isTimeout ? "upstream timeout" : upstreamMessage,
      );
    }

    const parsed = tryParseJson(upstream.body);
    if (upstream.statusCode < 200 || upstream.statusCode >= 300) {
      const redirectLocation = trimValue(upstream.headers?.location);
      const redirectHost = getHostOnly(redirectLocation);
      const locationText = redirectHost || "unknown";
      const upstreamMessage =
        (upstream.statusCode === 302 && redirectLocation.includes("accounts.google.com")
          ? "Webhook Apps Script dang yeu cau dang nhap (302). Deploy Web App voi quyen Anyone."
          : null) ||
        (upstream.statusCode === 302 && redirectLocation
          ? `Upstream redirect 302 toi ${locationText}.`
          : null) ||
        (parsed && typeof parsed.message === "string" && parsed.message.trim()) ||
        `Upstream returned status ${upstream.statusCode}.`;
      throw createError(502, "GOOGLE_SHEET_SYNC_FAILED", upstreamMessage);
    }

    if (!parsed || typeof parsed !== "object") {
      throw createError(
        502,
        "GOOGLE_SHEET_SYNC_FAILED",
        "Upstream response is not valid JSON.",
      );
    }

    if (parsed.success !== true) {
      const upstreamMessage =
        (typeof parsed.message === "string" && parsed.message.trim()) ||
        "Upstream returned success=false.";
      throw createError(502, "GOOGLE_SHEET_SYNC_FAILED", upstreamMessage);
    }

    const upstreamWrittenValue = trimValue(parsed.writtenValue);
    if (upstreamWrittenValue && upstreamWrittenValue !== endpoint) {
      throw createError(
        502,
        "GOOGLE_SHEET_SYNC_FAILED",
        `writtenValue mismatch. expected='${endpoint}' actual='${upstreamWrittenValue}'`,
      );
    }

    console.info("[sheet-sync] success", {
      sheetIdMasked: maskValue(sheetId),
      targetA1,
      endpointHost,
      writtenValueMatched: !upstreamWrittenValue || upstreamWrittenValue === endpoint,
    });

    res.json({
      success: true,
      sheetId,
      targetA1,
      endpoint,
      webhookUrl,
      upstream: parsed || undefined,
    });
  } catch (err) {
    next(err);
  }
});

app.use((err, _req, res, _next) => {
  const status = Number(err.status) || 500;
  const errorCode = err.errorCode || "INTERNAL_ERROR";

  if (status >= 500) {
    console.error(err);
  }

  res.status(status).json({
    success: false,
    errorCode,
    requestId: err.requestId || undefined,
    message: err.message || "Unexpected server error.",
  });
});

app.listen(PORT, () => {
  startTempCleanupScheduler();
  console.log(`PDF service running at http://localhost:${PORT}`);
});
