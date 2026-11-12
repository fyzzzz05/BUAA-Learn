import {localClock, shortDate, type CourseEntry, type TimetableProps} from './model'

export interface ScheduledCourse {
  id: string
  name: string
  code?: string | null
  teachers?: string | null
}

interface ScheduleRow {
  course_id?: string | null
  name?: string | null
  date?: string | null
  time?: string | null
  room?: string | null
  teachers?: string | null
  start_time?: string | null
  end_time?: string | null
}

export interface TimetableCache {
  start?: string | null
  weeks?: number | null
  complete?: boolean
  rows: ScheduleRow[]
}

const day = 86_400_000
const dateNumber = (date: string) => Date.parse(date + 'T00:00:00Z')
const dateKey = (date: number) => new Date(date).toISOString().slice(0, 10)

function minutes(time: string): number | null {
  const match = time.trim().match(/^(\d{1,2}):(\d{2})$/)
  if (!match) return null
  const hours = Number(match[1]), mins = Number(match[2])
  return mins < 60 && (hours < 24 || (hours === 24 && mins === 0)) ? hours * 60 + mins : null
}

function placeEntries(entries: CourseEntry[]) {
  let group: CourseEntry[] = [], laneEnds: number[] = [], groupEnd = -1
  function finish() {
    for (const entry of group) entry.lanes = laneEnds.length
    group = []
    laneEnds = []
    groupEnd = -1
  }
  for (const entry of entries) {
    if (entry.start === null || entry.end === null) continue
    if (entry.start >= groupEnd) finish()
    const free = laneEnds.findIndex(end => end <= entry.start!)
    entry.lane = free < 0 ? laneEnds.length : free
    laneEnds[entry.lane] = entry.end
    group.push(entry)
    groupEnd = Math.max(groupEnd, entry.end)
  }
  finish()
}

type CalendarView = Pick<TimetableProps, 'courseKeys' | 'weeks' | 'week' | 'currentWeek' |
  'dates' | 'weekOptions' | 'entries' | 'firstHour' | 'lastHour' | 'fullSemester'>

export function timetableView(
  cache: TimetableCache, courses: ScheduledCourse[], selectedWeek: number | null, now = new Date(),
): CalendarView {
  const today = dateNumber(localClock(now).date)
  const hasTerm = Boolean(cache.start && Number.isFinite(dateNumber(cache.start)) &&
    Number.isInteger(cache.weeks) && cache.weeks! > 0)
  const monday = today - ((new Date(today).getUTCDay() + 6) % 7) * day
  const start = hasTerm ? dateNumber(cache.start!) : monday
  const weeks = hasTerm ? cache.weeks! : 1
  const current = Math.floor((today - start) / (day * 7)) + 1
  const currentWeek = current >= 1 && current <= weeks ? current : null
  const week = Math.max(1, Math.min(weeks, selectedWeek ?? currentWeek ?? current))
  const firstDay = start + (week - 1) * 7 * day
  const dates = Array.from({length: 7}, (_, i) => dateKey(firstDay + i * day))
  const byDate = new Map(dates.map(date => [date, [] as CourseEntry[]]))
  const courseKeys = new Set(courses.map(course => course.code || course.id || course.name))
  cache.rows.forEach((row, index) => {
    if (!row.date) return
    const matches = row.name ? courses.filter(course => course.name === row.name) : []
    const course = courses.find(course => course.id === row.course_id) ?? (matches.length === 1 ? matches[0] : undefined)
    const name = course?.name || row.name || '课程'
    const code = course?.code || '', courseID = course?.id || ''
    courseKeys.add(code || courseID || name)
    const entries = byDate.get(row.date)
    if (!entries) return
    const [from = '', to = ''] = (row.time || '').split(/[-–—－~～至]/)
    const first = row.start_time || from, last = row.end_time || to
    const start = minutes(first), end = minutes(last)
    const timed = start !== null && end !== null && end > start
    entries.push({
      id: `schedule-${index}`, date: row.date, name, room: row.room || '',
      time: timed ? `${first.trim()}–${last.trim()}` : row.time || '时间待确认',
      start: timed ? start : null, end: timed ? end : null, courseID,
      teachers: row.teachers || course?.teachers || '', code, lane: 0, lanes: 1,
    })
  })
  const entries = [...byDate.values()].flatMap(rows => {
    rows.sort((a, b) => (a.start ?? Infinity) - (b.start ?? Infinity) || a.name.localeCompare(b.name, 'zh-CN'))
    placeEntries(rows)
    return rows
  })
  const weekOptions = Array.from({length: weeks}, (_, index) => {
    const startOfWeek = start + index * 7 * day
    return {value: String(index + 1), label: hasTerm ? `第 ${index + 1} 周` : '本周',
      description: `${shortDate(dateKey(startOfWeek))} – ${shortDate(dateKey(startOfWeek + 6 * day))}`}
  })
  return {
    week, weeks, currentWeek, dates, entries, weekOptions, courseKeys: [...courseKeys],
    fullSemester: Boolean(cache.complete),
    firstHour: Math.floor(Math.min(8 * 60, ...entries.map(entry => entry.start ?? Infinity)) / 60),
    lastHour: Math.ceil(Math.max(22 * 60, ...entries.map(entry => entry.end ?? -Infinity)) / 60),
  }
}
