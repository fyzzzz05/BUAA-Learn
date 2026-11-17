import {build} from 'esbuild'
import {execFileSync} from 'node:child_process'
import {mkdir,copyFile} from 'node:fs/promises'
const preview=process.argv.includes('--preview')
const output=preview?'../dist/native/windows/workspace-preview':'../native/windows/BUAALearn.App/Workspace'
await mkdir(output,{recursive:true})
await build({entryPoints:[preview?'src/apps/windows/preview.ts':'src/apps/windows/main.tsx'],outfile:`${output}/app.js`,bundle:true,format:'esm',platform:'browser',target:'es2022',jsx:'automatic',minify:true,legalComments:'eof',define:{'process.env.NODE_ENV':'"production"'}})
execFileSync(process.execPath,['node_modules/@tailwindcss/cli/dist/index.mjs','-i','src/apps/windows/style.css','-o',`${output}/app.css`,'--minify'],{stdio:'inherit'})
await copyFile('src/apps/windows/index.html',`${output}/index.html`)
