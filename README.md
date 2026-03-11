# PDF Stamp Backend (Last-Page Only)

This backend:

- receives PDF as base64 from Apps Script
- stamps certification text and image overlays
- returns stamped PDF as binary

Backend does not upload to Drive and does not write to Google Sheets.
No Google Service Account is required for this backend.

## Flow

1. User selects PDF and enters certification number/date.
2. Apps Script reads PDF and converts to base64.
3. Apps Script calls `POST /api/pdf/stamp`.
4. Backend stamps the PDF on the last page only.
5. Backend returns stamped PDF binary.
6. Apps Script uploads stamped PDF to Drive and writes link to Sheet.

## Install

```bash
npm install
```

## Local Packaging Helpers

```powershell
# Remove local build trash/artifacts
./scripts/clean-local-artifacts.ps1

# Prune backend payload (same rule as CI)
./scripts/prune-backend-payload.ps1

# Local publish for desktop app
./scripts/publish-wpf-local.ps1 -Version 1.3.17-local -Channel stable

# Sync root backend to desktop-app/backend mirror
./scripts/sync-backend.ps1

# Verify root backend and mirror are identical
./scripts/check-backend-sync.ps1
```

## Environment

```env
PORT=3000
MAX_FILE_SIZE_BYTES=20971520
GOOGLE_SHEET_SYNC_URL=
GOOGLE_SHEET_SYNC_API_KEY=
GOOGLE_SHEET_SYNC_TIMEOUT_MS=12000
TEMP_FILE_MAX_AGE_MS=1800000
TEMP_CLEANUP_INTERVAL_MS=300000
PDF_STAMP_DISABLE_SIGNATURE_INJECT=false
```

`GOOGLE_SHEET_SYNC_URL` is optional if desktop app sends `webhookUrl` in request payload.

Optional font override:

```env
PDF_STAMP_FONT_PATH=/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf
PDF_STAMP_FONT_BOLD_PATH=/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf
```

If not set, backend tries common system Unicode fonts automatically.

On Raspberry Pi, install fonts if missing:

```bash
sudo apt update
sudo apt install -y fonts-dejavu-core fonts-freefont-ttf fonts-liberation2
```

If you need stamp text to match Windows (Arial) as closely as possible:

```bash
sudo apt install -y ttf-mscorefonts-installer
```

## Run

```bash
node index.js
ngrok http 3000 --inspect=false
```

Health check:

```bash
curl http://localhost:3000/health
```

## API

### `POST /api/pdf/stamp`

Content-Type:

```text
application/json
```

Request body example:

```json
{
  "contentBase64": "<base64_pdf_or_data_url>",
  "certificationNumber": "OA-13",
  "certificationBookNumber": "Q-01",
  "certificationDate": "2026-02-09",
  "certificationText": "CHUNG THUC BAN SAO DUNG VOI BAN CHINH",
  "notaryTitle": "CONG CHUNG VIEN",
  "copyStampText": "BAN SAO",
  "copyStampPage": "first",
  "outputFileName": "certified_copy.pdf",
  "images": {
    "sealBase64": "data:image/png;base64,...",
    "certifiedStampBase64": "data:image/png;base64,...",
    "signatureBase64": "data:image/png;base64,..."
  },
  "imageLayout": {
    "seal": { "width": 120, "marginRight": 36, "marginBottom": 90 },
    "certifiedStamp": {
      "width": 110,
      "offsetX": 130,
      "marginBottom": 90,
      "blendMode": "Multiply"
    },
    "signature": { "width": 140, "offsetY": 72, "marginBottom": 90 },
    "copyStamp": {
      "width": 120,
      "anchor": "top-right",
      "marginTop": 36,
      "size": 20,
      "borderWidth": 3
    }
  },
  "textLayout": {
    "marginLeft": 28,
    "marginBottom": 90,
    "headingSize": 15,
    "bodySize": 12.5,
    "panelOpacity": 0
  },
  "profileName": "scan_sparse_portrait",
  "logTiming": true,
  "signatureFields": {
    "enabled": true,
    "enterpriseName": "sig_enterprise",
    "personalName": "sig_personal",
    "height": 52,
    "centerGap": 20,
    "lineGap": 8
  }
}
```

Required fields:

- `contentBase64`
- `certificationNumber`
- `certificationDate`

Notes:

- `fonts` in payload is optional.
- Backend supports profile-based layout via `config/stamp-profiles.json`.
- `config/stamp-profiles.json` now includes `classifier` and `scanInkProbe` blocks so thresholds can be tuned without code changes.
- Use `profileName`/`profile` to pick a specific profile manually.
- If `profileName` is omitted, backend runs a lightweight classifier on the last page and auto-selects one of:
  - `scan_sparse_portrait`, `scan_dense_portrait`
  - `scan_sparse_landscape`, `scan_dense_landscape`
  - `digital_text_portrait`, `digital_text_landscape`
  - `fallback_safe` (low-confidence fallback)
- Scan-first behavior: scan profiles default to fixed anchor (`anchorToLastText=false`) for stability on scanned documents.
- For scan profiles, `textLayout.fullPageScanMinMarginBottom` provides a safety floor when page is detected as full-page scan.
- Backend tries to anchor the stamp just below the last detected text or image content (ignoring the stamp text itself). If no content is found or there is not enough space, it falls back to the previous auto-margin based on displayed page height or `textLayout.marginBottom` if provided.
- Control this behavior with `textLayout.anchorToLastText` (default `true`), `textLayout.lastTextGap` (default `12`), and `textLayout.minMarginBottom` (default `12`).
- Auto-anchor scan mode: `textLayout.scanMode` supports `auto` (default), `textOnly`, and `textAndImages`.
- In `auto`, backend detects whether the last page has embedded images. If yes, it scans text + images; if not, it scans text only for better speed.
- Lightweight auto rule: for `digital_*` profiles in `scanMode=auto`, if page looks like full-page scan and there is no manual override, backend forces `textOnly` behavior (skip image-operator anchor scan).
- Legacy override: `textLayout.scanImages` (`true`/`false`) still works and takes priority over `scanMode`.
- Set `logTiming: false` (or env `PDF_STAMP_LOG_TIMING=false`) to disable per-request timing logs.
- To skip .NET signature-field injection for speed tests, set `PDF_STAMP_DISABLE_SIGNATURE_INJECT=true` (or request `disableSignatureFieldInjection=true`).
- `textLayout.notaryGap` controls the extra spacing before the notary line (default `4`).
- `textLayout.bottomSafeMargin` keeps a minimum distance from the page bottom when auto-anchoring (default `36`).
- `textLayout.panelOpacity` defaults to `0` (transparent). Set it > 0 to draw a white background.
- `certificationBookNumber`, `notaryTitle`, and `copyStampText` are optional; if omitted, placeholders/defaults are used.
- `copyStampText` renders a red bordered stamp text (default `BAN SAO`) on the first page. Set `copyStampText` to `null` to disable it.
- `images.certifiedStampBase64` should be PNG with transparent background for best results.
- `certifiedStamp` uses `blendMode: "Multiply"` by default to blend white backgrounds into the PDF.
- `imageLayout` supports `anchor` values: `bottom-right` (default), `bottom-left`, `top-right`, `top-left`. Use `marginTop` / `marginLeft` to position when anchored to the top or left.
- `imageLayout.copyStamp` controls the text stamp position and size (`size`, `paddingX`, `paddingY`, `borderWidth`).
- `imageLayout.copyStamp.textBaselineOffset` adjusts vertical centering of the text inside the stamp.
- `copyStampPage` controls where the text stamp goes: `first` (default), `last`, or a 0-based page index.
- By default, backend adds 2 unsigned PDF signature fields that Foxit Reader can detect:
  - enterprise field (`sig_enterprise`)
  - personal field (`sig_personal`)
  - enterprise field is shifted left by 10% field width
  - personal field is placed lower-right with 20% vertical overlap
  - signature fields are injected by the .NET/iText helper at `tools/signature-field-tool/SignatureFieldTool`
- Build helper before running backend:
  - `dotnet build tools/signature-field-tool/SignatureFieldTool/SignatureFieldTool.csproj -c Release`
  - New helper mode `--stdout` is used by backend to reduce temp-file I/O.
  - Desktop app release bundles `SignatureFieldTool.exe` self-contained, so client machine does not need to install .NET runtime manually.
- `signatureFields` controls field names and geometry (`enabled`, `enterpriseName`, `personalName`, `height`, `width`, `centerGap`, `lineGap`, `sideInset`, `replaceExisting`, `overlap`, `overlapOffsetX`, `overlapOffsetY`).
- `replaceExisting` defaults to `true`: if a signature field with the same name already exists, backend replaces it instead of creating `_2`, `_3`, ...
- `signatureFields.overlap` defaults to `true` (stacked/overlapped layout). Set `overlap: false` to separate left/right fields.
- `overlapOffsetX` / `overlapOffsetY` controls offset between overlapped fields.
  - default `overlapOffsetX` is `-10%` of field width (enterprise field left-shifted)
  - default `overlapOffsetY` is `-20%` of field height (personal field lower)
  - default `signatureFields.borderWidth` is `0` (hidden frame border)
  - Set `overlapOffsetX` explicitly if you want another value.
- You can override orientation-specific layout with `portrait` / `landscape` buckets:

```json
{
  "imageLayout": {
    "portrait": {
      "signature": { "width": 140, "offsetY": 72 }
    },
    "landscape": {
      "signature": { "width": 120, "offsetY": 48 }
    }
  },
  "textLayout": {
    "portrait": { "marginLeft": 28 },
    "landscape": { "marginLeft": 20 }
  },
  "signatureFields": {
    "portrait": { "height": 52, "centerGap": 20 },
    "landscape": { "height": 46, "centerGap": 16 }
  }
}
```

Overlap example:

```json
{
  "signatureFields": {
    "overlap": true,
    "overlapOffsetX": 0,
    "overlapOffsetY": 0
  }
}
```

Response:

- HTTP 200 with `application/pdf` binary

### `POST /api/google-sheet/set-endpoint`

Use this API to update Apps Script config cell automatically after ngrok endpoint is generated.

Request:

```json
{
  "sheetId": "1abcDEF.....",
  "targetA1": "CONFIG!B32",
  "webhookUrl": "https://script.google.com/macros/s/.../exec",
  "endpoint": "https://xxxx.ngrok-free.app/api/pdf/stamp"
}
```

Regression check:

```bash
npm run test:stamp-regression
```

Edit `scripts/regression-cases.sample.json` to match your real scan files.

Notes:

- `sheetId` can be either plain Sheet ID or full Google Sheet URL.
- `targetA1` is optional, default is `CONFIG!B32`.
- `webhookUrl` is optional. If omitted, backend uses `GOOGLE_SHEET_SYNC_URL` from env.
- If `GOOGLE_SHEET_SYNC_API_KEY` is set, backend sends it as `x-api-key` header.

Response:

- `success: true` when upstream sync API accepts the update.

## Error format

```json
{
  "success": false,
  "errorCode": "SOME_CODE",
  "message": "Readable error message"
}
```

## Release Gate (Real Machine Matrix)

- Manual matrix before release is documented at:
  - `docs/release-test-matrix.md`
