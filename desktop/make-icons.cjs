const fs = require('node:fs');
const path = require('node:path');
const { Resvg } = require('@resvg/resvg-js');
const svg = fs.readFileSync(path.join(__dirname, 'assets/icon.svg'));
fs.writeFileSync(path.join(__dirname, 'assets/icon.png'), new Resvg(svg, { fitTo: { mode: 'width', value: 1024 } }).render().asPng());
for (const name of ['LICENSE', 'THIRD_PARTY_NOTICES.md']) fs.copyFileSync(path.join(__dirname, '..', name), path.join(__dirname, name));
