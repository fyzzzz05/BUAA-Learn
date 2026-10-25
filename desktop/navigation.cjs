const externalHosts = new Set([
  'classroom.msa.buaa.edu.cn', 'resource.msa.buaa.edu.cn', 'github.com',
]);

function isInternal(raw, origin) {
  try { return new URL(raw).origin === origin; } catch { return false; }
}

function isExternalAllowed(raw) {
  try {
    const url = new URL(raw);
    return url.protocol === 'https:' && !url.username && !url.password &&
      !url.port && externalHosts.has(url.hostname);
  } catch { return false; }
}

module.exports = { isInternal, isExternalAllowed };
