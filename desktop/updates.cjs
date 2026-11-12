const { createHash } = require('node:crypto');
const { createReadStream, createWriteStream } = require('node:fs');
const { lstat, mkdir, rename, rm } = require('node:fs/promises');
const path = require('node:path');
const { Readable, Transform } = require('node:stream');
const { pipeline } = require('node:stream/promises');

const repository = 'fyzzzz05/BUAA-Learn';
const atomUrl = `https://github.com/${repository}/releases.atom`;
const releasesUrl = `https://api.github.com/repos/${repository}/releases?per_page=100`;
const downloadRoot = `https://github.com/${repository}/releases/download/`;
const downloadHosts = new Set(['github.com', 'release-assets.githubusercontent.com', 'objects.githubusercontent.com']);
const targets = new Set(['win-x64.exe', 'mac-arm64.dmg', 'mac-x64.dmg']);
const versionPattern = /^(0|[1-9]\d{0,8})\.(0|[1-9]\d{0,8})\.(0|[1-9]\d{0,8})$/;

function compareVersions(left, right) {
  const a = left.split('.').map(Number);
  const b = right.split('.').map(Number);
  return a[0] - b[0] || a[1] - b[1] || a[2] - b[2];
}

async function checkForUpdate(currentVersion, target, request = fetch) {
  if (!targets.has(target) || !versionPattern.test(currentVersion)) {
    throw new Error('更新信息格式异常。');
  }
  const response = await request(releasesUrl, {
    headers: { Accept: 'application/vnd.github+json' },
    cache: 'no-store', credentials: 'omit', redirect: 'error', signal: AbortSignal.timeout(15000),
  });
  if (response.status === 403 || response.status === 429) return checkPublicRelease(currentVersion, target, request);
  if (!response.ok) throw new Error('暂时无法获取更新，请稍后重试。');
  const releases = await response.json();
  if (!Array.isArray(releases)) throw new Error('更新信息格式异常。');

  const candidates = [];
  for (const release of releases) {
    const version = /^desktop-v(.+)$/.exec(release?.tag_name)?.[1];
    if (!version || !versionPattern.test(version) || release.draft || !release.published_at
        || compareVersions(version, currentVersion) <= 0) continue;
    const name = `BUAA-Learn-${version}-${target}`;
    const asset = Array.isArray(release.assets) && release.assets.find(item =>
      item?.name === name && item.state === 'uploaded');
    if (asset) candidates.push({ version, name, asset, body: release.body });
  }
  const next = candidates.sort((a, b) => compareVersions(b.version, a.version))[0];
  if (!next) return null;
  const { version, name, asset, body } = next;
  const sha256 = /^sha256:([a-f0-9]{64})$/.exec(asset.digest)?.[1];
  if (!sha256 || !Number.isSafeInteger(asset.size) || asset.size <= 0 || asset.size > 1024 ** 3) {
    throw new Error('安装包的校验信息不完整。');
  }
  return {
    version, name, size: asset.size, sha256,
    notes: typeof body === 'string' ? body.trim().slice(0, 8000) : '',
    url: `${downloadRoot}desktop-v${version}/${name}`,
  };
}

function unescapeXml(text) {
  const entities = { amp: '&', lt: '<', gt: '>', quot: '"', apos: "'" };
  return text.replace(/&(amp|lt|gt|quot|apos|#\d+|#x[\da-f]+);/gi, (raw, entity) => {
    if (entity[0] !== '#') return entities[entity] ?? raw;
    const point = entity[1] === 'x' ? parseInt(entity.slice(2), 16) : Number(entity.slice(1));
    return point >= 0 && point <= 0x10ffff ? String.fromCodePoint(point) : raw;
  });
}

async function checkPublicRelease(currentVersion, target, request) {
  const signal = AbortSignal.timeout(15000);
  const response = await request(atomUrl, { cache: 'no-store', credentials: 'omit', redirect: 'error', signal });
  if (!response.ok) throw new Error('暂时无法获取更新，请稍后重试。');
  const xml = await response.text();
  if (!/<feed\b/.test(xml)) throw new Error('更新信息格式异常。');
  const releases = [];
  for (const [, entry] of xml.matchAll(/<entry>([\s\S]*?)<\/entry>/g)) {
    const tag = /<id>[^<]*\/desktop-v([^<]+)<\/id>/.exec(entry)?.[1];
    if (tag && versionPattern.test(tag) && compareVersions(tag, currentVersion) > 0) {
      releases.push({ version: tag, entry });
    }
  }
  releases.sort((a, b) => compareVersions(b.version, a.version));
  for (const { version, entry } of releases) {
    const root = `${downloadRoot}desktop-v${version}/`;
    const name = `BUAA-Learn-${version}-${target}`;
    const url = root + name;
    const checksums = await requestReleaseAsset(root + 'SHA256SUMS.txt', request, signal);
    if (checksums.status === 404) { await checksums.body?.cancel(); continue; }
    if (!checksums.ok) throw new Error('安装包的校验信息暂时无法获取。');
    const sha256 = (await checksums.text()).split(/\r?\n/).map(line =>
      /^([a-f0-9]{64})\s+\*?(.+)$/.exec(line.trim())).find(match => match?.[2] === name)?.[1];
    if (!sha256) continue;
    const head = await requestReleaseAsset(url, request, signal, 'HEAD');
    const size = Number(head.headers.get('content-length'));
    if (!head.ok || !Number.isSafeInteger(size) || size <= 0 || size > 1024 ** 3) {
      throw new Error('安装包的校验信息不完整。');
    }
    const encodedNotes = /<content\b[^>]*>([\s\S]*?)<\/content>/.exec(entry)?.[1] ?? '';
    const notes = unescapeXml(unescapeXml(encodedNotes).replace(/<li>/gi, '• ').replace(/<[^>]*>/g, ''))
      .trim().slice(0, 8000);
    return { version, name, size, sha256, notes, url };
  }
  return null;
}

async function requestReleaseAsset(url, request, signal, method = 'GET') {
  let target = new URL(url);
  for (let redirects = 0; redirects <= 4; redirects++) {
    if (target.protocol !== 'https:' || !downloadHosts.has(target.hostname) || target.port
        || target.username || target.password) {
      throw new Error('安装包下载地址异常。');
    }
    const response = await request(target.href, { method, redirect: 'manual', credentials: 'omit', signal });
    if (![301, 302, 303, 307, 308].includes(response.status)) return response;
    const location = response.headers.get('location');
    await response.body?.cancel();
    if (!location) break;
    target = new URL(location, target);
  }
  throw new Error('安装包下载失败，请稍后重试。');
}

async function verifiedInstaller(update, directory) {
  const installer = path.join(directory, update.name);
  try {
    const info = await lstat(installer);
    if (!info.isFile() || info.size !== update.size) return null;
    const hash = createHash('sha256');
    for await (const chunk of createReadStream(installer)) hash.update(chunk);
    return hash.digest('hex') === update.sha256 ? installer : null;
  } catch (error) {
    if (error.code === 'ENOENT') return null;
    throw error;
  }
}

async function downloadUpdate(update, directory, { signal, progress, request = fetch }) {
  await mkdir(directory, { recursive: true, mode: 0o700 });
  const installer = path.join(directory, update.name);
  const partial = installer + '.part';
  try {
    // A previous process may have stopped while writing the partial download.
    await rm(partial, { force: true });
    const response = await requestReleaseAsset(update.url, request, signal);
    if (response.status !== 200 || !response.body) {
      await response.body?.cancel();
      throw new Error('安装包下载失败，请稍后重试。');
    }
    const body = response.body;
    let received = 0;
    const hash = createHash('sha256');
    const meter = new Transform({
      transform(chunk, _encoding, callback) {
        received += chunk.length;
        if (received > update.size) return callback(new Error('安装包大小与发布信息不符。'));
        hash.update(chunk);
        progress(received / update.size);
        callback(null, chunk);
      },
    });
    await pipeline(Readable.fromWeb(body), meter, createWriteStream(partial, { flags: 'wx', mode: 0o600 }), { signal });
    if (received !== update.size || hash.digest('hex') !== update.sha256) {
      throw new Error('安装包校验失败，请重新下载。');
    }
    // Windows cannot replace a stale cached installer with rename in every environment.
    await rm(installer, { force: true });
    await rename(partial, installer);
  } finally {
    await rm(partial, { force: true }).catch(() => {});
  }
}

module.exports = { checkForUpdate, downloadUpdate, verifiedInstaller };
