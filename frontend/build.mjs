import { build } from 'esbuild'
import { execFileSync } from 'node:child_process'
await build({
  entryPoints: ['src/single-select.tsx'],
  outfile: '../buaa_agent/static/combobox.js',
  bundle: true,
  format: 'esm',
  platform: 'browser',
  target: 'es2022',
  jsx: 'automatic',
  minify: true,
  legalComments: 'eof',
  define: {'process.env.NODE_ENV': '"production"'},
})
const {copyFile} = await import('node:fs/promises')
await copyFile('src/single-select.css','../buaa_agent/static/combobox.css')
await build({
  entryPoints: ['src/features/lesson-timeline/mount.tsx'],
  outfile: '../buaa_agent/static/lesson-timeline.js',
  bundle: true,
  format: 'esm',
  platform: 'browser',
  target: 'es2022',
  jsx: 'automatic',
  minify: true,
  legalComments: 'eof',
  define: {'process.env.NODE_ENV': '"production"'},
})
execFileSync(process.execPath, ['node_modules/@tailwindcss/cli/dist/index.mjs',
  '-i', 'src/lesson-timeline.css', '-o', '../buaa_agent/static/lesson-timeline.css', '--minify'],
  {stdio: 'inherit'})
await build({entryPoints:['src/features/navigation/mount.tsx'],outfile:'../buaa_agent/static/navigation.js',bundle:true,format:'esm',platform:'browser',target:'es2022',jsx:'automatic',minify:true,legalComments:'eof',define:{'process.env.NODE_ENV':'"production"'}})
execFileSync(process.execPath,['node_modules/@tailwindcss/cli/dist/index.mjs','-i','src/navigation.css','-o','../buaa_agent/static/navigation.css','--minify'],{stdio:'inherit'})
await build({entryPoints:['src/features/automations/mount.tsx'],outfile:'../buaa_agent/static/automations.js',bundle:true,format:'esm',platform:'browser',target:'es2022',jsx:'automatic',minify:true,legalComments:'eof',define:{'process.env.NODE_ENV':'"production"'}})
await copyFile('src/features/automations/style.css','../buaa_agent/static/automations.css')
