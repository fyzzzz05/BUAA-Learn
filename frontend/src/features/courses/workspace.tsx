import {useState} from 'react'
import {createRoot} from 'react-dom/client'
import {ArrowUpRight, BookOpen, LogIn, RefreshCw, Search} from 'lucide-react'
import {Button} from '@/shared/ui/arc/button/button'
import {Badge} from '@/shared/ui/arc/badge/badge'
import {EmptyState} from '@/shared/ui/arc/empty-state/empty-state'
import {Input} from '@/shared/ui/arc/input/input'
import '@/shared/ui/arc/foundation.css'
import {Timetable} from '../timetable/mount'
import {termLabel} from '../timetable/model'
import {timetableView, type ScheduledCourse, type TimetableCache} from '../timetable/web-model'
import '../timetable/board.css'
import './workspace.css'

interface Course extends ScheduledCourse {
  checked_at?: string
  recording_count?: number
  sync_error?: string | null
}

interface Props {
  page: 'timetable' | 'courses'
  owner: string
  semester: string
  courses: Course[]
  cache: TimetableCache
  loggedIn: boolean
  waiting: boolean
  loginMessage: string
  recordingAuthorized: boolean
  syncing: boolean
  syncingCourses: boolean
  onAction: (action: string) => void
  onOpen: (id: string, date: string) => void
}

function TimetablePage(props: Props) {
  const [week, setWeek] = useState<number | null>(null)
  const [weekdaysOnly, setWeekdaysOnly] = useState(true)
  const view = timetableView(props.cache, props.courses, week)
  return <Timetable {...view} workspace={0} semester={props.semester}
    canSync={props.loggedIn} syncing={props.syncing} weekdaysOnly={weekdaysOnly}
    SyncButton={Button} onWeek={setWeek} onView={setWeekdaysOnly}
    onSync={() => props.onAction('timetable-sync')} onOpen={props.onOpen}/>
}

function CoursesPage({courses, semester, loggedIn, waiting, loginMessage, recordingAuthorized, syncingCourses, onAction}: Props) {
  const [query, setQuery] = useState('')
  const needle = query.trim().toLocaleLowerCase('zh-CN')
  const visible = courses.filter(course =>
    [course.name, course.teachers, course.code].some(value => value?.toLocaleLowerCase('zh-CN').includes(needle)))
  return <section className="arc-courses" aria-labelledby="courses-title">
    <header className="courses-heading">
      <div><h1 id="courses-title">本学期课程</h1>{semester && <p>{termLabel(semester)}</p>}</div>
      {courses.length > 0 && <Button variant="secondary" loading={syncingCourses}
        disabled={!loggedIn || syncingCourses} onClick={() => onAction('recordings-sync')}>
        <RefreshCw aria-hidden="true"/>{syncingCourses ? '正在同步…' : '同步最新回放'}
      </Button>}
    </header>
    {!courses.length ? <div className="courses-empty">
      <EmptyState icon={<BookOpen/>} title={loggedIn ? '暂无本学期课程' : '连接北航课程'}
        description={waiting ? loginMessage : ''}
        action={<Button disabled={waiting || syncingCourses} onClick={() => onAction(loggedIn ? 'sync' : 'login')}>
          {loggedIn ? <RefreshCw aria-hidden="true"/> : <LogIn aria-hidden="true"/>}
          {waiting ? '登录中…' : syncingCourses ? '同步中…' : loggedIn ? '同步本学期课程' : '使用北航 SSO 登录'}
        </Button>}/>
    </div> : <>
      {!loggedIn && <p className="notice">当前显示本地缓存。登录学校后可获取新课次和课堂材料。</p>}
      {loggedIn && !recordingAuthorized && <p className="notice amber">{loginMessage}</p>}
      <div className="courses-filter">
        <Input label="搜索课程" type="search" placeholder="课程名、教师或课程编号" value={query} onChange={event => setQuery(event.currentTarget.value)}/>
        <span aria-live="polite">{visible.length} 门课程</span>
      </div>
      {visible.length ? <ul className="courses-grid">
        {visible.map(course => <li key={course.id}>
          <a className="course-card" href={`#/course/${encodeURIComponent(course.id)}`}>
            <div className="course-card-top"><span className="course-card-icon"><BookOpen aria-hidden="true"/></span><ArrowUpRight aria-hidden="true" className="course-card-arrow"/></div>
            <h2>{course.name}</h2>
            {course.teachers && <p>{course.teachers}</p>}
            <div className="course-card-meta">
              <Badge size="sm" tone={course.sync_error ? 'warning' : course.recording_count ? 'info' : 'neutral'}>
                {course.sync_error ? '同步需重试' : course.checked_at ? `${course.recording_count ?? 0} 节回放` : '尚未同步课次'}
              </Badge>
              {course.code && <span>{course.code}</span>}
            </div>
          </a>
        </li>)}
      </ul> : <div className="courses-empty"><EmptyState icon={<Search/>} title="没有匹配的课程" description=""
        action={<Button variant="secondary" onClick={() => setQuery('')}>清除搜索</Button>}/></div>}
    </>}
  </section>
}

export function mountCourseWorkspace(container: HTMLElement) {
  const root = createRoot(container)
  return {
    update(props: Props) {
      const key = `${props.owner}:${props.semester}`
      root.render(props.page === 'timetable' ? <TimetablePage key={key} {...props}/> : <CoursesPage key={key} {...props}/>)
    },
    unmount() {root.unmount()},
  }
}
