import {useEffect,useRef,useState,type ComponentType,type CSSProperties} from 'react'
import {createRoot} from 'react-dom/client'
import {AlertCircle,ArrowUpRight,CheckCircle2,ChevronLeft,ChevronRight,Clock3,MapPin,RefreshCw,UserRound,X} from 'lucide-react'
import {Button} from '@/shared/ui/button'
import {SearchSelect} from '@/shared/ui/search-select'
import {Sheet,SheetClose,SheetContent,SheetDescription,SheetHeader,SheetTitle} from '@/shared/ui/sheet'
import {allocateCourseColors,courseColor,dayNames,localClock,position,shortDate,termLabel,type CourseEntry,type TimetableProps} from './model'

type SyncButtonProps = {onClick: () => void; disabled: boolean; children: React.ReactNode}
type ViewProps = TimetableProps & {SyncButton?: ComponentType<SyncButtonProps>}

export function Timetable(props:ViewProps):React.JSX.Element {
 const SyncButton = props.SyncButton ?? Button
 const [selected,setSelected]=useState<CourseEntry|null>(null)
 const [now,setNow]=useState(()=>localClock())
 const status=useRef<HTMLDetailsElement>(null)
 const board=useRef<HTMLDivElement>(null)
 useEffect(()=>{const timer=setInterval(()=>setNow(localClock()),30_000);return()=>clearInterval(timer)},[])
 useEffect(()=>{board.current?.scrollTo({top:0});setSelected(null)},[props.week])
 useEffect(()=>{const close=(event:PointerEvent)=>{if(status.current&&!status.current.contains(event.target as Node))status.current.open=false};document.addEventListener('pointerdown',close);return()=>document.removeEventListener('pointerdown',close)},[])
 const shownDays=props.weekdaysOnly?5:7
 const weekendCount=props.entries.filter(e=>props.dates.slice(5).includes(e.date)).length
 const colors=allocateCourseColors(props.courseKeys)
 const valid=props.entries.filter(e=>e.start!==null&&e.end!==null)
 const unknown=props.entries.filter(e=>e.start===null)
 const today=props.dates.indexOf(now.date)
 const hourHeight=68
 const height=(props.lastHour-props.firstHour)*hourHeight
 const dateRange=`${shortDate(props.dates[0])} – ${shortDate(props.dates[6])}`
 const style={'--days':shownDays,'--hour-height':`${hourHeight}px`,'--grid-height':`${height}px`} as CSSProperties
 const openEntry=(entry:CourseEntry)=>setSelected(entry)
 return <section className="timetable" aria-label="本学期课程表">
  <header className="tt-heading"><div><h1>课程表</h1><p>{termLabel(props.semester)}</p></div><div className="tt-heading-actions">
   <details ref={status} className={`tt-status ${props.fullSemester?'is-complete':'is-partial'}`}>
    <summary>{props.syncing?<RefreshCw className="tt-spinning"/>:props.fullSemester?<CheckCircle2/>:<AlertCircle/>}{props.syncing?'正在同步':props.fullSemester?'已同步':'近期缓存'}</summary>
    <div className="tt-status-popup" role="status"><strong>{props.fullSemester?'本学期课表已同步':'尚未同步完整学期'}</strong><p>{props.fullSemester?`已缓存本学期 ${props.weeks} 周的排课，切换周次可直接查看。`:'当前显示近期缓存。同步课表后，可查看本学期全部周次。'}</p>{!props.canSync&&<p>请先通过右上角 SSO 登录连接学校。</p>}<Button variant="outline" onClick={()=>{status.current?.removeAttribute('open');props.onSync()}} disabled={!props.canSync||props.syncing}>同步课表</Button></div>
   </details>
   <SyncButton onClick={props.onSync} disabled={!props.canSync||props.syncing}><RefreshCw className={props.syncing?'tt-spinning':''}/>{props.syncing?'同步中…':'同步课表'}</SyncButton>
  </div></header>
  <div className="tt-toolbar">
   <div className="tt-week-nav"><Button variant="outline" size="icon" aria-label="上一周" disabled={props.week<=1} onClick={()=>props.onWeek(props.week-1)}><ChevronLeft/></Button>
    <div className="tt-week-select"><SearchSelect id="timetable-week" name="week" label="选择教学周" placeholder="选择周次" resetSearchOnClose value={String(props.week)} options={props.weekOptions} onChange={value=>{if(value)props.onWeek(Number(value))}}/></div>
    <Button variant="outline" size="icon" aria-label="下一周" disabled={props.week>=props.weeks} onClick={()=>props.onWeek(props.week+1)}><ChevronRight/></Button>
    <span className="tt-date-range">{dateRange}<span className="tt-parity">{props.week%2?'单周':'双周'}</span></span>
    <Button variant="ghost" onClick={()=>{if(props.currentWeek)props.onWeek(props.currentWeek)}} disabled={!props.currentWeek||props.week===props.currentWeek}>回到本周</Button>
   </div>
   <div className="tt-view" role="group" aria-label="课表视图"><Button variant="ghost" aria-pressed={props.weekdaysOnly} onClick={()=>props.onView(true)}>工作日</Button><Button variant="ghost" aria-pressed={!props.weekdaysOnly} onClick={()=>props.onView(false)}>完整周</Button></div>
  </div>
  {props.weekdaysOnly&&weekendCount>0&&<button className="tt-weekend-note" onClick={()=>props.onView(false)}>周末有 {weekendCount} 次课，查看完整周 <ArrowUpRight/></button>}
  <div ref={board} className={`tt-scroll ${props.weekdaysOnly?'is-workdays':'is-full-week'}`} style={style}>
   <div className="tt-board">
    <div className="tt-days"><div className="tt-time-heading">时间</div>{props.dates.slice(0,shownDays).map((date,index)=><div key={date} className={`tt-day ${date===now.date?'is-today':''}`}><span>{dayNames[index]}</span><strong>{Number(date.slice(8))}</strong></div>)}</div>
    <div className="tt-grid" style={{height}}>
     <div className="tt-time-rail">{Array.from({length:props.lastHour-props.firstHour},(_,i)=>props.firstHour+i).map(hour=><time key={hour} style={{top:(hour-props.firstHour)*hourHeight}}>{String(hour).padStart(2,'0')}:00</time>)}</div>
     {props.dates.slice(0,shownDays).map(date=><div key={date} className={`tt-day-column ${date===now.date?'is-today':''}`}>
      {valid.filter(entry=>entry.date===date).map(entry=>{
       const box=position(entry,props.firstHour,hourHeight), compact=box.height<76, tiny=box.height<46
       const colorKey=entry.code||entry.courseID||entry.name
       const color=colors[colorKey]??courseColor(colorKey)
       return <button key={entry.id} className={`tt-course ${compact?'is-compact':''} ${tiny?'is-tiny':''} ${entry.courseID?'':'is-unlinked'}`}
        style={{'--course-color':color,top:box.top,height:box.height,left:`calc(${entry.lane/entry.lanes*100}% + 5px)`,width:`calc(${100/entry.lanes}% - 10px)`} as CSSProperties}
        onClick={()=>openEntry(entry)} aria-label={`${entry.name} ${entry.date} ${entry.time} ${entry.room}，查看课程详情`} title={`${entry.name}\n${entry.time}\n${entry.room}`}>
        <strong>{entry.name}</strong><span className="tt-course-time">{entry.time}</span>{!tiny&&entry.room&&<span className="tt-course-room">{entry.room}</span>}
       </button>
      })}
     </div>)}
     {today>=0&&today<shownDays&&now.minutes>=props.firstHour*60&&now.minutes<props.lastHour*60&&<div className="tt-now" role="img" aria-label={`当前时间 ${String(Math.floor(now.minutes/60)).padStart(2,'0')}:${String(now.minutes%60).padStart(2,'0')}`} style={{top:(now.minutes-props.firstHour*60)*hourHeight/60,gridColumn:today+2}}><span/ ><i/></div>}
    </div>
    {!props.entries.length&&<div className="tt-empty">{props.fullSemester?'这一周暂无排课。':'这一周尚无本地排课，登录学校后同步课表。'}</div>}
   </div>
  </div>
  {unknown.length>0&&<div className="tt-unknown"><h2>时间待确认</h2>{unknown.map(entry=><Button key={entry.id} variant="outline" onClick={()=>openEntry(entry)}>{entry.name} · {shortDate(entry.date)}</Button>)}</div>}
  <Sheet open={selected!==null} onOpenChange={open=>{if(!open)setSelected(null)}}>
   <SheetContent className="tt-detail" showCloseButton={false}>
    <SheetClose render={<Button variant="ghost" size="icon" aria-label="关闭课程详情" className="tt-detail-close"/>}><X/></SheetClose>
    {selected&&<><SheetHeader><span className="tt-detail-week">第 {props.week} 周</span><SheetTitle>{selected.name}</SheetTitle><SheetDescription>{selected.date} · {dayNames[props.dates.indexOf(selected.date)]}</SheetDescription></SheetHeader>
     <div className="tt-detail-body"><dl><div><dt><Clock3/>时间</dt><dd>{selected.time}</dd></div><div><dt><MapPin/>教室</dt><dd>{selected.room||'未提供教室'}</dd></div><div><dt><UserRound/>教师</dt><dd>{selected.teachers||'未提供教师'}</dd></div>{selected.code&&<div><dt>课程编号</dt><dd>{selected.code}</dd></div>}</dl>
      {!selected.courseID&&<p className="tt-detail-warning">这条排课尚未关联到本学期课程。</p>}
      <Button disabled={!selected.courseID} onClick={()=>{props.onOpen(selected.courseID,selected.date);setSelected(null)}}>进入课程<ArrowUpRight/></Button>
     </div></>}
   </SheetContent>
  </Sheet>
 </section>
}

export function mountTimetable(container:HTMLElement,initial:TimetableProps):{update:(props:TimetableProps)=>void;unmount:()=>void} {
 const root=createRoot(container)
 root.render(<Timetable key={initial.workspace} {...initial}/>)
 return {update:props=>root.render(<Timetable key={props.workspace} {...props}/>),unmount:()=>root.unmount()}
}
