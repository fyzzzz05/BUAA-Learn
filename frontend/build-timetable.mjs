import {build} from 'esbuild'
import {execFileSync} from 'node:child_process'
await build({entryPoints:['src/features/timetable/mount.tsx'],outfile:'../native/macos-login/Resources/timetable-bundle.js',bundle:true,format:'iife',globalName:'BuaaTimetable',platform:'browser',target:'es2022',jsx:'automatic',minify:true,legalComments:'eof',define:{'process.env.NODE_ENV':'"production"'}})
execFileSync(process.execPath,['node_modules/@tailwindcss/cli/dist/index.mjs','-i','src/features/timetable/style.css','-o','../native/macos-login/Resources/timetable.css','--minify'],{stdio:'inherit'})
