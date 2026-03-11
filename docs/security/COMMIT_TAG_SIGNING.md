# Commit and Tag Signing Guide

This guide makes commit/tag authorship verifiable on GitHub.

## 1) Configure identity

```powershell
git config --global user.name "Huynh Minh"
git config --global user.email "minhhuynhdoanhoang@gmail.com"
```

## 2) Choose signing method

- Option A: GPG signing (traditional)
- Option B: SSH signing (simpler if you already use SSH keys)

## 3) Enable signing defaults

```powershell
git config --global commit.gpgsign true
git config --global tag.gpgSign true
```

## 4) GPG setup (Option A)

```powershell
gpg --list-secret-keys --keyid-format=long
git config --global gpg.format openpgp
git config --global user.signingkey <YOUR_GPG_KEY_ID>
```

Verify:

```powershell
git commit --allow-empty -S -m "test: signed commit"
git tag -s v-test-sign -m "test signed tag"
```

## 5) SSH setup (Option B)

```powershell
git config --global gpg.format ssh
git config --global user.signingkey <PATH_TO_PUBLIC_SSH_KEY>
```

Example:

```powershell
git config --global user.signingkey "$HOME/.ssh/id_ed25519.pub"
```

Then add that SSH signing key in GitHub:
- Settings -> SSH and GPG keys -> New SSH key -> key type "Signing Key"

## 6) Verify local config quickly

```powershell
./scripts/check-git-signing.ps1
```

## 7) Verify on GitHub UI

After push, commit/tag should show `Verified` badge on GitHub.
