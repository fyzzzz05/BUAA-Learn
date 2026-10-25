const { app, BrowserWindow, Menu, dialog, shell, session } = require('electron');
const { spawn, execFile } = require('node:child_process');
const { randomBytes } = require('node:crypto');
const { createInterface } = require('node:readline');
const path = require('node:path');
const fs = require('node:fs');
const { isInternal, isExternalAllowed } = require('./navigation.cjs');

const smoke = process.argv.includes('--smoke-test');
if (smoke) app.setPath('userData', process.env.BUAA_SMOKE_USER_DATA || path.join(app.getPath('temp'), 'buaa-desktop-smoke'));
const token = randomBytes(32).toString('hex');
let backend, origin, window, quitting = false, stopping = false;
const pause = ms => new Promise(resolve => setTimeout(resolve, ms));

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
  const log = fs.createWriteStream(path.join(app.getPath('userData'), 'desktop.log'), { flags: 'w', mode: 0o600 });
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
        if (res.ok && (await res.json()).status === 'ready') return;
      } catch { /* Wait for the bound service to start accepting requests. */ }
      if (backend.exitCode !== null) break;
      await pause(250);
    }
    throw new Error('本地服务启动超时，请在配置目录查看 desktop.log。');
  } finally { clearTimeout(timeout); }
}

async function stopBackend() {
  if (!backend || backend.exitCode !== null) return;
  stopping = true;
  if (origin) {
    try {
      await fetch(`${origin}/api/desktop/quit`, {
        method: 'POST', headers: { 'X-BUAA-Desktop': token, 'X-BUAA-Local': '1' },
        signal: AbortSignal.timeout(2000),
      });
    } catch { /* The service may already have stopped. */ }
  }
  for (let i = 0; i < 60 && backend.exitCode === null; i++) await pause(250);
  if (backend.exitCode === null) {
    if (process.platform === 'win32') execFile('taskkill', ['/pid', String(backend.pid), '/T', '/F']);
    else backend.kill('SIGKILL');
  }
}

function createWindow() {
  window = new BrowserWindow({
    title: 'BUAA Learn', width: 1400, height: 900, minWidth: 960, minHeight: 640,
    backgroundColor: '#ffffff', show: false,
    webPreferences: { nodeIntegration: false, contextIsolation: true, sandbox: true, spellcheck: false },
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
      console.log('BUAA_DESKTOP_OK');
      app.quit();
    } catch (error) { console.error(error.message); process.exitCode = 1; app.quit(); }
  });
}

if (!app.requestSingleInstanceLock()) app.quit();
else {
  app.on('second-instance', () => { if (window) { if (window.isMinimized()) window.restore(); window.focus(); } });
  app.whenReady().then(async () => {
    fs.mkdirSync(app.getPath('userData'), { recursive: true, mode: 0o700 });
    try {
      await startBackend();
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
  stopBackend().finally(() => app.quit());
});
