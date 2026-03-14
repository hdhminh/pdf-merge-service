const fs = require("fs");
const os = require("os");
const path = require("path");
const { spawnSync } = require("child_process");
const { performance } = require("perf_hooks");
const fontkit = require("@pdf-lib/fontkit");

// Avoid pdfjs-dist trying to load node-canvas for rendering; we only read text.
if (!globalThis.DOMMatrix) {
  globalThis.DOMMatrix = class DOMMatrix {};
}
if (!globalThis.Path2D) {
  globalThis.Path2D = class Path2D {};
}

const pdfjsLib = require("pdfjs-dist/legacy/build/pdf.js");
const {
  PDFDocument,
  BlendMode,
  concatTransformationMatrix,
  popGraphicsState,
  pushGraphicsState,
  rgb,
} = require("pdf-lib");
const { PDFDict, PDFName, PDFStream } = require("pdf-lib/cjs/core");

const DEFAULT_CERTIFICATION_TEXT =
  "CH\u1ee8NG TH\u1ef0C B\u1ea2N SAO \u0110\u00daNG V\u1edaI B\u1ea2N CH\u00cdNH";

const HEADING_COLOR = rgb(0.72, 0.11, 0.18);
const BODY_COLOR = rgb(0.08, 0.14, 0.52);
const WHITE = rgb(1, 1, 1);
const STAMP_PROFILES_RELATIVE_PATH = path.join(
  "config",
  "stamp-profiles.json",
);

const BUILTIN_STAMP_PROFILES = Object.freeze({
  version: 1,
  defaultProfile: "fallback_safe",
  classifier: {
    digitalMinTextItems: 24,
    digitalMinChars: 180,
    sparseMaxTextItems: 4,
    sparseMaxChars: 64,
    fullPageAspectDeltaMax: 0.1,
    fullPageImageMinWidthRatio: 0.8,
    fullPageImageMinHeightRatio: 0.8,
  },
  scanInkProbe: {
    enabled: true,
    maxProbeCount: 2,
    lumaThreshold: 244,
    alphaThreshold: 20,
    rowInkRatioMin: 0.003,
    colInkRatioMin: 0.0025,
    rowAdaptiveDelta: 0.02,
    colAdaptiveDelta: 0.015,
    minImageWidth: 120,
    minImageHeight: 120,
    rowSampleDivisor: 720,
    colSampleDivisor: 720,
  },
  profiles: {
    scan_sparse_portrait: {
      textLayout: {
        anchorToLastText: false,
        marginBottom: 96,
        marginLeft: 28,
        fullPageScanMinMarginBottom: 120,
      },
      signatureFields: {
        minBottom: 24,
        overlap: true,
        overlapOffsetYRatio: -0.2,
        height: 52,
      },
    },
    scan_dense_portrait: {
      textLayout: {
        anchorToLastText: false,
        marginBottom: 74,
        marginLeft: 28,
        fullPageScanMinMarginBottom: 108,
      },
      signatureFields: {
        minBottom: 20,
        overlap: true,
        overlapOffsetYRatio: -0.2,
        height: 48,
      },
    },
    scan_sparse_landscape: {
      textLayout: {
        anchorToLastText: false,
        marginBottom: 78,
        marginLeft: 20,
        fullPageScanMinMarginBottom: 96,
      },
      signatureFields: {
        minBottom: 20,
        overlap: true,
        overlapOffsetYRatio: -0.2,
        height: 44,
      },
    },
    scan_dense_landscape: {
      textLayout: {
        anchorToLastText: false,
        marginBottom: 62,
        marginLeft: 20,
        fullPageScanMinMarginBottom: 84,
      },
      signatureFields: {
        minBottom: 16,
        overlap: true,
        overlapOffsetYRatio: -0.2,
        height: 40,
      },
    },
    digital_text_portrait: {
      textLayout: {
        anchorToLastText: true,
        scanMode: "auto",
        marginLeft: 28,
        bottomSafeMargin: 36,
      },
      signatureFields: {
        minBottom: 12,
        overlap: true,
        overlapOffsetYRatio: -0.2,
      },
    },
    digital_text_landscape: {
      textLayout: {
        anchorToLastText: true,
        scanMode: "auto",
        marginLeft: 20,
        bottomSafeMargin: 30,
      },
      signatureFields: {
        minBottom: 10,
        overlap: true,
        overlapOffsetYRatio: -0.2,
      },
    },
    template_fixed_A: {
      textLayout: {
        anchorToLastText: false,
        marginBottom: 92,
        marginLeft: 28,
        fullPageScanMinMarginBottom: 112,
      },
      signatureFields: {
        minBottom: 24,
        overlap: true,
        overlapOffsetYRatio: -0.2,
        height: 52,
      },
    },
    fallback_safe: {
      textLayout: {
        anchorToLastText: false,
        marginBottom: 90,
        marginLeft: 28,
        fullPageScanMinMarginBottom: 108,
      },
      signatureFields: {
        minBottom: 24,
        overlap: true,
        overlapOffsetYRatio: -0.2,
        height: 52,
      },
    },
  },
});

let stampProfilesCache;

function resolvePdfjsStandardFontDataUrl() {
  try {
    const pkgPath = require.resolve("pdfjs-dist/package.json");
    const fontsDir = path.join(path.dirname(pkgPath), "standard_fonts");
    const normalized = fontsDir.replace(/\\/g, "/");
    return normalized.endsWith("/") ? normalized : `${normalized}/`;
  } catch (_err) {
    return null;
  }
}

const PDFJS_STANDARD_FONT_DATA_URL = resolvePdfjsStandardFontDataUrl();

function normalizeBase64(input) {
  if (typeof input !== "string" || !input.trim()) {
    return null;
  }

  const trimmed = input.trim();
  const commaIndex = trimmed.indexOf(",");
  if (trimmed.startsWith("data:") && commaIndex > -1) {
    return trimmed.slice(commaIndex + 1);
  }

  return trimmed;
}

function toPdfBytes(base64OrBuffer) {
  if (Buffer.isBuffer(base64OrBuffer)) {
    return base64OrBuffer;
  }

  const normalized = normalizeBase64(base64OrBuffer);
  if (!normalized) {
    return null;
  }

  return Buffer.from(normalized, "base64");
}

function parseImageInput(imageInput) {
  if (!imageInput || typeof imageInput !== "string") {
    return null;
  }

  const trimmed = imageInput.trim();
  let mimeType = null;

  if (trimmed.startsWith("data:")) {
    const firstPart = trimmed.slice(5, trimmed.indexOf(";"));
    mimeType = firstPart || null;
  }

  const bytes = toPdfBytes(trimmed);
  return bytes ? { bytes, mimeType } : null;
}

function isPng(bytes) {
  if (!bytes || bytes.length < 8) {
    return false;
  }

  return (
    bytes[0] === 0x89 &&
    bytes[1] === 0x50 &&
    bytes[2] === 0x4e &&
    bytes[3] === 0x47 &&
    bytes[4] === 0x0d &&
    bytes[5] === 0x0a &&
    bytes[6] === 0x1a &&
    bytes[7] === 0x0a
  );
}

function isJpeg(bytes) {
  if (!bytes || bytes.length < 4) {
    return false;
  }

  return (
    bytes[0] === 0xff &&
    bytes[1] === 0xd8 &&
    bytes[bytes.length - 2] === 0xff &&
    bytes[bytes.length - 1] === 0xd9
  );
}

async function embedImage(pdfDoc, imageInput) {
  const parsed = parseImageInput(imageInput);
  if (!parsed) {
    return null;
  }

  const { bytes, mimeType } = parsed;

  if (mimeType === "image/png" || (!mimeType && isPng(bytes))) {
    return pdfDoc.embedPng(bytes);
  }

  if (
    mimeType === "image/jpeg" ||
    mimeType === "image/jpg" ||
    (!mimeType && isJpeg(bytes))
  ) {
    return pdfDoc.embedJpg(bytes);
  }

  throw new Error("Unsupported image format. Use PNG or JPEG base64 data.");
}

function toNumber(value, fallback) {
  const n = Number(value);
  return Number.isFinite(n) ? n : fallback;
}

function hasNumericValue(value) {
  return Number.isFinite(Number(value));
}

function asObject(value) {
  if (value && typeof value === "object" && !Array.isArray(value)) {
    return value;
  }

  return {};
}

function isPlainObject(value) {
  return Boolean(value) && typeof value === "object" && !Array.isArray(value);
}

function deepMergeObjects(baseValue, overrideValue) {
  const base = isPlainObject(baseValue) ? baseValue : {};
  const override = isPlainObject(overrideValue) ? overrideValue : {};
  const merged = { ...base };

  for (const [key, value] of Object.entries(override)) {
    if (isPlainObject(value) && isPlainObject(base[key])) {
      merged[key] = deepMergeObjects(base[key], value);
      continue;
    }
    merged[key] = value;
  }

  return merged;
}

function normalizeProfileName(value) {
  if (typeof value !== "string") {
    return "";
  }
  return value.trim().toLowerCase();
}

function resolveStampProfilesFilePath() {
  const candidates = [
    path.join(__dirname, STAMP_PROFILES_RELATIVE_PATH),
    path.join(__dirname, "..", STAMP_PROFILES_RELATIVE_PATH),
    path.join(process.cwd(), STAMP_PROFILES_RELATIVE_PATH),
  ];

  for (const candidate of candidates) {
    try {
      if (fs.existsSync(candidate)) {
        return candidate;
      }
    } catch (_err) {
      // skip invalid candidate and continue
    }
  }

  return null;
}

function loadStampProfiles() {
  if (stampProfilesCache) {
    return stampProfilesCache;
  }

  let loaded = null;
  const profileFilePath = resolveStampProfilesFilePath();

  if (profileFilePath) {
    try {
      const raw = fs.readFileSync(profileFilePath, "utf8");
      const parsed = JSON.parse(raw);
      if (isPlainObject(parsed) && isPlainObject(parsed.profiles)) {
        loaded = parsed;
      }
    } catch (_err) {
      // fallback to built-in profiles when config file is invalid
    }
  }

  const effective = loaded || BUILTIN_STAMP_PROFILES;
  stampProfilesCache = {
    defaultProfile:
      normalizeProfileName(effective.defaultProfile) ||
      BUILTIN_STAMP_PROFILES.defaultProfile,
    profiles: asObject(effective.profiles),
    classifier: asObject(effective.classifier),
    scanInkProbe: asObject(effective.scanInkProbe),
  };

  return stampProfilesCache;
}

function getClassifierConfig(profilesConfig) {
  const raw = asObject(profilesConfig?.classifier);
  const base = asObject(BUILTIN_STAMP_PROFILES.classifier);

  return {
    digitalMinTextItems: Math.max(
      1,
      toNumber(raw.digitalMinTextItems, toNumber(base.digitalMinTextItems, 24)),
    ),
    digitalMinChars: Math.max(
      1,
      toNumber(raw.digitalMinChars, toNumber(base.digitalMinChars, 180)),
    ),
    sparseMaxTextItems: Math.max(
      0,
      toNumber(raw.sparseMaxTextItems, toNumber(base.sparseMaxTextItems, 4)),
    ),
    sparseMaxChars: Math.max(
      0,
      toNumber(raw.sparseMaxChars, toNumber(base.sparseMaxChars, 64)),
    ),
    fullPageAspectDeltaMax: Math.max(
      0,
      toNumber(raw.fullPageAspectDeltaMax, toNumber(base.fullPageAspectDeltaMax, 0.1)),
    ),
    fullPageImageMinWidthRatio: Math.min(
      1,
      Math.max(
        0.1,
        toNumber(
          raw.fullPageImageMinWidthRatio,
          toNumber(base.fullPageImageMinWidthRatio, 0.8),
        ),
      ),
    ),
    fullPageImageMinHeightRatio: Math.min(
      1,
      Math.max(
        0.1,
        toNumber(
          raw.fullPageImageMinHeightRatio,
          toNumber(base.fullPageImageMinHeightRatio, 0.8),
        ),
      ),
    ),
  };
}

function getScanInkProbeConfig(profilesConfig) {
  const raw = asObject(profilesConfig?.scanInkProbe);
  const base = asObject(BUILTIN_STAMP_PROFILES.scanInkProbe);

  return {
    enabled: parseBooleanLike(raw.enabled, parseBooleanLike(base.enabled, true)),
    maxProbeCount: Math.max(
      1,
      toNumber(raw.maxProbeCount, toNumber(base.maxProbeCount, 2)),
    ),
    lumaThreshold: Math.min(
      255,
      Math.max(1, toNumber(raw.lumaThreshold, toNumber(base.lumaThreshold, 244))),
    ),
    alphaThreshold: Math.min(
      255,
      Math.max(0, toNumber(raw.alphaThreshold, toNumber(base.alphaThreshold, 20))),
    ),
    rowInkRatioMin: Math.max(
      0.0001,
      toNumber(raw.rowInkRatioMin, toNumber(base.rowInkRatioMin, 0.003)),
    ),
    colInkRatioMin: Math.max(
      0.0001,
      toNumber(raw.colInkRatioMin, toNumber(base.colInkRatioMin, 0.0025)),
    ),
    rowAdaptiveDelta: Math.max(
      0,
      toNumber(raw.rowAdaptiveDelta, toNumber(base.rowAdaptiveDelta, 0.02)),
    ),
    colAdaptiveDelta: Math.max(
      0,
      toNumber(raw.colAdaptiveDelta, toNumber(base.colAdaptiveDelta, 0.015)),
    ),
    minImageWidth: Math.max(
      16,
      toNumber(raw.minImageWidth, toNumber(base.minImageWidth, 120)),
    ),
    minImageHeight: Math.max(
      16,
      toNumber(raw.minImageHeight, toNumber(base.minImageHeight, 120)),
    ),
    rowSampleDivisor: Math.max(
      64,
      toNumber(raw.rowSampleDivisor, toNumber(base.rowSampleDivisor, 720)),
    ),
    colSampleDivisor: Math.max(
      64,
      toNumber(raw.colSampleDivisor, toNumber(base.colSampleDivisor, 720)),
    ),
  };
}

function buildStrictFullPageScanProbeConfig(scanProbeConfig) {
  const base = asObject(scanProbeConfig);
  const baseLumaThreshold = Math.min(
    255,
    Math.max(1, toNumber(base.lumaThreshold, 244)),
  );

  return {
    ...base,
    // Harden thresholds for noisy full-page scans with large blank lower area.
    lumaThreshold: Math.max(96, Math.min(180, baseLumaThreshold - 64)),
    alphaThreshold: Math.max(50, toNumber(base.alphaThreshold, 20)),
    rowInkRatioMin: Math.max(0.0008, toNumber(base.rowInkRatioMin, 0.003) * 0.5),
    colInkRatioMin: Math.max(0.0006, toNumber(base.colInkRatioMin, 0.0025) * 0.5),
    rowAdaptiveDelta: Math.max(0.004, toNumber(base.rowAdaptiveDelta, 0.02) * 0.2),
    colAdaptiveDelta: Math.max(0.003, toNumber(base.colAdaptiveDelta, 0.015) * 0.2),
    fullPageTailDetection: true,
    tailMinBlankRatio: Math.max(0.12, toNumber(base.tailMinBlankRatio, 0.2)),
    tailBlankRowFloor: Math.max(0.0004, toNumber(base.tailBlankRowFloor, 0.0025)),
    tailBlankMedianDelta: Math.max(
      0.0003,
      toNumber(base.tailBlankMedianDelta, 0.0015),
    ),
  };
}

function resolveProfileFromConfig(profileName, profilesConfig) {
  const profiles = asObject(profilesConfig?.profiles);
  const normalizedName = normalizeProfileName(profileName);
  if (!normalizedName) {
    return null;
  }

  const profile = asObject(profiles[normalizedName]);
  if (!isPlainObject(profile) || Object.keys(profile).length === 0) {
    return null;
  }

  return {
    name: normalizedName,
    profile,
  };
}

function resolveProfileOverrideName(options) {
  const autoHints = new Set(["auto", "default", "classifier", "none"]);
  const candidates = [
    options?.profileName,
    options?.profile,
    options?.profileId,
  ];

  for (const candidate of candidates) {
    const normalized = normalizeProfileName(candidate);
    if (normalized && !autoHints.has(normalized)) {
      return normalized;
    }
  }

  return "";
}

function normalizeRotationAngle(angle) {
  const raw = Number(angle);
  if (!Number.isFinite(raw)) {
    return 0;
  }

  const normalized = ((raw % 360) + 360) % 360;
  const rounded = Math.round(normalized);

  if (rounded === 0 || rounded === 90 || rounded === 180 || rounded === 270) {
    return rounded;
  }

  return normalized;
}

function getPageMetrics(page) {
  const width = page.getWidth();
  const height = page.getHeight();
  const rotation = normalizeRotationAngle(page.getRotation()?.angle);
  const quarterTurn = rotation === 90 || rotation === 270;
  const visualWidth = quarterTurn ? height : width;
  const visualHeight = quarterTurn ? width : height;
  const orientation = visualWidth >= visualHeight ? "landscape" : "portrait";

  return {
    width,
    height,
    visualWidth,
    visualHeight,
    rotation,
    orientation,
  };
}

function getOrientationBucket(layout, orientation) {
  return asObject(asObject(layout)[orientation]);
}

function getTextLayoutForOrientation(textLayout, orientation, defaults) {
  const base = { ...asObject(textLayout) };
  delete base.portrait;
  delete base.landscape;

  return {
    ...asObject(defaults),
    ...base,
    ...getOrientationBucket(textLayout, orientation),
  };
}

function getImageLayoutForOrientation(
  imageLayout,
  orientation,
  imageKey,
  defaults,
) {
  const root = asObject(imageLayout);
  const orientationBucket = getOrientationBucket(root, orientation);

  return {
    ...asObject(defaults),
    ...asObject(root[imageKey]),
    ...asObject(orientationBucket[imageKey]),
  };
}

function getSignatureFieldsLayoutForOrientation(signatureFields, orientation) {
  const base = { ...asObject(signatureFields) };
  delete base.portrait;
  delete base.landscape;

  return {
    ...base,
    ...getOrientationBucket(signatureFields, orientation),
  };
}

function toFiniteNumberFromPdfObject(value) {
  if (value === null || value === undefined) {
    return Number.NaN;
  }

  if (typeof value?.asNumber === "function") {
    const n = Number(value.asNumber());
    return Number.isFinite(n) ? n : Number.NaN;
  }

  const n = Number(String(value));
  return Number.isFinite(n) ? n : Number.NaN;
}

function getPageBoxMetrics(page, pageMetrics) {
  const fallbackWidth = toNumber(pageMetrics?.width, page.getWidth());
  const fallbackHeight = toNumber(pageMetrics?.height, page.getHeight());
  const fallback = {
    minX: 0,
    minY: 0,
    width: fallbackWidth,
    height: fallbackHeight,
  };

  let box = null;
  try {
    if (typeof page?.node?.CropBox === "function") {
      box = page.node.CropBox();
    }
    if (!box && typeof page?.node?.MediaBox === "function") {
      box = page.node.MediaBox();
    }
  } catch (_err) {
    return fallback;
  }

  if (!box || typeof box.size !== "function" || typeof box.get !== "function") {
    return fallback;
  }
  if (box.size() < 4) {
    return fallback;
  }

  const x1 = toFiniteNumberFromPdfObject(box.get(0));
  const y1 = toFiniteNumberFromPdfObject(box.get(1));
  const x2 = toFiniteNumberFromPdfObject(box.get(2));
  const y2 = toFiniteNumberFromPdfObject(box.get(3));

  if (
    !Number.isFinite(x1) ||
    !Number.isFinite(y1) ||
    !Number.isFinite(x2) ||
    !Number.isFinite(y2)
  ) {
    return fallback;
  }

  const minX = Math.min(x1, x2);
  const minY = Math.min(y1, y2);
  const width = Math.abs(x2 - x1);
  const height = Math.abs(y2 - y1);
  if (width <= 0 || height <= 0) {
    return fallback;
  }

  return {
    minX,
    minY,
    width,
    height,
  };
}

function getDisplayToPageMatrix(page, pageMetrics) {
  const rotation = normalizeRotationAngle(
    pageMetrics?.rotation ?? page.getRotation()?.angle,
  );
  const box = getPageBoxMetrics(page, pageMetrics);
  const width = box.width;
  const height = box.height;
  const minX = box.minX;
  const minY = box.minY;

  if (rotation === 90) {
    return [0, 1, -1, 0, minX + width, minY];
  }

  if (rotation === 180) {
    return [-1, 0, 0, -1, minX + width, minY + height];
  }

  if (rotation === 270) {
    return [0, -1, 1, 0, minX, minY + height];
  }

  return [1, 0, 0, 1, minX, minY];
}

function transformPointByMatrix(point, matrix) {
  return {
    x: matrix[0] * point.x + matrix[2] * point.y + matrix[4],
    y: matrix[1] * point.x + matrix[3] * point.y + matrix[5],
  };
}

function mapDisplayRectToPageRect(page, pageMetrics, rect) {
  const matrix = getDisplayToPageMatrix(page, pageMetrics);
  const box = getPageBoxMetrics(page, pageMetrics);
  const points = [
    transformPointByMatrix({ x: rect.x, y: rect.y }, matrix),
    transformPointByMatrix({ x: rect.x + rect.width, y: rect.y }, matrix),
    transformPointByMatrix({ x: rect.x, y: rect.y + rect.height }, matrix),
    transformPointByMatrix(
      { x: rect.x + rect.width, y: rect.y + rect.height },
      matrix,
    ),
  ];

  const xs = points.map((point) => point.x);
  const ys = points.map((point) => point.y);
  const minX = Math.min(...xs);
  const maxX = Math.max(...xs);
  const minY = Math.min(...ys);
  const maxY = Math.max(...ys);

  const left = box.minX;
  const bottom = box.minY;
  const right = box.minX + box.width;
  const top = box.minY + box.height;
  const x = Math.max(left, Math.min(minX, right - 1));
  const y = Math.max(bottom, Math.min(minY, top - 1));
  const width = Math.max(1, Math.min(maxX - minX, right - x));
  const height = Math.max(1, Math.min(maxY - minY, top - y));

  return { x, y, width, height };
}

function beginDisplaySpace(page, pageMetrics) {
  const matrix = getDisplayToPageMatrix(page, pageMetrics);
  const isIdentity =
    matrix[0] === 1 &&
    matrix[1] === 0 &&
    matrix[2] === 0 &&
    matrix[3] === 1 &&
    matrix[4] === 0 &&
    matrix[5] === 0;

  if (isIdentity) {
    return false;
  }

  page.pushOperators(
    pushGraphicsState(),
    concatTransformationMatrix(
      matrix[0],
      matrix[1],
      matrix[2],
      matrix[3],
      matrix[4],
      matrix[5],
    ),
  );

  return true;
}

function endDisplaySpace(page, transformed) {
  if (!transformed) {
    return;
  }

  page.pushOperators(popGraphicsState());
}

function resolveBlendMode(value, fallback) {
  if (typeof value !== "string" || !value.trim()) {
    return fallback;
  }

  const normalized = value.trim().toLowerCase();
  const supported = Object.values(BlendMode).find(
    (modeName) => modeName.toLowerCase() === normalized,
  );

  return supported || fallback;
}

function getPanelHeight(layout) {
  const panelPaddingY = toNumber(layout.panelPaddingY, 8);
  const headingSize = toNumber(layout.headingSize, 13);
  const bodySize = toNumber(layout.bodySize, 11.5);
  const lineGap = toNumber(layout.lineGap, 4);
  const notaryGap = toNumber(layout.notaryGap, 4);

  const headingLineHeight = headingSize + lineGap;
  const bodyLineHeight = bodySize + lineGap;

  const contentHeight =
    headingLineHeight +
    bodyLineHeight +
    bodyLineHeight +
    headingLineHeight +
    notaryGap;

  return contentHeight + panelPaddingY * 2;
}

function getImageBlockHeight(image, layout) {
  if (!image) {
    return 0;
  }

  const targetWidth = toNumber(layout.width, 120);
  const targetHeight = toNumber(
    layout.height,
    (image.height / image.width) * targetWidth,
  );
  const offsetY = toNumber(layout.offsetY, 0);

  return targetHeight + offsetY;
}

function getStampBlockHeight({
  textLayout,
  sealImage,
  sealLayout,
  certifiedStampImage,
  certifiedStampLayout,
  signatureImage,
  signatureLayout,
}) {
  const panelHeight = getPanelHeight(textLayout);
  const sealHeight = getImageBlockHeight(sealImage, sealLayout);
  const certifiedStampHeight = getImageBlockHeight(
    certifiedStampImage,
    certifiedStampLayout,
  );
  const signatureHeight = getImageBlockHeight(signatureImage, signatureLayout);

  return Math.max(
    panelHeight,
    sealHeight,
    certifiedStampHeight,
    signatureHeight,
  );
}

function resolveTextScanMode(textLayout) {
  const raw = typeof textLayout?.scanMode === "string" ? textLayout.scanMode : "";
  const normalized = raw.trim().toLowerCase();

  if (normalized === "textonly") {
    return "textOnly";
  }

  if (normalized === "textandimages") {
    return "textAndImages";
  }

  return "auto";
}

function toPositiveNumberFromPdfObject(value) {
  if (value === null || value === undefined) {
    return Number.NaN;
  }

  if (typeof value?.asNumber === "function") {
    const n = Number(value.asNumber());
    return Number.isFinite(n) && n > 0 ? n : Number.NaN;
  }

  const n = Number(String(value));
  return Number.isFinite(n) && n > 0 ? n : Number.NaN;
}

function inspectPageImageSignal(page, pageMetrics, classifierConfig) {
  const pageWidth = Math.max(1, toNumber(pageMetrics?.visualWidth, page?.getWidth()));
  const pageHeight = Math.max(1, toNumber(pageMetrics?.visualHeight, page?.getHeight()));
  const classifier = getClassifierConfig({ classifier: classifierConfig });
  const pageAspect = pageWidth / pageHeight;
  const signal = {
    hasImages: false,
    imageCount: 0,
    largestImageWidth: 0,
    largestImageHeight: 0,
    largestImageBytesPerPixel: Number.POSITIVE_INFINITY,
    largestImageAspectDelta: Number.POSITIVE_INFINITY,
    likelyFullPageScan: false,
  };

  try {
    const resources = page?.node?.Resources?.();
    if (!resources || !(resources instanceof PDFDict)) {
      return signal;
    }

    const xObjectDict = resources.lookupMaybe(PDFName.of("XObject"), PDFDict);
    if (!xObjectDict) {
      return signal;
    }

    for (const key of xObjectDict.keys()) {
      const xObject = xObjectDict.lookup(key);
      if (!(xObject instanceof PDFStream)) {
        continue;
      }

      const subtype = xObject.dict.get(PDFName.of("Subtype"));
      if (!subtype || subtype.toString() !== "/Image") {
        continue;
      }

      signal.hasImages = true;
      signal.imageCount += 1;

      const width = toPositiveNumberFromPdfObject(
        xObject.dict.get(PDFName.of("Width")),
      );
      const height = toPositiveNumberFromPdfObject(
        xObject.dict.get(PDFName.of("Height")),
      );
      if (!Number.isFinite(width) || !Number.isFinite(height)) {
        continue;
      }

      const area = width * height;
      const currentLargestArea = signal.largestImageWidth * signal.largestImageHeight;
      if (area <= currentLargestArea) {
        continue;
      }

      signal.largestImageWidth = width;
      signal.largestImageHeight = height;
      try {
        const rawBytes = xObject.getContents?.();
        const rawLength =
          rawBytes && typeof rawBytes.length === "number" ? rawBytes.length : 0;
        signal.largestImageBytesPerPixel =
          rawLength > 0 && area > 0 ? rawLength / area : Number.POSITIVE_INFINITY;
      } catch (_err) {
        signal.largestImageBytesPerPixel = Number.POSITIVE_INFINITY;
      }
      const imageAspect = width / height;
      signal.largestImageAspectDelta =
        pageAspect > 0 ? Math.abs(imageAspect - pageAspect) / pageAspect : 0;
    }
  } catch (_err) {
    return signal;
  }

  const minExpectedWidth = pageWidth * classifier.fullPageImageMinWidthRatio;
  const minExpectedHeight = pageHeight * classifier.fullPageImageMinHeightRatio;
  const hasLargeImage =
    signal.largestImageWidth >= minExpectedWidth &&
    signal.largestImageHeight >= minExpectedHeight;
  const isAspectClose =
    Number.isFinite(signal.largestImageAspectDelta) &&
    signal.largestImageAspectDelta <= classifier.fullPageAspectDeltaMax;
  signal.likelyFullPageScan =
    signal.hasImages && signal.imageCount === 1 && hasLargeImage && isAspectClose;

  return signal;
}

function pageHasEmbeddedImageXObject(page, pageMetrics, classifierConfig) {
  return inspectPageImageSignal(page, pageMetrics, classifierConfig).hasImages;
}

function isManualScanModeOverride(textLayout) {
  if (typeof textLayout?.scanImages === "boolean") {
    return true;
  }

  if (typeof textLayout?.scanMode !== "string") {
    return false;
  }

  const mode = resolveTextScanMode(textLayout);
  return mode === "textOnly" || mode === "textAndImages";
}

function isDigitalProfileName(profileName) {
  return typeof profileName === "string" && profileName.startsWith("digital_");
}

function isScanProfileName(profileName) {
  return typeof profileName === "string" && profileName.startsWith("scan_");
}

function hasManualAnchorOverride(textLayout) {
  return (
    isPlainObject(textLayout) &&
    Object.prototype.hasOwnProperty.call(textLayout, "anchorToLastText")
  );
}

function shouldForceTextOnlyForLikelyFullPageScan(
  textLayout,
  selectedProfileName,
  pageImageSignal,
) {
  if (!isDigitalProfileName(selectedProfileName)) {
    return false;
  }
  if (!pageImageSignal?.likelyFullPageScan) {
    return false;
  }
  if (isManualScanModeOverride(textLayout)) {
    return false;
  }

  return resolveTextScanMode(textLayout) === "auto";
}

function shouldEnableImageAnchorForPartialScan({
  selectedProfileName,
  pageImageSignal,
  requestTextLayout,
}) {
  if (!isScanProfileName(selectedProfileName)) {
    return false;
  }
  if (!pageImageSignal?.hasImages) {
    return false;
  }
  if (pageImageSignal?.likelyFullPageScan) {
    return false;
  }
  if (toNumber(pageImageSignal?.imageCount, 0) < 2) {
    return false;
  }
  if (hasManualAnchorOverride(requestTextLayout)) {
    return false;
  }
  if (isManualScanModeOverride(requestTextLayout)) {
    return false;
  }

  return true;
}

const SCAN_MARGIN_LIFT_THRESHOLD = 72;

function shouldLiftScanMarginFromProbe({
  selectedProfileName,
  requestTextLayout,
  probeMarginBottom,
  baseMarginBottom,
}) {
  if (!isScanProfileName(selectedProfileName)) {
    return false;
  }
  if (hasManualAnchorOverride(requestTextLayout)) {
    return false;
  }
  if (isManualScanModeOverride(requestTextLayout)) {
    return false;
  }
  if (!Number.isFinite(probeMarginBottom) || !Number.isFinite(baseMarginBottom)) {
    return false;
  }

  return probeMarginBottom >= baseMarginBottom + SCAN_MARGIN_LIFT_THRESHOLD;
}

function shouldScanImagesInAnchor(textLayout, pageImageSignal) {
  if (typeof textLayout?.scanImages === "boolean") {
    return textLayout.scanImages;
  }

  const scanMode = resolveTextScanMode(textLayout);
  if (scanMode === "textOnly") {
    return false;
  }

  if (scanMode === "textAndImages") {
    return true;
  }

  return pageImageSignal?.hasImages === true;
}

function resolveSampleStep(length, divisor, maxStep = 8) {
  const safeLength = Math.max(1, toNumber(length, 1));
  const safeDivisor = Math.max(32, toNumber(divisor, 720));
  return Math.min(maxStep, Math.max(1, Math.floor(safeLength / safeDivisor)));
}

function isInkPixel(data, pixelOffset, stride, probeConfig) {
  const lumaThreshold = toNumber(probeConfig?.lumaThreshold, 244);
  const alphaThreshold = toNumber(probeConfig?.alphaThreshold, 20);

  if (stride <= 0) {
    return false;
  }

  if (stride === 1) {
    return Number(data[pixelOffset]) < lumaThreshold;
  }

  if (stride === 2) {
    const gray = Number(data[pixelOffset]);
    const alpha = Number(data[pixelOffset + 1]);
    return alpha >= alphaThreshold && gray < lumaThreshold;
  }

  const r = Number(data[pixelOffset]);
  const g = Number(data[pixelOffset + 1]);
  const b = Number(data[pixelOffset + 2]);
  const alpha = stride >= 4 ? Number(data[pixelOffset + 3]) : 255;
  if (alpha < alphaThreshold) {
    return false;
  }

  const luma = 0.2126 * r + 0.7152 * g + 0.0722 * b;
  return luma < lumaThreshold;
}

function computeInkBoundsNormalized(imageLike, probeConfig) {
  const width = Math.floor(toNumber(imageLike?.width, 0));
  const height = Math.floor(toNumber(imageLike?.height, 0));
  const data = imageLike?.data;

  if (
    width <= 0 ||
    height <= 0 ||
    !data ||
    typeof data.length !== "number" ||
    data.length === 0
  ) {
    return null;
  }

  const minImageWidth = Math.max(1, toNumber(probeConfig?.minImageWidth, 120));
  const minImageHeight = Math.max(1, toNumber(probeConfig?.minImageHeight, 120));
  if (width < minImageWidth || height < minImageHeight) {
    return null;
  }

  const pixelCount = width * height;
  if (pixelCount <= 0) {
    return null;
  }

  const stride = Math.floor(data.length / pixelCount);
  if (stride < 1 || stride > 4) {
    return null;
  }

  const rowStep = resolveSampleStep(height, probeConfig?.rowSampleDivisor, 6);
  const colStep = resolveSampleStep(width, probeConfig?.colSampleDivisor, 8);
  const rowInkRatioMin = Math.max(
    0.00005,
    toNumber(probeConfig?.rowInkRatioMin, 0.003),
  );
  const colInkRatioMin = Math.max(
    0.00005,
    toNumber(probeConfig?.colInkRatioMin, rowInkRatioMin),
  );

  const rowEntries = [];
  const rowRatios = [];
  for (let y = 0; y < height; y += rowStep) {
    let rowSamples = 0;
    let rowInk = 0;
    let rowMinX = width;
    let rowMaxX = -1;

    for (let x = 0; x < width; x += colStep) {
      const pixelOffset = (y * width + x) * stride;
      rowSamples += 1;
      if (!isInkPixel(data, pixelOffset, stride, probeConfig)) {
        continue;
      }

      rowInk += 1;
      if (x < rowMinX) {
        rowMinX = x;
      }
      if (x > rowMaxX) {
        rowMaxX = x;
      }
    }

    const ratio = rowSamples > 0 ? rowInk / rowSamples : 0;
    rowRatios.push(ratio);
    rowEntries.push({
      y,
      ratio,
      rowMinX,
      rowMaxX,
    });
  }

  if (rowEntries.length === 0) {
    return null;
  }

  const sortedRowRatios = [...rowRatios].sort((a, b) => a - b);
  const rowP85Ratio =
    sortedRowRatios[Math.floor((sortedRowRatios.length - 1) * 0.85)] || 0;
  const rowPeakRatio = sortedRowRatios[sortedRowRatios.length - 1] || 0;
  const rowMedianRatio =
    sortedRowRatios[Math.floor((sortedRowRatios.length - 1) * 0.5)] || 0;
  const topHalfCount = Math.max(1, Math.floor(rowRatios.length * 0.5));
  const topHalfMean =
    rowRatios.slice(0, topHalfCount).reduce((sum, value) => sum + value, 0) /
    topHalfCount;
  const bottomHalfValues = rowRatios.slice(topHalfCount);
  const bottomHalfMean =
    (bottomHalfValues.length > 0
      ? bottomHalfValues.reduce((sum, value) => sum + value, 0) /
        bottomHalfValues.length
      : topHalfMean) || 0;
  const bottomToTopInkRatio =
    topHalfMean > 0 ? bottomHalfMean / topHalfMean : Number.POSITIVE_INFINITY;
  const trailingBlankThreshold = Math.max(
    0.0002,
    rowMedianRatio * 0.4,
    rowInkRatioMin * 0.6,
  );
  let trailingBlankRows = 0;
  for (let i = rowEntries.length - 1; i >= 0; i -= 1) {
    const row = rowEntries[i];
    if (!row || row.ratio <= trailingBlankThreshold || row.rowMaxX < 0) {
      trailingBlankRows += 1;
      continue;
    }
    break;
  }
  const trailingBlankRatio =
    rowEntries.length > 0 ? trailingBlankRows / rowEntries.length : 0;
  const rowAdaptiveDelta = Math.max(
    0,
    toNumber(probeConfig?.rowAdaptiveDelta, 0.02),
  );
  const effectiveRowThreshold = Math.max(
    rowInkRatioMin,
    rowMedianRatio + rowAdaptiveDelta,
    rowP85Ratio * 0.22,
    rowPeakRatio * 0.08,
  );

  let minXFromRows = width;
  let maxXFromRows = -1;
  let minY = height;
  let maxY = -1;
  for (const row of rowEntries) {
    if (row.ratio < effectiveRowThreshold || row.rowMaxX < 0) {
      continue;
    }

    if (row.y < minY) {
      minY = row.y;
    }
    if (row.y > maxY) {
      maxY = row.y;
    }
    if (row.rowMinX < minXFromRows) {
      minXFromRows = row.rowMinX;
    }
    if (row.rowMaxX > maxXFromRows) {
      maxXFromRows = row.rowMaxX;
    }
  }

  if (maxXFromRows < 0 || maxY < 0) {
    return null;
  }

  const colRatios = [];
  const colEntries = [];
  for (let x = 0; x < width; x += colStep) {
    let colSamples = 0;
    let colInk = 0;

    for (let y = 0; y < height; y += rowStep) {
      const pixelOffset = (y * width + x) * stride;
      colSamples += 1;
      if (isInkPixel(data, pixelOffset, stride, probeConfig)) {
        colInk += 1;
      }
    }

    const ratio = colSamples > 0 ? colInk / colSamples : 0;
    colRatios.push(ratio);
    colEntries.push({ x, ratio });
  }

  const sortedColRatios = [...colRatios].sort((a, b) => a - b);
  const colP85Ratio =
    sortedColRatios[Math.floor((sortedColRatios.length - 1) * 0.85)] || 0;
  const colPeakRatio = sortedColRatios[sortedColRatios.length - 1] || 0;
  const colMedianRatio =
    sortedColRatios[Math.floor((sortedColRatios.length - 1) * 0.5)] || 0;
  const colAdaptiveDelta = Math.max(
    0,
    toNumber(probeConfig?.colAdaptiveDelta, 0.015),
  );
  const effectiveColThreshold = Math.max(
    colInkRatioMin,
    colMedianRatio + colAdaptiveDelta,
    colP85Ratio * 0.22,
    colPeakRatio * 0.08,
  );

  let minX = width;
  let maxX = -1;
  for (const col of colEntries) {
    if (col.ratio < effectiveColThreshold) {
      continue;
    }
    if (col.x < minX) {
      minX = col.x;
    }
    if (col.x > maxX) {
      maxX = col.x;
    }
  }

  if (maxX < 0) {
    minX = minXFromRows;
    maxX = maxXFromRows;
  }

  if (parseBooleanLike(probeConfig?.fullPageTailDetection, false)) {
    const tailMinBlankRatio = Math.max(
      0.05,
      Math.min(0.9, toNumber(probeConfig?.tailMinBlankRatio, 0.2)),
    );
    const tailBlankRowFloor = Math.max(
      0.0001,
      toNumber(probeConfig?.tailBlankRowFloor, 0.0025),
    );
    const tailBlankMedianDelta = Math.max(
      0,
      toNumber(probeConfig?.tailBlankMedianDelta, 0.0015),
    );
    const tailBlankThreshold = Math.max(
      tailBlankRowFloor,
      rowMedianRatio + tailBlankMedianDelta,
    );

    trailingBlankRows = 0;
    for (let i = rowEntries.length - 1; i >= 0; i -= 1) {
      const row = rowEntries[i];
      if (!row || row.rowMaxX < 0 || row.ratio <= tailBlankThreshold) {
        trailingBlankRows += 1;
        continue;
      }
      break;
    }

    if (trailingBlankRows > 0 && trailingBlankRows < rowEntries.length) {
      const trailingBlankRatio = trailingBlankRows / rowEntries.length;
      if (trailingBlankRatio >= tailMinBlankRatio) {
        const contentIndex = Math.max(0, rowEntries.length - trailingBlankRows - 1);
        const contentRow = rowEntries[contentIndex];
        if (contentRow && Number.isFinite(contentRow.y)) {
          maxY = Math.min(maxY, contentRow.y + rowStep);
        }
      }
    }
  }

  return {
    minX: minX / width,
    maxX: Math.min(1, (maxX + colStep) / width),
    minY: minY / height,
    maxY: Math.min(1, (maxY + rowStep) / height),
    trailingBlankRatio,
    bottomToTopInkRatio,
  };
}

function resolveImageObjectFromPdfJs(page, args, imageObjectCache) {
  if (!Array.isArray(args) || args.length === 0) {
    return null;
  }

  const firstArg = args[0];
  if (typeof firstArg === "string") {
    const cacheKey = `obj:${firstArg}`;
    if (imageObjectCache.has(cacheKey)) {
      return imageObjectCache.get(cacheKey);
    }

    let resolved = null;
    try {
      resolved = page?.objs?.get?.(firstArg) || null;
    } catch (_err) {
      resolved = null;
    }

    imageObjectCache.set(cacheKey, resolved);
    return resolved;
  }

  if (isPlainObject(firstArg) && firstArg.data && firstArg.width && firstArg.height) {
    return firstArg;
  }

  return null;
}

function resolveImageInkDisplayBounds({
  page,
  args,
  ctm,
  viewportTransform,
  util,
  probeConfig,
  imageObjectCache,
  imageInkBoundsCache,
}) {
  const imageObject = resolveImageObjectFromPdfJs(page, args, imageObjectCache);
  if (!imageObject) {
    return null;
  }

  const cacheKey = Array.isArray(args) && typeof args[0] === "string"
    ? `ink:${args[0]}`
    : null;

  let normalizedBounds = null;
  if (cacheKey && imageInkBoundsCache.has(cacheKey)) {
    normalizedBounds = imageInkBoundsCache.get(cacheKey);
  } else {
    normalizedBounds = computeInkBoundsNormalized(imageObject, probeConfig);
    if (cacheKey) {
      imageInkBoundsCache.set(cacheKey, normalizedBounds);
    }
  }

  if (!normalizedBounds) {
    return null;
  }

  const points = [
    [normalizedBounds.minX, normalizedBounds.minY],
    [normalizedBounds.minX, normalizedBounds.maxY],
    [normalizedBounds.maxX, normalizedBounds.minY],
    [normalizedBounds.maxX, normalizedBounds.maxY],
  ].map((point) => util.applyTransform(util.applyTransform(point, ctm), viewportTransform));

  const ys = points.map((point) => point[1]).filter(Number.isFinite);
  if (ys.length === 0) {
    return null;
  }

  return {
    top: Math.min(...ys),
    bottom: Math.max(...ys),
    trailingBlankRatio: toNumber(normalizedBounds.trailingBlankRatio, Number.NaN),
    bottomToTopInkRatio: toNumber(
      normalizedBounds.bottomToTopInkRatio,
      Number.NaN,
    ),
  };
}

async function inspectPageTextSignal({
  pdfBuffer,
  pageIndex,
  rotation,
  analysisCache,
}) {
  if (!pdfBuffer) {
    return {
      textItemCount: 0,
      charCount: 0,
      hasText: false,
      failed: true,
    };
  }

  const cacheKey = `text:${pageIndex}:${normalizeRotationAngle(rotation)}`;
  if (analysisCache?.textSignal?.has(cacheKey)) {
    if (Number.isFinite(analysisCache?.hits)) {
      analysisCache.hits += 1;
    }
    return analysisCache.textSignal.get(cacheKey);
  }
  if (Number.isFinite(analysisCache?.misses)) {
    analysisCache.misses += 1;
  }

  const pdfData =
    pdfBuffer instanceof Uint8Array
      ? new Uint8Array(pdfBuffer.buffer, pdfBuffer.byteOffset, pdfBuffer.byteLength)
      : new Uint8Array(pdfBuffer);

  let loadingTask = null;

  try {
    const loadOptions = {
      data: pdfData,
      disableWorker: true,
      verbosity: pdfjsLib.VerbosityLevel.ERRORS,
    };

    if (PDFJS_STANDARD_FONT_DATA_URL) {
      loadOptions.standardFontDataUrl = PDFJS_STANDARD_FONT_DATA_URL;
    }

    loadingTask = pdfjsLib.getDocument(loadOptions);
    const pdf = await loadingTask.promise;
    const page = await pdf.getPage(pageIndex + 1);
    await page.getViewport({
      scale: 1,
      rotation: page.rotate || rotation || 0,
    });
    const textContent = await page.getTextContent();

    let textItemCount = 0;
    let charCount = 0;
    for (const item of textContent.items || []) {
      const text = typeof item.str === "string" ? item.str.trim() : "";
      if (!text) {
        continue;
      }
      textItemCount += 1;
      charCount += text.length;
    }

    const result = {
      textItemCount,
      charCount,
      hasText: textItemCount > 0,
      failed: false,
    };
    if (analysisCache?.textSignal) {
      analysisCache.textSignal.set(cacheKey, result);
    }
    return result;
  } catch (_err) {
    const result = {
      textItemCount: 0,
      charCount: 0,
      hasText: false,
      failed: true,
    };
    if (analysisCache?.textSignal) {
      analysisCache.textSignal.set(cacheKey, result);
    }
    return result;
  } finally {
    if (loadingTask && typeof loadingTask.destroy === "function") {
      try {
        loadingTask.destroy();
      } catch (_err) {
        // ignore cleanup errors
      }
    }
  }
}

function fallbackProfileSelection(reason, profilesConfig) {
  const defaultName =
    normalizeProfileName(profilesConfig?.defaultProfile) || "fallback_safe";
  const fallback =
    resolveProfileFromConfig(defaultName, profilesConfig) ||
    resolveProfileFromConfig("fallback_safe", profilesConfig) || {
      name: "fallback_safe",
      profile: asObject(BUILTIN_STAMP_PROFILES.profiles.fallback_safe),
    };

  return {
    name: fallback.name,
    profile: fallback.profile,
    confidence: "low",
    reason,
    fallbackUsed: true,
  };
}

async function classifyStampProfile({
  pdfBuffer,
  pageIndex,
  pageMetrics,
  pageImageSignal,
  options,
  profilesConfig,
  classifierConfig,
  analysisCache,
}) {
  const effectiveProfilesConfig = profilesConfig || loadStampProfiles();
  const classifier = getClassifierConfig({
    classifier: classifierConfig || effectiveProfilesConfig.classifier,
  });
  const manualProfileName = resolveProfileOverrideName(options);
  if (manualProfileName) {
    const manual = resolveProfileFromConfig(
      manualProfileName,
      effectiveProfilesConfig,
    );
    if (manual) {
      return {
        name: manual.name,
        profile: manual.profile,
        confidence: "manual",
        reason: "manual_profile",
        fallbackUsed: false,
      };
    }
  }

  const textSignal = await inspectPageTextSignal({
    pdfBuffer,
    pageIndex,
    rotation: pageMetrics?.rotation,
    analysisCache,
  });
  const orientation = pageMetrics?.orientation === "landscape"
    ? "landscape"
    : "portrait";
  const likelyFullPageScan = pageImageSignal?.likelyFullPageScan === true;
  const textItemCount = toNumber(textSignal?.textItemCount, 0);
  const charCount = toNumber(textSignal?.charCount, 0);
  const looksDigital =
    textItemCount >= classifier.digitalMinTextItems ||
    charCount >= classifier.digitalMinChars;
  const looksSparse =
    textItemCount <= classifier.sparseMaxTextItems &&
    charCount <= classifier.sparseMaxChars;

  let candidateName = "";
  let reason = "";
  let confidence = "medium";

  if (likelyFullPageScan || (pageImageSignal?.hasImages && !looksDigital)) {
    if (orientation === "landscape") {
      candidateName = looksSparse
        ? "scan_sparse_landscape"
        : "scan_dense_landscape";
    } else {
      candidateName = looksSparse ? "scan_sparse_portrait" : "scan_dense_portrait";
    }
    reason = likelyFullPageScan ? "full_page_scan_signal" : "image_heavy_scan";
    confidence = likelyFullPageScan ? "high" : "medium";
  } else if (looksDigital) {
    candidateName =
      orientation === "landscape"
        ? "digital_text_landscape"
        : "digital_text_portrait";
    reason = "text_layer_digital";
    confidence = "high";
  } else {
    return fallbackProfileSelection(
      "low_confidence_classifier",
      effectiveProfilesConfig,
    );
  }

  const resolved = resolveProfileFromConfig(candidateName, effectiveProfilesConfig);
  if (!resolved) {
    return fallbackProfileSelection("profile_not_found", effectiveProfilesConfig);
  }

  return {
    name: resolved.name,
    profile: resolved.profile,
    confidence,
    reason,
    fallbackUsed: false,
    textItemCount,
    charCount,
  };
}

function parseBooleanLike(value, fallback) {
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

function shouldLogStampTiming(options) {
  if (typeof options?.logTiming === "boolean") {
    return options.logTiming;
  }

  return parseBooleanLike(process.env.PDF_STAMP_LOG_TIMING, true);
}

function shouldSkipSignatureInject(options, signatureFieldsLayout) {
  if (signatureFieldsLayout?.enabled === false) {
    return {
      skip: true,
      reason: "layout_disabled",
    };
  }

  const explicitDisable = parseBooleanLike(
    options?.disableSignatureFieldInjection,
    undefined,
  );
  if (explicitDisable === true) {
    return {
      skip: true,
      reason: "request_disabled",
    };
  }

  if (parseBooleanLike(process.env.PDF_STAMP_DISABLE_SIGNATURE_INJECT, false)) {
    return {
      skip: true,
      reason: "env_disabled",
    };
  }

  return {
    skip: false,
    reason: "",
  };
}

function roundTimingMs(value) {
  if (!Number.isFinite(value)) {
    return 0;
  }
  return Math.round(value * 10) / 10;
}

async function findLastContentBaselineY(
  pdfBuffer,
  pageIndex,
  rotation,
  ignoredTexts,
  textLayout,
  scanImages,
  scanProbeConfig,
  analysisCache,
  probeDiagnostics,
) {
  if (!pdfBuffer) {
    return null;
  }

  // pdfjs expects binary data as Uint8Array (not Node Buffer).
  const pdfData =
    pdfBuffer instanceof Uint8Array
      ? new Uint8Array(pdfBuffer.buffer, pdfBuffer.byteOffset, pdfBuffer.byteLength)
      : new Uint8Array(pdfBuffer);

  const ignoredSet = new Set(
    Array.isArray(ignoredTexts)
      ? ignoredTexts
          .map((value) =>
            typeof value === "string" ? value.trim().toUpperCase() : "",
          )
          .filter(Boolean)
      : [],
  );

  const cacheKey = [
    "baseline",
    pageIndex,
    normalizeRotationAngle(rotation),
    scanImages ? "img1" : "img0",
    textLayout?.ignoreFooter === false ? "footer0" : "footer1",
    toNumber(textLayout?.footerIgnoreRatio, 0.06),
    toNumber(textLayout?.footerIgnoreHeight, -1),
  ].join(":");

  if (analysisCache?.baseline?.has(cacheKey)) {
    if (Number.isFinite(analysisCache?.hits)) {
      analysisCache.hits += 1;
    }
    return analysisCache.baseline.get(cacheKey);
  }
  if (Number.isFinite(analysisCache?.misses)) {
    analysisCache.misses += 1;
  }

  let loadingTask = null;
  const imageObjectCache = new Map();
  const imageInkBoundsCache = new Map();

  try {
    const loadOptions = {
      data: pdfData,
      disableWorker: true,
      verbosity: pdfjsLib.VerbosityLevel.ERRORS,
    };

    if (PDFJS_STANDARD_FONT_DATA_URL) {
      loadOptions.standardFontDataUrl = PDFJS_STANDARD_FONT_DATA_URL;
    }

    loadingTask = pdfjsLib.getDocument(loadOptions);
    const pdf = await loadingTask.promise;
    const page = await pdf.getPage(pageIndex + 1);
    const viewport = page.getViewport({
      scale: 1,
      rotation: page.rotate || rotation || 0,
    });
    const textContent = await page.getTextContent();
    const footerIgnoreEnabled = textLayout?.ignoreFooter !== false;
    const footerIgnoreRatio = Math.max(
      0,
      toNumber(textLayout?.footerIgnoreRatio, 0.06),
    );
    const footerIgnoreHeight = Math.max(
      0,
      toNumber(
        textLayout?.footerIgnoreHeight,
        viewport.height * footerIgnoreRatio,
      ),
    );
    const footerStartY = footerIgnoreEnabled
      ? Math.max(0, viewport.height - footerIgnoreHeight)
      : Number.POSITIVE_INFINITY;

    let maxTextY = Number.NEGATIVE_INFINITY;
    let maxImageY = Number.NEGATIVE_INFINITY;
    let maxTextYAll = Number.NEGATIVE_INFINITY;
    let maxImageYAll = Number.NEGATIVE_INFINITY;
    let maxTrailingBlankRatio = Number.NEGATIVE_INFINITY;
    let minBottomToTopInkRatio = Number.POSITIVE_INFINITY;
    let inkStatsObserved = false;

    for (const item of textContent.items || []) {
      const text = typeof item.str === "string" ? item.str.trim() : "";
      if (!text) {
        continue;
      }

      if (ignoredSet.has(text.toUpperCase())) {
        continue;
      }

      const transformed = pdfjsLib.Util.transform(
        viewport.transform,
        item.transform,
      );
      const y = transformed[5];
      if (!Number.isFinite(y)) {
        continue;
      }

      // viewport.transform already returns top-origin display coordinates.
      const displayY = y;
      if (Number.isFinite(displayY) && displayY > maxTextYAll) {
        maxTextYAll = displayY;
      }
      if (footerIgnoreEnabled && displayY >= footerStartY) {
        continue;
      }
      if (Number.isFinite(displayY) && displayY > maxTextY) {
        maxTextY = displayY;
      }
    }

    if (scanImages) {
      try {
        const opList = await page.getOperatorList();
        const fnArray = opList.fnArray || [];
        const argsArray = opList.argsArray || [];
        const { OPS, Util } = pdfjsLib;
        let ctm = [1, 0, 0, 1, 0, 0];
        const stack = [];

        for (let i = 0; i < fnArray.length; i += 1) {
          const fn = fnArray[i];
        const args = argsArray[i];

          switch (fn) {
            case OPS.save:
              stack.push(ctm);
              break;
            case OPS.restore:
              ctm = stack.pop() || ctm;
              break;
            case OPS.transform:
              if (Array.isArray(args) && args.length >= 6) {
                ctm = Util.transform(ctm, args);
              }
              break;
            case OPS.paintFormXObjectBegin: {
              stack.push(ctm);
              if (
                Array.isArray(args) &&
                Array.isArray(args[0]) &&
                args[0].length >= 6
              ) {
                ctm = Util.transform(ctm, args[0]);
              }
              if (
                Array.isArray(args) &&
                Array.isArray(args[1]) &&
                args[1].length >= 4
              ) {
                const bbox = Util.getAxialAlignedBoundingBox(args[1], ctm);
                const points = [
                  Util.applyTransform([bbox[0], bbox[1]], viewport.transform),
                  Util.applyTransform([bbox[0], bbox[3]], viewport.transform),
                  Util.applyTransform([bbox[2], bbox[1]], viewport.transform),
                  Util.applyTransform([bbox[2], bbox[3]], viewport.transform),
                ];
                const displayBottom = Math.max(...points.map((pt) => pt[1]));
                const displayTop = Math.min(...points.map((pt) => pt[1]));
                if (
                  Number.isFinite(displayBottom) &&
                  displayBottom > maxImageYAll
                ) {
                  maxImageYAll = displayBottom;
                }

                if (footerIgnoreEnabled && displayTop >= footerStartY) {
                  break;
                }

                const effectiveBottom =
                  footerIgnoreEnabled && displayBottom >= footerStartY
                    ? footerStartY - 1
                    : displayBottom;

                if (
                  Number.isFinite(effectiveBottom) &&
                  effectiveBottom > maxImageY
                ) {
                  maxImageY = effectiveBottom;
                }
              }
              break;
            }
            case OPS.paintFormXObjectEnd:
              ctm = stack.pop() || ctm;
              break;
            case OPS.paintImageXObject:
            case OPS.paintJpegXObject:
            case OPS.paintImageXObjectRepeat:
            case OPS.paintInlineImageXObject:
            case OPS.paintInlineImageXObjectGroup:
            case OPS.paintImageMaskXObject:
            case OPS.paintImageMaskXObjectGroup:
            case OPS.paintImageMaskXObjectRepeat: {
              const bbox = Util.getAxialAlignedBoundingBox([0, 0, 1, 1], ctm);
              const points = [
                Util.applyTransform([bbox[0], bbox[1]], viewport.transform),
                Util.applyTransform([bbox[0], bbox[3]], viewport.transform),
                Util.applyTransform([bbox[2], bbox[1]], viewport.transform),
                Util.applyTransform([bbox[2], bbox[3]], viewport.transform),
              ];
              const displayBottom = Math.max(...points.map((pt) => pt[1]));
              const displayTop = Math.min(...points.map((pt) => pt[1]));
              let contentBottom = displayBottom;
              let contentTop = displayTop;

              if (scanProbeConfig?.enabled !== false) {
                const inkBounds = resolveImageInkDisplayBounds({
                  page,
                  args,
                  ctm,
                  viewportTransform: viewport.transform,
                  util: Util,
                  probeConfig: scanProbeConfig,
                  imageObjectCache,
                  imageInkBoundsCache,
                });
                if (inkBounds) {
                  contentBottom = inkBounds.bottom;
                  contentTop = inkBounds.top;
                  if (Number.isFinite(inkBounds.trailingBlankRatio)) {
                    maxTrailingBlankRatio = Math.max(
                      maxTrailingBlankRatio,
                      inkBounds.trailingBlankRatio,
                    );
                  }
                  if (Number.isFinite(inkBounds.bottomToTopInkRatio)) {
                    minBottomToTopInkRatio = Math.min(
                      minBottomToTopInkRatio,
                      inkBounds.bottomToTopInkRatio,
                    );
                  }
                  inkStatsObserved = true;
                }
              }

              if (
                Number.isFinite(contentBottom) &&
                contentBottom > maxImageYAll
              ) {
                maxImageYAll = contentBottom;
              }

              if (footerIgnoreEnabled && contentTop >= footerStartY) {
                break;
              }

              const effectiveBottom =
                footerIgnoreEnabled && contentBottom >= footerStartY
                  ? footerStartY - 1
                  : contentBottom;

              if (
                Number.isFinite(effectiveBottom) &&
                effectiveBottom > maxImageY
              ) {
                maxImageY = effectiveBottom;
              }
              break;
            }
            default:
              break;
          }
        }
      } catch (_err) {
        // ignore operator parsing issues
      }
    }

    let baselineY = Number.NEGATIVE_INFINITY;
    if (Number.isFinite(maxTextY)) {
      baselineY = Math.max(baselineY, maxTextY);
    }
    if (Number.isFinite(maxImageY)) {
      baselineY = Math.max(baselineY, maxImageY);
    }
    if (!Number.isFinite(baselineY)) {
      if (Number.isFinite(maxTextYAll)) {
        baselineY = Math.max(baselineY, maxTextYAll);
      }
      if (Number.isFinite(maxImageYAll)) {
        baselineY = Math.max(baselineY, maxImageYAll);
      }
    }

    if (!Number.isFinite(baselineY)) {
      if (analysisCache?.baseline) {
        analysisCache.baseline.set(cacheKey, null);
      }
      return null;
    }

    const result = {
      baselineY,
      pageHeight: viewport.height,
      probeStats: {
        imageInkStatsObserved: inkStatsObserved,
        trailingBlankRatio: Number.isFinite(maxTrailingBlankRatio)
          ? maxTrailingBlankRatio
          : null,
        bottomToTopInkRatio: Number.isFinite(minBottomToTopInkRatio)
          ? minBottomToTopInkRatio
          : null,
      },
    };
    if (isPlainObject(probeDiagnostics)) {
      probeDiagnostics.imageInkStatsObserved = result.probeStats.imageInkStatsObserved;
      probeDiagnostics.trailingBlankRatio = result.probeStats.trailingBlankRatio;
      probeDiagnostics.bottomToTopInkRatio = result.probeStats.bottomToTopInkRatio;
    }
    if (analysisCache?.baseline) {
      analysisCache.baseline.set(cacheKey, result);
    }

    return result;
  } catch (_err) {
    if (analysisCache?.baseline) {
      analysisCache.baseline.set(cacheKey, null);
    }
    return null;
  } finally {
    if (loadingTask && typeof loadingTask.destroy === "function") {
      try {
        loadingTask.destroy();
      } catch (_err) {
        // ignore cleanup errors
      }
    }
  }
}

async function resolveAutoMarginBottom({
  pdfBuffer,
  pageIndex,
  pageMetrics,
  textLayout,
  stampBlockHeight,
  ignoredTexts,
  scanImages,
  allowWhenAnchorDisabled,
  scanProbeConfig,
  analysisCache,
  probeDiagnostics,
}) {
  if (textLayout.anchorToLastText === false && allowWhenAnchorDisabled !== true) {
    return null;
  }

  if (!Number.isFinite(stampBlockHeight) || stampBlockHeight <= 0) {
    return null;
  }

  const lastText = await findLastContentBaselineY(
    pdfBuffer,
    pageIndex,
    pageMetrics?.rotation,
    ignoredTexts,
    textLayout,
    scanImages,
    scanProbeConfig,
    analysisCache,
    probeDiagnostics,
  );
  if (!lastText || !Number.isFinite(lastText.baselineY)) {
    return null;
  }

  const minMarginBottom = toNumber(textLayout.minMarginBottom, 12);
  const bottomSafeMargin = toNumber(textLayout.bottomSafeMargin, 36);
  const effectiveMinMargin = Math.max(minMarginBottom, bottomSafeMargin);
  const lastTextGap = toNumber(textLayout.lastTextGap, 12);
  const pageHeight = toNumber(lastText.pageHeight, pageMetrics?.visualHeight);
  const maxBottom = pageHeight - stampBlockHeight;

  if (!Number.isFinite(maxBottom) || maxBottom <= 0) {
    return null;
  }

  // baselineY is measured in display space (top-origin), while marginBottom
  // is measured from PDF bottom-origin, so convert before placing the panel.
  const targetBottom =
    pageHeight - lastText.baselineY - lastTextGap - stampBlockHeight;

  if (!Number.isFinite(targetBottom)) {
    return null;
  }

  return Math.min(Math.max(targetBottom, effectiveMinMargin), maxBottom);
}

const DEFAULT_NOTARY_TITLE = "C\u00d4NG CH\u1ee8NG VI\u00caN";

function resolveCertificationText(rawText) {
  if (typeof rawText !== "string") {
    return DEFAULT_CERTIFICATION_TEXT;
  }

  const trimmed = rawText.trim();
  if (!trimmed) {
    return DEFAULT_CERTIFICATION_TEXT;
  }

  // Guard against mojibake/encoding loss from non-UTF8 clients.
  if (trimmed.includes("?") || trimmed.includes("\uFFFD")) {
    return DEFAULT_CERTIFICATION_TEXT;
  }

  return trimmed;
}

function resolveNotaryTitle(rawTitle) {
  if (typeof rawTitle !== "string") {
    return DEFAULT_NOTARY_TITLE;
  }

  const trimmed = rawTitle.trim();
  if (!trimmed) {
    return DEFAULT_NOTARY_TITLE;
  }

  // Guard against mojibake/encoding loss from non-UTF8 clients.
  if (trimmed.includes("?") || trimmed.includes("\uFFFD")) {
    return DEFAULT_NOTARY_TITLE;
  }

  return trimmed;
}

function formatDateLine(dateValue) {
  if (typeof dateValue !== "string" || !dateValue.trim()) {
    return "Ng\u00e0y -- th\u00e1ng -- n\u0103m ----";
  }

  const raw = dateValue.trim();
  let day = null;
  let month = null;
  let year = null;

  if (/^\d{4}-\d{1,2}-\d{1,2}$/.test(raw)) {
    const [y, m, d] = raw.split("-").map((n) => Number(n));
    year = y;
    month = m;
    day = d;
  } else if (/^\d{1,2}\/\d{1,2}\/\d{4}$/.test(raw)) {
    const [d, m, y] = raw.split("/").map((n) => Number(n));
    year = y;
    month = m;
    day = d;
  } else {
    const parsed = new Date(raw);
    if (!Number.isNaN(parsed.getTime())) {
      year = parsed.getFullYear();
      month = parsed.getMonth() + 1;
      day = parsed.getDate();
    }
  }

  if (!day || !month || !year) {
    return `Ng\u00e0y ${raw}`;
  }

  const dd = String(day).padStart(2, "0");
  const mm = String(month).padStart(2, "0");
  return `Ng\u00e0y ${dd} th\u00e1ng ${mm} n\u0103m ${year}`;
}
function buildStampLines(options) {
  const heading = resolveCertificationText(
    options.certificationText,
  ).toLocaleUpperCase("vi-VN");
  const certNo = options.certificationNumber || "--";
  const bookNo = options.certificationBookNumber || "--";
  const notaryTitle = resolveNotaryTitle(options.notaryTitle).toLocaleUpperCase(
    "vi-VN",
  );

  return {
    heading,
    line2: `S\u1ed1 ch\u1ee9ng th\u1ef1c: ${certNo}  Quy\u1ec3n s\u1ed1: ${bookNo} SCT/BS`,
    line3: formatDateLine(options.certificationDate),
    line4: notaryTitle,
  };
}

function resolveCopyStampText(options) {
  const isDisabledExplicitly =
    options?.copyStampEnabled === false || options?.enableCopyStamp === false;

  if (isDisabledExplicitly) {
    return null;
  }

  if (options && typeof options.copyStampText === "string") {
    const trimmed = options.copyStampText.trim();
    return trimmed ? trimmed.toUpperCase() : null;
  }

  if (options && options.copyStampText === null) {
    return null;
  }

  return "B\u1ea2N SAO";
}

function resolveCopyStampPageIndex(options, pageCount) {
  const totalPages = Number.isFinite(pageCount) ? pageCount : 0;
  if (totalPages <= 1) {
    return 0;
  }

  const raw = options?.copyStampPage;
  if (typeof raw === "number" && Number.isFinite(raw)) {
    const idx = Math.round(raw);
    return Math.min(Math.max(idx, 0), totalPages - 1);
  }

  if (typeof raw === "string") {
    const normalized = raw.trim().toLowerCase();
    if (normalized === "last") {
      return totalPages - 1;
    }
    if (normalized === "first") {
      return 0;
    }
  }

  return 0;
}

function getAnchorPosition(pageMetrics, layout, boxWidth, boxHeight) {
  const pageWidth = toNumber(pageMetrics?.visualWidth, boxWidth);
  const pageHeight = toNumber(pageMetrics?.visualHeight, boxHeight);
  const marginRight = toNumber(layout.marginRight, 36);
  const marginLeft = toNumber(layout.marginLeft, marginRight);
  const marginBottom = hasNumericValue(layout.marginBottom)
    ? Number(layout.marginBottom)
    : Math.max(72, pageHeight * 0.12);
  const marginTop = toNumber(layout.marginTop, marginBottom);
  const offsetX = toNumber(layout.offsetX, 0);
  const offsetY = toNumber(layout.offsetY, 0);

  const anchorRaw = typeof layout.anchor === "string" ? layout.anchor : "";
  const anchor = anchorRaw.trim().toLowerCase() || "bottom-right";

  let x = pageWidth - marginRight - boxWidth - offsetX;
  let y = marginBottom + offsetY;

  if (anchor === "bottom-left") {
    x = marginLeft + offsetX;
    y = marginBottom + offsetY;
  } else if (anchor === "top-left") {
    x = marginLeft + offsetX;
    y = pageHeight - marginTop - boxHeight - offsetY;
  } else if (anchor === "top-right") {
    x = pageWidth - marginRight - boxWidth - offsetX;
    y = pageHeight - marginTop - boxHeight - offsetY;
  }

  return { x, y };
}

function drawBottomRightImage(page, image, layout, pageMetrics) {
  if (!image) {
    return null;
  }

  const pageHeight = toNumber(pageMetrics?.visualHeight, page.getHeight());
  const targetWidth = toNumber(layout.width, 120);
  const targetHeight = toNumber(
    layout.height,
    (image.height / image.width) * targetWidth,
  );
  const marginBottom = hasNumericValue(layout.marginBottom)
    ? Number(layout.marginBottom)
    : Math.max(72, pageHeight * 0.12);
  const opacity = toNumber(layout.opacity, 1);
  const blendMode = resolveBlendMode(layout.blendMode, undefined);

  const { x, y } = getAnchorPosition(
    pageMetrics,
    {
      ...layout,
      marginBottom,
    },
    targetWidth,
    targetHeight,
  );

  const drawOptions = {
    x,
    y,
    width: targetWidth,
    height: targetHeight,
    opacity,
  };

  if (blendMode) {
    drawOptions.blendMode = blendMode;
  }

  page.drawImage(image, drawOptions);
  return {
    x,
    y,
    width: targetWidth,
    height: targetHeight,
  };
}

function drawPersonalSignatureNearOfficeSignature(
  page,
  signatureImage,
  signatureLayout,
  pageMetrics,
  officeSignatureRect,
) {
  if (!signatureImage || !officeSignatureRect) {
    return drawBottomRightImage(page, signatureImage, signatureLayout, pageMetrics);
  }

  if (signatureLayout?.attachToOfficeSignature === false) {
    return drawBottomRightImage(page, signatureImage, signatureLayout, pageMetrics);
  }

  const targetWidth = toNumber(signatureLayout.width, 140);
  const targetHeight = toNumber(
    signatureLayout.height,
    (signatureImage.height / signatureImage.width) * targetWidth,
  );
  const opacity = toNumber(signatureLayout.opacity, 1);
  const blendMode = resolveBlendMode(signatureLayout.blendMode, undefined);
  const offsetX = toNumber(signatureLayout.officeOffsetX, 0);
  const ratioY = toNumber(signatureLayout.officeOffsetYRatio, -0.2);
  const offsetY = hasNumericValue(signatureLayout.officeOffsetY)
    ? Number(signatureLayout.officeOffsetY)
    : officeSignatureRect.height * ratioY;
  const alignRaw =
    typeof signatureLayout.officeAlign === "string"
      ? signatureLayout.officeAlign.trim().toLowerCase()
      : "";

  let x = officeSignatureRect.x + offsetX;
  if (alignRaw === "center" || !alignRaw) {
    x = officeSignatureRect.x + (officeSignatureRect.width - targetWidth) / 2 + offsetX;
  } else if (alignRaw === "right") {
    x = officeSignatureRect.x + officeSignatureRect.width - targetWidth + offsetX;
  }

  let y = officeSignatureRect.y + offsetY;
  const visualWidth = toNumber(pageMetrics?.visualWidth, page.getWidth());
  const visualHeight = toNumber(pageMetrics?.visualHeight, page.getHeight());
  x = Math.max(0, Math.min(x, Math.max(0, visualWidth - targetWidth)));
  y = Math.max(0, Math.min(y, Math.max(0, visualHeight - targetHeight)));

  const drawOptions = {
    x,
    y,
    width: targetWidth,
    height: targetHeight,
    opacity,
  };
  if (blendMode) {
    drawOptions.blendMode = blendMode;
  }

  page.drawImage(signatureImage, drawOptions);
  return {
    x,
    y,
    width: targetWidth,
    height: targetHeight,
  };
}

function drawCopyStampText(page, data, layout, pageMetrics) {
  if (!data?.text) {
    return;
  }

  const pageHeight = toNumber(pageMetrics?.visualHeight, page.getHeight());
  const fontSize = toNumber(layout.size, 18);
  const paddingX = toNumber(layout.paddingX, 8);
  const paddingY = toNumber(layout.paddingY, 4);
  const borderWidth = toNumber(layout.borderWidth, 3);
  const marginBottom = hasNumericValue(layout.marginBottom)
    ? Number(layout.marginBottom)
    : Math.max(72, pageHeight * 0.12);

  const textWidth = data.font.widthOfTextAtSize(data.text, fontSize);
  const textHeight = data.font.heightAtSize(fontSize);
  const textAscent = data.font.heightAtSize(fontSize, { descender: false });
  const visualHeight =
    Number.isFinite(textAscent) && textAscent > 0 ? textAscent : textHeight;
  const baselineOffset = hasNumericValue(layout.textBaselineOffset)
    ? Number(layout.textBaselineOffset)
    : textHeight * 0;

  const boxWidth = hasNumericValue(layout.width)
    ? Number(layout.width)
    : textWidth + paddingX * 2;
  const boxHeight = hasNumericValue(layout.height)
    ? Number(layout.height)
    : textHeight + paddingY * 2;

  const { x, y } = getAnchorPosition(
    pageMetrics,
    {
      ...layout,
      marginBottom,
    },
    boxWidth,
    boxHeight,
  );

  page.drawRectangle({
    x,
    y,
    width: boxWidth,
    height: boxHeight,
    borderColor: HEADING_COLOR,
    borderWidth,
  });

  const textX = x + (boxWidth - textWidth) / 2;
  const textY = y + (boxHeight - visualHeight) / 2 + baselineOffset;

  page.drawText(data.text, {
    x: textX,
    y: textY,
    size: fontSize,
    font: data.font,
    color: HEADING_COLOR,
  });
}

function canFontRenderText(font, text) {
  if (!font || typeof text !== "string" || !text) {
    return false;
  }

  try {
    if (typeof font.encodeText === "function") {
      font.encodeText(text);
    }
    return true;
  } catch (_err) {
    return false;
  }
}

function resolvePanelFont(preferredFont, fallbackFont, text) {
  if (canFontRenderText(preferredFont, text)) {
    return preferredFont;
  }
  if (canFontRenderText(fallbackFont, text)) {
    return fallbackFont;
  }
  return preferredFont || fallbackFont;
}

function getCertificationPanelGeometry(page, data, layout, pageMetrics) {
  const pageWidth = toNumber(pageMetrics?.visualWidth, page.getWidth());
  const pageHeight = toNumber(pageMetrics?.visualHeight, page.getHeight());

  const marginLeft = toNumber(layout.marginLeft, 28);
  const marginBottom = hasNumericValue(layout.marginBottom)
    ? Number(layout.marginBottom)
    : Math.max(72, pageHeight * 0.12);
  const panelPaddingX = toNumber(layout.panelPaddingX, 10);
  const panelPaddingY = toNumber(layout.panelPaddingY, 8);
  const headingSize = toNumber(layout.headingSize, 13);
  const bodySize = toNumber(layout.bodySize, 11.5);
  const lineGap = toNumber(layout.lineGap, 4);
  const notaryGap = toNumber(layout.notaryGap, 4);
  const maxPanelWidth = toNumber(layout.maxPanelWidth, pageWidth * 0.74);
  const panelOpacity = toNumber(layout.panelOpacity, 0);
  const headingFont = resolvePanelFont(
    data.headingFont,
    data.bodyFont,
    data.lines.heading,
  );
  const notaryFont = resolvePanelFont(
    data.headingFont,
    data.bodyFont,
    data.lines.line4,
  );

  const headingWidth = headingFont.widthOfTextAtSize(
    data.lines.heading,
    headingSize,
  );
  const line2Width = data.bodyFont.widthOfTextAtSize(
    data.lines.line2,
    bodySize,
  );
  const line3Width = data.bodyFont.widthOfTextAtSize(
    data.lines.line3,
    bodySize,
  );
  const line4Width = notaryFont.widthOfTextAtSize(
    data.lines.line4,
    headingSize,
  );
  const contentWidth = Math.max(
    headingWidth,
    line2Width,
    line3Width,
    line4Width,
  );

  const panelWidth = Math.min(
    maxPanelWidth,
    contentWidth + panelPaddingX * 2,
    pageWidth - marginLeft - 20,
  );

  const headingLineHeight = headingSize + lineGap;
  const bodyLineHeight = bodySize + lineGap;
  const contentHeight =
    headingLineHeight +
    bodyLineHeight +
    bodyLineHeight +
    headingLineHeight +
    notaryGap;
  const panelHeight = contentHeight + panelPaddingY * 2;
  const textStartY = marginBottom + panelHeight - panelPaddingY - headingSize;
  const line1Y = textStartY;
  const line2Y = textStartY - headingLineHeight;
  const line3Y = textStartY - headingLineHeight - bodyLineHeight;
  const notaryLineY =
    textStartY -
    headingLineHeight -
    bodyLineHeight -
    bodyLineHeight -
    notaryGap;

  return {
    marginLeft,
    marginBottom,
    panelPaddingX,
    headingSize,
    bodySize,
    panelOpacity,
    headingFont,
    notaryFont,
    panelWidth,
    panelHeight,
    line1Y,
    line2Y,
    line3Y,
    notaryLineY,
  };
}

function sanitizeSignatureFieldName(value, fallback) {
  if (typeof value !== "string") {
    return fallback;
  }

  const sanitized = value.trim().replace(/[^\w.-]+/g, "_");
  return sanitized || fallback;
}

function reserveFieldName(preferredName, existingNames) {
  if (!existingNames.has(preferredName)) {
    existingNames.add(preferredName);
    return preferredName;
  }

  let i = 2;
  while (existingNames.has(`${preferredName}_${i}`)) {
    i += 1;
  }

  const candidate = `${preferredName}_${i}`;
  existingNames.add(candidate);
  return candidate;
}

function addSignatureFieldsForFoxit(
  pdfDoc,
  page,
  pageIndex,
  pageMetrics,
  panelGeometry,
  signatureFieldsLayout,
) {
  if (!panelGeometry || signatureFieldsLayout?.enabled === false) {
    return [];
  }

  const sideInset = Math.max(
    0,
    toNumber(signatureFieldsLayout?.sideInset, panelGeometry.panelPaddingX),
  );
  let centerGap = Math.max(0, toNumber(signatureFieldsLayout?.centerGap, 20));
  const lineGap = Math.max(0, toNumber(signatureFieldsLayout?.lineGap, 6));
  const minBottom = Math.max(0, toNumber(signatureFieldsLayout?.minBottom, 8));
  const minHeight = Math.max(10, toNumber(signatureFieldsLayout?.minHeight, 12));
  const minUsableHeight = Math.max(
    minHeight,
    toNumber(signatureFieldsLayout?.minUsableHeight, 36),
  );
  const minWidth = Math.max(24, toNumber(signatureFieldsLayout?.minWidth, 120));
  const overlap = signatureFieldsLayout?.overlap !== false;
  const enterpriseShiftLeftRatio = Math.max(
    0,
    toNumber(signatureFieldsLayout?.enterpriseShiftLeftRatio, 0.1),
  );

  const areaLeft = panelGeometry.marginLeft + sideInset;
  const areaRight =
    panelGeometry.marginLeft + panelGeometry.panelWidth - sideInset;
  const availableWidth = areaRight - areaLeft;

  if (!Number.isFinite(availableWidth) || availableWidth <= minWidth * 2) {
    return [];
  }

  if (centerGap > availableWidth * 0.35) {
    centerGap = availableWidth * 0.12;
  }

  let fieldWidth = hasNumericValue(signatureFieldsLayout?.width)
    ? Number(signatureFieldsLayout.width)
    : (availableWidth - centerGap) / 2;
  fieldWidth = Math.max(minWidth, fieldWidth);

  if (fieldWidth * 2 + centerGap > availableWidth) {
    fieldWidth = (availableWidth - centerGap) / 2;
  }

  if (!Number.isFinite(fieldWidth) || fieldWidth < 24) {
    return [];
  }
  let fieldHeight = Math.max(
    minHeight,
    toNumber(signatureFieldsLayout?.height, 52),
  );
  const overlapOffsetXRatio = hasNumericValue(
    signatureFieldsLayout?.overlapOffsetXRatio,
  )
    ? Number(signatureFieldsLayout.overlapOffsetXRatio)
    : 0;
  const overlapOffsetYRatio = hasNumericValue(
    signatureFieldsLayout?.overlapOffsetYRatio,
  )
    ? Number(signatureFieldsLayout.overlapOffsetYRatio)
    : -0.2;
  const overlapOffsetX = hasNumericValue(signatureFieldsLayout?.overlapOffsetX)
    ? Number(signatureFieldsLayout.overlapOffsetX)
    : fieldWidth * overlapOffsetXRatio;
  const overlapOffsetY = hasNumericValue(signatureFieldsLayout?.overlapOffsetY)
    ? Number(signatureFieldsLayout.overlapOffsetY)
    : fieldHeight * overlapOffsetYRatio;
  const enterpriseExtraShiftLeftRatio = Math.max(
    0,
    toNumber(signatureFieldsLayout?.enterpriseExtraShiftLeftRatio, 0.05),
  );
  const enterpriseExtraShiftLeft = overlap
    ? fieldWidth * enterpriseExtraShiftLeftRatio
    : 0;
  const maxTop = panelGeometry.notaryLineY - lineGap;
  if (!Number.isFinite(maxTop) || maxTop <= 0) {
    return [];
  }

  let lowerBound = minBottom;
  let availableHeight = maxTop - lowerBound;
  if (availableHeight < fieldHeight) {
    const relaxedLowerBoundForPreferred = Math.max(0, maxTop - fieldHeight);
    if (relaxedLowerBoundForPreferred < lowerBound) {
      lowerBound = relaxedLowerBoundForPreferred;
      availableHeight = maxTop - lowerBound;
    }
  }

  if (availableHeight < minUsableHeight) {
    const relaxedLowerBound = Math.max(0, maxTop - minUsableHeight);
    if (relaxedLowerBound < lowerBound) {
      lowerBound = relaxedLowerBound;
      availableHeight = maxTop - lowerBound;
    }
  }

  if (availableHeight < minHeight) {
    return [];
  }

  if (fieldHeight > availableHeight) {
    fieldHeight = availableHeight;
  }

  if (fieldHeight < minUsableHeight && availableHeight >= minUsableHeight) {
    fieldHeight = minUsableHeight;
  }

  if (!Number.isFinite(fieldHeight) || fieldHeight < minHeight) {
    return [];
  }

  const y = Math.max(lowerBound, maxTop - fieldHeight);

  const personalWidth = fieldWidth;
  const personalHeight = fieldHeight;
  const personalMinX = 0;
  const personalMaxX = Math.max(0, areaRight - personalWidth);
  const personalBaseX = areaLeft + overlapOffsetX;
  const personalX = Math.max(personalMinX, Math.min(personalBaseX, personalMaxX));
  const enterpriseShiftLeft = overlap
    ? fieldWidth * enterpriseShiftLeftRatio
    : 0;
  const enterpriseMaxX = Math.max(0, areaRight - fieldWidth);
  const enterpriseXRaw = personalX - enterpriseShiftLeft - enterpriseExtraShiftLeft;
  const enterpriseX = Math.max(0, Math.min(enterpriseXRaw, enterpriseMaxX));
  const enterpriseRectDisplay = {
    x: enterpriseX,
    y,
    width: fieldWidth,
    height: fieldHeight,
  };
  const personalYRaw = enterpriseRectDisplay.y + overlapOffsetY;
  const personalY = Math.max(
    lowerBound,
    Math.min(personalYRaw, maxTop - personalHeight),
  );
  const personalRectDisplay = overlap
    ? {
        x: Math.max(personalMinX, Math.min(personalX, personalMaxX)),
        y: personalY,
        width: personalWidth,
        height: personalHeight,
      }
    : {
        x: areaRight - fieldWidth,
        y,
        width: fieldWidth,
        height: fieldHeight,
      };

  const enterpriseRect = mapDisplayRectToPageRect(
    page,
    pageMetrics,
    enterpriseRectDisplay,
  );
  const personalRect = mapDisplayRectToPageRect(
    page,
    pageMetrics,
    personalRectDisplay,
  );

  const enterprisePreferredName = sanitizeSignatureFieldName(
    signatureFieldsLayout?.enterpriseName,
    "sig_enterprise",
  );
  const personalPreferredName = sanitizeSignatureFieldName(
    signatureFieldsLayout?.personalName,
    "sig_personal",
  );
  const replaceExisting = signatureFieldsLayout?.replaceExisting !== false;

  let enterpriseName = enterprisePreferredName;
  let personalName = personalPreferredName;

  if (!replaceExisting) {
    const form = pdfDoc.getForm();
    const existingNames = new Set(form.getFields().map((field) => field.getName()));
    enterpriseName = reserveFieldName(enterprisePreferredName, existingNames);
    personalName = reserveFieldName(personalPreferredName, existingNames);
  } else if (enterpriseName === personalName) {
    personalName = `${personalName}_2`;
  }

  return [
    {
      name: enterpriseName,
      pageIndex,
      x: enterpriseRect.x,
      y: enterpriseRect.y,
      width: enterpriseRect.width,
      height: enterpriseRect.height,
      rotation: normalizeRotationAngle(pageMetrics?.rotation),
    },
    {
      name: personalName,
      pageIndex,
      x: personalRect.x,
      y: personalRect.y,
      width: personalRect.width,
      height: personalRect.height,
      rotation: normalizeRotationAngle(pageMetrics?.rotation),
    },
  ];
}

let signatureFieldToolExePathCache;

function resolveSignatureFieldToolExePath() {
  if (signatureFieldToolExePathCache !== undefined) {
    return signatureFieldToolExePathCache;
  }

  const toolRelativeParts = [
    "tools",
    "signature-field-tool",
    "SignatureFieldTool",
    "publish",
    "win-x64",
    "SignatureFieldTool.exe",
  ];
  const baseDirs = [
    __dirname,
    path.join(__dirname, ".."),
    path.join(__dirname, "..", ".."),
    process.cwd(),
  ];
  const uniqueBaseDirs = [...new Set(baseDirs.map((x) => path.resolve(x)))];

  for (const baseDir of uniqueBaseDirs) {
    const exePath = path.join(baseDir, ...toolRelativeParts);
    if (fs.existsSync(exePath)) {
      signatureFieldToolExePathCache = exePath;
      return signatureFieldToolExePathCache;
    }
  }

  signatureFieldToolExePathCache = null;
  return signatureFieldToolExePathCache;
}

function injectSignatureFieldsWithIText(
  pdfBytes,
  fieldSpecs,
  signatureFieldsLayout,
) {
  if (!Array.isArray(fieldSpecs) || fieldSpecs.length === 0) {
    return pdfBytes;
  }

  const toolExePath = resolveSignatureFieldToolExePath();
  if (!toolExePath) {
    throw new Error(
      "SignatureFieldTool.exe not found. Build/publish tools/signature-field-tool/SignatureFieldTool first.",
    );
  }

  const tmpDir = fs.mkdtempSync(path.join(os.tmpdir(), "pdf-sigfields-"));
  const inputPath = path.join(tmpDir, "input.pdf");
  const jobPath = path.join(tmpDir, "job.json");

  try {
    fs.writeFileSync(inputPath, pdfBytes);

    const borderWidth = Math.max(0, toNumber(signatureFieldsLayout?.borderWidth, 0));
    const borderGrayRaw = toNumber(signatureFieldsLayout?.borderGray, 0.65);
    const borderGray = Math.min(1, Math.max(0, borderGrayRaw));
    const replaceExisting = signatureFieldsLayout?.replaceExisting !== false;

    const job = {
      input: inputPath,
      output: path.join(tmpDir, "output.pdf"),
      replaceExisting,
      borderWidth,
      borderGray,
      fields: fieldSpecs.map((field) => ({
        name: String(field.name || ""),
        pageIndex: Number(field.pageIndex),
        x: Number(field.x),
        y: Number(field.y),
        width: Number(field.width),
        height: Number(field.height),
        rotation: Number(field.rotation),
      })),
    };
    fs.writeFileSync(jobPath, JSON.stringify(job), "utf8");

    const runResult = spawnSync(toolExePath, ["--job", jobPath, "--stdout"], {
      maxBuffer: 64 * 1024 * 1024,
    });
    if (runResult.error || runResult.status !== 0) {
      const stderr = Buffer.isBuffer(runResult.stderr)
        ? runResult.stderr.toString("utf8").trim()
        : String(runResult.stderr || "").trim();
      const stdout = Buffer.isBuffer(runResult.stdout)
        ? runResult.stdout.toString("utf8").trim()
        : String(runResult.stdout || "").trim();
      const details = stderr || stdout || "unknown error";
      throw new Error(`Signature field injection failed: ${details}`);
    }

    const stdoutBytes = Buffer.isBuffer(runResult.stdout)
      ? runResult.stdout
      : Buffer.from(runResult.stdout || "", "binary");
    if (stdoutBytes.length > 0) {
      return stdoutBytes;
    }

    throw new Error("Signature field injection failed: no output bytes produced.");
  } finally {
    try {
      fs.rmSync(tmpDir, { recursive: true, force: true });
    } catch (_err) {
      // best effort temp cleanup
    }
  }
}

function drawCertificationPanel(page, data, layout, pageMetrics) {
  const panel = getCertificationPanelGeometry(page, data, layout, pageMetrics);

  if (panel.panelOpacity > 0) {
    page.drawRectangle({
      x: panel.marginLeft,
      y: panel.marginBottom,
      width: panel.panelWidth,
      height: panel.panelHeight,
      color: WHITE,
      opacity: panel.panelOpacity,
      borderOpacity: 0,
    });
  }

  page.drawText(data.lines.heading, {
    x: panel.marginLeft + panel.panelPaddingX,
    y: panel.line1Y,
    size: panel.headingSize,
    font: panel.headingFont || data.headingFont,
    color: HEADING_COLOR,
  });

  page.drawText(data.lines.line2, {
    x: panel.marginLeft + panel.panelPaddingX,
    y: panel.line2Y,
    size: panel.bodySize,
    font: data.bodyFont,
    color: BODY_COLOR,
  });

  page.drawText(data.lines.line3, {
    x: panel.marginLeft + panel.panelPaddingX,
    y: panel.line3Y,
    size: panel.bodySize,
    font: data.bodyFont,
    color: BODY_COLOR,
  });

  page.drawText(data.lines.line4, {
    x: panel.marginLeft + panel.panelPaddingX,
    y: panel.notaryLineY,
    size: panel.headingSize,
    font: panel.notaryFont || data.headingFont,
    color: HEADING_COLOR,
  });

  return panel;
}

async function tryEmbedCustomFont(pdfDoc, fontInput) {
  const bytes = toPdfBytes(fontInput);
  if (!bytes) {
    return null;
  }

  try {
    return await pdfDoc.embedFont(bytes, { subset: true });
  } catch (_err) {
    return null;
  }
}

function readFirstExistingFile(candidatePaths) {
  for (const candidate of candidatePaths) {
    if (!candidate) {
      continue;
    }

    try {
      if (fs.existsSync(candidate)) {
        return fs.readFileSync(candidate);
      }
    } catch (_err) {
      // skip bad path and continue
    }
  }

  return null;
}

function getDefaultFontCandidates() {
  const envRegular = process.env.PDF_STAMP_FONT_PATH;
  const envBold = process.env.PDF_STAMP_FONT_BOLD_PATH;
  const localFontsDir = path.join(__dirname, "fonts");

  return {
    regular: [
      envRegular,
      "C:\\Windows\\Fonts\\arial.ttf",
      "C:\\Windows\\Fonts\\tahoma.ttf",
      "/usr/share/fonts/truetype/msttcorefonts/Arial.ttf",
      "/usr/share/fonts/truetype/msttcorefonts/arial.ttf",
      "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf",
      "/usr/share/fonts/truetype/noto/NotoSans-Regular.ttf",
      "/usr/share/fonts/truetype/freefont/FreeSans.ttf",
      "/usr/share/fonts/truetype/liberation2/LiberationSans-Regular.ttf",
      "/usr/share/fonts/truetype/liberation/LiberationSans-Regular.ttf",
      path.join(localFontsDir, "DejaVuSans.ttf"),
    ],
    bold: [
      envBold,
      "C:\\Windows\\Fonts\\arialbd.ttf",
      "C:\\Windows\\Fonts\\tahomabd.ttf",
      "/usr/share/fonts/truetype/msttcorefonts/Arial_Bold.ttf",
      "/usr/share/fonts/truetype/msttcorefonts/arialbd.ttf",
      "/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf",
      "/usr/share/fonts/truetype/noto/NotoSans-Bold.ttf",
      "/usr/share/fonts/truetype/freefont/FreeSansBold.ttf",
      "/usr/share/fonts/truetype/liberation2/LiberationSans-Bold.ttf",
      "/usr/share/fonts/truetype/liberation/LiberationSans-Bold.ttf",
      path.join(localFontsDir, "DejaVuSans-Bold.ttf"),
    ],
  };
}

async function resolveFonts(pdfDoc, options) {
  pdfDoc.registerFontkit(fontkit);

  const regularCustom = await tryEmbedCustomFont(
    pdfDoc,
    options.fonts?.regularBase64,
  );
  const boldCustom =
    (await tryEmbedCustomFont(pdfDoc, options.fonts?.boldBase64)) ||
    regularCustom;

  if (regularCustom && boldCustom) {
    return {
      regular: regularCustom,
      bold: boldCustom,
    };
  }

  const candidates = getDefaultFontCandidates();
  const regularBytes = readFirstExistingFile(candidates.regular);
  const boldBytes = readFirstExistingFile(candidates.bold);

  if (!regularBytes) {
    throw new Error(
      "No Vietnamese-capable font found on server. Set PDF_STAMP_FONT_PATH.",
    );
  }

  const regular = await pdfDoc.embedFont(regularBytes, { subset: true });
  const bold = await pdfDoc.embedFont(boldBytes || regularBytes, {
    subset: true,
  });

  return {
    regular,
    bold,
  };
}

async function stampPdf(pdfBuffer, options) {
  const requestOptions = options || {};
  const profilesConfig = loadStampProfiles();
  const classifierConfig = getClassifierConfig(profilesConfig);
  const scanProbeConfig = getScanInkProbeConfig(profilesConfig);
  const analysisCache = {
    textSignal: new Map(),
    baseline: new Map(),
    hits: 0,
    misses: 0,
  };
  const timingEnabled = shouldLogStampTiming(requestOptions);
  const timingStart = performance.now();
  const timing = {
    loadMs: 0,
    fontMs: 0,
    classifyMs: 0,
    anchorMs: 0,
    probeMs: 0,
    drawMs: 0,
    saveMs: 0,
    injectMs: 0,
  };
  const timingContext = {
    requestId: String(
      requestOptions.requestId ||
        `${Date.now().toString(36)}${Math.random().toString(36).slice(2, 8)}`,
    ),
    profile: "",
    profileConfidence: "",
    profileReason: "",
    profileFallback: false,
    textItemCount: 0,
    charCount: 0,
    scanMode: "",
    scanImagesInAnchor: false,
    forcedTextOnlyForLikelyScan: false,
    partialScanImageAnchor: false,
    scanMarginLifted: false,
    likelyFullPageScan: false,
    imageCount: 0,
    classifierDigitalTextItems: 0,
    classifierDigitalChars: 0,
    classifierSparseTextItems: 0,
    classifierSparseChars: 0,
    analysisCacheHits: 0,
    analysisCacheMisses: 0,
    signatureInjectSkipped: false,
    signatureInjectSkipReason: "",
    finalMarginBottom: 0,
    autoMarginBottom: 0,
    probeMarginBottom: 0,
    strictProbeMarginBottom: 0,
    strictProbeUsed: false,
    fullPageLowProbeFallbackApplied: false,
    largestImageBytesPerPixel: 0,
    strictProbeTrailingBlankRatio: 0,
    strictProbeBottomToTopInkRatio: 0,
    strictProbeInkStatsObserved: false,
  };
  let injectError = "";
  timingContext.classifierDigitalTextItems = classifierConfig.digitalMinTextItems;
  timingContext.classifierDigitalChars = classifierConfig.digitalMinChars;
  timingContext.classifierSparseTextItems = classifierConfig.sparseMaxTextItems;
  timingContext.classifierSparseChars = classifierConfig.sparseMaxChars;

  try {
    const loadStart = performance.now();
    const pdfDoc = await PDFDocument.load(pdfBuffer);
    const pages = pdfDoc.getPages();

    if (pages.length === 0) {
      throw new Error("Input PDF has no pages.");
    }

    const fontStart = performance.now();
    const fonts = await resolveFonts(pdfDoc, requestOptions);
    timing.fontMs = performance.now() - fontStart;
    const lines = buildStampLines(requestOptions);
    const copyStampText = resolveCopyStampText(requestOptions);

    const sealImage = await embedImage(pdfDoc, requestOptions.images?.sealBase64);
    const certifiedStampImage = await embedImage(
      pdfDoc,
      requestOptions.images?.certifiedStampBase64,
    );
    const signatureImage = await embedImage(
      pdfDoc,
      requestOptions.images?.signatureBase64,
    );

    const pageIndex = pages.length - 1;
    const page = pages[pageIndex];
    const pageMetrics = getPageMetrics(page);
    const pageImageSignal = inspectPageImageSignal(
      page,
      pageMetrics,
      classifierConfig,
    );
    timing.loadMs = performance.now() - loadStart;

    const anchorStart = performance.now();
    const classifyStart = performance.now();
    const profileSelection = await classifyStampProfile({
      pdfBuffer,
      pageIndex,
      pageMetrics,
      pageImageSignal,
      options: requestOptions,
      profilesConfig,
      classifierConfig,
      analysisCache,
    });
    timing.classifyMs = performance.now() - classifyStart;
    const selectedProfileName = profileSelection.name || "fallback_safe";
    const selectedProfile = asObject(profileSelection.profile);

    timingContext.profile = selectedProfileName;
    timingContext.profileConfidence = String(profileSelection.confidence || "");
    timingContext.profileReason = String(profileSelection.reason || "");
    timingContext.profileFallback = profileSelection.fallbackUsed === true;
    timingContext.textItemCount = toNumber(profileSelection.textItemCount, 0);
    timingContext.charCount = toNumber(profileSelection.charCount, 0);
    timingContext.likelyFullPageScan = pageImageSignal.likelyFullPageScan === true;
    timingContext.imageCount = toNumber(pageImageSignal.imageCount, 0);
    timingContext.largestImageBytesPerPixel = Number.isFinite(
      pageImageSignal.largestImageBytesPerPixel,
    )
      ? pageImageSignal.largestImageBytesPerPixel
      : 0;

    const requestTextLayout = asObject(requestOptions.textLayout);
    let textLayoutInput = deepMergeObjects(
      selectedProfile.textLayout,
      requestTextLayout,
    );
    const imageLayoutInput = deepMergeObjects(
      selectedProfile.imageLayout,
      requestOptions.imageLayout,
    );
    const signatureFieldsInput = deepMergeObjects(
      selectedProfile.signatureFields,
      requestOptions.signatureFields,
    );

    const forceImageAnchorForPartialScan = shouldEnableImageAnchorForPartialScan({
      selectedProfileName,
      pageImageSignal,
      requestTextLayout,
    });
    if (forceImageAnchorForPartialScan) {
      textLayoutInput = deepMergeObjects(textLayoutInput, {
        anchorToLastText: true,
        scanMode: "textAndImages",
      });
    }
    timingContext.partialScanImageAnchor = forceImageAnchorForPartialScan;

    const textLayoutBase = getTextLayoutForOrientation(
      textLayoutInput,
      pageMetrics.orientation,
      {},
    );
    const sealLayoutBase = getImageLayoutForOrientation(
      imageLayoutInput,
      pageMetrics.orientation,
      "seal",
      {
        width: 120,
        marginRight: 36,
      },
    );
    const certifiedStampLayoutBase = getImageLayoutForOrientation(
      imageLayoutInput,
      pageMetrics.orientation,
      "certifiedStamp",
      {
        width: 110,
        marginRight: 36,
        offsetX: 130,
        blendMode: BlendMode.Multiply,
      },
    );
    const signatureLayoutBase = getImageLayoutForOrientation(
      imageLayoutInput,
      pageMetrics.orientation,
      "signature",
      {
        width: 140,
        marginRight: 36,
        attachToOfficeSignature: true,
        officeAlign: "center",
        officeOffsetYRatio: -0.2,
      },
    );

    const stampBlockHeight = getStampBlockHeight({
      textLayout: textLayoutBase,
      sealImage,
      sealLayout: sealLayoutBase,
      certifiedStampImage,
      certifiedStampLayout: certifiedStampLayoutBase,
      signatureImage,
      signatureLayout: signatureLayoutBase,
    });

    const forcedTextOnlyForLikelyScan = shouldForceTextOnlyForLikelyFullPageScan(
      textLayoutBase,
      selectedProfileName,
      pageImageSignal,
    );
    let scanImagesInAnchor = shouldScanImagesInAnchor(
      textLayoutBase,
      pageImageSignal,
    );
    if (forcedTextOnlyForLikelyScan) {
      scanImagesInAnchor = false;
    }

    timingContext.scanMode = resolveTextScanMode(textLayoutBase);
    timingContext.scanImagesInAnchor = scanImagesInAnchor;
    timingContext.forcedTextOnlyForLikelyScan = forcedTextOnlyForLikelyScan;

    let defaultMarginBottom = Math.max(72, pageMetrics.visualHeight * 0.12);
    const hasExplicitMarginBottom = hasNumericValue(textLayoutBase.marginBottom);
    const explicitMarginBottom = hasExplicitMarginBottom
      ? Number(textLayoutBase.marginBottom)
      : null;

    const ignoredTexts = [
      lines.heading,
      lines.line2,
      lines.line3,
      lines.line4,
      copyStampText,
    ].filter(Boolean);

    const autoMarginStart = performance.now();
    const autoProbeDiagnostics = {};
    let autoMarginBottom = await resolveAutoMarginBottom({
      pdfBuffer,
      pageIndex,
      pageMetrics,
      textLayout: textLayoutBase,
      stampBlockHeight,
      ignoredTexts,
      scanImages: scanImagesInAnchor,
      scanProbeConfig,
      analysisCache,
      probeDiagnostics: autoProbeDiagnostics,
    });
    timing.probeMs += performance.now() - autoMarginStart;
    timingContext.autoMarginBottom = Number.isFinite(autoMarginBottom)
      ? roundTimingMs(autoMarginBottom)
      : 0;

    if (
      !Number.isFinite(autoMarginBottom) &&
      isScanProfileName(selectedProfileName) &&
      scanProbeConfig.enabled !== false &&
      toNumber(scanProbeConfig.maxProbeCount, 2) > 1
    ) {
      const probeStart = performance.now();
      const probeDiagnostics = {};
      const probeMarginBottom = await resolveAutoMarginBottom({
        pdfBuffer,
        pageIndex,
        pageMetrics,
        textLayout: {
          ...textLayoutBase,
          anchorToLastText: true,
        },
        stampBlockHeight,
        ignoredTexts,
        scanImages: true,
        allowWhenAnchorDisabled: true,
        scanProbeConfig,
        analysisCache,
        probeDiagnostics,
      });
      timing.probeMs += performance.now() - probeStart;
      timingContext.probeMarginBottom = Number.isFinite(probeMarginBottom)
        ? roundTimingMs(probeMarginBottom)
        : 0;
      timingContext.strictProbeTrailingBlankRatio = Number.isFinite(
        probeDiagnostics.trailingBlankRatio,
      )
        ? roundTimingMs(probeDiagnostics.trailingBlankRatio)
        : 0;
      timingContext.strictProbeBottomToTopInkRatio = Number.isFinite(
        probeDiagnostics.bottomToTopInkRatio,
      )
        ? roundTimingMs(probeDiagnostics.bottomToTopInkRatio)
        : 0;
      timingContext.strictProbeInkStatsObserved =
        probeDiagnostics.imageInkStatsObserved === true;
      const baseMarginBottom = Number.isFinite(explicitMarginBottom)
        ? explicitMarginBottom
        : defaultMarginBottom;
      if (
        shouldLiftScanMarginFromProbe({
          selectedProfileName,
          requestTextLayout,
          probeMarginBottom,
          baseMarginBottom,
        })
      ) {
        autoMarginBottom = probeMarginBottom;
        timingContext.scanMarginLifted = true;
      }
    }

    const fullPageScanMinMargin = toNumber(
      textLayoutBase.fullPageScanMinMarginBottom,
      Number.NaN,
    );

    const shouldRunStrictFullPageProbe =
      isScanProfileName(selectedProfileName) &&
      pageImageSignal?.likelyFullPageScan === true &&
      !hasManualAnchorOverride(requestTextLayout) &&
      !isManualScanModeOverride(requestTextLayout) &&
      Number.isFinite(fullPageScanMinMargin) &&
      scanProbeConfig.enabled !== false &&
      (!Number.isFinite(autoMarginBottom) || autoMarginBottom < fullPageScanMinMargin + 20);

    if (shouldRunStrictFullPageProbe) {
      const strictProbeStart = performance.now();
      const strictProbeMarginBottom = await resolveAutoMarginBottom({
        pdfBuffer,
        pageIndex,
        pageMetrics,
        textLayout: {
          ...textLayoutBase,
          anchorToLastText: true,
        },
        stampBlockHeight,
        ignoredTexts,
        scanImages: true,
        allowWhenAnchorDisabled: true,
        scanProbeConfig: buildStrictFullPageScanProbeConfig(scanProbeConfig),
        analysisCache,
      });
      timing.probeMs += performance.now() - strictProbeStart;
      timingContext.strictProbeMarginBottom = Number.isFinite(strictProbeMarginBottom)
        ? roundTimingMs(strictProbeMarginBottom)
        : 0;

      if (
        Number.isFinite(strictProbeMarginBottom) &&
        strictProbeMarginBottom >= fullPageScanMinMargin + 24
      ) {
        autoMarginBottom = strictProbeMarginBottom;
        timingContext.scanMarginLifted = true;
        timingContext.strictProbeUsed = true;
      }
    }

    if (Number.isFinite(autoMarginBottom)) {
      defaultMarginBottom = autoMarginBottom;
    } else if (Number.isFinite(explicitMarginBottom)) {
      defaultMarginBottom = explicitMarginBottom;
    }

    if (
      isScanProfileName(selectedProfileName) &&
      pageImageSignal?.likelyFullPageScan === true &&
      !hasNumericValue(requestTextLayout.marginBottom) &&
      !hasManualAnchorOverride(requestTextLayout) &&
      Number.isFinite(fullPageScanMinMargin) &&
      defaultMarginBottom < fullPageScanMinMargin
    ) {
      defaultMarginBottom = fullPageScanMinMargin;
      timingContext.scanMarginLifted = true;
    }

    const fullPageLowProbeFallbackRatio = Math.max(
      0.18,
      Math.min(
        0.55,
        toNumber(textLayoutBase.fullPageLowProbeFallbackRatio, 0.32),
      ),
    );
    const fullPageLowProbeMaxDetectedMargin = Math.max(
      12,
      toNumber(textLayoutBase.fullPageLowProbeMaxDetectedMargin, 54),
    );
    const fullPageLowProbeMaxBpp = Math.max(
      0.01,
      toNumber(textLayoutBase.fullPageLowProbeMaxBpp, 0.09),
    );
    const strictProbeMarginBottom = toNumber(
      timingContext.strictProbeMarginBottom,
      Number.NaN,
    );
    const strictProbeTrailingBlankRatio = toNumber(
      timingContext.strictProbeTrailingBlankRatio,
      Number.NaN,
    );
    const strictProbeBottomToTopInkRatio = toNumber(
      timingContext.strictProbeBottomToTopInkRatio,
      Number.NaN,
    );
    const fullPageLowProbeMinTrailingBlankRatio = Math.max(
      0.08,
      Math.min(
        0.9,
        toNumber(textLayoutBase.fullPageLowProbeMinTrailingBlankRatio, 0.16),
      ),
    );
    const fullPageLowProbeMaxBottomToTopInkRatio = Math.max(
      0.05,
      Math.min(
        2,
        toNumber(textLayoutBase.fullPageLowProbeMaxBottomToTopInkRatio, 0.42),
      ),
    );
    const hasLowBottomInkSignal =
      (Number.isFinite(strictProbeTrailingBlankRatio) &&
        strictProbeTrailingBlankRatio >= fullPageLowProbeMinTrailingBlankRatio) ||
      (Number.isFinite(strictProbeBottomToTopInkRatio) &&
        strictProbeBottomToTopInkRatio <= fullPageLowProbeMaxBottomToTopInkRatio);
    const shouldApplyLowProbeFallback =
      isScanProfileName(selectedProfileName) &&
      pageImageSignal?.likelyFullPageScan === true &&
      toNumber(pageImageSignal?.imageCount, 0) === 1 &&
      !hasNumericValue(requestTextLayout.marginBottom) &&
      !hasManualAnchorOverride(requestTextLayout) &&
      !isManualScanModeOverride(requestTextLayout) &&
      Number.isFinite(strictProbeMarginBottom) &&
      strictProbeMarginBottom <= fullPageLowProbeMaxDetectedMargin &&
      hasLowBottomInkSignal &&
      Number.isFinite(pageImageSignal?.largestImageBytesPerPixel) &&
      pageImageSignal.largestImageBytesPerPixel <= fullPageLowProbeMaxBpp;

    if (shouldApplyLowProbeFallback) {
      const fallbackMarginBottom = pageMetrics.visualHeight * fullPageLowProbeFallbackRatio;
      if (fallbackMarginBottom > defaultMarginBottom) {
        defaultMarginBottom = fallbackMarginBottom;
        timingContext.scanMarginLifted = true;
        timingContext.fullPageLowProbeFallbackApplied = true;
      }
    }

    const textLayout = {
      ...getTextLayoutForOrientation(
        textLayoutInput,
        pageMetrics.orientation,
        { marginBottom: defaultMarginBottom },
      ),
      // Ensure auto/explicit resolved margin wins over profile default.
      marginBottom: defaultMarginBottom,
    };
    timingContext.finalMarginBottom = roundTimingMs(defaultMarginBottom);
    const sealLayout = getImageLayoutForOrientation(
      imageLayoutInput,
      pageMetrics.orientation,
      "seal",
      {
        width: 120,
        marginRight: 36,
        marginBottom: defaultMarginBottom,
      },
    );
    const certifiedStampLayout = getImageLayoutForOrientation(
      imageLayoutInput,
      pageMetrics.orientation,
      "certifiedStamp",
      {
        width: 110,
        marginRight: 36,
        offsetX: 130,
        marginBottom: defaultMarginBottom,
        blendMode: BlendMode.Multiply,
      },
    );
    const signatureLayout = getImageLayoutForOrientation(
      imageLayoutInput,
      pageMetrics.orientation,
      "signature",
      {
        width: 140,
        marginRight: 36,
        attachToOfficeSignature: true,
        officeAlign: "center",
        officeOffsetYRatio: -0.2,
        marginBottom: defaultMarginBottom,
      },
    );
    const signatureFieldsLayout = getSignatureFieldsLayoutForOrientation(
      signatureFieldsInput,
      pageMetrics.orientation,
    );
    timing.anchorMs = performance.now() - anchorStart;

    const drawStart = performance.now();
    let panelGeometry = null;
    const transformedDisplaySpace = beginDisplaySpace(page, pageMetrics);
    try {
      panelGeometry = drawCertificationPanel(
        page,
        {
          lines,
          headingFont: fonts.bold,
          bodyFont: fonts.regular,
        },
        textLayout,
        pageMetrics,
      );

      const officeSignatureRect = drawBottomRightImage(
        page,
        sealImage,
        sealLayout,
        pageMetrics,
      );
      drawBottomRightImage(
        page,
        certifiedStampImage,
        certifiedStampLayout,
        pageMetrics,
      );
      drawPersonalSignatureNearOfficeSignature(
        page,
        signatureImage,
        signatureLayout,
        pageMetrics,
        officeSignatureRect,
      );
    } finally {
      endDisplaySpace(page, transformedDisplaySpace);
    }

    const signatureFieldSpecs = addSignatureFieldsForFoxit(
      pdfDoc,
      page,
      pageIndex,
      pageMetrics,
      panelGeometry,
      signatureFieldsLayout,
    );

    if (copyStampText) {
      const copyStampPageIndex = resolveCopyStampPageIndex(
        requestOptions,
        pages.length,
      );
      const copyStampPage = pages[copyStampPageIndex];
      const copyStampMetrics = getPageMetrics(copyStampPage);
      const copyStampLayout = getImageLayoutForOrientation(
        imageLayoutInput,
        copyStampMetrics.orientation,
        "copyStamp",
        {
          width: 120,
          marginRight: 36,
          marginTop: 36,
          anchor: "top-right",
        },
      );

      const copyStampDisplaySpace = beginDisplaySpace(
        copyStampPage,
        copyStampMetrics,
      );
      try {
        drawCopyStampText(
          copyStampPage,
          {
            text: copyStampText,
            font: fonts.bold,
          },
          copyStampLayout,
          copyStampMetrics,
        );
      } finally {
        endDisplaySpace(copyStampPage, copyStampDisplaySpace);
      }
    }
    timing.drawMs = performance.now() - drawStart;

    const saveStart = performance.now();
    const stampedBytes = await pdfDoc.save({ useObjectStreams: false });
    timing.saveMs = performance.now() - saveStart;

    const injectStart = performance.now();
    try {
      if (!Array.isArray(signatureFieldSpecs) || signatureFieldSpecs.length === 0) {
        timingContext.signatureInjectSkipped = true;
        timingContext.signatureInjectSkipReason = "no_fields";
        return Buffer.from(stampedBytes);
      }

      const skipInject = shouldSkipSignatureInject(
        requestOptions,
        signatureFieldsLayout,
      );
      if (skipInject.skip) {
        timingContext.signatureInjectSkipped = true;
        timingContext.signatureInjectSkipReason = skipInject.reason;
        return Buffer.from(stampedBytes);
      }

      return injectSignatureFieldsWithIText(
        stampedBytes,
        signatureFieldSpecs,
        signatureFieldsLayout,
      );
    } catch (err) {
      injectError = String(err?.message || err || "");
      throw err;
    } finally {
      timing.injectMs = performance.now() - injectStart;
    }
  } finally {
    if (timingEnabled) {
      timingContext.analysisCacheHits = toNumber(analysisCache?.hits, 0);
      timingContext.analysisCacheMisses = toNumber(analysisCache?.misses, 0);
      const totalMs = performance.now() - timingStart;
      const payload = {
        request_id: timingContext.requestId,
        load_ms: roundTimingMs(timing.loadMs),
        font_ms: roundTimingMs(timing.fontMs),
        classify_ms: roundTimingMs(timing.classifyMs),
        anchor_ms: roundTimingMs(timing.anchorMs),
        probe_ms: roundTimingMs(timing.probeMs),
        draw_ms: roundTimingMs(timing.drawMs),
        save_ms: roundTimingMs(timing.saveMs),
        inject_ms: roundTimingMs(timing.injectMs),
        total_ms: roundTimingMs(totalMs),
        profile: timingContext.profile,
        confidence: timingContext.profileConfidence,
        reason: timingContext.profileReason,
        fallback: timingContext.profileFallback,
        text_items: timingContext.textItemCount,
        chars: timingContext.charCount,
        scan_mode: timingContext.scanMode,
        scan_images: timingContext.scanImagesInAnchor,
        forced_text_only: timingContext.forcedTextOnlyForLikelyScan,
        partial_scan_anchor: timingContext.partialScanImageAnchor,
        scan_margin_lifted: timingContext.scanMarginLifted,
        likely_full_page_scan: timingContext.likelyFullPageScan,
        image_count: timingContext.imageCount,
        classifier_digital_min_items: timingContext.classifierDigitalTextItems,
        classifier_digital_min_chars: timingContext.classifierDigitalChars,
        classifier_sparse_max_items: timingContext.classifierSparseTextItems,
        classifier_sparse_max_chars: timingContext.classifierSparseChars,
        analysis_cache_hits: timingContext.analysisCacheHits,
        analysis_cache_misses: timingContext.analysisCacheMisses,
        inject_skipped: timingContext.signatureInjectSkipped,
        inject_skip_reason: timingContext.signatureInjectSkipReason || undefined,
        auto_margin_bottom: timingContext.autoMarginBottom,
        probe_margin_bottom: timingContext.probeMarginBottom,
        strict_probe_margin_bottom: timingContext.strictProbeMarginBottom,
        strict_probe_used: timingContext.strictProbeUsed,
        full_page_low_probe_fallback: timingContext.fullPageLowProbeFallbackApplied,
        image_bpp: roundTimingMs(timingContext.largestImageBytesPerPixel),
        strict_probe_trailing_blank_ratio: timingContext.strictProbeTrailingBlankRatio,
        strict_probe_bottom_to_top_ink_ratio:
          timingContext.strictProbeBottomToTopInkRatio,
        strict_probe_ink_stats_observed: timingContext.strictProbeInkStatsObserved,
        final_margin_bottom: timingContext.finalMarginBottom,
        inject_error: injectError || undefined,
      };
      console.info(`[stamp-timing] ${JSON.stringify(payload)}`);
    }
  }
}

module.exports = {
  DEFAULT_CERTIFICATION_TEXT,
  stampPdf,
  toPdfBytes,
};



