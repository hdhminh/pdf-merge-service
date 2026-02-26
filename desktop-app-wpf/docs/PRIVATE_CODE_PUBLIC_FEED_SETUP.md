# Private Code + Public Update Feed

Muc tieu:
- Repo code: private (khong lo source).
- Repo feed update: public (chi chua release assets).
- Client van auto-update binh thuong.

## 1) Tao update feed repo public
Tao 1 repo moi, vi du:
- `https://github.com/hdhminh/pdf-merge-service-updates`

Repo nay co the de trong, chi can dung de chua Releases.

## 2) Cau hinh repo code private
Trong repo code private (`pdf-merge-service`), vao:
- Settings -> Secrets and variables -> Actions

Them:
- Variable: `UPDATE_FEED_REPO_URL`
  - Value: `https://github.com/hdhminh/pdf-merge-service-updates`
  - Ghi chu: workflow hien da set san default URL nay, variable chi can neu ban muon doi repo feed.

- Secret: `UPDATE_FEED_PAT`
  - PAT de upload release sang feed repo.
  - Fine-grained token: cap quyen `Contents: Read and write` cho feed repo.

## 3) Release tu repo private
Tag release nhu binh thuong:

```bash
git tag v1.3.4
git push origin v1.3.4
```

Workflow se:
- Build/test trong repo private.
- Pack Velopack.
- Upload release assets sang `UPDATE_FEED_REPO_URL`.
- Embed `UpdateRepoUrl` vao app = feed repo URL.

## 4) Client su dung
- Lan dau cai `...Setup.exe` tu feed repo release.
- Ve sau app tu check/update tu feed repo.

## 5) Luu y
- Khong gui client file exe build roi ben ngoai Velopack release.
- Neu doi feed repo URL, can release ban moi de app nhan URL moi.
