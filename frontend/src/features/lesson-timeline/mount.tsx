import { flushSync } from "react-dom"
import { createRoot } from "react-dom/client"
import { AlertCircle, BookOpen } from "lucide-react"
import { Timeline, type TimelineItem } from "@/components/ui/timeline"

interface SourceRange {
  start_id: string
  end_id: string
  start: number
  end: number
}
interface Segment {
  id: string
  start: number
  end: number
}
interface Finding {
  text: string
  evidence_ids: string[]
  source_range?: SourceRange
  deadline?: string
  audience?: string
  submission?: string
}
export interface LessonSection {
  kind: "chapter" | "gap" | "unclassified"
  title: string
  summary: string
  start: number
  end: number
  start_id?: string
  end_id?: string
  knowledge_points?: Finding[]
  assignments?: Finding[]
  notices?: Finding[]
}
interface TimelineHandle {
  unmount: () => void
}

function clock(seconds: number): string {
  const value = Math.max(0, Math.floor(seconds))
  return [Math.floor(value / 3600), Math.floor(value / 60) % 60, value % 60]
    .map(part => String(part).padStart(2, "0")).join(":")
}

function SourceButton({range}: {range: SourceRange}): React.JSX.Element {
  return <button type="button" className="lesson-source-button" data-action="source-range"
    data-id={`${range.start_id}|${range.end_id}`}>
    {clock(range.start)}–{clock(range.end)} 查看原文
  </button>
}

function FindingRows({rows, evidence}: {rows: Finding[], evidence: Record<string, Segment>}): React.JSX.Element {
  return <ul className="lesson-finding-list">{rows.map((row, index) => {
    const refs = row.evidence_ids.map(id => evidence[id]).filter(Boolean).sort((a, b) => a.start - b.start)
    const range = row.source_range ?? (refs.length ? {
      start_id: refs[0].id, end_id: refs.at(-1)!.id,
      start: refs[0].start, end: Math.max(...refs.map(ref => ref.end)),
    } : undefined)
    return <li key={index}><p>{row.text}</p>{row.deadline !== undefined &&
      <p className="lesson-assignment-meta">待核验 · 截止：{row.deadline} · 对象：{row.audience}<br />提交：{row.submission}</p>}
      {range && <SourceButton range={range} />}
    </li>
  })}</ul>
}

function ChapterContent({section, evidence}: {section: LessonSection, evidence: Record<string, Segment>}): React.JSX.Element {
  const points = section.knowledge_points ?? []
  const assignments = section.assignments ?? []
  const notices = section.notices ?? []
  return <div className="lesson-chapter-content">
    {section.start_id && section.end_id && <SourceButton range={{
      start_id: section.start_id, end_id: section.end_id, start: section.start, end: section.end,
    }} />}
    {points.length + assignments.length + notices.length > 0 && <details className="lesson-chapter-findings">
      <summary>知识点 {points.length} · 作业 {assignments.length} · 事项 {notices.length}</summary>
      {points.length > 0 && <><h5>本段知识点</h5><FindingRows rows={points} evidence={evidence} /></>}
      {assignments.length > 0 && <><h5>本段作业</h5><FindingRows rows={assignments} evidence={evidence} /></>}
      {notices.length > 0 && <><h5>本段事项</h5><FindingRows rows={notices} evidence={evidence} /></>}
    </details>}
  </div>
}

export function mountLessonTimeline(
  container: HTMLElement, sections: LessonSection[], evidence: Record<string, Segment>,
): TimelineHandle {
  const root = createRoot(container)
  const items: TimelineItem[] = sections.map((section, index) => ({
    id: `chapter-${index}`,
    title: section.title,
    description: section.summary,
    timestamp: `${clock(section.start)}–${clock(section.end)}`,
    status: section.kind !== "chapter" ? "pending" : "default",
    icon: section.kind !== "chapter" ? <AlertCircle className="h-3 w-3" /> : <BookOpen className="h-3 w-3" />,
    content: section.kind === "gap" ? undefined : <ChapterContent section={section} evidence={evidence} />,
  }))
  flushSync(() => root.render(<div className="buaa-timeline">
    <Timeline items={items} variant="spacious" orientation="vertical" timestampPosition="top" />
  </div>))
  return {unmount: () => root.unmount()}
}
