export interface CourseEntry {
 id:string;date:string;name:string;room:string;time:string;start:number|null;end:number|null
 courseID:string;teachers:string;code:string;lane:number;lanes:number
}
export interface TimetableProps {
 workspace:number;courseKeys:string[];semester:string;weeks:number;week:number;currentWeek:number|null
 dates:string[];weekOptions:{value:string;label:string;description:string}[]
 entries:CourseEntry[];firstHour:number;lastHour:number
 fullSemester:boolean;canSync:boolean;syncing:boolean;weekdaysOnly:boolean
 onWeek:(week:number)=>void;onView:(weekdaysOnly:boolean)=>void;onSync:()=>void;onOpen:(id:string,date:string)=>void
}
export const dayNames=['周一','周二','周三','周四','周五','周六','周日']
const colors=['#3269a5','#237b7a','#527b38','#8056a4','#a66629','#a34e4e','#a3457d','#4e61a4','#7a732a','#8d6445','#a44965','#237596','#546b9e','#577d6a','#8b672e','#9d526f','#657b2d','#366f72','#744f8c','#965c36','#51749b','#7b6c48','#42714c','#8d4e58']
export function courseColor(key:string):string {
 let hash=5381
 for(const char of key)hash=((hash*33)^char.charCodeAt(0))>>>0
 return colors[hash%colors.length]
}
export function allocateCourseColors(keys:string[]):Record<string,string> {
 const used=new Set<string>(),result:Record<string,string>={}
 for(const key of [...new Set(keys)].sort()) {
  let index=colors.indexOf(courseColor(key)), attempts=0
  while(used.has(colors[index])&&attempts++<colors.length)index=(index+7)%colors.length
  result[key]=colors[index];used.add(colors[index])
 }
 return result
}
export function position(entry:CourseEntry,firstHour:number,hourHeight:number):{top:number;height:number} {
 return {top:((entry.start??firstHour*60)-firstHour*60)*hourHeight/60+3,height:Math.max(24,((entry.end??0)-(entry.start??0))*hourHeight/60-6)}
}
export function localClock(now=new Date()):{date:string;minutes:number} {
 const parts=new Intl.DateTimeFormat('en-CA',{timeZone:'Asia/Shanghai',year:'numeric',month:'2-digit',day:'2-digit',hour:'2-digit',minute:'2-digit',hourCycle:'h23'}).formatToParts(now)
 const part=(key:string)=>parts.find(p=>p.type===key)?.value??''
 return {date:`${part('year')}-${part('month')}-${part('day')}`,minutes:Number(part('hour'))*60+Number(part('minute'))}
}
export function shortDate(value:string):string{return `${Number(value.slice(5,7))}月${Number(value.slice(8,10))}日`}
export function termLabel(value:string):string {
 const match=value.match(/^(\d{4})-(\d{4})([123])$/)
 return match?`${match[1]}–${match[2]} 学年 · 第${['一','二','三'][Number(match[3])-1]}学期`:value
}
