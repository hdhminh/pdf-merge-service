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

## Environment

```env
PORT=3000
MAX_FILE_SIZE_BYTES=20971520
```

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
ngrok http 3000
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
- Backend tries to anchor the stamp just below the last detected text or image content (ignoring the stamp text itself). If no content is found or there is not enough space, it falls back to the previous auto-margin based on displayed page height or `textLayout.marginBottom` if provided.
- Control this behavior with `textLayout.anchorToLastText` (default `true`), `textLayout.lastTextGap` (default `12`), and `textLayout.minMarginBottom` (default `12`).
- Auto-anchor scan mode: `textLayout.scanMode` supports `auto` (default), `textOnly`, and `textAndImages`.
- In `auto`, backend detects whether the last page has embedded images. If yes, it scans text + images; if not, it scans text only for better speed.
- Legacy override: `textLayout.scanImages` (`true`/`false`) still works and takes priority over `scanMode`.
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
  - personal field is placed below enterprise with 25% vertical overlap
  - signature fields are injected by the .NET/iText helper at `tools/signature-field-tool/SignatureFieldTool`
- Build helper before running backend:
  - `dotnet build tools/signature-field-tool/SignatureFieldTool/SignatureFieldTool.csproj -c Release`
  - `dotnet` runtime/SDK is required at runtime when `signatureFields.enabled` is `true`
- `signatureFields` controls field names and geometry (`enabled`, `enterpriseName`, `personalName`, `height`, `width`, `centerGap`, `lineGap`, `sideInset`, `replaceExisting`, `overlap`, `overlapOffsetX`, `overlapOffsetY`).
- `replaceExisting` defaults to `true`: if a signature field with the same name already exists, backend replaces it instead of creating `_2`, `_3`, ...
- `signatureFields.overlap` defaults to `true` (stacked/overlapped layout). Set `overlap: false` to separate left/right fields.
- `overlapOffsetX` / `overlapOffsetY` controls offset between overlapped fields.
  - default `overlapOffsetX` is `0`
  - default `overlapOffsetY` is `-25%` of field height (personal field lower)
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

## Error format

```json
{
  "success": false,
  "errorCode": "SOME_CODE",
  "message": "Readable error message"
}
```
