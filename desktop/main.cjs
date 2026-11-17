const { app, BrowserWindow, Menu, dialog, shell, session, ipcMain } = require('electron');
const { spawn, execFile } = require('node:child_process');
const { randomBytes } = require('node:crypto');
const { once } = require('node:events');
const { createInterface } = require('node:readline');
const path = require('node:path');
const fs = require('node:fs');
const { isInternal, isExternalAllowed } = require('./navigation.cjs');
const { createUpdateSettings } = require('./settings.cjs');

const smoke = process.argv.includes('--smoke-test');
if (smoke) app.setPath('userData', process.env.BUAA_SMOKE_USER_DATA || path.join(app.getPath('temp'), 'buaa-desktop-smoke'));
const token = randomBytes(32).toString('hex');
let backend, origin, window, quitting = false, stopping = false;
const pause = ms => new Promise(resolve => setTimeout(resolve, ms));

let desktopSettings, pendingInstaller, dataDirectory;
const updateTarget = app.isPackaged
  ? process.platform === 'win32' && process.arch === 'x64' ? 'win-x64.exe'
    : process.platform === 'darwin' && ['arm64', 'x64'].includes(process.arch) ? `mac-${process.arch}.dmg`
    : null
  : null;
const diagnosticLog = () => path.join(app.getPath('userData'), 'desktop.log');

function registerDesktopSettings() {
  desktopSettings = createUpdateSettings({
    version: app.getVersion(),
    target: updateTarget,
    directory: path.join(app.getPath('userData'), 'desktop-update-cache'),
    publish: state => {
      if (window && !window.isDestroyed()) window.webContents.send('desktop-settings:update', state);
    },
    install: async installer => {
      if (quitting) return false;
      pendingInstaller = installer;
      app.quit();
      return true;
    },
  });
  const handlers = {
    state: desktopSettings.state,
    check: desktopSettings.check,
    download: desktopSettings.download,
    cancel: desktopSettings.cancel,
    install: desktopSettings.install,
    'open-data': async () => !(await shell.openPath(dataDirectory)),
    'open-log': async () => !(await shell.openPath(diagnosticLog())),
  };
  for (const [name, handle] of Object.entries(handlers)) {
    ipcMain.handle(`desktop-settings:${name}`, event => {
      const frame = event.senderFrame;
      if (quitting || event.sender !== window?.webContents || !frame
          || frame !== event.sender.mainFrame || !isInternal(frame.url, origin)) {
        throw new Error('Desktop settings are only available in the app window.');
      }
      return handle();
    });
  }
}

async function openInstaller(installer) {
  if (process.platform === 'darwin') {
    if (await shell.openPath(installer)) throw new Error('Could not open installer image.');
    return;
  }
  const child = spawn(installer, ['--updated'], { detached: true, stdio: 'ignore' });
  await once(child, 'spawn');
  child.unref();
}

async function startBackend() {
  const exe = process.platform === 'win32' ? 'buaa-backend.exe' : 'buaa-backend';
  const executable = app.isPackaged
    ? path.join(process.resourcesPath, 'backend', exe)
    : path.resolve(__dirname, '../dist/backend/buaa-backend', exe);
  backend = spawn(executable, ['--user-data', app.getPath('userData')], {
    cwd: app.getPath('userData'), windowsHide: true,
    env: { ...process.env, BUAA_DESKTOP_TOKEN: token, PYTHONUNBUFFERED: '1' },
    stdio: ['ignore', 'pipe', 'pipe'],
  });
  // Only service diagnostics, never access logs or configuration values.
  const logPath = diagnosticLog();
  try {
    if (fs.statSync(logPath).size >= 5 * 1024 ** 2) fs.renameSync(logPath, logPath + '.previous');
  } catch (error) {
    if (error.code !== 'ENOENT') console.error('Could not rotate diagnostic log:', error.code);
  }
  const log = fs.createWriteStream(logPath, { flags: 'a', mode: 0o600 });
  log.on('error', error => console.error('Could not write diagnostic log:', error.code));
  backend.stderr.pipe(log);
  const address = new Promise((resolve, reject) => {
    const lines = createInterface({ input: backend.stdout });
    lines.on('line', line => {
      const match = /^BUAA_PORT=(\d+)$/.exec(line);
      if (match) resolve(`http://127.0.0.1:${match[1]}`);
    });
    backend.once('error', reject);
    backend.once('exit', () => reject(new Error('本地服务启动失败，请在配置目录查看 desktop.log。')));
  });
  const timeout = setTimeout(() => { if (!origin) backend.kill(); }, 60000);
  try {
    origin = await address;
    for (let i = 0; i < 240; i++) {
      try {
        const res = await fetch(`${origin}/api/desktop/health`, {
          headers: { 'X-BUAA-Desktop': token }, signal: AbortSignal.timeout(1500),
        });
        if (res.ok) {
          const health = await res.json();
          if (health.status === 'ready') {
            dataDirectory = health.data_directory || path.join(app.getPath('userData'), 'data');
            return;
          }
        }
      } catch { /* Wait for the bound service to start accepting requests. */ }
      if (backend.exitCode !== null) break;
      await pause(250);
    }
    throw new Error('本地服务启动超时，请在配置目录查看 desktop.log。');
  } finally { clearTimeout(timeout); }
}

async function stopBackend() {
  if (!backend || backend.exitCode !== null || backend.signalCode !== null) return;
  stopping = true;
  if (origin) {
    try {
      await fetch(`${origin}/api/desktop/quit`, {
        method: 'POST', headers: { 'X-BUAA-Desktop': token, 'X-BUAA-Local': '1' },
        signal: AbortSignal.timeout(2000),
      });
    } catch { /* The service may already have stopped. */ }
  }
  for (let i = 0; i < 60 && backend.exitCode === null && backend.signalCode === null; i++) await pause(250);
  if (backend.exitCode === null && backend.signalCode === null) {
    if (process.platform === 'win32') {
      await new Promise(resolve => execFile('taskkill', ['/pid', String(backend.pid), '/T', '/F'], { timeout: 3000 }, resolve));
    } else {
      backend.kill('SIGKILL');
    }
    for (let i = 0; i < 12 && backend.exitCode === null && backend.signalCode === null; i++) await pause(250);
  }
}

function createWindow() {
  window = new BrowserWindow({
    title: 'BUAA Learn', width: 1400, height: 900, minWidth: 960, minHeight: 640,
    backgroundColor: '#ffffff', show: false,
    webPreferences: { preload: path.join(__dirname, 'settings-preload.cjs'), nodeIntegration: false, contextIsolation: true, sandbox: true, spellcheck: false },
  });
  window.once('ready-to-show', () => window.show());
  window.on('closed', () => { window = null; });
  window.webContents.on('will-navigate', (event, url) => {
    if (!isInternal(url, origin)) { event.preventDefault(); if (isExternalAllowed(url)) shell.openExternal(url); }
  });
  window.webContents.setWindowOpenHandler(({ url }) => {
    if (isExternalAllowed(url)) shell.openExternal(url);
    return { action: 'deny' };
  });
  window.loadURL(origin);
  if (smoke) window.webContents.once('did-finish-load', async () => {
    try {
      const ready = await window.webContents.executeJavaScript(`(async () => {
        const state = await fetch('/api/state').then(r => r.json());
        return !!document.querySelector('main') && Array.isArray(state.courses) && typeof require === 'undefined';
      })()`);
      if (!ready) throw new Error('Renderer smoke test failed');
      if (process.env.BUAA_SMOKE_SCREENSHOT) {
        await pause(1000);
        fs.writeFileSync(process.env.BUAA_SMOKE_SCREENSHOT, (await window.webContents.capturePage()).toPNG());
      }
      const settingsReady = await window.webContents.executeJavaScript(`(async () => {
        const state = await window.buaaDesktop.settings();
        location.hash = '#/settings';
        for (let attempt = 0; attempt < 50; attempt++) {
          const settings = document.querySelector('#desktop-settings');
          if (settings?.textContent.includes(state.version) && settings.querySelectorAll('button').length === 3) {
            return typeof require === 'undefined';
          }
          await new Promise(resolve => setTimeout(resolve, 100));
        }
        return false;
      })()`);
      if (!settingsReady) throw new Error('Desktop settings smoke test failed');
      console.log('BUAA_DESKTOP_OK');
      app.quit();
    } catch (error) { console.error(error.message); process.exitCode = 1; app.quit(); }
  });
}

if (!app.requestSingleInstanceLock()) app.quit();
else {
  app.on('second-instance', () => {
    if (!window && origin && !quitting) createWindow();
    if (window) { if (window.isMinimized()) window.restore(); window.focus(); }
  });
  app.whenReady().then(async () => {
    fs.mkdirSync(app.getPath('userData'), { recursive: true, mode: 0o700 });
    try {
      await startBackend();
      registerDesktopSettings();
      const ses = session.defaultSession;
      ses.setPermissionRequestHandler((_contents, _permission, callback) => callback(false));
      ses.setPermissionCheckHandler(() => false);
      ses.webRequest.onBeforeSendHeaders({ urls: [`${origin}/*`] }, (details, callback) => {
        callback({ requestHeaders: { ...details.requestHeaders, 'X-BUAA-Desktop': token } });
      });
      ses.on('will-download', (_event, item) => {
        item.setSaveDialogOptions({ title: '保存导出文件', defaultPath: path.join(app.getPath('downloads'), item.getFilename()) });
      });
      backend.on('exit', () => {
        if (!quitting && !stopping) {
          dialog.showErrorBox('BUAA Learn', '本地服务已停止。请重新打开应用；诊断记录位于配置目录 desktop.log。');
          app.quit();
        }
      });
      Menu.setApplicationMenu(Menu.buildFromTemplate([
        ...(process.platform === 'darwin' ? [{ role: 'appMenu' }] : []),
        { label: '文件', submenu: [
          { label: '模型与设置', click: () => window?.loadURL(`${origin}/#/settings`) },
          { label: '打开配置目录', click: () => shell.openPath(app.getPath('userData')) },
          { type: 'separator' }, { role: 'quit', label: '退出 BUAA Learn' },
        ] },
        { role: 'editMenu', label: '编辑' },
        { label: '视图', submenu: [{ role: 'reload', label: '重新加载' }, { role: 'resetZoom' }, { role: 'zoomIn' }, { role: 'zoomOut' }, { role: 'togglefullscreen' }] },
        { role: 'windowMenu', label: '窗口' },
      ]));
      createWindow();
      app.on('activate', () => { if (!window && !quitting) createWindow(); });
    } catch (error) {
      if (smoke) { console.error(error.message); process.exitCode = 1; }
      else dialog.showErrorBox('BUAA Learn 启动失败', error.message);
      app.quit();
    }
  });
}
app.on('window-all-closed', () => { if (process.platform !== 'darwin') app.quit(); });
app.on('before-quit', event => {
  if (quitting) return;
  event.preventDefault(); quitting = true;
  desktopSettings?.cancel();
  stopBackend().then(async () => {
    if (!pendingInstaller) return;
    try {
      if (backend && backend.exitCode === null && backend.signalCode === null) {
        throw new Error('Local service is still running.');
      }
      await openInstaller(pendingInstaller);
    } catch {
      dialog.showErrorBox('BUAA Learn 更新', '安装程序未能启动。请重新打开设置后重试。');
      app.relaunch();
    }
  }).finally(() => app.quit());
});
