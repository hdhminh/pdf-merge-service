const fs = require("fs");
const os = require("os");
const path = require("path");
const { spawnSync } = require("child_process");
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

const DEFAULT_CERTIFICATION_TEXT = "CHỨNG THỰC BẢN SAO ĐÚNG VỚI BẢN CHÍNH";

const HEADING_COLOR = rgb(0.72, 0.11, 0.18);
const BODY_COLOR = rgb(0.08, 0.14, 0.52);
const WHITE = rgb(1, 1, 1);

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

function getDisplayToPageMatrix(page, pageMetrics) {
  const rotation = normalizeRotationAngle(
    pageMetrics?.rotation ?? page.getRotation()?.angle,
  );
  const width = toNumber(pageMetrics?.width, page.getWidth());
  const height = toNumber(pageMetrics?.height, page.getHeight());

  if (rotation === 90) {
    return [0, 1, -1, 0, width, 0];
  }

  if (rotation === 180) {
    return [-1, 0, 0, -1, width, height];
  }

  if (rotation === 270) {
    return [0, -1, 1, 0, 0, height];
  }

  return [1, 0, 0, 1, 0, 0];
}

function transformPointByMatrix(point, matrix) {
  return {
    x: matrix[0] * point.x + matrix[2] * point.y + matrix[4],
    y: matrix[1] * point.x + matrix[3] * point.y + matrix[5],
  };
}

function mapDisplayRectToPageRect(page, pageMetrics, rect) {
  const matrix = getDisplayToPageMatrix(page, pageMetrics);
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

  const pageWidth = page.getWidth();
  const pageHeight = page.getHeight();
  const x = Math.max(0, Math.min(minX, pageWidth - 1));
  const y = Math.max(0, Math.min(minY, pageHeight - 1));
  const width = Math.max(1, Math.min(maxX - minX, pageWidth - x));
  const height = Math.max(1, Math.min(maxY - minY, pageHeight - y));

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
  const headingSize = toNumber(layout.headingSize, 15);
  const bodySize = toNumber(layout.bodySize, 12.5);
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

function pageHasEmbeddedImageXObject(page) {
  try {
    const resources = page?.node?.Resources?.();
    if (!resources || !(resources instanceof PDFDict)) {
      return false;
    }

    const xObjectDict = resources.lookupMaybe(PDFName.of("XObject"), PDFDict);
    if (!xObjectDict) {
      return false;
    }

    for (const key of xObjectDict.keys()) {
      const xObject = xObjectDict.lookup(key);
      if (!(xObject instanceof PDFStream)) {
        continue;
      }

      const subtype = xObject.dict.lookupMaybe(PDFName.of("Subtype"), PDFName);
      if (subtype && subtype.toString() === "/Image") {
        return true;
      }
    }
  } catch (_err) {
    return false;
  }

  return false;
}

function shouldScanImagesInAnchor(textLayout, pageHasImages) {
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

  return pageHasImages === true;
}

async function findLastContentBaselineY(
  pdfBuffer,
  pageIndex,
  rotation,
  ignoredTexts,
  textLayout,
  scanImages,
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
      return null;
    }

    return {
      baselineY,
      pageHeight: viewport.height,
    };
  } catch (_err) {
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
}) {
  if (textLayout.anchorToLastText === false) {
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

function formatDateLine(dateValue) {
  if (typeof dateValue !== "string" || !dateValue.trim()) {
    return "Ngày -- tháng -- năm ----";
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
    return `Ngày ${raw}`;
  }

  const dd = String(day).padStart(2, "0");
  const mm = String(month).padStart(2, "0");
  return `Ngày ${dd} tháng ${mm} năm ${year}`;
}

function buildStampLines(options) {
  const heading = (
    options.certificationText || DEFAULT_CERTIFICATION_TEXT
  ).toUpperCase();
  const certNo = options.certificationNumber || "--";
  const bookNo = options.certificationBookNumber || "--";
  const notaryTitle = (
    options.notaryTitle || "C\u00D4NG CH\u1EE8NG VI\u00CAN"
  ).toUpperCase();

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
    return;
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
}

function drawCopyStampText(page, data, layout, pageMetrics) {
  if (!data?.text) {
    return;
  }

  const pageHeight = toNumber(pageMetrics?.visualHeight, page.getHeight());
  const fontSize = toNumber(layout.size, 20);
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

function getCertificationPanelGeometry(page, data, layout, pageMetrics) {
  const pageWidth = toNumber(pageMetrics?.visualWidth, page.getWidth());
  const pageHeight = toNumber(pageMetrics?.visualHeight, page.getHeight());

  const marginLeft = toNumber(layout.marginLeft, 28);
  const marginBottom = hasNumericValue(layout.marginBottom)
    ? Number(layout.marginBottom)
    : Math.max(72, pageHeight * 0.12);
  const panelPaddingX = toNumber(layout.panelPaddingX, 10);
  const panelPaddingY = toNumber(layout.panelPaddingY, 8);
  const headingSize = toNumber(layout.headingSize, 15);
  const bodySize = toNumber(layout.bodySize, 12.5);
  const lineGap = toNumber(layout.lineGap, 4);
  const notaryGap = toNumber(layout.notaryGap, 4);
  const maxPanelWidth = toNumber(layout.maxPanelWidth, pageWidth * 0.74);
  const panelOpacity = toNumber(layout.panelOpacity, 0);

  const headingWidth = data.headingFont.widthOfTextAtSize(
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
  const line4Width = data.headingFont.widthOfTextAtSize(
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
  const lineGap = Math.max(0, toNumber(signatureFieldsLayout?.lineGap, 8));
  const minBottom = Math.max(0, toNumber(signatureFieldsLayout?.minBottom, 8));
  const minHeight = Math.max(10, toNumber(signatureFieldsLayout?.minHeight, 12));
  const minWidth = Math.max(24, toNumber(signatureFieldsLayout?.minWidth, 120));
  const overlap = signatureFieldsLayout?.overlap !== false;

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
  const overlapOffsetX = hasNumericValue(signatureFieldsLayout?.overlapOffsetX)
    ? Number(signatureFieldsLayout.overlapOffsetX)
    : 0;
  const overlapOffsetY = hasNumericValue(signatureFieldsLayout?.overlapOffsetY)
    ? Number(signatureFieldsLayout.overlapOffsetY)
    : -fieldHeight * 0.25;
  let y = panelGeometry.notaryLineY - lineGap - fieldHeight;

  if (y < minBottom) {
    y = minBottom;
  }

  const maxTop = panelGeometry.notaryLineY - lineGap;
  if (y + fieldHeight > maxTop) {
    fieldHeight = maxTop - y;
  }

  if (!Number.isFinite(fieldHeight) || fieldHeight < minHeight) {
    return [];
  }

  const enterpriseRectDisplay = {
    x: areaLeft,
    y,
    width: fieldWidth,
    height: fieldHeight,
  };
  const personalRectDisplay = overlap
    ? {
        x: enterpriseRectDisplay.x + overlapOffsetX,
        y: enterpriseRectDisplay.y + overlapOffsetY,
        width: fieldWidth,
        height: fieldHeight,
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

let signatureFieldToolDllPathCache;

function resolveSignatureFieldToolDllPath() {
  if (signatureFieldToolDllPathCache !== undefined) {
    return signatureFieldToolDllPathCache;
  }

  const toolRelativeParts = [
    "tools",
    "signature-field-tool",
    "SignatureFieldTool",
    "bin",
    "Release",
  ];
  const frameworkDirs = ["net8.0", "net9.0", "net10.0"];
  const baseDirs = [__dirname, path.join(__dirname, "..", "..")];

  for (const baseDir of baseDirs) {
    for (const framework of frameworkDirs) {
      const dllPath = path.join(
        baseDir,
        ...toolRelativeParts,
        framework,
        "SignatureFieldTool.dll",
      );
      if (fs.existsSync(dllPath)) {
        signatureFieldToolDllPathCache = dllPath;
        return signatureFieldToolDllPathCache;
      }
    }
  }

  signatureFieldToolDllPathCache = null;
  return signatureFieldToolDllPathCache;
}

function injectSignatureFieldsWithIText(
  pdfBytes,
  fieldSpecs,
  signatureFieldsLayout,
) {
  if (!Array.isArray(fieldSpecs) || fieldSpecs.length === 0) {
    return pdfBytes;
  }

  const dllPath = resolveSignatureFieldToolDllPath();
  if (!dllPath) {
    throw new Error(
      "SignatureFieldTool.dll not found. Build tools/signature-field-tool/SignatureFieldTool first.",
    );
  }

  const dotnetCheck = spawnSync("dotnet", ["--version"], { encoding: "utf8" });
  if (dotnetCheck.error || dotnetCheck.status !== 0) {
    throw new Error("dotnet runtime is required to inject signature fields.");
  }

  const tmpDir = fs.mkdtempSync(path.join(os.tmpdir(), "pdf-sigfields-"));
  const inputPath = path.join(tmpDir, "input.pdf");
  const outputPath = path.join(tmpDir, "output.pdf");
  const jobPath = path.join(tmpDir, "job.json");

  try {
    fs.writeFileSync(inputPath, pdfBytes);

    const borderWidth = Math.max(0, toNumber(signatureFieldsLayout?.borderWidth, 0));
    const borderGrayRaw = toNumber(signatureFieldsLayout?.borderGray, 0.65);
    const borderGray = Math.min(1, Math.max(0, borderGrayRaw));
    const replaceExisting = signatureFieldsLayout?.replaceExisting !== false;

    const job = {
      input: inputPath,
      output: outputPath,
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

    const runResult = spawnSync("dotnet", [dllPath, "--job", jobPath], {
      encoding: "utf8",
      maxBuffer: 8 * 1024 * 1024,
    });
    if (runResult.error || runResult.status !== 0) {
      const stderr = (runResult.stderr || "").trim();
      const stdout = (runResult.stdout || "").trim();
      const details = stderr || stdout || "unknown error";
      throw new Error(`Signature field injection failed: ${details}`);
    }

    if (!fs.existsSync(outputPath)) {
      throw new Error("Signature field injection failed: output PDF not produced.");
    }

    return fs.readFileSync(outputPath);
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
    font: data.headingFont,
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
    font: data.headingFont,
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
  const pdfDoc = await PDFDocument.load(pdfBuffer);
  const pages = pdfDoc.getPages();

  if (pages.length === 0) {
    throw new Error("Input PDF has no pages.");
  }

  const fonts = await resolveFonts(pdfDoc, options || {});
  const lines = buildStampLines(options || {});

  const copyStampText = resolveCopyStampText(options || {});

  const sealImage = await embedImage(pdfDoc, options.images?.sealBase64);
  const certifiedStampImage = await embedImage(
    pdfDoc,
    options.images?.certifiedStampBase64,
  );
  const signatureImage = await embedImage(
    pdfDoc,
    options.images?.signatureBase64,
  );

  const page = pages[pages.length - 1];
  const pageMetrics = getPageMetrics(page);

  const textLayoutBase = getTextLayoutForOrientation(
    options.textLayout || {},
    pageMetrics.orientation,
    {},
  );
  const sealLayoutBase = getImageLayoutForOrientation(
    options.imageLayout || {},
    pageMetrics.orientation,
    "seal",
    {
      width: 120,
      marginRight: 36,
    },
  );
  const certifiedStampLayoutBase = getImageLayoutForOrientation(
    options.imageLayout || {},
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
    options.imageLayout || {},
    pageMetrics.orientation,
    "signature",
    {
      width: 140,
      marginRight: 36,
      offsetY: 72,
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
  const scanImagesInAnchor = shouldScanImagesInAnchor(
    textLayoutBase,
    pageHasEmbeddedImageXObject(page),
  );

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

  const autoMarginBottom = await resolveAutoMarginBottom({
    pdfBuffer,
    pageIndex: pages.length - 1,
    pageMetrics,
    textLayout: textLayoutBase,
    stampBlockHeight,
    ignoredTexts,
    scanImages: scanImagesInAnchor,
  });

  if (Number.isFinite(autoMarginBottom)) {
    defaultMarginBottom = autoMarginBottom;
  } else if (Number.isFinite(explicitMarginBottom)) {
    defaultMarginBottom = explicitMarginBottom;
  }

  const textLayout = getTextLayoutForOrientation(
    options.textLayout || {},
    pageMetrics.orientation,
    { marginBottom: defaultMarginBottom },
  );
  const sealLayout = getImageLayoutForOrientation(
    options.imageLayout || {},
    pageMetrics.orientation,
    "seal",
    {
      width: 120,
      marginRight: 36,
      marginBottom: defaultMarginBottom,
    },
  );
  const certifiedStampLayout = getImageLayoutForOrientation(
    options.imageLayout || {},
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
    options.imageLayout || {},
    pageMetrics.orientation,
    "signature",
    {
      width: 140,
      marginRight: 36,
      offsetY: 72,
      marginBottom: defaultMarginBottom,
    },
  );
  const signatureFieldsLayout = getSignatureFieldsLayoutForOrientation(
    options.signatureFields || {},
    pageMetrics.orientation,
  );

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

    drawBottomRightImage(page, sealImage, sealLayout, pageMetrics);
    drawBottomRightImage(
      page,
      certifiedStampImage,
      certifiedStampLayout,
      pageMetrics,
    );
    drawBottomRightImage(page, signatureImage, signatureLayout, pageMetrics);
  } finally {
    endDisplaySpace(page, transformedDisplaySpace);
  }

  const signatureFieldSpecs = addSignatureFieldsForFoxit(
    pdfDoc,
    page,
    pages.length - 1,
    pageMetrics,
    panelGeometry,
    signatureFieldsLayout,
  );

  if (copyStampText) {
    const copyStampPageIndex = resolveCopyStampPageIndex(
      options || {},
      pages.length,
    );
    const copyStampPage = pages[copyStampPageIndex];
    const copyStampMetrics = getPageMetrics(copyStampPage);
    const copyStampLayout = getImageLayoutForOrientation(
      options.imageLayout || {},
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

  const stampedBytes = await pdfDoc.save({ useObjectStreams: false });
  return injectSignatureFieldsWithIText(
    stampedBytes,
    signatureFieldSpecs,
    signatureFieldsLayout,
  );
}

module.exports = {
  DEFAULT_CERTIFICATION_TEXT,
  stampPdf,
  toPdfBytes,
};
