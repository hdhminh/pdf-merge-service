const express = require("express");
const http = require("http");
const https = require("https");
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

function postJson(targetUrl, payload, headers = {}, timeoutMs = 12000) {
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
          resolve({
            statusCode: Number(res.statusCode) || 0,
            body: Buffer.concat(chunks).toString("utf8"),
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
      outputFileName,
    } = req.body || {};

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
    });

    const finalName = sanitizePdfFileName(
      outputFileName || req.file?.originalname,
      "stamped",
    );

    res.setHeader("Content-Type", "application/pdf");
    res.setHeader("Content-Disposition", `attachment; filename="${finalName}"`);
    res.send(Buffer.from(stampedBytes));
  } catch (err) {
    next(err);
  }
});

app.post("/api/google-sheet/set-endpoint", async (req, res, next) => {
  try {
    const sheetId = normalizeGoogleSheetId(req.body?.sheetId);
    const targetA1 = normalizeTargetA1(req.body?.targetA1);
    const webhookUrl = trimValue(req.body?.webhookUrl) || GOOGLE_SHEET_SYNC_URL;
    const endpoint = trimValue(req.body?.endpoint);
    const webhookHost = (() => {
      try {
        return new URL(webhookUrl).host;
      } catch (_err) {
        return "invalid-webhook-url";
      }
    })();

    console.info("[sheet-sync] request", {
      sheetId,
      targetA1,
      webhookHost,
      endpoint,
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
      const upstreamMessage =
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
      sheetId,
      targetA1,
      endpoint,
      writtenValue: upstreamWrittenValue || "",
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
    message: err.message || "Unexpected server error.",
  });
});

app.listen(PORT, () => {
  console.log(`PDF service running at http://localhost:${PORT}`);
});
