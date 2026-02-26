const elements = {
  profileBadge: document.getElementById("profileBadge"),
  ngrokBadge: document.getElementById("ngrokBadge"),
  publicStampUrl: document.getElementById("publicStampUrl"),
  statusText: document.getElementById("statusText"),
  newLinkBtn: document.getElementById("newLinkBtn"),
  refreshBtn: document.getElementById("refreshBtn"),
  copyStampBtn: document.getElementById("copyStampBtn"),
  tokenSelect: document.getElementById("tokenSelect"),
  applyTokenBtn: document.getElementById("applyTokenBtn"),
  removeTokenBtn: document.getElementById("removeTokenBtn"),
  tokenNameInput: document.getElementById("tokenNameInput"),
  tokenValueInput: document.getElementById("tokenValueInput"),
  addTokenBtn: document.getElementById("addTokenBtn"),
};

let busy = false;
let latestStatus = null;

function setStatusText(text) {
  elements.statusText.textContent = text;
}

function toErrorMessage(err, fallback) {
  if (!err) return fallback;
  if (typeof err === "string") return err;
  if (err.error) return err.error;
  if (err.message) return err.message;
  return fallback;
}

function setBadge(element, text, type) {
  element.textContent = text;
  element.classList.remove("badge-on", "badge-off", "badge-error");
  if (type === "on") {
    element.classList.add("badge-on");
    return;
  }
  if (type === "error") {
    element.classList.add("badge-error");
    return;
  }
  element.classList.add("badge-off");
}

function setBusy(value) {
  busy = value;
  const disabled = Boolean(value);

  [
    elements.newLinkBtn,
    elements.refreshBtn,
    elements.copyStampBtn,
    elements.applyTokenBtn,
    elements.removeTokenBtn,
    elements.addTokenBtn,
  ].forEach((btn) => {
    btn.disabled = disabled;
  });

  elements.tokenSelect.disabled = disabled;
  elements.tokenNameInput.disabled = disabled;
  elements.tokenValueInput.disabled = disabled;
}

function renderTokenProfiles(status) {
  const profiles = Array.isArray(status?.app?.ngrokProfiles)
    ? status.app.ngrokProfiles
    : [];
  const activeId = status?.app?.activeNgrokProfileId || "";

  elements.tokenSelect.innerHTML = "";

  if (profiles.length === 0) {
    const opt = document.createElement("option");
    opt.value = "";
    opt.textContent = "Chưa có token";
    elements.tokenSelect.appendChild(opt);
    elements.tokenSelect.value = "";
    setBadge(elements.profileBadge, "Chưa có token", "off");
    elements.applyTokenBtn.disabled = true;
    elements.removeTokenBtn.disabled = true;
    return;
  }

  profiles.forEach((profile, index) => {
    const opt = document.createElement("option");
    opt.value = profile.id;
    const name = profile.name || `Token ${index + 1}`;
    const preview = profile.tokenPreview ? ` (${profile.tokenPreview})` : "";
    opt.textContent = `${name}${preview}`;
    elements.tokenSelect.appendChild(opt);
  });

  elements.tokenSelect.value = activeId || profiles[0].id;
  setBadge(
    elements.profileBadge,
    `Đang dùng: ${status?.app?.activeNgrokProfileName || "Token"}`,
    "on",
  );
}

function renderNgrok(status) {
  const stampUrl = status?.ngrok?.stampUrl || "";
  elements.publicStampUrl.value = stampUrl;

  if (stampUrl) {
    setBadge(elements.ngrokBadge, "Đã có link", "on");
    return;
  }

  if (status?.ngrok?.lastError) {
    setBadge(elements.ngrokBadge, status.ngrok.lastError, "error");
    return;
  }

  setBadge(elements.ngrokBadge, "Chưa có link", "off");
}

function syncStatusTextFromState(status) {
  if (busy) return;

  if (status?.ngrok?.stampUrl) {
    setStatusText("Sẵn sàng.");
    return;
  }

  if (status?.ngrok?.lastError) {
    setStatusText(status.ngrok.lastError);
  }
}

function render(status) {
  latestStatus = status;
  renderTokenProfiles(status);
  renderNgrok(status);
  syncStatusTextFromState(status);
}

async function refreshStatus() {
  const status = await window.desktopApi.getStatus();
  render(status);
  return status;
}

async function runAction(message, handler) {
  setBusy(true);
  setStatusText(message);

  try {
    const response = await handler();

    if (response && typeof response === "object" && "ok" in response) {
      render(response.status);
      setStatusText(response.ok ? "Hoàn tất." : response.error || "Có lỗi xảy ra.");
      return;
    }

    render(response);
    setStatusText("Hoàn tất.");
  } catch (err) {
    setStatusText(toErrorMessage(err, "Có lỗi xảy ra."));
  } finally {
    setBusy(false);
  }
}

async function copyLink() {
  const value = String(elements.publicStampUrl.value || "").trim();
  if (!value) {
    setStatusText("Chưa có link để copy.");
    return;
  }

  await window.desktopApi.copyText(value);
  setStatusText("Đã copy link.");
}

elements.newLinkBtn.addEventListener("click", async () => {
  await runAction("Đang tạo link ngrok...", () => window.desktopApi.startNgrok(true));
});

elements.refreshBtn.addEventListener("click", async () => {
  await runAction("Đang làm mới trạng thái...", () => window.desktopApi.getStatus());
});

elements.copyStampBtn.addEventListener("click", async () => {
  try {
    await copyLink();
  } catch (err) {
    setStatusText(toErrorMessage(err, "Không thể copy link."));
  }
});

elements.applyTokenBtn.addEventListener("click", async () => {
  const profileId = String(elements.tokenSelect.value || "").trim();
  if (!profileId) {
    setStatusText("Bạn chưa chọn token.");
    return;
  }

  await runAction("Đang áp dụng token và tạo lại link...", () =>
    window.desktopApi.selectNgrokToken(profileId, true),
  );
});

elements.addTokenBtn.addEventListener("click", async () => {
  const token = String(elements.tokenValueInput.value || "").trim();
  const name = String(elements.tokenNameInput.value || "").trim();

  if (!token) {
    setStatusText("Bạn cần nhập token trước khi thêm.");
    return;
  }

  await runAction("Đang thêm token và tạo link...", async () => {
    const result = await window.desktopApi.addNgrokToken({ name, token });
    elements.tokenNameInput.value = "";
    elements.tokenValueInput.value = "";
    return result;
  });
});

elements.removeTokenBtn.addEventListener("click", async () => {
  const profileId = String(elements.tokenSelect.value || "").trim();
  if (!profileId) {
    setStatusText("Bạn chưa chọn token để xóa.");
    return;
  }

  await runAction("Đang xóa token...", () => window.desktopApi.removeNgrokToken(profileId));
});

async function boot() {
  setBusy(true);
  setStatusText("Đang tải trạng thái...");

  try {
    await refreshStatus();
    setStatusText("Sẵn sàng.");
  } catch (err) {
    setStatusText(toErrorMessage(err, "Không lấy được trạng thái ban đầu."));
  } finally {
    setBusy(false);
  }

  setInterval(async () => {
    if (busy) return;

    try {
      await refreshStatus();
    } catch (_err) {
      if (latestStatus?.ngrok?.publicUrl) {
        setStatusText("Mất kết nối tạm thời, đang thử lại...");
      }
    }
  }, 5000);
}

boot();
