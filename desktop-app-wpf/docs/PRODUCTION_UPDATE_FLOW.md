# Production Auto-Update Flow (Velopack + GitHub Releases + CI)

## 1) Muc tieu
- Build/phat hanh app WPF len GitHub Releases theo version.
- Client da cai app qua Velopack se tu check update khi mo app.
- Khi co ban moi: app tu download, apply, va restart.

## 2) Da cau hinh trong code
- `UpdateService` dung `Velopack.UpdateManager + GithubSource`.
- `Program.cs` goi `VelopackApp.Build().Run()` ngay entrypoint.
- `MainViewModel.InitializeAsync()` goi check update nen.
- Build metadata duoc inject qua MSBuild:
  - `UpdateRepoUrl`
  - `UpdateChannel`

## 3) CI/CD workflows
- CI: `.github/workflows/desktop-app-wpf.yml`
  - restore + build + test + publish artifact.
- Release: `.github/workflows/desktop-app-wpf-release.yml`
  - trigger khi push tag `v*` hoac `workflow_dispatch`.
  - publish app
  - `vpk pack`
  - `vpk upload github`

## 4) Cach dev phat hanh ban moi
1. Tang version theo semver (vd `1.4.0`).
2. Tao tag va push:
   - `git tag v1.4.0`
   - `git push origin v1.4.0`
3. GitHub Actions workflow `Desktop App WPF Release` se tao/update release `v1.4.0`.
4. Artifacts Velopack se duoc upload len release.

## 5) Client nhan update
- Lan dau: client cai app tu installer Velopack trong Release.
- Cac lan sau: chi can mo app, updater se tu xu ly neu co ban moi.
- Neu client chay ban exe roi (portable/dev publish), updater se bo qua check update.

## 6) Ghi chu van hanh
- Neu dung private repo, can token co quyen doc release feed.
- Nen dung code-signing cho installer/exe de giam canh bao SmartScreen.
- Kenh release:
  - `stable` cho production.
  - `beta` cho thu nghiem (workflow co support).

## 7) Kien truc private code
- Dung 2 repo:
  - Repo code: private.
  - Repo update feed: public (chi chua release assets).
- Xem huong dan chi tiet:
  - `desktop-app-wpf/docs/PRIVATE_CODE_PUBLIC_FEED_SETUP.md`
