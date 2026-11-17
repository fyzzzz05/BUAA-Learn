import {useEffect,useState} from 'react'
import type {NativeBridge} from '@/shared/native/bridge'
import {clock,type Job} from './types'
interface CachedImage {status:'cached'|'missing'|'corrupt';mime:string|null;bytes:string|null;width:number;height:number}
export function Slides({bridge,lesson,slides,anchor,onSelect,revision,job}:{bridge:NativeBridge;lesson:string;revision:number;job?:Job|null;anchor:string|null;onSelect:(id:string)=>void;slides:{id:string;start:number;url:string}[]}):React.JSX.Element {
 const [index,setIndex]=useState(()=>Math.max(0,slides.findIndex(slide=>slide.id===anchor)))
 if(slides.length===0)return <><PdfControls bridge={bridge} lesson={lesson} revision={revision} job={job} count={0}/><p>暂无截图索引。</p></>
 const selected=Math.min(index,slides.length-1);const slide=slides[selected]
 return <><PdfControls bridge={bridge} lesson={lesson} revision={revision} job={job} count={slides.length}/><div className="slide-toolbar"><button disabled={selected===0} onClick={()=>{setIndex(selected-1);onSelect(slides[selected-1].id)}}>上一张</button><span>第 {selected+1} / {slides.length} 张 · {clock(slide.start)}</span><button disabled={selected===slides.length-1} onClick={()=>{setIndex(selected+1);onSelect(slides[selected+1].id)}}>下一张</button></div><Slide key={`${bridge.owner}:${lesson}:${slide.id}:${slide.url}`} bridge={bridge} lesson={lesson} id={slide.id} number={selected+1}/></>
}
function Slide({bridge,lesson,id,number}:{bridge:NativeBridge;lesson:string;id:string;number:number}):React.JSX.Element {
 const [image,setImage]=useState<CachedImage>();const [error,setError]=useState('');const [loading,setLoading]=useState(true)
 useEffect(()=>{let active=true;void bridge.call<CachedImage>('slide',{lesson,slide:id,download:false}).then(value=>{if(active)setImage(value)}).catch((e:Error)=>{if(active)setError(e.message)}).finally(()=>{if(active)setLoading(false)});return()=>{active=false}},[bridge,lesson,id])
 const download=async():Promise<void>=>{setLoading(true);setError('');try{setImage(await bridge.call<CachedImage>('slide',{lesson,slide:id,download:true}))}catch(e){setError(e instanceof Error?e.message:'图片获取失败，请重试。')}finally{setLoading(false)}}
 return <div className="slide-view">{error&&<p role="alert" className="error">{error}</p>}{loading&&<p role="status">正在读取图片…</p>}{image?.status==='cached'&&image.bytes&&image.mime?<img src={`data:${image.mime};base64,${image.bytes}`} width={image.width} height={image.height} alt={`第 ${number} 张课堂截图`} decoding="async"/>:!loading&&<><p>{image?.status==='corrupt'?'缓存图片已损坏，请重新获取。':'图片尚未缓存。'}</p><button onClick={()=>void download()}>获取图片</button></>}</div>
}

type PdfStatus = 'missing' | 'stale' | 'ready'

function PdfControls({bridge, lesson, revision, job, count}: {
 bridge: NativeBridge
 lesson: string
 revision: number
 job?: Job|null
 count: number
}): React.JSX.Element {
 const [status, setStatus] = useState<PdfStatus>('missing')
 const [pending, setPending] = useState(false)
 const [error, setError] = useState('')
 const exporting = job?.kind === 'slides-pdf' && job.target === lesson
 useEffect(() => {
  let active = true
  void bridge.call<PdfStatus>('pdf_status', {lesson})
   .then(value => { if (active) setStatus(value) })
   .catch((e: Error) => { if (active) setError(e.message) })
  return () => { active = false }
 }, [bridge, lesson, revision])

 async function run(method: 'export_pdf'|'open_pdf'|'save_pdf_as'): Promise<void> {
  setPending(true)
  setError('')
  try { await bridge.call(method, {lesson}) }
  catch (e) { setError(e instanceof Error ? e.message : 'PDF 操作失败。') }
  finally { setPending(false) }
 }

 return <>
  <div className="toolbar">
   <button disabled={pending || exporting || count === 0 || count > 1000} onClick={() => void run('export_pdf')}>
    {exporting ? '正在导出…' : status === 'missing' ? '导出去重 PDF' : '重新导出 PDF'}
   </button>
   {status !== 'missing' && <button disabled={pending} onClick={() => void run('open_pdf')}>
    {status === 'stale' ? '打开旧 PDF' : '打开 PDF'}
   </button>}
   {status !== 'missing' && <button disabled={pending} onClick={() => void run('save_pdf_as')}>另存为</button>}
   {status === 'stale' && <span role="status">课堂截图已更新。</span>}
   {count > 1000 && <span>单次最多导出 1000 张截图。</span>}
  </div>
  {error && <p role="alert" className="error">{error}</p>}
 </>
}
