import {useState} from 'react'
import {createRoot} from 'react-dom/client'
import {Checkbox} from '@/shared/ui/checkbox'
import {Switch} from '@/shared/ui/switch'
import {Button} from '@/shared/ui/button'
import {Input} from '@/shared/ui/input'

type Choice={enabled:boolean;auto_analyze:boolean}
type Rule=Choice&{course_id:string;next_run?:string;pending_count?:number}
type Course={id:string;name:string;teachers:string;code:string;recording_count?:number}
type Draft={owner:string;changes:Record<string,Choice>;order:string[];query?:string;enabledOnly?:boolean}
type SaveResult={saved:Rule[];errors:{id:string;message:string}[]}
type Props={owner:string;courses:Course[];rules:Rule[];draft:Draft|null;
 onDraft:(draft:Draft)=>void;onSave:(changes:Record<string,Choice>)=>Promise<SaveResult>}
const off:Choice={enabled:false,auto_analyze:false}
// Old paused rules may retain auto_analyze; disabled acquisition always renders notes off.
const normalize=(choice:Choice):Choice=>({enabled:choice.enabled,auto_analyze:choice.enabled&&choice.auto_analyze})
const equal=(a:Choice,b:Choice)=>a.enabled===b.enabled&&a.auto_analyze===b.auto_analyze
const sorted=(courses:Course[],rules:Rule[])=>[...courses].sort((a,b)=>Number(rules.find(r=>r.course_id===b.id)?.enabled||false)-Number(rules.find(r=>r.course_id===a.id)?.enabled||false)||(b.recording_count||0)-(a.recording_count||0)||a.name.localeCompare(b.name)).map(c=>c.id)
function AutomationList(props:Props){
 const valid=props.draft?.owner===props.owner?props.draft:null
 const [rules,setRules]=useState(props.rules)
 const [changes,setChanges]=useState<Record<string,Choice>>(valid?.changes||{})
 const [order,setOrder]=useState(valid?.order||sorted(props.courses,props.rules))
 const [query,setQuery]=useState(valid?.query||'')
 const [enabledOnly,setEnabledOnly]=useState(valid?.enabledOnly||false)
 const [saving,setSaving]=useState(false)
 const [message,setMessage]=useState('')
 const [errors,setErrors]=useState<Record<string,string>>({})
 const [saveFailed,setSaveFailed]=useState(false)
 const baseline=(id:string)=>normalize(rules.find(r=>r.course_id===id)||off)
 const selected=(id:string)=>normalize(changes[id]||baseline(id))
 function publish(next:Record<string,Choice>,nextOrder=order,nextQuery=query,nextFilter=enabledOnly){
  setChanges(next);props.onDraft({owner:props.owner,changes:next,order:nextOrder,query:nextQuery,enabledOnly:nextFilter})
 }
 function updateMany(ids:string[],patch:Partial<Choice>){
  const next={...changes},nextErrors={...errors}
  for(const id of ids){
   const choice=normalize({...selected(id),...patch})
   if(equal(choice,baseline(id)))delete next[id];else next[id]=choice
   delete nextErrors[id]
  }
  publish(next);setMessage('');setErrors(nextErrors);setSaveFailed(Object.keys(nextErrors).length>0)
 }
 async function save(){
  setSaving(true);setMessage('');setErrors({});setSaveFailed(false)
  try{
   const result=await props.onSave(changes)
   const nextRules=[...rules.filter(r=>!result.saved.some(x=>x.course_id===r.course_id)),...result.saved]
   const next={...changes};result.saved.forEach(r=>delete next[r.course_id])
   setRules(nextRules)
   const nextOrder=sorted(props.courses,nextRules);setOrder(nextOrder);publish(next,nextOrder)
   setErrors(Object.fromEntries(result.errors.map(e=>[e.id,e.message])))
   setSaveFailed(result.errors.length>0)
   setMessage(result.errors.length?`已保存 ${result.saved.length} 门，${result.errors.length} 门未保存。`:`已保存 ${result.saved.length} 门课程的更改`)
  }catch{setSaveFailed(true);setMessage('保存失败，更改已保留，请重试。')}
  finally{setSaving(false)}
 }
 const ids=[...order.filter(id=>props.courses.some(c=>c.id===id)),...props.courses.filter(c=>!order.includes(c.id)).map(c=>c.id)]
 const search=query.trim().toLocaleLowerCase()
 const filtered=ids.filter(id=>{const c=props.courses.find(c=>c.id===id)!;return (!enabledOnly||selected(id).enabled)&&(!search||`${c.name} ${c.teachers} ${c.code}`.toLocaleLowerCase().includes(search))})
 const dirty=Object.keys(changes).length
 const enabled=props.courses.filter(c=>selected(c.id).enabled).length
 const savedEnabled=props.courses.filter(c=>baseline(c.id).enabled).length
 return <section className="automation-list" aria-label="课程自动化设置">
  <div className="automation-list-heading"><h2>我的课程</h2><span>{dirty?`已保存开启 ${savedEnabled} 门 · 当前选择 ${enabled} 门`:`已开启 ${enabled} 门课`}</span></div>
  <div className="automation-toolbar">
   <Input className="automation-search" value={query} onChange={e=>{setQuery(e.target.value);publish(changes,order,e.target.value)}} placeholder="搜索课程或教师" aria-label="搜索自动化课程" disabled={saving}/>
   <label className="automation-filter"><Checkbox checked={enabledOnly} onCheckedChange={value=>{setEnabledOnly(value);publish(changes,order,query,value)}} disabled={saving}/>只看已开启</label>
  </div>
  <div className="automation-bulk"><span>{filtered.length} 门{search||enabledOnly?'符合筛选':'课程'}</span><div><Button variant="ghost" disabled={saving||!filtered.some(id=>!selected(id).enabled)} onClick={()=>updateMany(filtered,{enabled:true})}>{search||enabledOnly?'全选当前列表':'全选'}</Button><Button variant="ghost" disabled={saving||!filtered.some(id=>selected(id).enabled)} onClick={()=>updateMany(filtered,{enabled:false})}>{search||enabledOnly?'取消当前列表选择':'全不选'}</Button></div></div>
  <div className="automation-column-labels"><span>自动获取</span><span>课程</span><span>自动生成笔记</span></div>
  <p className="automation-cost" id="automation-cost-note">开启笔记会将课堂原文发送至配置的模型服务，并产生调用费用；保存后生效。</p>
  <div className="automation-rows">{filtered.map(id=>{const c=props.courses.find(c=>c.id===id)!,choice=selected(id),rule=rules.find(r=>r.course_id===id);return <div key={id} className={`automation-course-row ${choice.enabled?'is-enabled':''}`} onClick={e=>{if(!saving&&!(e.target as Element).closest('button,input,label,[role="checkbox"],[role="switch"]'))updateMany([id],{enabled:!choice.enabled})}}>
   <div className="automation-enable"><Checkbox id={`auto-${id}`} checked={choice.enabled} disabled={saving} onCheckedChange={value=>updateMany([id],{enabled:value})} aria-label={`自动获取${c.name}`}/></div>
   <div className="automation-course-info"><div className="automation-course-title"><label htmlFor={`auto-${id}`}>{c.name}</label>{changes[id]&&<span className="automation-row-state">待保存</span>}</div><p>{c.teachers||c.code} · {c.recording_count||0} 节回放</p>
    {errors[id]?<p className="automation-row-error" role="alert">{errors[id]}</p>:!changes[id]&&rule?.enabled?<span className="automation-rule-state">{rule.pending_count?`${rule.pending_count} 个课次待检查`:rule.next_run?`下次检查 ${new Date(rule.next_run).toLocaleString('zh-CN',{timeZone:'Asia/Shanghai',month:'numeric',day:'numeric',hour:'2-digit',minute:'2-digit',hour12:false})}`:'等待后续课表'}</span>:null}
   </div>
   <div className="automation-notes" title={!choice.enabled?'请先开启自动获取':undefined} onClick={e=>e.stopPropagation()}><Switch checked={choice.auto_analyze} disabled={!choice.enabled||saving} onCheckedChange={value=>updateMany([id],{auto_analyze:value})} aria-label={`为${c.name}自动生成笔记`} aria-describedby="automation-cost-note"/></div>
  </div>})}
  {!filtered.length&&<div className="automation-no-courses">{ids.length?'没有符合筛选的课程。':'同步本学期课程后，在这里选择需要自动整理的课程。'}</div>}
  </div>
  <div className={`automation-savebar ${saveFailed?'has-error':''}`}><div><strong>{saving?'正在保存更改…':saveFailed?'部分更改未保存':dirty?`有未保存的更改 · ${dirty} 门`:'没有待保存的更改'}</strong><span role="status">{message||(!dirty?'勾选课程后，点击保存生效。':'')}</span></div><div className="automation-save-actions"><Button variant="outline" disabled={!dirty||saving} onClick={()=>{publish({});setErrors({});setSaveFailed(false);setMessage('已撤销未保存的更改')}}>撤销更改</Button><Button disabled={!dirty||saving} onClick={save}>{saving?'正在保存…':saveFailed?'重试未保存项':'保存更改'}</Button></div></div>
 </section>
}
export function mountAutomations(container:HTMLElement,props:Props){const root=createRoot(container,{identifierPrefix:'automation-'});root.render(<AutomationList {...props}/>);return {unmount:()=>root.unmount()}}
