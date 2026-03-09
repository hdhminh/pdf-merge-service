# Release Test Matrix (May That)

## Muc tieu
- Xac nhan ban release chay on tren may Windows pho bien.
- Dam bao client cai app la dung ngay, khong can cai Node/.NET/ngrok thu cong.

## Cau hinh bat buoc moi may test
- Chua cai Node.js.
- Chua cai .NET SDK/Runtime.
- Chua cai ngrok thu cong.
- Da go bo ban cu cua app (neu co), sau do khoi dong lai may.

## Matrix
| ID | May/OS | Kieu may | Mang | Ket qua |
|---|---|---|---|---|
| M1 | Windows 11 23H2/24H2 x64 | May that | Wifi van phong | TODO |
| M2 | Windows 10 22H2 x64 | May that | Wifi van phong | TODO |
| M3 | Windows 11 x64 | May yeu (RAM 8GB) | Wifi gia dinh | TODO |

## Checklist test cho tung may
1. Cai file `PdfStampNgrokDesktop-stable-Setup.exe` tu ban release moi nhat.
2. Mo app lan dau:
   - Khong bao loi thieu backend/runtime.
   - Mo duoc cua so chinh trong <= 10 giay.
3. Them token va bam `Dung`:
   - Khong crash.
   - Luu profile xong mo lai app van con.
4. Bam `Tao link`:
   - Tao duoc endpoint `/api/pdf/stamp`.
   - Trang thai hien `Da tao link`.
5. Bam `Huy link`:
   - Tunnel dung that su.
   - Bam `Tao link` lai tao endpoint moi binh thuong.
6. Kiem tra webhook Google Sheet (neu bat panel dev):
   - Endpoint duoc ghi dung vao o cau hinh.
7. Kiem tra huong dan trong app (`?`):
   - Mo du 4 buoc, anh hien dung, khong loi font.
8. Kiem tra auto-update:
   - Tang version test (hoac dung channel beta).
   - App hien thong bao cap nhat va cap nhat thanh cong sau khi bam `Cap nhat`.
9. Chay regression stamp scan:
   - Chinh danh sach file trong `scripts/regression-cases.sample.json` (hoac tao manifest rieng).
   - Chay `npm run test:stamp-regression`.
   - Dam bao tat ca case `PASS` va xem log `[stamp-timing]` khong bi regression lon.

## Tieu chi dat release
- Tat ca M1, M2 bat buoc `PASS`.
- M3 khuyen nghi `PASS` (neu fail, can ghi ro nguyen nhan/perf).
- Khong con loi P1 (khoi dong that bai, tao/huy link hong, update hong).

## Mau log can thu thap khi fail
- `%APPDATA%\\PdfStampNgrokDesktop\\logs\\app-*.log`
- Screenshot man hinh loi.
- Thoi diem test + ID may test (M1/M2/M3).
