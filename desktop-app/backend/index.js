const express = require("express");
const multer = require("multer");
const { stampPdf, toPdfBytes } = require("./pdfStamp");

const app = express();
app.use(express.json({ limit: "50mb" }));

const PORT = Number(process.env.PORT) || 3000;
const MAX_FILE_SIZE = Number(process.env.MAX_FILE_SIZE_BYTES) || 20 * 1024 * 1024;
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
