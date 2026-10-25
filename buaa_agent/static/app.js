import {mountAutomations} from "/automations.js";
import {mountNavigation, mountTaskProgress} from "/navigation.js";
import {mountCombobox, hasOpenCombobox} from "/combobox.js";
const $ = (selector) => document.querySelector(selector);
const esc = (value) => String(value ?? '').replace(/[&<>"']/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
const enc = encodeURIComponent;
const clock = (s) => {s = Math.max(0, Math.floor(s || 0)); return [Math.floor(s/3600),Math.floor(s/60)%60,s%60].map(x=>String(x).padStart(2,'0')).join(':');};
const date = (value) => value ? new Date(value).toLocaleDateString('zh-CN',{month:'numeric',day:'numeric',weekday:'short',timeZone:'Asia/Shanghai'}) : '';
const datetime = (value) => value ? new Date(value).toLocaleString('zh-CN',{timeZone:'Asia/Shanghai',hour12:false}) : '尚未同步';
let state, currentCourse, currentLesson, courseTab = 'lessons';
let asking = false, busy = false, refreshSignature = '', toastTimer, routeVersion = 0;
let homeDraft = '', homeCourse = '';
let pendingChatCourse = '';
let pendingJobResult = null;
let automationDraft = null, automationHandle = null;
const selectHandles = new Map();
let timelineHandle = null;
const navigation=mountNavigation($('#navigation-root'),$('#navigation-trigger'));
let readerLesson = '', readerTab = '', readerSearch = '', readerRange = null, readerSaveTimer, readerReturnFocus, readerMemoryKey;


function icon(name, extra='') {return `<img class="ui-icon ${extra}" src="/icons/${esc(name)}.svg" alt="" aria-hidden="true">`;}

async function api(path, options = {}) {
  const response = await fetch('/api' + path, {...options, headers: {'Content-Type':'application/json','X-BUAA-Local':'1',...options.headers}});
  const data = await response.json();
  if (!response.ok) throw new Error(typeof data.detail === 'string' ? data.detail : '操作未完成，请检查输入后重试');
  return data;
}
function toast(message) {
  ($('#reader').open?$('#reader'):document.body).appendChild($('#toast'));
  $('#toast').textContent = message; $('#toast').hidden = false;
  clearTimeout(toastTimer); toastTimer = setTimeout(()=>$('#toast').hidden=true,6000);
}
function route() {
  const parts = location.hash.replace(/^#\/?/,'').split('/').map(decodeURIComponent);
  return {page:parts[0] || 'home',course:parts[1],lesson:parts[2]==='lesson'?parts[3]:null};
}
function button(text, action, id='', primary=false, extra='') {
  const icons={sync:'refresh-cw','course-sync':'refresh-cw',analyze:'play',materials:'download','run-automation':'play',login:'log-in',logout:'log-out'};
  return `<button class="${primary?'primary':''}" data-action="${action}" data-id="${esc(id)}" ${extra}>${icons[action]?icon(icons[action]):''}${esc(text)}</button>`;
}
function badge(text,color='') {return `<span class="badge ${color}">${esc(text)}</span>`;}
function empty(title, text, actions='') {return `<div class="empty"><h2>${esc(title)}</h2><p>${esc(text)}</p><div class="actions">${actions}</div></div>`;}
function courseHref(id) {return `#/course/${enc(id)}`;}
function lessonHref(courseId,id) {return `${courseHref(courseId)}/lesson/${enc(id)}`;}
function readMemory(key, fallback={}) {try{return JSON.parse(localStorage.getItem(key))||fallback;}catch{return fallback;}}
function memoryKey() {return `buaa-reader:${state.auth.profile.owner||'local'}:${currentLesson.lesson.id}`;}
function rememberReader() {
  if(!$('#reader').open||!readerLesson)return;
  const body=$('#reader-body'), saved=readMemory(readerMemoryKey);
  const anchor=[...body.querySelectorAll('.segment[id], [data-reader-anchor]')].find(el=>el.offsetTop+el.offsetHeight>body.scrollTop);
  saved.tab=readerTab;saved[readerTab]={top:body.scrollTop,search:readerSearch,range:readerRange,anchor:anchor?.id,marker:anchor?.dataset.readerAnchor,offset:anchor?body.scrollTop-anchor.offsetTop:0};
  try{localStorage.setItem(readerMemoryKey,JSON.stringify(saved));}catch{/* Reading still works when storage is unavailable. */}
}
function readerPosition() {
  const body=$('#reader-body'), range=body.scrollHeight-body.clientHeight;
  $('#reader-position').textContent=`阅读位置 ${range>0?Math.round(body.scrollTop/range*100):0}%`;
}
function readerTabs() {
  const m=currentLesson.materials;
  return [['analysis','学习笔记'],['transcript',`课堂文本${m?' '+m.segments.length:''}`],['slides',`PPT 截图${m?' '+m.slides.length:''}`],['quality','材料情况']].map(([id,name])=>`<button class="tab ${readerTab===id?'active':''}" role="tab" aria-selected="${readerTab===id}" aria-controls="reader-body" data-action="reader-tab" data-id="${id}">${esc(name)}</button>`).join('');
}
function setReaderBody(html){timelineHandle?.unmount();timelineHandle=null;$('#reader-body').innerHTML=html;}
function restoreReaderPosition(position) {
  const body=$('#reader-body');
  const anchor=position.anchor?body.querySelector(`#${CSS.escape(position.anchor)}`):position.marker?body.querySelector(`[data-reader-anchor="${CSS.escape(position.marker)}"]`):null;
  body.scrollTop=anchor?anchor.offsetTop+(position.offset||0):(position.top||0);readerPosition();
}
async function mountReaderTimeline(position) {
  const container=$('#lesson-timeline');if(!container)return;
  try{
    const {mountLessonTimeline}=await import('/lesson-timeline.js');
    if(!container.isConnected||readerTab!=='analysis'||!$('#reader').open)return;
    timelineHandle=mountLessonTimeline(container,currentLesson.analysis.sections,currentLesson.analysis.evidence);
    restoreReaderPosition(position);
  }catch{if(container.isConnected)container.innerHTML='<p class="notice error">时间轴组件未能加载，请刷新页面后重试。</p>';}
}
function openReader(tab) {
  const dialog=$('#reader');
  if(dialog.open)rememberReader();else readerReturnFocus=document.activeElement;
  const saved=readMemory(memoryKey());
  readerMemoryKey=memoryKey();readerLesson=currentLesson.lesson.id;readerTab=tab||saved.tab||'analysis';
  const position=saved[readerTab]||{};readerSearch=position.search||'';readerRange=position.range||null;
  $('#reader-course').textContent=currentCourse.course.name;
  $('#reader-title').textContent=currentLesson.lesson.title;
  $('#reader-tabs').innerHTML=readerTabs();
  setReaderBody(readerTab==='analysis'?analysisView():readerTab==='transcript'?transcriptView():readerTab==='slides'?slidesView():qualityView());
  if(readerTab==='transcript'&&$('#transcript-search')){
    $('#transcript-search').value=readerSearch;
    $('#transcript-rows').innerHTML=transcriptRows(visibleTranscript());
  }
  if(!dialog.open){dialog.showModal();document.body.classList.add('reader-open');}
  const body=$('#reader-body');
  body.querySelectorAll('.finding,.slide-grid figure').forEach((el,i)=>el.dataset.readerAnchor=String(i));
  restoreReaderPosition(position);void mountReaderTimeline(position);
}
function closeReader(){if(!$('#reader').open)return;rememberReader();timelineHandle?.unmount();timelineHandle=null;$('#reader').close();document.body.classList.remove('reader-open');readerLesson='';if(readerReturnFocus?.isConnected)readerReturnFocus.focus({preventScroll:true});}
let progressUnmounts=[];
function renderJobs() {
  progressUnmounts.forEach(unmount=>unmount());progressUnmounts=[];
  const active=state.jobs.filter(x=>['queued','running'].includes(x.status));
  const latest=state.jobs.find(j=>!['queued','running'].includes(j.status));
  const recentSuccess=latest?.status==='completed'&&Date.now()-new Date(latest.finished_at).getTime()<5000;
  const job=active.find(j=>j.status==='running')||active[0]||(['failed','needs_login','interrupted'].includes(latest?.status)||recentSuccess?latest:null);
  if(!job){$('#job-strip').innerHTML='';$('#reader-job').innerHTML='';return;}
  const running=['queued','running'].includes(job.status), p=job.progress;
  const lesson=currentLesson?.lesson.id===job.target?currentLesson.lesson:null;
  const course=state.courses.find(c=>c.id===job.target||c.id===lesson?.course_id);
  const labels={'slides-pdf':'导出去重 PDF',schedule:'更新课表',analyze:'课堂分析',materials:'获取材料',sync:'同步课程',course:'同步课次',automation:'课程自动化'};
  $('#job-strip').innerHTML=`<div class="job-bar ${running?'':recentSuccess?'job-completed':'job-failed'}"><div class="job-detail"><strong>${running?'<span class="spinner" aria-hidden="true"></span>':''}${esc(labels[job.kind]||'学习任务')}${job.course_name||course?' · '+esc(job.course_name||course.name):''}</strong><span>${esc(job.message)}</span></div><div class="job-progress">${running?'<div data-task-progress></div>':recentSuccess?'<span>已完成</span>':`<button class="small" data-action="retry-job" data-id="${esc(job.id)}">重试</button>`}<a href="#/jobs">查看任务${active.length>1?' · '+active.length+' 项':''}</a></div></div>`;
  $('#reader-job').innerHTML=$('#job-strip').innerHTML;
  document.querySelectorAll('[data-task-progress]').forEach(el=>progressUnmounts.push(mountTaskProgress(el,p?.completed??null,p?.total??null,p?.unit||'段')));
}
function renderShell() {
  const r = route();
  document.querySelectorAll('[data-nav]').forEach(a=>{const active=a.dataset.nav===(r.page==='course'?'courses':r.page);a.classList.toggle('active',active);if(active)a.setAttribute('aria-current','page');else a.removeAttribute('aria-current');});
  const title = {home:'开始学习',courses:'本学期课程',tasks:'作业与事项',automations:'课程自动化',settings:'模型与设置',jobs:'任务状态',course:currentCourse?.course?.name || '课程学习'}[r.page] || '开始学习';
  $('#breadcrumb').textContent = title;
  const a = state.auth;
  $('#connection').innerHTML = button(a.state==='waiting'?'登录中…':a.logged_in?'断开连接':'SSO 登录',a.logged_in?'logout':'login','','',a.state==='waiting'?'disabled':'');

  const recent=JSON.parse(localStorage.getItem('buaa-recent-courses')||'[]').map(id=>state.courses.find(c=>c.id===id)).filter(Boolean).slice(0,5);
  navigation.update({page:r.page==='course'?'courses':r.page,name:a.profile.name||'个人学习空间',recent,count:state.tasks.filter(x=>!x.done).length});
  document.body.classList.toggle('home-page',r.page==='home');

  renderJobs();
}
function courseOptions() {return state.courses.map(c=>({value:c.id,label:c.name,description:`${c.teachers||c.code} · ${c.recording_count||0} 节回放`,search:c.code}));}
function renderMain(html) {
  automationHandle?.unmount();automationHandle=null;
  for(const handle of selectHandles.values())handle.unmount();
  selectHandles.clear();
  $('#main').innerHTML=html;
  const mount=(target,props)=>{const el=$(target);if(el)selectHandles.set(props.id,mountCombobox(el,props));};
  mount('#home-course-select',{id:'home-course',name:'course',label:'学习课程',placeholder:'选择课程',options:courseOptions(),value:homeCourse,compact:true,onChange:value=>{homeCourse=value;}});
  const automationEl=$('#automation-course-list');
  if(automationEl){
    const owner=state.auth.profile.owner||'local';
    if(automationDraft?.owner!==owner)automationDraft=null;
    automationHandle=mountAutomations(automationEl,{owner,courses:state.courses,rules:state.automations,draft:automationDraft,
      onDraft:draft=>{automationDraft=draft;},
      onSave:async changes=>{
        const saved=[],errors=[];busy=true;
        try{for(const [id,choice] of Object.entries(changes)){
          try{const rule=await api(`/courses/${enc(id)}/automation`,{method:'PUT',body:JSON.stringify({...choice,interval_minutes:120})});saved.push(rule);state.automations=[...state.automations.filter(a=>a.course_id!==id),rule];}
          catch(e){errors.push({id,message:e.message});}
        }}finally{busy=false;}
        return {saved,errors};
      }});
  }
}
function homePage() {
  const hour=Number(new Intl.DateTimeFormat('en-US',{timeZone:'Asia/Shanghai',hour:'numeric',hourCycle:'h23'}).format(new Date()));
  const greeting=hour>=5&&hour<12?'早上好':hour<18&&hour>=12?'下午好':'晚上好';
  const suggestions=['梳理这节课的知识点与例子','老师留了哪些作业和重要事项？','帮我解释一个没听懂的概念'];
  return `<section class="learn-home" aria-labelledby="home-greeting"><h1 id="home-greeting">${greeting}，今天想学点什么？</h1>
    <form id="home-form" class="home-composer ${homeDraft.trim()?'has-question':''}"><label class="sr-only" for="home-question">学习问题</label><textarea id="home-question" name="question" maxlength="3000" rows="2" placeholder="选择一门课程，问问课堂里没听懂的内容…">${esc(homeDraft)}</textarea>
    <div class="composer-toolbar"><div class="composer-select">${icon('book-open')}<div id="home-course-select"></div></div>
    <div class="composer-tools"><a href="#/courses" class="icon-button" aria-label="查看全部课程" title="查看全部课程">${icon('library')}</a><a href="#/automations" class="icon-button" aria-label="课程自动化" title="课程自动化">${icon('timer')}</a><button class="send-button" type="submit" aria-label="发送问题" title="发送问题">${icon('arrow-up')}</button></div></div></form>
    <div class="actions spaced"><button class="quiet" data-action="choose-lesson">${icon('book-open')}选择一节课学习${icon('arrow-up-right')}</button></div>
    <div class="learning-suggestions" aria-label="问题建议">${suggestions.map(s=>`<button class="suggestion" data-action="suggestion" data-id="${esc(s)}"><span>${esc(s)}</span>${icon('arrow-up-right')}</button>`).join('')}</div>
    ${!state.courses.length?`<div class="home-connect"><span>${state.auth.state==='waiting'?'请在学校认证窗口中完成登录。':'先连接你的北航课程，建立个人学习空间。'}</span>${button(state.auth.logged_in?'同步课程':'连接北航课程',state.auth.logged_in?'sync':'login')}</div>`:''}</section>`;
}
async function refresh(render=true) {
  state = await api('/state');
  renderShell();
  if (render) await renderRoute();
}
function coursesPage() {
  const semester = state.semester.id?.replace(/^(\d{4})-\d{4}([12])$/,(_,y,s)=>`${y}${s==='1'?' 秋季':' 春季'}`);
  if (!state.courses.length) return `<div class="page-heading"><div><h1>本学期课程</h1><p>连接学校，获取你的课程与课表。</p></div></div><div class="empty"><div class="empty-icon">${icon('graduation-cap')}</div><h2>${state.auth.logged_in?'已登录，开始同步课程':'连接你的北航课程'}</h2><p>通过学校统一身份认证获取本学期课程和课表。密码仅在学校页面输入，学习资料保存在本地。</p><div class="actions">${button(state.auth.logged_in?'同步本学期课程':'使用北航 SSO 登录',state.auth.logged_in?'sync':'login','',true,state.auth.state==='waiting'?'disabled':'')}</div></div>`;
  return `<div class="page-heading"><div><h1>本学期课程<span class="semester">${esc(semester)}</span></h1><p>${state.courses.length} 门课程</p></div><div class="actions">${button('同步本学期课程','sync')}</div></div>
    ${!state.auth.logged_in?'<div class="notice">当前显示本地缓存。登录学校后可获取新课次和课堂材料。</div>':''}
    <div class="dashboard"><section class="panel"><div class="panel-head"><h2>我的课程 <span class="small-text muted">回放数量 ↓</span></h2></div><div class="table-wrap"><table><thead><tr><th>课程</th><th>授课教师</th><th>课堂材料</th><th></th></tr></thead><tbody id="course-rows">${courseRows(state.courses)}</tbody></table></div></section><aside class="stack"><section class="panel"><div class="panel-head"><h2>本周课表</h2><span class="small-text muted">${state.schedule.rows.length} 次</span></div><div class="panel-body">${scheduleView()}</div></section><section class="panel"><div class="panel-body"><p class="small-text muted">上次同步<br>${esc(datetime(state.semester.synced_at))}</p></div></section></aside></div>`;
}
function courseRows(rows) {
  return [...rows].sort((a,b)=>(b.recording_count||0)-(a.recording_count||0)||a.code.localeCompare(b.code)).map(c=>`<tr><td><a class="course-link" href="${courseHref(c.id)}">${esc(c.name)}</a><div class="course-code">${esc(c.code)}</div></td><td class="teacher">${esc(c.teachers)}</td><td>${c.sync_error?badge('同步需重试','amber'):c.checked_at?badge(`${c.recording_count||0} 节回放`,c.recording_count?'blue':''):badge('尚未同步课次')}</td><td><a class="button small" href="${courseHref(c.id)}">进入课程</a></td></tr>`).join('') || '<tr><td colspan="4" class="muted">没有匹配的课程。</td></tr>';
}
function scheduleView() {
  const rows = [...state.schedule.rows].sort((a,b)=>(a.date+a.time).localeCompare(b.date+b.time));
  let previous = '';
  return rows.map(x=>{const day=x.date!==previous?`<div class="schedule-day">${esc(date(x.date+'T12:00:00+08:00'))}</div>`:'';previous=x.date;const c=state.courses.find(c=>c.id===x.course_id);const content=`<strong>${esc(x.name)}</strong><span>${esc(x.time)}</span><div class="muted">${esc(x.room||'地点未提供')}</div>`;return `${day}${c?`<a class="schedule-item schedule-course-link" href="${courseHref(c.id)}" aria-label="进入${esc(c.name)}课程">${content}</a>`:`<div class="schedule-item">${content}</div>`}`;}).join('') || '<p class="muted">本周暂无排课记录。</p>';
}
function coursePage() {
  const {course:c}=currentCourse;
  const lessons=[...currentCourse.lessons].sort((a,b)=>Number(b.has_recording)-Number(a.has_recording)||(a.has_recording?b.date.localeCompare(a.date):a.date.localeCompare(b.date)));
  return `<a class="back-link" href="#/courses">返回本学期课程</a><div class="page-heading"><div><h1>${esc(c.name)}</h1><p>${esc(c.teachers)} · ${esc(c.code)}</p></div><div class="actions">${button('同步课次','course-sync',c.id)}<a class="button" href="#/automations">设置自动化</a></div></div>
    ${c.sync_error?`<div class="notice amber">${esc(c.sync_error)}</div>`:''}
    <div class="tabs spaced"><button class="tab ${courseTab==='lessons'?'active':''}" data-action="course-tab" data-id="lessons">课堂学习</button><button class="tab ${courseTab==='chat'?'active':''}" data-action="course-tab" data-id="chat">课程问答</button></div>
    <div class="spaced">${courseTab==='chat'?chatView():`<div class="course-layout"><section class="panel"><div class="panel-head"><h3>课程目录</h3><span class="small-text muted">${lessons.length} 节</span></div><div class="lesson-list">${lessons.map(l=>`<a class="lesson-item ${currentLesson?.lesson.id===l.id?'active':''}" href="${lessonHref(c.id,l.id)}"><strong>${esc(l.title)}</strong><p>${esc(date(l.date))}${l.duration?' · '+clock(l.duration):''}</p>${l.analysis_at?badge('已分析','green'):l.materials_at?badge('材料已获取','blue'):badge(l.has_recording?'回放可用':'尚无回放',l.has_recording?'blue':'')}</a>`).join('')||'<div class="panel-body"><p class="muted small-text">先同步课程目录，查看可学习的课次。</p></div>'}</div></section><section>${currentLesson?lessonView():empty(lessons.length?'选择一节课开始':'还没有同步课堂目录',lessons.length?'从左侧选择一节有回放的课，阅读原始材料或生成学习笔记。':'同步后会显示已录制与尚未开始的课次。',button('同步课次','course-sync',c.id,true))}</section></div>`}</div>`;
}
function findingRows(rows, kind) {
  if (!rows.length) return '<p class="muted small-text">本次材料未提取到此类内容；识别文本可能不完整。</p>';
  return rows.map(x=>{const sources=(x.evidence_ids||[]).map(id=>({id,...currentLesson.analysis.evidence[id]})).filter(s=>Number.isFinite(s.start)).sort((a,b)=>a.start-b.start);const range=x.source_range||(sources.length?{start_id:sources[0].id,end_id:sources.at(-1).id,start:sources[0].start,end:Math.max(...sources.map(s=>s.end))}:null);return `<div class="finding"><p>${esc(x.text)}</p>${kind==='assignments'?`<p class="muted small-text">截止：${esc(x.deadline)} · 对象：${esc(x.audience)}<br>提交：${esc(x.submission)} ${badge('待核验','amber')}</p>`:''}${range?`<div class="evidence-links"><button class="evidence" data-action="source-range" data-id="${esc(range.start_id+'|'+range.end_id)}">${clock(range.start)}–${clock(range.end)} 查看原话</button></div>`:''}</div>`;}).join('');
}
function timelineView() {
  return '<h3>课堂时间轴</h3><div id="lesson-timeline"></div>';
}
function analysisView() {
  const a=currentLesson.analysis;
  if (!a) return empty('尚未生成课堂时间轴','',button('开始分析','analyze',currentLesson.lesson.id,true));
  if (!a.timeline_version) return empty('这份笔记尚无课堂时间轴','',button('重新分析','analyze',currentLesson.lesson.id,true));
  return `${a.timeline_status==='partial'?'<p class="notice amber">部分时间段尚未整理完成。</p>':''}${timelineView()}`;
}
function transcriptView() {
  const m=currentLesson.materials;
  if (!m&&!currentLesson.analysis?.evidence) return materialEmpty();
  const range=readerRange?transcriptRange():[];
  return `<div class="inline-heading"><h3>原始识别文本</h3><input id="transcript-search" class="search" value="${esc(readerSearch)}" placeholder="查找课堂原话" aria-label="查找课堂原话"></div>${readerRange?`<div class="transcript-range notice"><span>当前原文范围：${range.length?clock(range[0].start)+'–'+clock(Math.max(...range.map(s=>s.end))):'来源暂不可用'}</span>${button('显示全部文本','clear-source-range')}</div>`:''}<div id="transcript-rows">${transcriptRows(visibleTranscript())}</div>`;
}
function transcriptRange() {
  const rows=readerRange&&currentLesson.analysis?.evidence?Object.values(currentLesson.analysis.evidence):currentLesson.materials?.segments||Object.values(currentLesson.analysis?.evidence||{});
  if(!readerRange)return rows;
  const first=rows.findIndex(s=>s.id===readerRange.start_id), last=rows.findIndex(s=>s.id===readerRange.end_id);
  return first>=0&&last>=first?rows.slice(first,last+1):[];
}
function visibleTranscript(){return transcriptRange().filter(s=>s.text.toLowerCase().includes(readerSearch.toLowerCase()));}
function openSourceRange(start,end=start) {
  openReader('transcript');readerSearch='';readerRange={start_id:start,end_id:end};
  setReaderBody(transcriptView());$('#reader-body').scrollTop=0;
  const el=$('#reader-body .segment');if(el){el.setAttribute('tabindex','-1');el.focus({preventScroll:true});}
  rememberReader();readerPosition();
}
function transcriptRows(rows) {return rows.map(s=>`<div class="segment" id="segment-${esc(s.id)}"><time>${clock(s.start)}</time><p>${esc(s.text)}</p></div>`).join('') || '<p class="muted">这节课暂时没有可用的识别文本。</p>';}
function materialEmpty() {return empty('先获取课堂材料','获取带时间戳的识别文本和 PPT 截图索引，不下载完整视频。',button('获取材料','materials',currentLesson.lesson.id,true));}
function slidesView() {
  const m=currentLesson.materials;
  if(!m)return materialEmpty();
  const exported=currentLesson.slides_pdf, id=currentLesson.lesson.id;
  const pending=state.jobs.some(j=>j.kind==='slides-pdf'&&j.target===id&&['queued','running'].includes(j.status));
  return `<div class="inline-heading"><div><h3>PPT 截图</h3><p class="muted small-text">按截图内容去重，保留首次出现的课堂顺序。</p></div><div class="actions">${exported?`<a class="button primary" href="/api/lessons/${enc(id)}/slides/pdf" download>下载去重 PDF · ${exported.slide_count} 页</a>`:''}${button(pending?'正在导出…':exported?'重新导出':'导出去重 PDF','slides-pdf',id,!exported,!m.slides.length||pending?'disabled':'')}</div></div>
    ${exported?`<div class="notice ${exported.current?'':'amber'}">上次导出：${exported.original_count} 张截图 → ${exported.slide_count} 页，合并 ${exported.duplicate_count} 张重复画面。${exported.current?'':'课堂截图已更新，请重新导出。'}</div>`:''}
    <div class="slide-grid">${m.slides.map(s=>`<figure><a href="${esc(s.url)}" target="_blank" rel="noreferrer"><img src="${esc(s.url)}" alt="${esc(currentLesson.lesson.title)} ${clock(s.start)} 的屏幕截图" loading="lazy" referrerpolicy="no-referrer"></a><figcaption>${clock(s.start)}</figcaption></figure>`).join('')||'<p class="muted">暂无截图记录。</p>'}</div>`;
}
function lessonOverview(a) {
  const assignments=a.assignments||[], notices=a.notices||[], reviews=a.review_suggestions||[];
  const column=(title,rows,body,emptyText)=>`<section class="lesson-action-column"><div class="inline-heading"><h3>${title}</h3><span class="small-text muted">${rows.length}</span></div><div class="lesson-action-body" tabindex="0" role="region" aria-label="${title}">${rows.length?body:`<p class="muted small-text">${emptyText}</p>`}</div></section>`;
  return `<div class="lesson-action-grid">${column('课后作业',assignments,findingRows(assignments,'assignments'),'本次未提取到明确作业。')}${column('重要事项',notices,findingRows(notices,'notices'),'本次未提取到重要事项。')}${column('复习建议',reviews,reviews.map(x=>`<div class="finding"><p>${esc(x)}</p></div>`).join(''),'本次未生成复习建议。')}</div>`;
}
function lessonView() {
  const {lesson:l,materials:m,analysis:a}=currentLesson;
  return `<div class="panel lesson-overview"><div class="lesson-heading"><div class="overview-heading"><div><div class="overview-status">${esc(date(l.date))} · ${esc(l.teachers)}${l.duration?' · '+clock(l.duration):''}</div><h2>${esc(l.title)}</h2></div>${a?button('继续学习','open-reader','',true):button('分析这节课','analyze',l.id,true,!l.has_recording?'disabled':'')}</div><div class="overview-tools">${a?button('本节课内容','open-reader','analysis'):''}${button(`课堂原文${m?' · '+m.segments.length:''}`,'open-reader','transcript')}${button(`PPT${m?' · '+m.slides.length+' 张':''}`,'open-reader','slides')}<a class="button" href="${esc(l.source_url)}" target="_blank" rel="noreferrer">原始回放</a>${a?`<a class="button" href="/api/lessons/${enc(l.id)}/export" download>导出笔记</a>`:''}<details class="overview-operations"><summary>更多操作</summary><div class="actions">${button(m?'刷新材料':'获取材料','materials',l.id,false,!l.has_recording?'disabled':'')}${a?button('重新分析','analyze',l.id):''}${button('材料情况','open-reader','quality')}</div></details></div></div>${a?lessonOverview(a):`<div class="overview-empty"><h3>${l.has_recording?'准备开始这节课':'课堂回放尚未发布'}</h3><p>${m?`已获取 ${m.segments.length} 段课堂原文和 ${m.slides.length} 张截图，分析后将在这里展示课堂主题、作业和重要事项。`:'回放材料就绪后，可以生成本节课的学习笔记。'}</p>${m?button('阅读课堂原文','open-reader','transcript'):''}</div>`}</div>`;
}
function qualityView() {
  const m=currentLesson.materials;
  if(!m)return materialEmpty();
  return `<h3>材料覆盖情况</h3><p>识别文本 ${m.segments.length} 段，时间区间覆盖 ${m.coverage.ratio===null?'未知':Math.round(m.coverage.ratio*100)+'%'}，PPT 截图 ${m.slides.length} 条。</p><div class="notice amber spaced">${esc(m.coverage.note)}自动分析尚未对缺失片段重新转写，也未核听原声。</div><h3>超过45秒的文本空白</h3>${m.coverage.gaps.map(g=>`<div class="finding small-text">${clock(g.start)}–${clock(g.end)}<span class="muted"> · ${(g.end-g.start).toFixed(0)} 秒</span></div>`).join('')||'<p class="muted">未发现超过45秒的空白。</p>'}<h3>原始视频</h3>${m.videos.map(v=>`<p class="small-text">${esc(v.type)}录像 · ${clock(v.duration)}</p>`).join('')}<p class="muted small-text spaced">材料获取于 ${esc(datetime(m.fetched_at))}。录像请通过“打开原始回放”查看。</p>`;
}
function chatView() {
  return `<section class="panel"><div class="panel-head"><h2>围绕这门课学习</h2>${badge('回答附课堂来源','blue')}</div><div class="panel-body"><div class="notice">回答基于已获取的课堂原文。</div><form id="chat-form" class="chat-form"><label for="question">你的问题</label><textarea id="question" name="question" required minlength="2" maxlength="3000" placeholder="你想了解这门课的什么内容？"></textarea><div class="actions"><button class="primary" ${asking?'disabled':''}>${asking?'正在查找课堂材料…':'提问'}</button></div></form><div id="chat-history">${currentCourse.chat.map(chatMessage).join('')}</div></div></section>`;
}
function chatMessage(c) {return `<article class="chat-message"><h3>${esc(c.question)}</h3><div class="answer">${esc(c.answer)}</div>${c.sources.map(s=>`<details class="source-quote"><summary>[${esc(s.id)}] ${esc(s.title)} · ${clock(s.start)} 原文</summary><p>${esc(s.text)}</p><a href="${lessonHref(currentCourse.course.id,s.lesson_id)}">查看这节课</a></details>`).join('')}</article>`;}
function tasksPage() {
  const tasks=[...state.tasks].sort((a,b)=>Number(a.done)-Number(b.done));
  const notices=state.notices||[];
  return `<div class="page-heading"><div><h1>作业与事项</h1></div></div><div class="notice amber">作业由 AI 提取，请核对课堂原话。</div><section class="panel"><div class="panel-head"><h2>课堂作业</h2><span class="muted small-text">${tasks.filter(x=>!x.done).length} 项未完成</span></div>${tasks.map(t=>`<label class="task-item ${t.done?'done':''}"><input type="checkbox" data-task="${esc(t.id)}" ${t.done?'checked':''} aria-label="标记作业完成"><div><div class="task-text">${esc(t.text)}</div><div class="task-meta">${esc(t.course_name)} · ${esc(t.lesson_title)}<br>截止：${esc(t.deadline)} · 提交：${esc(t.submission)}</div><div class="actions spaced">${badge('待核验','amber')}<a href="${lessonHref(t.course_id,t.lesson_id)}">查看课堂来源</a></div></div></label>`).join('')||'<div class="panel-body"><p class="muted">还没有提取到作业。先选择一节课进行分析；没有结果不代表老师没有布置作业。</p></div>'}</section><section class="panel spaced"><div class="panel-head"><h2>重要事项</h2><span class="muted small-text">${notices.length} 条</span></div>${notices.map(n=>`<div class="panel-body"><p>${esc(n.text)}</p><div class="task-meta">${esc(n.course_name)} · ${esc(n.lesson_title)}</div><a class="small-text" href="${lessonHref(n.course_id,n.lesson_id)}">查看课堂原话</a></div>`).join('')||'<div class="panel-body muted">当前尚未从已分析的课堂中提取到重要事项。</div>'}</section>`;
}
function automationsPage() {
  return `<div class="automation-page"><div class="page-heading"><div><h1>课程自动化</h1></div><div class="actions">${button('同步课表','schedule-sync')}</div></div><div class="automation-page-intro"><p>上课次日起，每 2 小时检查新回放。</p><details><summary>运行说明</summary><div>保持本地服务运行和学校登录有效。首次开启从昨天的课次开始，较早的课堂可手动分析；没有后续课表时等待同步。<br><a href="#/jobs">查看任务状态</a></div></details></div>${!state.auth.logged_in?'<div class="notice amber">学校尚未登录。可以先保存设置，登录后自动获取新材料。</div>':''}<div id="automation-course-list"></div></div>`;
}
function jobResultLink(job) {
  if(job.status!=='completed')return '';
  const link=(href,label)=>` <a class="button quiet small" href="${esc(href)}">${label}</a>`;
  if(job.kind==='slides-pdf')return link(`/api/lessons/${enc(job.target)}/slides/pdf`,'下载 PDF');
  if(['analyze','materials'].includes(job.kind))return ` <button class="quiet small" data-action="job-result" data-id="${esc(job.id)}">${job.kind==='analyze'?'查看笔记':'查看材料'}</button>`;
  if(['course','automation'].includes(job.kind))return link(courseHref(job.target),'查看课程');
  if(['sync','schedule'].includes(job.kind))return link('#/courses',job.kind==='schedule'?'查看课表':'查看课程');
  return '';
}
function jobsView() {
  const labels={'slides-pdf':'导出去重 PDF',schedule:'更新课表',sync:'同步学期课程',course:'同步课次',materials:'获取课堂材料',analyze:'分析课堂',automation:'课程自动化'};
  const statuses={queued:'等待中',running:'处理中',completed:'已完成',failed:'失败',needs_login:'需要登录',interrupted:'已中断'};
  return state.jobs.map(j=>`<div class="job-item"><strong class="job-name">${esc(labels[j.kind]||j.kind)}</strong><p class="job-message">${esc(j.message)} · ${esc(datetime(j.created_at))}</p><div class="job-status">${badge(statuses[j.status]||j.status,j.status==='completed'?'green':['failed','needs_login','interrupted'].includes(j.status)?'amber':'blue')}${jobResultLink(j)}${['failed','needs_login','interrupted'].includes(j.status)?` <button class="quiet small" data-action="retry-job" data-id="${esc(j.id)}">重试</button>`:''}</div></div>`).join('')||'<div class="panel-body muted">暂无任务。</div>';
}
function jobsPage() {
  return `<div class="page-heading"><div><h1>任务状态</h1></div></div><section class="panel" aria-label="任务记录">${jobsView()}</section>`;
}
function settingsPage() {
  return `<div class="page-heading"><div><h1>模型与设置</h1></div></div><div class="settings-columns"><section class="panel"><div class="panel-head"><h2>学习模型</h2>${badge(state.model.ready?(state.model.has_key?'已配置':'服务已填写'):'待配置',state.model.ready?(state.model.has_key?'green':'blue'):'')}</div><div class="panel-body"><form id="model-form" class="form-grid"><label>服务地址<input type="url" name="base_url" value="${esc(state.model.base_url)}" placeholder="https://你的模型服务地址/v1" required><small>兼容 Chat Completions 接口；本地模型可使用 localhost HTTP。</small></label><label>模型名称<input name="model" value="${esc(state.model.model)}" placeholder="服务商提供的模型 ID" required></label><label>API Key<input type="password" name="api_key" autocomplete="off" placeholder="${state.model.has_key?'已配置，留空保留现有密钥':'本地无鉴权模型可留空'}"><small>密钥只保存在当前进程中，重启后重新填写，或通过环境变量配置。</small></label><div class="notice">分析和问答会把相关课堂文本发送到你配置的服务。PPT 截图当前仅供本地页面查看，不发送给模型。</div><div><button class="primary">保存配置</button></div></form></div></section><aside class="panel"><div class="panel-body"><h3>本地运行</h3><ul><li>学校密码只输入在学校 SSO 页面。</li><li>学校凭据只留在进程内，关闭服务后需要重新登录。</li><li>课程、材料、笔记保存在本机 SQLite 数据库。</li><li>当前为个人使用的本地 Web，不开放公网访问。</li></ul></div></aside></div>`;
}
async function renderRoute() {
  const version=++routeVersion, r=route();
  const wasOpen=$('#reader').open, previousLesson=readerLesson;
  if(wasOpen)rememberReader();
  try {
    if(r.page==='course'&&r.course) {
      const course=await api(`/courses/${enc(r.course)}`);
      if(version!==routeVersion)return;
      const last=readMemory(`buaa-last-lesson:${state.auth.profile.owner||'local'}:${r.course}`, '');
      const selected=r.lesson||(course.lessons.some(l=>l.id===last)?last:null);
      const lesson=selected?await api(`/lessons/${enc(selected)}`):null;
      if(version!==routeVersion)return;
      currentCourse=course;currentLesson=lesson;
      if(currentLesson&&currentLesson.lesson.course_id!==r.course)throw new Error('课次不属于当前课程');
      if(currentLesson)localStorage.setItem(`buaa-last-lesson:${state.auth.profile.owner||'local'}:${r.course}`,JSON.stringify(currentLesson.lesson.id));
      if(pendingChatCourse===r.course){courseTab='chat';pendingChatCourse='';}
      const recent=JSON.parse(localStorage.getItem('buaa-recent-courses')||'[]');
      localStorage.setItem('buaa-recent-courses',JSON.stringify([r.course,...recent.filter(id=>id!==r.course)].slice(0,5)));
      renderMain(coursePage());
    } else renderMain(r.page==='tasks'?tasksPage():r.page==='automations'?automationsPage():r.page==='settings'?settingsPage():r.page==='jobs'?jobsPage():r.page==='courses'?coursesPage():homePage());
    renderShell();
    if(pendingJobResult&&r.page==='course'&&currentLesson?.lesson.id===pendingJobResult.lesson){const tab=pendingJobResult.tab;pendingJobResult=null;openReader(tab);}
    if(wasOpen){if(currentLesson?.lesson.id===previousLesson&&r.page==='course')openReader(readerTab);else closeReader();}
  } catch(e) {renderMain(empty('暂时无法打开',e.message,'<a class="button" href="#/courses">返回课程</a>'));}
}
async function action(name,id) {

  if(name==='choose-lesson'){location.hash=homeCourse?courseHref(homeCourse):'#/courses';return;}
  if(name==='suggestion'){homeDraft=id;$('#home-question').value=id;$('#home-form').classList.add('has-question');$('#home-question').focus();return;}
  if(name==='login'){await api('/auth/login',{method:'POST'});toast('请在新开的 Chrome 窗口完成学校登录');await refresh(false);return;}
  if(name==='logout'){await api('/auth/logout',{method:'POST'});await refresh();return;}
  if(name==='open-reader'){openReader(id);return;}
  if(name==='reader-tab'){openReader(id);return;}
  if(name==='close-reader'){closeReader();return;}
  if(name==='reader-top'){$('#reader-body').scrollTop=0;rememberReader();readerPosition();return;}
  if(name==='course-tab'){courseTab=id;renderMain(coursePage());return;}
  if(name==='evidence'){openSourceRange(id);return;}
  if(name==='source-range'){const [start,end]=id.split('|');openSourceRange(start,end);return;}
  if(name==='clear-source-range'){readerRange=null;readerSearch='';setReaderBody(transcriptView());$('#reader-body').scrollTop=0;$('#transcript-search').focus();rememberReader();readerPosition();return;}
  if(name==='job-result'){
    const job=state.jobs.find(j=>j.id===id);
    if(!job||job.status!=='completed'||!['analyze','materials'].includes(job.kind))return;
    const result=await api(`/lessons/${enc(job.target)}`);
    pendingJobResult={lesson:result.lesson.id,tab:job.kind==='analyze'?'analysis':'transcript'};
    const href=lessonHref(result.lesson.course_id,result.lesson.id);
    if(location.hash===href)await renderRoute();else location.hash=href;
    return;
  }
  if(name==='retry-job'){const j=state.jobs.find(x=>x.id===id);if(j.kind==='schedule'){await api('/schedule/sync',{method:'POST'});await refresh(false);return;}return action({'slides-pdf':'slides-pdf',sync:'sync',course:'course-sync',materials:'materials',analyze:'analyze',automation:'run-automation'}[j.kind],j.target);}
  const path=name==='slides-pdf'?`/lessons/${enc(id)}/slides/export`:name==='schedule-sync'?'/schedule/sync':name==='sync'?'/sync':name==='course-sync'?`/courses/${enc(id)}/sync`:name==='run-automation'?`/courses/${enc(id)}/automation/run`:`/lessons/${enc(id)}/${name==='analyze'?'analyze':'materials'}`;
  await api(path,{method:'POST'});toast('任务已加入队列，可在页面顶部查看进度');await refresh(false);
}
document.addEventListener('click', async event=>{
  if(event.target.closest('.skip')){event.preventDefault();$('#main').focus();return;}
  const element=event.target.closest('[data-action]');if(!element)return;
  const {action:name,id}=element.dataset;
  element.disabled=true;
  try{await action(name,id);}catch(e){toast(e.message);}finally{if(element.isConnected)element.disabled=false;}
});
document.addEventListener('input',event=>{
  if(event.target.id==='home-question'){homeDraft=event.target.value;$('#home-form').classList.toggle('has-question',homeDraft.trim().length>0);}
  if(event.target.id==='transcript-search'){readerSearch=event.target.value;$('#transcript-rows').innerHTML=transcriptRows(visibleTranscript());$('#reader-body').scrollTop=0;rememberReader();readerPosition();}
});
document.addEventListener('change',async event=>{
  if(event.target.dataset.task){try{await api(`/tasks/${enc(event.target.dataset.task)}`,{method:'PATCH',body:JSON.stringify({done:event.target.checked})});await refresh();}catch(e){event.target.checked=!event.target.checked;toast(e.message);}}
});
document.addEventListener('submit',async event=>{
  const form=event.target;if(!['model-form','chat-form','home-form'].includes(form.id))return;
  if(busy){event.preventDefault();return;}
  event.preventDefault();const data=new FormData(form);const submit=form.querySelector('button[type=submit],button:not([type])');if(submit)submit.disabled=true;
  busy=true;
  try{
    if(form.id==='home-form'){
      const courseId=data.get('course');homeDraft=String(data.get('question')||'');homeCourse=String(courseId||'');
      if(!state.courses.length){await action(state.auth.logged_in?'sync':'login');return;}
      if(!courseId){$('#home-course').focus();throw new Error('先选择一门课程，再开始学习');}
      if(homeDraft.trim().length<2){$('#home-question').focus();throw new Error('写下你想了解的问题，或点击下方的问题建议');}
      if(!state.model.ready){toast('请先在“模型与设置”中配置学习模型；你的问题已保留');location.hash='#/settings';return;}
      asking=true;form.setAttribute('aria-busy','true');if(submit){submit.disabled=true;submit.innerHTML='<span class="spinner" aria-hidden="true"></span>';submit.setAttribute('aria-label','正在查找课堂材料');}
      await api(`/courses/${enc(courseId)}/ask`,{method:'POST',body:JSON.stringify({question:homeDraft})});
      homeDraft='';pendingChatCourse=String(courseId);
      if(location.hash===courseHref(courseId))await renderRoute();else location.hash=courseHref(courseId);
    }
    if(form.id==='model-form'){await api('/model',{method:'PUT',body:JSON.stringify({base_url:data.get('base_url'),model:data.get('model'),api_key:data.get('api_key')||null})});toast('模型配置已保存');await refresh();}
    if(form.id==='chat-form'){asking=true;if(submit)submit.textContent='正在查找课堂材料…';const result=await api(`/courses/${enc(currentCourse.course.id)}/ask`,{method:'POST',body:JSON.stringify({question:data.get('question')})});currentCourse.chat.unshift(result);$('#chat-history').innerHTML=currentCourse.chat.map(chatMessage).join('');form.reset();}
  }catch(e){toast(e.message);}finally{busy=false;asking=false;form.removeAttribute('aria-busy');if(submit?.isConnected){submit.disabled=false;if(form.id==='chat-form')submit.textContent='提问';if(form.id==='home-form'){submit.innerHTML=icon('arrow-up');submit.setAttribute('aria-label','发送问题');}}}
});

window.addEventListener('hashchange',()=>{closeReader();courseTab='lessons';renderRoute();});
$('#reader').addEventListener('cancel',event=>{event.preventDefault();closeReader();});
$('#reader').addEventListener('click',event=>{if(event.target===$('#reader')){const b=$('#reader').getBoundingClientRect();if(event.clientX<b.left||event.clientX>b.right||event.clientY<b.top||event.clientY>b.bottom)closeReader();}});
$('#reader-body').addEventListener('scroll',()=>{readerPosition();clearTimeout(readerSaveTimer);readerSaveTimer=setTimeout(rememberReader,150);});
window.addEventListener('pagehide',rememberReader);
document.addEventListener('keydown',event=>{if(event.target.id==='home-question'&&event.key==='Enter'&&!event.shiftKey&&!event.isComposing){event.preventDefault();$('#home-form').requestSubmit();}});
async function poll() {
  try{
    const old=state, next=await api('/state');state=next;renderShell();
    if(next.auth.logged_in&&!old?.auth.logged_in){await api('/sync',{method:'POST'});toast('登录成功，正在同步本学期课程');}
    const signature=JSON.stringify([next.auth.state,next.courses,next.jobs.map(x=>[x.id,x.status]),next.tasks,next.automations]);
    const editing=['INPUT','TEXTAREA','SELECT'].includes(document.activeElement.tagName);
    if(signature!==refreshSignature&&!busy&&!editing&&!hasOpenCombobox()&&!(route().page==='automations'&&Object.keys(automationDraft?.changes||{}).length)){refreshSignature=signature;await renderRoute();}
  }catch{ /* Transient server restarts keep the current local view intact. */ }
}
try{await refresh();setInterval(poll,3000);}catch(e){renderMain(empty('本地服务未连接','请确认启动服务的终端仍在运行，然后刷新页面。'));}
