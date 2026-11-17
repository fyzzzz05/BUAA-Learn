export interface Workspace {accounts:string[];owner:string|null;connected:boolean;draining:boolean;account_revision?:number;active_job?:Job|null;login_available?:boolean;login?:{status:string;message:string}|null}
export interface Course {id:string;name:string;code:string;teachers?:string;recording_count?:number;lesson_count?:number}
export interface Lesson {id:string;course_id:string;title:string;date:string;has_recording:boolean;materials_at?:string;analysis_at?:string;analyzed_at?:string;slide_count?:number;segment_count?:number}
export interface Segment {id:string;start:number;end:number;text:string}
export interface Finding {text:string;evidence_ids?:string[];source_range?:{start_id:string;end_id:string}}
export interface Section {knowledge_points?:Finding[];assignments?:Finding[];notices?:Finding[];kind:string;title:string;summary:string;start:number;end:number;start_id?:string;end_id?:string}
export interface Material {digest?:string;fetched_at?:string;segments:Segment[];slides:{id:string;start:number;url:string}[];coverage?:{ratio?:number|null;note?:string;gaps?:{start:number;end:number}[]};limitations?:string[];duration:number}
export interface LessonData {lesson:Lesson;materials:Material|null;analysis:{sections?:Section[];timeline_status?:string;timeline_warnings?:string[];created_at?:string;digest?:string}|null;last_tab:string}
export interface CourseData {semesters:{id:string;available:boolean;cached:boolean}[];semester:string|null;courses:Course[];schedule:{rows:{date:string;course_id?:string;course_name?:string;name?:string;start_time?:string}[]}|null}
export interface Job {id:string;owner:string;kind:string;target:string;status:string;message:string;created_at:string;revision:number}
export interface ReaderState {tab:string;search:string;anchor:string|null;offset:number;snapshot:string}
export function clock(seconds:number):string {const value=Math.max(0,Math.floor(seconds));return [Math.floor(value/3600),Math.floor(value/60)%60,value%60].map(x=>String(x).padStart(2,'0')).join(':')}
