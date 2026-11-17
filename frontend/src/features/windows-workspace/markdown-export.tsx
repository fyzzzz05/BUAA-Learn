import {useState} from 'react'
import type {NativeBridge} from '@/shared/native/bridge'

interface ExportFile {
 file_name: string
 path: string
}

export function MarkdownExport({bridge, lesson, enabled}: {
 bridge: NativeBridge
 lesson: string
 enabled: boolean
}): React.JSX.Element {
 const [pending, setPending] = useState(false)
 const [saved, setSaved] = useState<ExportFile>()
 const [error, setError] = useState('')

 async function save(): Promise<void> {
  if (pending) return
  setPending(true)
  setError('')
  try {
   setSaved(await bridge.call<ExportFile>('export_markdown', {lesson}))
  } catch (e) {
   setError(e instanceof Error ? e.message : '导出失败，请重试。')
  } finally {
   setPending(false)
  }
 }

 async function saveAs(): Promise<void> {
  setPending(true)
  setError('')
  try { await bridge.call('save_markdown_as', {lesson}) }
  catch (e) { setError(e instanceof Error ? e.message : '另存失败，请重试。') }
  finally { setPending(false) }
 }

 async function openFolder(): Promise<void> {
  setError('')
  try {
   await bridge.call('open_markdown_folder', {lesson})
  } catch {
   setError('无法打开文件夹。文件路径：')
  }
 }

 return <>
  <button disabled={!enabled || pending} onClick={() => void save()}>
   {pending ? '正在导出…' : '导出 Markdown'}
  </button>
  {saved && <div className="markdown-export-result">
   <span role="status" title={saved.path}>已导出 {saved.file_name}</span>
   <button disabled={pending} onClick={() => void saveAs()}>另存为</button>
   <button onClick={() => void openFolder()}>打开文件夹</button>
   {error && <p role="alert" className="error">{error} <span>{saved.path}</span></p>}
  </div>}
  {!saved && error && <p role="alert" className="error">{error}</p>}
 </>
}
