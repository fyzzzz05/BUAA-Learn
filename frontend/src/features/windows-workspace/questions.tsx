import {useEffect, useRef, useState, type FormEvent} from 'react'
import type {NativeBridge} from '@/shared/native/bridge'
import {SearchSelect} from '@/shared/ui/search-select'
import {clock, type Lesson, type Segment} from './types'

interface QuestionSource extends Segment {
 lesson_id: string
 title: string
}

interface Chat {
 id: string
 question: string
 answer: string
 sources: QuestionSource[]
}

export function CourseQuestions({bridge, course, lessons}: {
 bridge: NativeBridge
 course: string
 lessons: Lesson[]
}): React.JSX.Element {
 const [history, setHistory] = useState<Chat[]>()
 const [question, setQuestion] = useState('')
 const [scope, setScope] = useState('all')
 const [error, setError] = useState('')
 const [pending, setPending] = useState(false)
 const active = useRef<string>(undefined)
 useEffect(() => {
  let mounted = true
  const owner = bridge.owner
  void bridge.call<Chat[]>('course_chat', {course})
   .then(rows => { if (mounted) setHistory(rows) })
   .catch((e: Error) => { if (mounted) setError(e.message) })
  return () => {
   mounted = false
   if (active.current && bridge.owner === owner)
    void bridge.call('cancel_question', {request: active.current}).catch(() => {})
  }
 }, [bridge, course])

 async function submit(event: FormEvent<HTMLFormElement>): Promise<void> {
  event.preventDefault()
  const text = question.trim()
  if (!text || active.current || !history) return
  const request = crypto.randomUUID()
  active.current = request
  setPending(true)
  setError('')
  try {
   const chat = await bridge.call<Chat>('ask_course', {
    request, course, lesson: scope.startsWith('lesson:') ? scope.slice(7) : null,
    question: text, acknowledge_cost: true
   })
   setHistory(rows => [chat, ...(rows ?? [])].slice(0, 20))
   setQuestion('')
  } catch (e) {
   setError(e instanceof Error ? e.message : '问答未完成，请重试。')
  } finally {
   active.current = undefined
   setPending(false)
  }
 }

 return <>
  <form className="course-question" onSubmit={event => void submit(event)}>
   <SearchSelect id="question-scope" name="scope" label="问答范围" placeholder="选择范围"
    options={[{value: 'all', label: '整门课程'}, ...lessons.map(lesson => ({
     value: `lesson:${lesson.id}`, label: lesson.title, description: lesson.date?.slice(0, 10)
    }))]} value={scope} onChange={value => setScope(value || 'all')}/>
   <input aria-label="课堂问题" placeholder="输入课堂问题" value={question} maxLength={2000}
    disabled={pending} onChange={event => setQuestion(event.target.value)}/>
   <p>提问会将问题和相关课堂文本发送给模型服务，可能产生费用。</p>
   <div className="toolbar">
    <button disabled={pending || !history || !question.trim()}>提问</button>
    {pending && <><span role="status">正在回答…</span><button type="button" onClick={() => {
     if (active.current) void bridge.call('cancel_question', {request: active.current})
      .catch((e: Error) => setError(e.message))
    }}>取消</button></>}
   </div>
  </form>
  {error && <p className="error" role="alert">{error}</p>}
  {history?.map(chat => <article className="course-answer" key={chat.id}>
   <h2>{chat.question}</h2>
   <p>{chat.answer}</p>
   {chat.sources.map(source => <details key={source.id}>
    <summary>[{source.id}] {source.title} · {clock(source.start)}–{clock(source.end)}</summary>
    <blockquote>{source.text}</blockquote>
    <a href={`#/lesson/${encodeURIComponent(source.lesson_id)}`}>打开课堂</a>
   </details>)}
  </article>)}
 </>
}
