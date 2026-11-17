import {useCallback,useEffect,useState,useRef,type CSSProperties} from 'react'
import {BookOpen,Library,Clock3,Settings2,ArrowLeft,RefreshCw,ListChecks,Repeat2} from 'lucide-react'
import {SidebarProvider,Sidebar,SidebarHeader,SidebarContent,SidebarFooter,SidebarMenu,SidebarMenuItem,SidebarMenuButton,SidebarTrigger,useSidebar} from '@/shared/ui/sidebar'
import {TooltipProvider} from '@/shared/ui/tooltip'
import {SearchSelect} from '@/shared/ui/search-select'
import type {NativeBridge} from '@/shared/native/bridge'
import type {Workspace,CourseData,Lesson,Job} from './types'
import {Reader} from './reader'
import {CourseQuestions} from './questions'
import {Automation,type LeaveAutomation} from './automation'
import {ModelSettings} from './settings'
import {Work,WorkSourceView} from './work'

function safeId(value:string):string {try{return decodeURIComponent(value)}catch{return ''}}
function useData<T>(load:()=>Promise<T>,deps:unknown[]):{data:T|undefined;error:string;loading:boolean}{
 const [state,setState]=useState<{data:T|undefined;error:string;loading:boolean}>({data:undefined,error:'',loading:true})
 useEffect(()=>{let active=true;setState({data:undefined,error:'',loading:true});void load().then(data=>{if(active)setState({data,error:'',loading:false})}).catch((error:unknown)=>{if(active)setState({data:undefined,error:error instanceof Error?error.message:'读取失败。',loading:false})});return()=>{active=false}},deps)
 return state
}
function State({loading,error}:{loading:boolean;error:string}):React.JSX.Element|null{return error?<p role="alert" className="error">{error}</p>:loading?<p role="status">正在读取…</p>:null}
export function WorkspaceApp({bridge}:{bridge:NativeBridge}):React.JSX.Element {
 const [workspace,setWorkspace]=useState<Workspace>();const [error,setError]=useState('');const [revision,setRevision]=useState(0)
 const [route,setRoute]=useState(()=>location.hash.slice(1)||'/courses');const [busy,setBusy]=useState(false)
 const refreshId=useRef(0)
 const leaveGuard=useRef<LeaveAutomation|undefined>(undefined)
 const refresh=useCallback(async()=>{const id=++refreshId.current;const state=await bridge.call<Workspace>('workspace');if(id!==refreshId.current)return;bridge.owner=state.owner;setWorkspace(state);setRevision(x=>x+1)},[bridge])
 useEffect(()=>{void refresh().catch((e:Error)=>setError(e.message));return bridge.subscribe(()=>{void refresh().catch((e:Error)=>setError(e.message))})},[bridge,refresh])
 useEffect(()=>{
  const change=()=>{
   const next=location.hash.slice(1)||'/courses'
   if(next===route)return
   if(!leaveGuard.current){setRoute(next);return}
   window.history.replaceState(null,'',`#${route}`)
   leaveGuard.current(()=>{location.hash=next;setRoute(next)})
  }
  window.addEventListener('hashchange',change)
  return()=>window.removeEventListener('hashchange',change)
 },[route])
 const action=async(method:string,args:Record<string,unknown>={}):Promise<void>=>{if(busy)return;setBusy(true);setError('');try{await bridge.call(method,args);if(method==='select_account')location.hash='/courses';if(method!=='open_replay')await refresh()}catch(e){setError(e instanceof Error?e.message:'操作失败。')}finally{setBusy(false)}}
 const parts=route.split('/').filter(Boolean);const section=parts[0];const id=safeId(parts[1]??'')
 return <TooltipProvider><SidebarProvider style={{'--sidebar-width':'232px','--sidebar-width-icon':'56px'} as CSSProperties}>
  <WorkspaceNavigation section={section} connected={workspace?.connected??false}/>
  <div className="workspace"><header><div className="header-title"><SidebarTrigger aria-label="切换侧栏"/><span>BUAA Learn</span></div>{workspace?.active_job&&<a href="#/jobs" className="active-job" role="status" title={workspace.active_job.message}>{workspace.active_job.message}</a>}{workspace&&(workspace.owner||workspace.login?.status==='running')&&<div className="connection-controls">{workspace.login?.status==='running'?<><span role="status">登录中</span><button onClick={()=>void action('cancel_login')}>取消登录</button></>:<button disabled={!workspace.login_available} onClick={()=>void action(workspace.connected?'disconnect':'connect')}>{workspace.connected?'退出登录':'连接学校'}</button>}</div>}{workspace&&workspace.accounts.length>0&&<div className="account-picker"><SearchSelect id="account" name="account" label="本地账号" placeholder="选择账号" options={workspace.accounts.map(value=>({value,label:value}))} value={workspace.owner??''} onChange={account=>{if(!account||account===workspace.owner)return;const select=()=>void action('select_account',{account});if(leaveGuard.current)leaveGuard.current(select);else select()}}/></div>}</header>
  <main aria-busy={busy}>{error&&<p role="alert" className="error">{error}</p>}{workspace?.login&&['failed','cancelled'].includes(workspace.login.status)&&<p role="status">{workspace.login.message}</p>}{!workspace?<p role="status">正在打开学习空间…</p>:!workspace.owner?<><h1>{section==='settings'?'模型与设置':section==='work'?'作业与事项':section==='automation'?'课程自动化':'本学期课程'}</h1><p>{['settings','work','automation'].includes(section)?'请先连接学校，建立本地账号。':'尚无本地课程。'}</p><button disabled={!workspace.login_available||workspace.login?.status==='running'} onClick={()=>void action('connect')}>连接学校</button></>:section==='automation'?<Automation key={workspace.owner} bridge={bridge} revision={revision} leaveGuard={leaveGuard}/>:section==='work'?<Work key={workspace.owner} bridge={bridge} revision={revision}/>:section==='lesson'&&parts[2]==='source'?<WorkSourceView key={`${workspace.owner}:${route}`} bridge={bridge} lesson={id} kind={parts[3]??''} id={safeId(parts[4]??'')}/>:section==='lesson'?<Reader key={`${workspace.owner}:${id}`} id={id} bridge={bridge} revision={revision} action={action} job={workspace.active_job}/>:section==='course'?<Lessons key={`${workspace.owner}:${id}`} id={id} questions={parts[2]==='ask'} bridge={bridge} revision={revision} action={action}/>:section==='settings'?<><ModelSettings key={`${workspace.owner}:settings`} bridge={bridge} session={`${workspace.account_revision??0}:${workspace.connected}:${workspace.login?.status??'idle'}`}/><button onClick={()=>void action('open_data_folder')}>打开数据目录</button></>:section==='jobs'?<Jobs bridge={bridge} revision={revision} action={action}/>:<Courses bridge={bridge} revision={revision} action={action} connected={workspace.connected}/>}</main></div>
 </SidebarProvider></TooltipProvider>
}
interface ScreenProps {bridge:NativeBridge;revision:number;action:(method:string,args?:Record<string,unknown>)=>Promise<void>}
function Courses({bridge,revision,action,connected}:ScreenProps&{connected:boolean}):React.JSX.Element {
 const {data,error,loading}=useData(()=>bridge.call<CourseData>('courses'),[bridge,bridge.owner,revision]);const [search,setSearch]=useState('')
 const courses=data?.courses.filter(x=>`${x.name} ${x.code}`.toLowerCase().includes(search.toLowerCase()))??[]
 return <><div className="page-heading"><h1>本学期课程</h1><button disabled={!connected} onClick={()=>void action('refresh_semesters')}><RefreshCw/>刷新学期</button></div><State loading={loading} error={error}/>{data&&<><div className="toolbar"><div className="term-picker"><SearchSelect id="semester" name="semester" label="学期" placeholder="选择学期" value={data.semester??''} options={data.semesters.map(x=>({value:x.id,label:x.id}))} onChange={semester=>{if(semester)void action(data.semesters.find(x=>x.id===semester)?.cached?'select_semester':'sync_semester',{semester})}}/></div><input aria-label="搜索课程" placeholder="搜索课程" value={search} onChange={e=>setSearch(e.target.value)}/><button disabled={!connected||!data.semester} onClick={()=>void action('sync_semester',{semester:data.semester})}>同步课程</button></div><div className="course-grid">{courses.map(course=><a className="course-card" href={`#/course/${encodeURIComponent(course.id)}`} key={course.id}><span className="eyebrow">{course.code}</span><h2>{course.name}</h2><p>{course.teachers}</p><span>{course.recording_count===undefined?'尚未同步课次':`${course.recording_count} 节回放`}</span></a>)}</div>{courses.length===0&&<p>{search?'没有找到匹配的课程。':data.semester?'该学期暂无本地课程。':'请选择学期。'}</p>}{data.schedule?.rows.length!==undefined&&data.schedule.rows.length>0&&<section className="schedule"><h2>课表</h2><ul>{data.schedule.rows.map((row,i)=><li key={i}><time>{row.date} {row.start_time}</time>{row.course_id?<a href={`#/course/${encodeURIComponent(row.course_id)}`}>{row.course_name||row.name||row.course_id}</a>:<span>{row.course_name||row.name||'未关联课程'}</span>}</li>)}</ul></section>}</>}</>
}
function Lessons({bridge,revision,id,action,questions}:ScreenProps&{id:string;questions:boolean}):React.JSX.Element {
 const {data,error,loading}=useData(()=>bridge.call<Lesson[]>('lessons',{course:id}),[bridge,bridge.owner,revision,id])
 if(questions)return <><a className="back" href={`#/course/${encodeURIComponent(id)}`}><ArrowLeft/>课程课次</a><h1>课程问答</h1><State loading={loading} error={error}/><CourseQuestions key={id} course={id} lessons={data??[]} bridge={bridge}/></>
 return <><a className="back" href="#/courses"><ArrowLeft/>课程</a><div className="page-heading"><h1>课程课次</h1><a href={`#/course/${encodeURIComponent(id)}/ask`}>课程问答</a><button onClick={()=>void action('sync_course',{course:id})}>同步课次</button></div><State loading={loading} error={error}/><div className="lesson-list">{data?.map(lesson=><a key={lesson.id} href={`#/lesson/${encodeURIComponent(lesson.id)}`} className="lesson-row"><div><h2>{lesson.title}</h2><time>{lesson.date?new Date(lesson.date).toLocaleString('zh-CN',{timeZone:'Asia/Shanghai'}):'时间待确认'}</time></div><span>{(lesson.analysis_at||lesson.analyzed_at)?'已分析':lesson.materials_at?'已有材料':lesson.has_recording?'可获取':'尚无回放'}</span></a>)}</div>{data?.length===0&&<p>暂无本地课次，请连接学校后同步。</p>}</>
}
function Jobs({bridge,revision,action}:ScreenProps):React.JSX.Element {
 const {data,error,loading}=useData(()=>bridge.call<Job[]>('jobs'),[bridge,bridge.owner,revision]);const labels:Record<string,string>={queued:'排队',running:'运行中',completed:'完成',failed:'失败',needs_login:'需登录',interrupted:'中断'}
 return <><h1>任务状态</h1><State loading={loading} error={error}/>{data?.length===0&&<p>暂无任务。</p>}{[...(data??[])].sort((a,b)=>b.created_at.localeCompare(a.created_at)).map(job=><article className="job-row" key={job.id}><div><strong>{labels[job.status]??job.status} · {job.kind}</strong><p>{job.message}</p></div>{['failed','needs_login','interrupted'].includes(job.status)&&<button onClick={()=>void action('retry_job',{job:job.id})}>重试</button>}</article>)}</>
}

function WorkspaceNavigation({section,connected}:{section:string;connected:boolean}):React.JSX.Element {
 const {setOpenMobile}=useSidebar()
 return <Sidebar collapsible="icon"><SidebarHeader><a onClick={()=>setOpenMobile(false)} className="brand" href="#/courses"><BookOpen aria-hidden="true"/><span>BUAA <i>Learn</i></span></a></SidebarHeader><SidebarContent><SidebarMenu>{[{id:'courses',label:'本学期课程',icon:Library},{id:'work',label:'作业与事项',icon:ListChecks},{id:'automation',label:'课程自动化',icon:Repeat2},{id:'jobs',label:'任务状态',icon:Clock3},{id:'settings',label:'模型与设置',icon:Settings2}].map(item=><SidebarMenuItem key={item.id}><SidebarMenuButton render={<a onClick={()=>setOpenMobile(false)} href={`#/${item.id}`} aria-current={(section===item.id||(item.id==='courses'&&['course','lesson'].includes(section)))?'page':undefined}/>} isActive={section===item.id||(item.id==='courses'&&['course','lesson'].includes(section))} tooltip={item.label}><item.icon/><span>{item.label}</span></SidebarMenuButton></SidebarMenuItem>)}</SidebarMenu></SidebarContent><SidebarFooter><span className="account-status">{connected?'学校已连接':'离线'}</span></SidebarFooter></Sidebar>
}
