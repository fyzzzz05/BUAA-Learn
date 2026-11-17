import {useEffect, useRef, useState, type RefObject} from 'react'
import type {NativeBridge} from '@/shared/native/bridge'
import {Checkbox} from '@/shared/ui/checkbox'
import {Switch} from '@/shared/ui/switch'

interface Rule {
 enabled: boolean
 auto_analyze: boolean
}
interface Course extends Rule {
 id: string
 name: string
 code?: string
 next_run?: string
 pending_count: number
}
interface Settings {
 model_ready: boolean
 courses: Course[]
}
export type LeaveAutomation = (leave: () => void) => void

export function Automation({bridge, revision, leaveGuard}: {
 bridge: NativeBridge
 revision: number
 leaveGuard: RefObject<LeaveAutomation|undefined>
}): React.JSX.Element {
 const [settings, setSettings] = useState<Settings>()
 const [drafts, setDrafts] = useState<Record<string, Rule>>({})
 const [query, setQuery] = useState('')
 const [onlyEnabled, setOnlyEnabled] = useState(false)
 const [saving, setSaving] = useState(false)
 const [error, setError] = useState('')
 const dialog = useRef<HTMLDialogElement>(null)
 const next = useRef<(() => void)|undefined>(undefined)
 const savingRef = useRef(false)
 const dirty = Object.keys(drafts).length > 0
 useEffect(() => {
  let active = true
  void bridge.call<Settings>('automation_settings')
   .then(value => { if (active && !savingRef.current) setSettings(value) })
   .catch((e: Error) => { if (active) setError(e.message) })
  return () => { active = false }
 }, [bridge, revision])
 useEffect(() => {
  if (!dirty) return
  leaveGuard.current = leave => {
   next.current = leave
   if (!dialog.current?.open) dialog.current?.showModal()
  }
  const confirmClose = (event: BeforeUnloadEvent) => { event.preventDefault(); event.returnValue = true }
  window.addEventListener('beforeunload', confirmClose)
  return () => {
   leaveGuard.current = undefined
   window.removeEventListener('beforeunload', confirmClose)
  }
 }, [dirty, leaveGuard])

 const filtered = (settings?.courses ?? []).filter(course => {
  const rule = drafts[course.id] ?? course
  return (!onlyEnabled || rule.enabled) && `${course.name} ${course.code ?? ''}`.toLowerCase().includes(query.trim().toLowerCase())
 })
 function setRules(changes: [Course, Rule][]): void {
  setDrafts(previous => {
   const updated = {...previous}
   for (const [course, rule] of changes) {
    if (course.enabled === rule.enabled && course.auto_analyze === rule.auto_analyze) delete updated[course.id]
    else updated[course.id] = rule
   }
   return updated
  })
 }
 function setEnabled(courses: Course[], enabled: boolean): void {
  setRules(courses.map(course => [course, {enabled, auto_analyze: enabled && (drafts[course.id] ?? course).auto_analyze}]))
 }
 function leave(): void {
  const go = next.current
  next.current = undefined
  leaveGuard.current = undefined
  dialog.current?.close()
  go?.()
 }
 async function save(thenLeave = false): Promise<void> {
  if (savingRef.current || !dirty) return
  savingRef.current = true
  setSaving(true)
  setError('')
  try {
   const result = await bridge.call<Settings>('save_automation', {
    changes: Object.entries(drafts).map(([course, rule]) => ({course, ...rule}))
   })
   setSettings(result)
   setDrafts({})
   if (thenLeave) leave()
  } catch (e) {
   setError(e instanceof Error ? e.message : '保存失败，草稿已保留。')
  } finally {
   savingRef.current = false
   setSaving(false)
  }
 }
 return <>
  <div className="page-heading"><h1>课程自动化</h1>
   <button disabled={!dirty || saving} onClick={() => void save()}>保存更改</button>
  </div>
  <div className="toolbar automation-toolbar">
   <input aria-label="搜索课程" placeholder="搜索课程" value={query} onChange={e => setQuery(e.target.value)}/>
   <label><Checkbox checked={onlyEnabled} onCheckedChange={value => setOnlyEnabled(value)}/>仅显示已开启</label>
   <button disabled={saving || filtered.length === 0} onClick={() => setEnabled(filtered, true)}>全选</button>
   <button disabled={saving || filtered.length === 0} onClick={() => setEnabled(filtered, false)}>全不选</button>
  </div>
  <p>自动笔记会向模型服务发送课堂文本，可能产生费用。</p>
  {settings && !settings.model_ready && <a href="#/settings">配置模型服务</a>}
  {error && <p role="alert" className="error">{error}</p>}
  {!settings && !error && <p role="status">正在读取…</p>}
  {settings && filtered.length === 0 && <p>暂无匹配课程。</p>}
  <div className="automation-list">
   {filtered.map(course => {
    const rule = drafts[course.id] ?? course
    return <article className="lesson-row automation-row" key={course.id}>
     <div><h2>{course.name}</h2>
      {course.enabled && course.next_run && <time>下次检查 {new Date(course.next_run).toLocaleString('zh-CN', {timeZone: 'Asia/Shanghai'})}</time>}
     </div>
     <div className="automation-controls">
      <label><Checkbox aria-label={`${course.name} 自动获取`} checked={rule.enabled} disabled={saving}
       onCheckedChange={value => setEnabled([course], value)}/>自动获取</label>
      <label><Switch aria-label={`${course.name} 自动笔记`} checked={rule.auto_analyze}
       disabled={saving || !rule.enabled || !settings?.model_ready && !rule.auto_analyze}
       onCheckedChange={value => setRules([[course, {...rule, auto_analyze: value}]])}/>自动笔记</label>
     </div>
    </article>
   })}
  </div>
  <dialog ref={dialog} aria-labelledby="automation-leave">
   <h2 id="automation-leave">有未保存的更改</h2>
   {error && <p role="alert" className="error">{error}</p>}
   <div className="toolbar">
    <button disabled={saving} onClick={() => {next.current = undefined; dialog.current?.close()}}>继续编辑</button>
    <button disabled={saving} onClick={() => {setDrafts({}); leave()}}>放弃更改</button>
    <button disabled={saving} onClick={() => void save(true)}>保存并离开</button>
   </div>
  </dialog>
 </>
}
