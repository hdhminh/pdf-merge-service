# Release Notes v1.3.24 (2026-03-11)

## Highlights

- Improved scan stamping behavior with profile/classifier updates for better bottom anchoring on scan-heavy PDFs.
- Updated signature-field placement defaults:
  - enterprise field left-shifted by 10% field width
  - personal field lower-right with 20% vertical overlap
- Added/updated Guide Step 5 asset and wording to clearly show:
  - enterprise signature box (con dau)
  - personal signature box (cong chung vien)
- Fixed release pipeline blocker by accepting the current ngrok vendor checksum in `scripts/ensure-bundled-ngrok.ps1`.

## Operational Notes

- Tag `v1.3.23` failed at `Ensure bundled ngrok` because vendor zip checksum changed.
- `v1.3.24` re-ran release with checksum fix and completed successfully.
