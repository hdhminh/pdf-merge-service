# PDF Stamp Desktop App

Desktop app nay chay backend PDF stamp local va tao ngrok endpoint de dan vao Apps Script.

## Muc tieu su dung

- Quan ly nhieu ngrok authtoken theo profile
- Swap nhanh giua cac token
- Bam 1 nut de tao link ngrok
- Bam nut Copy de copy endpoint ngay

## Cac buoc dung app

1. Mo app
2. Them token (name + authtoken)
3. Chon profile can dung
4. Bam `Tao link ngrok`
5. Bam `Copy` de copy endpoint `/api/pdf/stamp`

## Cau hinh

Mac dinh app doc/ghi config user o file:

- `%APPDATA%/pdf-stamp-desktop-app/app-config.json` (tuy runtime)

Mau config:

```json
{
  "backendPort": 3000,
  "ngrokAuthtoken": "",
  "ngrokRegion": "ap",
  "autoStartNgrok": true,
  "ngrokProfiles": [],
  "activeNgrokProfileId": null
}
```

## Phat trien

```bash
cd desktop-app
npm install
npm start
```

## Build Windows

```bash
cd desktop-app
npm run dist:win
```

Neu can portable:

```bash
npm run dist:win:portable
```

## Bien moi truong tuy chon

- `BACKEND_PORT`
- `NGROK_CMD`
- `NGROK_AUTHTOKEN`
- `NGROK_REGION`
- `AUTO_START_NGROK`
