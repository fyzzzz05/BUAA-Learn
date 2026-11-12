import test from 'node:test'
import assert from 'node:assert/strict'
import {readFileSync} from 'node:fs'
import {transformSync} from 'esbuild'
const {code}=transformSync(readFileSync(new URL('./model.ts',import.meta.url),'utf8'),{loader:'ts',format:'esm',target:'es2022'})
const {allocateCourseColors,position,localClock,termLabel}=await import(`data:text/javascript;base64,${Buffer.from(code).toString('base64')}`)

test('09:50 class position and 95-minute height use minutes',()=>{
 const box=position({start:590,end:685},8,68)
 assert.ok(Math.abs(box.top-127.6666667)<.001)
 assert.ok(Math.abs(box.height-101.6666667)<.001)
 const short=position({start:590,end:635},8,68)
 assert.equal(short.height,45)
})
test('Shanghai date and time are independent of the host timezone',()=>{
 assert.deepEqual(localClock(new Date('2026-11-08T16:05:00Z')),{date:'2026-11-09',minutes:5})
 assert.deepEqual(localClock(new Date('2026-11-09T15:59:00Z')),{date:'2026-11-09',minutes:1439})
})
test('course colors do not change with roster order or collide in an 18-course semester',()=>{
 const keys=['B320026075','B130031052','B030031085','B130032052','B310023005','B130031049','B130032037','G130031006',...Array.from({length:10},(_,i)=>`course-${i}`)]
 const colors=allocateCourseColors(keys)
 assert.deepEqual(colors,allocateCourseColors([...keys].reverse()))
 assert.equal(new Set(Object.values(colors)).size,18)
 assert.notEqual(colors.B030031085,colors.B130031049)
})
test('term label uses the selected school term',()=>{
 assert.equal(termLabel('2026-20271'),'2026–2027 学年 · 第一学期')
 assert.equal(termLabel('2026-20272'),'2026–2027 学年 · 第二学期')
})

test('web schedule keeps overlapping classes in separate lanes and preserves unknown times', async () => {
 const {buildSync}=await import('esbuild')
 const {outputFiles:[bundle]}=buildSync({
  entryPoints:[new URL('./web-model.ts',import.meta.url).pathname],
  bundle:true,write:false,format:'esm',target:'es2022',
 })
 const {timetableView}=await import(`data:text/javascript;base64,${Buffer.from(bundle.text).toString('base64')}`)
 const courses=[{id:'a',name:'同名课程'},{id:'b',name:'同名课程'}]
 const cache={start:'2026-09-07',weeks:19,complete:true,rows:[
  {date:'2026-11-09',time:'09:50–11:25',course_id:'a'},
  {date:'2026-11-09',time:'10:00 至 10:45',course_id:'b'},
  {date:'2026-11-09',time:'待定',name:'同名课程'},
 ]}
 const now=new Date('2026-11-08T16:05:00Z')
 const view=timetableView(cache,courses,null,now)
 assert.equal(view.week,10)
 assert.equal(view.dates[0],'2026-11-09')
 assert.deepEqual(view.entries.map(({courseID,start,lane,lanes})=>[courseID,start,lane,lanes]),
  [['a',590,0,2],['b',600,1,2],['',null,0,1]])
 assert.equal(timetableView(cache,courses,11,now).entries.length,0)
})
