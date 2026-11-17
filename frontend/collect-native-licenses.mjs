// Collect the resolved Rust crates' own notices; never ship local registry paths.
import {execFileSync} from 'node:child_process'
import {mkdir,readdir,cp,readFile,writeFile,rm,rename} from 'node:fs/promises'
import {dirname,join,resolve,basename} from 'node:path'
import {fileURLToPath} from 'node:url'
import {randomUUID} from 'node:crypto'
const root=fileURLToPath(new URL('../',import.meta.url))
const metadata=JSON.parse(execFileSync('cargo',['metadata','--locked','--offline','--format-version','1','--filter-platform','x86_64-pc-windows-msvc','--manifest-path',join(root,'native/core/Cargo.toml')],{encoding:'utf8',maxBuffer:16*1024*1024}))
const parent=join(root,'dist/native/windows');await mkdir(parent,{recursive:true})
const draft=join(parent,`.licenses-${randomUUID()}`);await mkdir(draft)
const manifest=[]
try{
 for(const pkg of metadata.packages.filter(pkg=>pkg.source).sort((a,b)=>a.name.localeCompare(b.name)||a.version.localeCompare(b.version))){
  const source=dirname(pkg.manifest_path);const destination=join(draft,`${pkg.name}-${pkg.version}`);await mkdir(destination)
  const names=(await readdir(source,{withFileTypes:true})).filter(entry=>/^(licen[cs]e|copying|notice)/i.test(entry.name)).map(entry=>entry.name)
  if(pkg.license_file){const path=resolve(source,pkg.license_file);if(dirname(path)!==source)throw new Error(`Review nested license file for ${pkg.name}`);if(!names.includes(basename(path)))names.push(basename(path))}
  if(!pkg.license||names.length===0)throw new Error(`Missing declared license/text: ${pkg.name} ${pkg.version}`)
  for(const name of names)await cp(join(source,name),join(destination,name),{recursive:true,dereference:false})
  manifest.push({name:pkg.name,version:pkg.version,license:pkg.license,files:names.sort()})
 }
 await cp(join(root,'LICENSE'),join(draft,'BUAA-Learn-LICENSE'))
 await writeFile(join(draft,'manifest.json'),JSON.stringify({target:'x86_64-pc-windows-msvc',packages:manifest},null,2)+'\n')
 const lock=await readFile(join(root,'native/core/Cargo.lock'));await writeFile(join(draft,'Cargo.lock'),lock)
 await rm(join(parent,'licenses'),{recursive:true,force:true});await rename(draft,join(parent,'licenses'))
 console.log(`Collected original notices for ${manifest.length} resolved Rust crates.`)
}catch(error){await rm(draft,{recursive:true,force:true});throw error}
