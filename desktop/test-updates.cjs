const assert = require('node:assert/strict');
const { createHash } = require('node:crypto');
const { mkdtemp, readdir, rm, writeFile } = require('node:fs/promises');
const { tmpdir } = require('node:os');
const path = require('node:path');
const test = require('node:test');
const { createUpdateSettings } = require('./settings.cjs');
const { checkForUpdate, downloadUpdate, verifiedInstaller } = require('./updates.cjs');

const content = Buffer.from('synthetic installer');
const metadata = {
  version: '0.2.10', size: content.length, sha256: createHash('sha256').update(content).digest('hex'),
};
function release(version, target, overrides = {}) {
  return {
    tag_name: `desktop-v${version}`,
    published_at: '2026-11-11T00:00:00Z',
    body: '更新内容',
    assets: [{
      name: `BUAA-Learn-${version}-${target}`, state: 'uploaded',
      size: metadata.size, digest: `sha256:${metadata.sha256}`, ...overrides,
    }],
  };
}
const list = releases => async () => Response.json(releases);
const update = () => checkForUpdate('0.2.3', 'win-x64.exe', list([release('0.2.10', 'win-x64.exe')]));

test('check finds the highest newer version for the exact platform and architecture', async () => {
  const request = list([
    release('0.2.10', 'win-x64.exe'), release('0.5.0', 'mac-arm64.dmg'),
    {...release('1.0.0', 'win-x64.exe'), draft: true}, release('0.2.9', 'win-x64.exe'),
  ]);
  const next = await checkForUpdate('0.2.9', 'win-x64.exe', request);
  assert.equal(next.version, '0.2.10');
  assert.equal(next.notes, '更新内容');
  assert.equal(await checkForUpdate('0.2.10', 'win-x64.exe', request), null);
  assert.equal(await checkForUpdate('0.2.0', 'mac-x64.dmg', request), null);
  assert.equal((await checkForUpdate('0.2.0', 'mac-arm64.dmg', request)).version, '0.5.0');
  await assert.rejects(checkForUpdate('0.2.9', 'win-x64.exe', list([
    release('0.2.10', 'win-x64.exe', { digest: 'invalid' }),
  ])), /校验/);
});

test('only verified downloads become launchable and cached bytes are checked again', async t => {
  const directory = await mkdtemp(path.join(tmpdir(), 'buaa-update-test-'));
  t.after(() => rm(directory, { recursive: true, force: true }));
  const next = await update();
  const progress = [];
  await downloadUpdate(next, directory, {
    signal: AbortSignal.timeout(1000), progress: value => progress.push(value),
    request: async url => url === next.url
      ? Response.redirect('https://release-assets.githubusercontent.com/synthetic')
      : new Response(content),
  });
  const installer = path.join(directory, next.name);
  assert.deepEqual(await readdir(directory), [next.name]);
  assert.equal(progress.at(-1), 1);
  assert.equal(await verifiedInstaller(next, directory), installer);
  await writeFile(installer, Buffer.alloc(content.length));
  assert.equal(await verifiedInstaller(next, directory), null);
});

test('bad checksum and unrelated redirect leave no partial installer', async t => {
  const directory = await mkdtemp(path.join(tmpdir(), 'buaa-update-test-'));
  t.after(() => rm(directory, { recursive: true, force: true }));
  const next = await update();
  const options = { signal: AbortSignal.timeout(1000), progress: () => {} };
  await assert.rejects(downloadUpdate(next, directory, {
    ...options, request: async () => new Response(Buffer.alloc(content.length)),
  }), /校验失败/);
  let requests = 0;
  await assert.rejects(downloadUpdate(next, directory, {
    ...options, request: async () => {
      requests++;
      return Response.redirect('https://github.com.example.test/synthetic');
    },
  }), /地址异常/);
  assert.equal(requests, 1);
  assert.deepEqual(await readdir(directory), []);
});


test('settings reports download progress and hands off only a reverified installer', async t => {
  const directory = await mkdtemp(path.join(tmpdir(), 'buaa-update-settings-test-'));
  t.after(() => rm(directory, { recursive: true, force: true }));
  const events = [];
  let launched;
  const settings = createUpdateSettings({
    version: '0.2.4', target: 'mac-arm64.dmg', directory,
    publish: state => events.push(state),
    install: async filename => { launched = filename; },
    request: async url => url.startsWith('https://api.github.com/')
      ? Response.json([release('0.2.10', 'mac-arm64.dmg')])
      : new Response(content),
  });
  assert.equal(settings.state().manualInstall, true);
  assert.equal((await settings.check()).status, 'available');
  assert.equal((await settings.download()).status, 'ready');
  assert.ok(events.some(state => state.status === 'downloading' && state.percent === 100));
  const filename = path.join(directory, 'BUAA-Learn-0.2.10-mac-arm64.dmg');
  await writeFile(filename, Buffer.alloc(content.length));
  assert.equal((await settings.install()).status, 'available');
  assert.equal(launched, undefined);
  await settings.download();
  await settings.install();
  assert.equal(launched, filename);
});

test('a rate-limited API falls back to the public feed and official checksums', async () => {
  const name = 'BUAA-Learn-0.2.10-win-x64.exe';
  const request = async (url, options) => {
    if (url.startsWith('https://api.github.com/')) return new Response(null, {status: 403});
    if (url.endsWith('/releases.atom')) return new Response(
      '<feed><entry><id>tag:github.com,2008:Repository/1/desktop-v0.2.10</id>' +
      '<content type="html">&lt;p&gt;同步修复&lt;/p&gt;</content></entry></feed>');
    if (url.endsWith('/SHA256SUMS.txt')) return new Response(`${metadata.sha256}  ${name}\n`);
    assert.equal(options.method, 'HEAD');
    assert.ok(url.endsWith(name));
    return new Response(null, {headers: {'Content-Length': String(content.length)}});
  };
  const result = await checkForUpdate('0.2.4', 'win-x64.exe', request);
  assert.equal(result.sha256, metadata.sha256);
  assert.equal(result.notes, '同步修复');
  assert.equal(result.size, content.length);
  assert.equal(await checkForUpdate('0.2.4', 'mac-x64.dmg', request), null);
});
