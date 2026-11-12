const { contextBridge, ipcRenderer } = require('electron');

contextBridge.exposeInMainWorld('buaaDesktop', {
  settings: () => ipcRenderer.invoke('desktop-settings:state'),
  checkUpdate: () => ipcRenderer.invoke('desktop-settings:check'),
  downloadUpdate: () => ipcRenderer.invoke('desktop-settings:download'),
  cancelUpdate: () => ipcRenderer.invoke('desktop-settings:cancel'),
  installUpdate: () => ipcRenderer.invoke('desktop-settings:install'),
  openData: () => ipcRenderer.invoke('desktop-settings:open-data'),
  openLog: () => ipcRenderer.invoke('desktop-settings:open-log'),
  onUpdate: callback => {
    const listener = (_event, state) => callback(state);
    ipcRenderer.on('desktop-settings:update', listener);
    return () => ipcRenderer.removeListener('desktop-settings:update', listener);
  },
});
