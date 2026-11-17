const { checkForUpdate, downloadUpdate, verifiedInstaller } = require('./updates.cjs');

function createUpdateSettings({ version, target, directory, publish, install, request }) {
  let state = { version, supported: Boolean(target), manualInstall: target?.endsWith('.dmg') ?? false,
    status: 'idle', update: null, error: '', percent: 0 };
  let update, busy, controller;
  const change = patch => {
    state = { ...state, ...patch };
    publish(state);
    return state;
  };

  async function run(status, operation, fallback) {
    if (!target || busy) return state;
    busy = true;
    change({ status, error: '', percent: 0 });
    try {
      return await operation();
    } catch (error) {
      console.error('Desktop update:', error.name);
      return change({ status: fallback, error: error.message.startsWith('安装包') ? error.message : '操作失败，请稍后重试。' });
    } finally {
      busy = false;
    }
  }

  return {
    state: () => state,
    check: () => run('checking', async () => {
      const candidate = await checkForUpdate(version, target, request);
      if (!candidate) { update = null; return change({ status: 'current', update: null }); }
      const { version: nextVersion, size, notes } = candidate;
      const cached = await verifiedInstaller(candidate, directory);
      update = candidate;
      return change({ status: cached ? 'ready' : 'available', update: { version: nextVersion, size, notes } });
    }, state.status === 'ready' || state.status === 'available' ? state.status : 'idle'),
    download: () => state.status === 'available' ? run('downloading', async () => {
      controller = new AbortController();
      let percent = -1;
      try {
        await downloadUpdate(update, directory, {
          signal: AbortSignal.any([controller.signal, AbortSignal.timeout(30 * 60000)]),
          request,
          progress: fraction => {
            const next = Math.floor(fraction * 100);
            if (percent !== next) change({ percent: percent = next });
          },
        });
        return change({ status: 'ready' });
      } catch (error) {
        if (controller.signal.aborted) return change({ status: 'available', percent: 0 });
        throw error;
      } finally {
        controller = undefined;
      }
    }, 'available') : state,
    cancel: () => { controller?.abort(); },
    install: () => state.status === 'ready' ? run('installing', async () => {
      const installer = await verifiedInstaller(update, directory);
      if (!installer) {
        return change({ status: 'available', error: '安装包校验失败，请重新下载。' });
      }
      if (await install(installer) === false) return change({ status: 'ready' });
      return state;
    }, 'ready') : state,
  };
}

module.exports = { createUpdateSettings };
