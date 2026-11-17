import {useEffect,useState} from 'react'
import {Checkbox} from '@/shared/ui/checkbox'
import type {NativeBridge} from '@/shared/native/bridge'
import {clock,type Segment} from './types'

interface WorkItem {
 id:string;kind:'task'|'notice';course_name:string;lesson_id:string;lesson_title:string
 text:string;deadline:string;audience:string;submission:string;done:boolean;superseded:boolean
}
interface WorkList {assignments:WorkItem[];notices:WorkItem[]}
interface WorkSource {lesson_title:string;superseded:boolean;rows:Segment[]}

function WorkRow({item,onToggle,pending}:{item:WorkItem;onToggle?:(done:boolean)=>void;pending?:boolean}):React.JSX.Element {
 const fields=[['截止',item.deadline],['对象',item.audience],['提交',item.submission]].filter(([,value])=>value&&value!=='未说明')
 const source=`#/lesson/${encodeURIComponent(item.lesson_id)}/source/${item.kind}/${encodeURIComponent(item.id)}`
 return <article className="work-row">
  {onToggle&&<Checkbox className="work-check" aria-label={`${item.done?'取消完成':'完成'}：${item.text}`} checked={item.done} disabled={pending} onCheckedChange={onToggle}/>}
  <div>
   <h3>{item.text}</h3>
   <p className="work-meta">{[item.course_name,item.lesson_title,item.kind==='task'?'待核验':'',item.superseded&&item.done?'曾完成':''].filter(Boolean).join(' · ')}</p>
   {fields.length>0&&<dl className="work-fields">{fields.map(([label,value])=><div key={label}><dt>{label}</dt><dd>{value}</dd></div>)}</dl>}
   {item.lesson_id&&<a href={source}>查看课堂来源</a>}
  </div>
 </article>
}

export function Work({bridge,revision}:{bridge:NativeBridge;revision:number}):React.JSX.Element {
 const [data,setData]=useState<WorkList>()
 const [error,setError]=useState('')
 const [pending,setPending]=useState<string>()
 const [reload,setReload]=useState(0)
 useEffect(()=>{
  let active=true
  void bridge.call<WorkList>('work_items').then(value=>{if(active)setData(value)}).catch((e:Error)=>{if(active)setError(e.message)})
  return()=>{active=false}
 },[bridge,revision,reload])
 async function toggle(id:string,done:boolean):Promise<void>{
  setPending(id);setError('')
  try {await bridge.call('set_task_done',{task:id,done})}
  catch(e){setError(e instanceof Error?e.message:'更新作业失败。')}
  finally {setReload(n=>n+1);setPending(undefined)}
 }
 const active=data?.assignments.filter(row=>!row.superseded).sort((a,b)=>Number(a.done)-Number(b.done))??[]
 const history=data?.assignments.filter(row=>row.superseded)??[]
 return <>
  <h1>作业与事项</h1>
  {error&&<p role="alert" className="error">{error}</p>}
  {!data&&!error&&<p role="status">正在读取…</p>}
  {data&&<>
   <section className="work-group"><h2>作业</h2>
    {active.length===0&&<p>暂无当前作业。</p>}
    {active.map(item=><WorkRow key={item.id} item={item} pending={pending!==undefined} onToggle={done=>void toggle(item.id,done)}/>)}
    {history.length>0&&<details className="work-history"><summary>已被替代的作业（{history.length}）</summary>{history.map(item=><WorkRow key={item.id} item={item}/>)}</details>}
   </section>
   <section className="work-group"><h2>重要事项</h2>
    {data.notices.length===0&&<p>暂无重要事项。</p>}
    {data.notices.map(item=><WorkRow key={item.id} item={item}/>)}
   </section>
  </>}
 </>
}

export function WorkSourceView({bridge,lesson,kind,id}:{bridge:NativeBridge;lesson:string;kind:string;id:string}):React.JSX.Element {
 const [source,setSource]=useState<WorkSource>()
 const [error,setError]=useState('')
 const [page,setPage]=useState(0)
 useEffect(()=>{
  let active=true
  void bridge.call<WorkSource>('work_source',{kind,id,lesson}).then(value=>{if(active)setSource(value)}).catch((e:Error)=>{if(active)setError(e.message)})
  return()=>{active=false}
 },[bridge,id,kind,lesson])
 const count=Math.ceil((source?.rows.length??0)/100)
 return <>
  <a className="back" href="#/work">作业与事项</a>
  <div className="page-heading"><h1>{source?.lesson_title||'课堂来源'}</h1><a href={`#/lesson/${encodeURIComponent(lesson)}`}>打开课堂</a></div>
  {error&&<p role="alert" className="error">{error}</p>}
  {!source&&!error&&<p role="status">正在读取…</p>}
  {source?.superseded&&<p>{source.rows.length>0?'已被替代 · 原版课堂来源':'已被替代'}</p>}
  {source?.rows.length===0&&<p>暂无可核验的来源。</p>}
  {source?.rows.slice(page*100,(page+1)*100).map(row=><article className="transcript-row" key={row.id}><time>{clock(row.start)}</time><p>{row.text}</p></article>)}
  {count>1&&<div className="pagination"><button disabled={page===0} onClick={()=>setPage(n=>n-1)}>上一页</button><span>{page+1} / {count}</span><button disabled={page+1===count} onClick={()=>setPage(n=>n+1)}>下一页</button></div>}
 </>
}
