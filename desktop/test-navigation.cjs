const { test } = require('node:test');
const assert = require('node:assert/strict');
const { isInternal, isExternalAllowed } = require('./navigation.cjs');
test('only the current service origin stays in the app', () => {
  const origin = 'http://127.0.0.1:43210';
  assert.equal(isInternal(`${origin}/#/courses`, origin), true);
  for (const url of ['http://127.0.0.1:8765', 'https://127.0.0.1:43210', 'file:///etc/passwd', 'javascript:alert(1)']) assert.equal(isInternal(url, origin), false);
});
test('external links require exact school or project HTTPS hosts', () => {
  assert.equal(isExternalAllowed('https://classroom.msa.buaa.edu.cn/livingroom?sub_id=1'), true);
  for (const url of ['http://github.com', 'https://github.com.evil.test', 'https://user:password@github.com', 'https://github.com:1234', 'file:///tmp/a', 'mailto:a@b.cn']) assert.equal(isExternalAllowed(url), false);
});
