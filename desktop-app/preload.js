const { contextBridge, ipcRenderer } = require("electron");

function invoke(channel, payload) {
  return ipcRenderer.invoke(channel, payload);
}

contextBridge.exposeInMainWorld("desktopApi", {
  getStatus: () => invoke("status:get"),
  startBackend: () => invoke("backend:start"),
  stopBackend: () => invoke("backend:stop"),
  startNgrok: (restart = true) => invoke("ngrok:start", { restart }),
  stopNgrok: () => invoke("ngrok:stop"),
  addNgrokToken: (payload) => invoke("config:add-token", payload),
  removeNgrokToken: (profileId) =>
    invoke("config:remove-token", { profileId }),
  selectNgrokToken: (profileId, restartNgrok = true) =>
    invoke("config:select-token", { profileId, restartNgrok }),
  copyText: (text) => invoke("clipboard:copy", text),
});
