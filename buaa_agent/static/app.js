import {mountNavigation, mountTaskProgress} from "/navigation.js";
import {createArcControls, mountAutomations, mountCourseWorkspace, mountDesktopSettings} from "/course-workspace.js";
const $ = (selector) => document.querySelector(selector);
const esc = (value) => String(value ?? '').replace(/[&<>"']/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
const enc = encodeURIComponent;
const clock = (s) => {s = Math.max(0, Math.floor(s || 0)); return [Math.floor(s/3600),Math.floor(s/60)%60,s%60].map(x=>String(x).padStart(2,'0')).join(':');};
const date = (value) => value ? new Date(value).toLocaleDateString('zh-CN',{month:'numeric',day:'numeric',weekday:'short',timeZone:'Asia/Shanghai'}) : '';
const datetime = (value) => value ? new Date(value).toLocaleString('zh-CN',{timeZone:'Asia/Shanghai',hour12:false}) : '尚未同步';
let state, currentCourse, currentLesson, scheduledDate;
let busy = false, refreshSignature = '', toastTimer, routeVersion = 0;
let pendingJobResult = null;
let automationDraft = null, automationHandle = null;
let courseWorkspace, desktopSettingsHandle;
let timelineHandle = null;
const arc=createArcControls();
let connectionHTML, jobDisplayKey;
const navigation=mountNavigation($('#navigation-root'),$('#navigation-trigger'));
let readerLesson = '', readerTab = '', readerSearch = '', readerRange = null, readerSaveTimer, readerReturnFocus, readerReturnFocusVisible, readerMemoryKey;


function icon(name, extra='') {return `<img class="ui-icon ${extra}" src="/icons/${esc(name)}.svg" alt="" aria-hidden="true">`;}

async function api(path, options = {}) {
  let response;
  try {
    response = await fetch('/api' + path, {...options, headers: {'Content-Type':'application/json','X-BUAA-Local':'1',...options.headers}});
  } catch {
    throw new ApiError('无法连接本地服务，请重新打开 BUAA Learn 或重启本地服务后重试。', 0);
  }
  const data = await response.json().catch(() => null);
  if (!response.ok) {
    const fallback = response.status >= 500 ? '本地服务处理请求时出错' : '操作未完成';
    throw new ApiError(typeof data?.detail === 'string' ? data.detail : `${fallback}，请重试（HTTP ${response.status}）`, response.status);
  }
  if (!data) throw new ApiError('本地服务返回了无法读取的数据，请重试。', response.status);
  return data;
}
class ApiError extends Error {
  constructor(message, status) {
    super(message);
    this.status = status;
  }
}
function toast(message) {
  ($('#reader').open?$('#reader'):document.body).appendChild($('#toast'));
  $('#toast').textContent = message; $('#toast').hidden = false;
  clearTimeout(toastTimer); toastTimer = setTimeout(()=>$('#toast').hidden=true,6000);
}
function route() {
  const parts = location.hash.replace(/^#\/?/,'').split('/').map(decodeURIComponent);
  const page = ['courses','course','tasks','automations','settings','jobs'].includes(parts[0]) ? parts[0] : 'timetable';
  return {page,course:parts[1],lesson:parts[2]==='lesson'?parts[3]:null};
}
function arcSlot(kind, attributes, className='', tag='div') {
  const data=Object.entries(attributes).map(([name,value])=>` data-${name==='action'?'command':name}="${esc(value)}"`).join('');
  return `<${tag} class="${className}" data-arc="${kind}"${data}></${tag}>`;
}
function button(text, action, id='', primary=false, extra='') {
  return arcSlot('button',{label:text,action,id,variant:primary?'primary':'secondary',disabled:extra.includes('disabled')},'', 'span');
}
function smallAction(text, action, id='') {
  return arcSlot('button',{label:text,action,id,size:'sm',variant:'ghost'},'', 'span');
}
function badge(text,color='') {
  const tone={blue:'info',green:'success',amber:'warning',red:'danger'}[color]||'neutral';
  return arcSlot('badge',{label:text,tone},'', 'span');
}
function alert(title, description='', tone='warning') {
  return arcSlot('alert',{label:title,description,tone},'arc-alert');
}
function empty(title, text, actions='', icon='') {
  return `<div class="arc-empty-frame">${arcSlot('empty',{label:title,description:text,icon})}<div class="actions">${actions}</div></div>`;
}
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
  return [['analysis','学习笔记'],['transcript',`课堂文本${m?' '+m.segments.length:''}`],['slides',`PPT 截图${m?' '+m.slides.length:''}`],['quality','材料情况']];
}
function setReaderBody(html){timelineHandle?.unmount();timelineHandle=null;arc.replace($('#reader-body'),html);}
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
function openReader(tab, trigger, focusVisible) {
  const dialog=$('#reader');
  if(dialog.open)rememberReader();
  else {
    readerReturnFocus=trigger||document.activeElement;
    readerReturnFocusVisible=focusVisible??readerReturnFocus?.matches(':focus-visible');
  }
  const saved=readMemory(memoryKey());
  readerMemoryKey=memoryKey();readerLesson=currentLesson.lesson.id;readerTab=tab||saved.tab||'analysis';
  const position=saved[readerTab]||{};readerSearch=position.search||'';readerRange=position.range||null;
  $('#reader-course').textContent=currentCourse.course.name;
  $('#reader-title').textContent=currentLesson.lesson.title;
  arc.readerTabs($('#reader-tabs'),readerTabs(),readerTab,openReader);
  $('#reader-body').setAttribute('aria-labelledby',`reader-tab-${readerTab}`);
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
function closeReader() {
  const dialog=$('#reader');
  if(!dialog.open)return;
  rememberReader();
  $('#source-video')?.replaceChildren();
  timelineHandle?.unmount();
  timelineHandle=null;
  dialog.close();
  document.body.classList.remove('reader-open');
  let trigger=readerReturnFocus;
  if(!trigger?.isConnected&&currentLesson?.lesson.id===readerLesson&&trigger?.dataset.action){
    const {action,id}=trigger.dataset;
    trigger=[...$('#main').querySelectorAll('[data-action]')].find(el=>el.dataset.action===action&&el.dataset.id===id);
  }
  readerLesson='';
  trigger?.focus({preventScroll:true,focusVisible:readerReturnFocusVisible});
}
let progressUnmounts=[];
function renderJobs() {
  const active=state.jobs.filter(x=>['queued','running'].includes(x.status));
  const latest=state.jobs.find(j=>!['queued','running'].includes(j.status));
  const recentSuccess=latest?.status==='completed'&&Date.now()-new Date(latest.finished_at).getTime()<5000;
  const job=active.find(j=>j.status==='running')||active[0]||(['failed','needs_login','interrupted'].includes(latest?.status)||recentSuccess?latest:null);
  const displayKey=JSON.stringify([job,active.length,currentLesson?.lesson.id]);
  if(displayKey===jobDisplayKey)return;
  jobDisplayKey=displayKey;
  progressUnmounts.forEach(unmount=>unmount());progressUnmounts=[];
  if(!job){arc.replace($('#job-strip'),'');arc.replace($('#reader-job'),'');return;}
  const running=['queued','running'].includes(job.status), p=job.progress;
  const lesson=currentLesson?.lesson.id===job.target?currentLesson.lesson:null;
  const course=state.courses.find(c=>c.id===job.target||c.id===lesson?.course_id);
  const labels={'slides-pdf':'导出去重 PDF',schedule:'更新课表',timetable:'同步学期课表',analyze:'课堂分析',materials:'获取材料',sync:'同步课程',recordings:'同步最新回放',course:'同步课次',automation:'课程自动化'};
  const strip=`<div class="job-bar ${running?'':recentSuccess?'job-completed':'job-failed'}"><div class="job-detail"><strong>${running?'<span class="spinner" aria-hidden="true"></span>':''}${esc(labels[job.kind]||'学习任务')}${job.course_name||course?' · '+esc(job.course_name||course.name):''}</strong><span>${esc(job.message)}</span></div><div class="job-progress">${running?'<div data-task-progress></div>':recentSuccess?'<span>已完成</span>':smallAction('重试','retry-job',job.id)}<a href="#/jobs">查看任务${active.length>1?' · '+active.length+' 项':''}</a></div></div>`;
  arc.replace($('#job-strip'),strip);
  arc.replace($('#reader-job'),strip);
  document.querySelectorAll('[data-task-progress]').forEach(el=>progressUnmounts.push(mountTaskProgress(el,p?.completed??null,p?.total??null,p?.unit||'段')));
}
function renderShell() {
  const r = route();
  document.querySelectorAll('[data-nav]').forEach(a=>{const active=a.dataset.nav===(r.page==='course'?'courses':r.page);a.classList.toggle('active',active);if(active)a.setAttribute('aria-current','page');else a.removeAttribute('aria-current');});
  const title = {timetable:'课程表',courses:'本学期课程',tasks:'作业与事项',automations:'课程自动化',settings:'模型与设置',jobs:'任务状态',course:currentCourse?.course?.name || '课程学习'}[r.page] || '课程表';
  $('#breadcrumb').textContent = title;
  const a = state.auth;
  const nextConnection = button(a.state==='waiting'?'登录中…':a.logged_in?'断开连接':'SSO 登录',a.logged_in?'logout':'login','','',a.state==='waiting'?'disabled':'');
  if(nextConnection!==connectionHTML){arc.replace($('#connection'),nextConnection);connectionHTML=nextConnection;}

  const recent=JSON.parse(localStorage.getItem('buaa-recent-courses')||'[]').map(id=>state.courses.find(c=>c.id===id)).filter(Boolean).slice(0,5);
  navigation.update({page:r.page==='course'?'courses':r.page,name:a.profile.name||'个人学习空间',recent,count:state.tasks.filter(x=>!x.done).length});

  renderJobs();
}
function renderMain(html) {
  automationHandle?.unmount();automationHandle=null;
  courseWorkspace?.unmount();courseWorkspace=null;
  desktopSettingsHandle?.unmount();desktopSettingsHandle=null;
  arc.replace($('#main'),html);
  const settingsRoot=$('#desktop-settings');
  if(settingsRoot)desktopSettingsHandle=mountDesktopSettings(settingsRoot);
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
function renderCourseWorkspace(page) {
  if (!courseWorkspace) {
    renderMain('<div id="course-workspace"></div>');
    courseWorkspace=mountCourseWorkspace($('#course-workspace'));
  }
  const {auth, semester, courses, jobs, timetable} = state;
  const active = kind => jobs.some(job => job.kind===kind && ['queued','running'].includes(job.status));
  courseWorkspace.update({
    page, owner:auth.profile.owner||'', semester:semester.id||'', courses,
    cache:timetable||{rows:state.schedule?.rows||[]},
    loggedIn:auth.logged_in, waiting:auth.state==='waiting', loginMessage:auth.message,
    recordingAuthorized:auth.recording_authorized, syncing:active('timetable'),
    syncingCourses:active('sync')||active('recordings'),
    onAction: name => action(name).catch(error => toast(error.message)),
    onOpen: (id,date) => {scheduledDate={id,date};location.hash=courseHref(id);},
  });
}
async function refresh(render=true) {
  state = await api('/state');
  renderShell();
  if (render) await renderRoute();
}
function coursePage() {
  const {course:c}=currentCourse;
  const lessons=[...currentCourse.lessons].sort((a,b)=>Number(b.has_recording)-Number(a.has_recording)||(a.has_recording?b.date.localeCompare(a.date):a.date.localeCompare(b.date)));
  return `<a class="back-link" href="#/courses">返回本学期课程</a><div class="page-heading"><div><h1>${esc(c.name)}</h1><p>${esc(c.teachers)} · ${esc(c.code)}</p></div><div class="actions">${button('同步课次','course-sync',c.id)}<a class="button" href="#/automations">设置自动化</a></div></div>
    ${c.sync_error?alert('课次同步失败',c.sync_error):''}
    <div class="spaced"><div class="course-layout"><section class="panel"><div class="panel-head"><h3>课程目录</h3><span class="small-text muted">${lessons.length} 节</span></div><div class="lesson-list">${lessons.map(l=>`<a class="lesson-item ${currentLesson?.lesson.id===l.id?'active':''}" href="${lessonHref(c.id,l.id)}"><strong>${esc(l.title)}</strong><p>${esc(date(l.date))}${l.duration?' · '+clock(l.duration):''}</p>${l.analysis_at?badge('笔记已生成','green'):l.materials_at?badge('材料已就绪','blue'):badge(l.has_recording?'回放可用':'尚无回放',l.has_recording?'blue':'')}</a>`).join('')||'<div class="panel-body"><p class="muted small-text">先同步课程目录，查看可学习的课次。</p></div>'}</div></section><section>${currentLesson?lessonView():empty(lessons.length?'选择一节课开始':'还没有同步课堂目录',lessons.length?'从左侧选择一节有回放的课，阅读原始材料或生成学习笔记。':'同步后会显示已录制与尚未开始的课次。',button('同步课次','course-sync',c.id,true))}</section></div></div>`;
}
function timelineView() {
  return '<h3>课堂时间轴</h3><div id="lesson-timeline"></div>';
}
function analysisView() {
  const a=currentLesson.analysis;
  if (!a) return empty('尚未生成课堂时间轴','',button('开始分析','analyze',currentLesson.lesson.id,true));
  if (!a.timeline_version) return empty('这份笔记尚无课堂时间轴','',button('重新分析','analyze',currentLesson.lesson.id,true));
  return `${a.timeline_status==='partial'?alert('部分时间段尚未整理完成'):''}${timelineView()}`;
}
function transcriptDownload() {
  if(!currentLesson.materials?.segments?.length)return '';
  return `<a class="button" href="/api/lessons/${enc(currentLesson.lesson.id)}/transcript.txt" download>导出课堂文本</a>`;
}
function sourceVideo() {
  const start=transcriptRange()[0]?.start;
  if(!readerRange||!Number.isFinite(start)||start<0)return null;
  const videos=(currentLesson.materials?.videos||[]).filter(video=>{
    try {
      const url=new URL(video.url);
      return url.origin==='https://resource.msa.buaa.edu.cn'&&!url.username&&!url.password
        &&url.pathname.toLowerCase().endsWith('.mp4')&&(!video.duration||start<video.duration);
    } catch {return false;}
  });
  const video=videos.find(item=>item.type==='教师')||videos[0];
  return video?{url:video.url,start}:null;
}
function playSourceVideo() {
  const source=sourceVideo(), container=$('#source-video');
  if(!source||!container)return;
  const existing=container.querySelector('video');
  if(existing){
    if(existing.readyState>0)existing.currentTime=Math.min(source.start,existing.duration);
    void existing.play().catch(()=>{});
    return;
  }
  const video=document.createElement('video');
  video.controls=true;
  video.playsInline=true;
  video.setAttribute('aria-label','课堂原始回放');
  const url=new URL(source.url);
  url.hash=`t=${source.start}`;
  video.src=url.href;
  video.addEventListener('error',()=>{
    const message=document.createElement('p');
    message.className='notice error';
    message.setAttribute('role','alert');
    message.textContent='暂时无法播放这条回放。可以从课次详情打开学校原始回放。';
    container.replaceChildren(message);
  },{once:true});
  container.replaceChildren(video);
  void video.play().catch(()=>{});
}
function transcriptView() {
  const m=currentLesson.materials;
  if (!m&&!currentLesson.analysis?.evidence) return materialEmpty();
  const range=readerRange?transcriptRange():[];
  return `<div class="inline-heading"><h3>原始识别文本</h3>${arcSlot('input',{id:'transcript-search',label:'查找课堂原话',placeholder:'查找课堂原话',type:'search',value:readerSearch},'reader-arc-search')}${transcriptDownload()}</div>${readerRange?`<div class="transcript-range notice"><span>当前原文范围：${range.length?clock(range[0].start)+'–'+clock(Math.max(...range.map(s=>s.end))):'来源暂不可用'}</span><div class="actions">${sourceVideo()?button(`从 ${clock(range[0].start)} 播放回放`,'play-source-video') :''}${button('显示全部文本','clear-source-range')}</div></div><div id="source-video"></div>`:''}<div id="transcript-rows">${transcriptRows(visibleTranscript())}</div>`;
}
function transcriptRange() {
  const rows=readerRange&&currentLesson.analysis?.evidence?Object.values(currentLesson.analysis.evidence):currentLesson.materials?.segments||Object.values(currentLesson.analysis?.evidence||{});
  if(!readerRange)return rows;
  const first=rows.findIndex(s=>s.id===readerRange.start_id), last=rows.findIndex(s=>s.id===readerRange.end_id);
  return first>=0&&last>=first?rows.slice(first,last+1):[];
}
function visibleTranscript(){return transcriptRange().filter(s=>s.text.toLowerCase().includes(readerSearch.toLowerCase()));}
function openSourceRange(start,end=start,trigger,focusVisible) {
  openReader('transcript',trigger,focusVisible);readerSearch='';readerRange={start_id:start,end_id:end};
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
function lessonFindings(analysis) {
  const evidence=analysis.evidence||{};
  const renderGroup=(title,rows,emptyText,isAssignment=false)=>`<section class="lesson-findings-group">
    <h3>${title}</h3>
    ${rows.length?`<ul>${rows.map(row=>{
      const refs=(row.evidence_ids||[]).filter(id=>evidence[id]).sort((a,b)=>evidence[a].start-evidence[b].start);
      const range=row.source_range;
      const start=range?.start_id||refs[0],end=range?.end_id||refs.at(-1);
      const hasSource=evidence[start]&&evidence[end];
      const deadline=isAssignment&&row.deadline&&row.deadline!=='未说明'?row.deadline:null;
      return `<li><p>${esc(row.text)}</p><div class="lesson-finding-meta">
        ${hasSource?arcSlot('button',{label:'查看原话',action:'source-range',id:start+'|'+end,size:'sm'},'lesson-source-pill','span'):''}
        ${deadline?`<span>截止：${esc(deadline)}</span>`:''}
      </div></li>`;
    }).join('')}</ul>`:`<p class="muted">${emptyText}</p>`}
  </section>`;
  return `<div class="lesson-findings">
    ${renderGroup('本节作业',analysis.assignments||[],'未提取到明确作业。',true)}
    ${renderGroup('重要事项',analysis.notices||[],'未提取到重要事项。')}
  </div>`;
}
function lessonView() {
  const {lesson:l,materials:m,analysis:a}=currentLesson;
  const metadata=[date(l.date),l.teachers,l.duration?clock(l.duration):''].filter(Boolean);
  const job=state.jobs.find(j=>j.target===l.id&&['materials','analyze'].includes(j.kind)&&['queued','running'].includes(j.status));
  const hasText=Boolean(m?.segments?.length);
  const canReadText=hasText||Boolean(Object.keys(a?.evidence||{}).length);
  let primary='';
  if(a)primary=button('继续阅读','open-reader','',true);
  else if(job)primary=button(job.kind==='analyze'?'正在生成笔记…':'正在获取材料…',job.kind,l.id,true,'disabled');
  else if(hasText)primary=state.model.ready?button('生成笔记','analyze',l.id,true):'<a class="button primary" href="#/settings">配置学习模型</a>';
  else if(l.has_recording)primary=state.auth.logged_in?button(m?'刷新材料':'获取材料','materials',l.id,true):button(state.auth.state==='waiting'?'登录中…':'登录后获取材料','login','',true,state.auth.state==='waiting'?'disabled':'');
  const status=!a&&m&&!hasText?'暂无可用课堂文本。':!a&&!m&&!l.has_recording?'课堂回放尚未发布。':'';
  return `<div class="panel lesson-overview">
    <div class="lesson-heading">
      <div class="overview-heading">
        <div><h2>${esc(l.title)}</h2><div class="overview-status">${esc(metadata.join(' · '))}</div></div>
        ${primary?`<div class="lesson-primary">${primary}</div>`:''}
      </div>
      ${status?`<p class="lesson-availability">${status}</p>`:''}
      ${m||a||l.source_url?`<div class="overview-tools">
        ${canReadText?button('课堂原文','open-reader','transcript'):''}
        ${m?.slides?.length?button('PPT 截图','open-reader','slides'):''}
        ${l.source_url?`<a class="button" href="${esc(l.source_url)}" target="_blank" rel="noreferrer">原始回放</a>`:''}
        ${transcriptDownload()}
        ${a?`<a class="button" href="/api/lessons/${enc(l.id)}/export" download>导出笔记</a>`:''}
      </div>`:''}
    </div>
    ${a?lessonFindings(a):''}
  </div>`;
}
function qualityView() {
  const m=currentLesson.materials;
  if(!m)return materialEmpty();
  return `<h3>材料覆盖情况</h3><p>识别文本 ${m.segments.length} 段，时间区间覆盖 ${m.coverage.ratio===null?'未知':Math.round(m.coverage.ratio*100)+'%'}，PPT 截图 ${m.slides.length} 条。</p><div class="notice amber spaced">${esc(m.coverage.note)}自动分析尚未对缺失片段重新转写，也未核听原声。</div><h3>超过45秒的文本空白</h3>${m.coverage.gaps.map(g=>`<div class="finding small-text">${clock(g.start)}–${clock(g.end)}<span class="muted"> · ${(g.end-g.start).toFixed(0)} 秒</span></div>`).join('')||'<p class="muted">未发现超过45秒的空白。</p>'}<h3>原始视频</h3>${m.videos.map(v=>`<p class="small-text">${esc(v.type)}录像 · ${clock(v.duration)}</p>`).join('')}<p class="muted small-text spaced">材料获取于 ${esc(datetime(m.fetched_at))}。录像请通过“打开原始回放”查看。</p>`;
}
function tasksPage() {
  const tasks=[...state.tasks].sort((a,b)=>Number(a.done)-Number(b.done));
  const notices=state.notices||[];
  return `<div class="page-heading"><div><h1>作业与事项</h1></div></div>${alert('作业由 AI 提取','请核对课堂原话。')}<section class="panel"><div class="panel-head"><h2>课堂作业</h2><span class="muted small-text">${tasks.filter(x=>!x.done).length} 项未完成</span></div>${tasks.map(t=>`<label class="task-item ${t.done?'done':''}"><input type="checkbox" data-task="${esc(t.id)}" ${t.done?'checked':''} aria-label="标记作业完成"><div><div class="task-text">${esc(t.text)}</div><div class="task-meta">${esc(t.course_name)} · ${esc(t.lesson_title)}<br>截止：${esc(t.deadline)} · 提交：${esc(t.submission)}</div><div class="actions spaced"><a href="${lessonHref(t.course_id,t.lesson_id)}">查看课堂来源</a></div></div></label>`).join('')||empty('暂无已提取的作业','没有结果不代表老师没有布置作业。')}</section><section class="panel spaced"><div class="panel-head"><h2>重要事项</h2><span class="muted small-text">${notices.length} 条</span></div>${notices.map(n=>`<div class="panel-body arc-notice-row"><p>${esc(n.text)}</p><div class="task-meta">${esc(n.course_name)} · ${esc(n.lesson_title)}</div><a class="small-text" href="${lessonHref(n.course_id,n.lesson_id)}">查看课堂原话</a></div>`).join('')||empty('暂无已提取的重要事项','')}</section>`;
}
function automationsPage() {
  return `<div class="automation-page"><div class="page-heading"><div><h1>课程自动化</h1></div><div class="actions">${button('同步课表','schedule-sync')}</div></div><div class="automation-page-intro"><p>上课次日起，每 2 小时检查新回放。</p><details><summary>运行说明</summary><div>保持本地服务运行和学校登录有效。首次开启从昨天的课次开始，较早的课堂可手动分析；没有后续课表时等待同步。<br>自动生成笔记会将课堂原文发送给所配置的模型服务，可能产生调用费用。<br><a href="#/jobs">查看任务状态</a></div></details></div>${!state.auth.logged_in?alert('学校尚未登录','可以先保存设置，登录后自动获取新材料。'):''}<div id="automation-course-list"></div></div>`;
}
function jobResultLink(job) {
  if(job.status!=='completed')return '';
  const link=(href,label)=>` <a class="button quiet small" href="${esc(href)}">${label}</a>`;
  if(job.kind==='slides-pdf')return link(`/api/lessons/${enc(job.target)}/slides/pdf`,'下载 PDF');
  if(['analyze','materials'].includes(job.kind))return smallAction(job.kind==='analyze'?'查看笔记':'查看材料','job-result',job.id);
  if(['course','automation'].includes(job.kind))return link(courseHref(job.target),'查看课程');
  if(['schedule','timetable'].includes(job.kind))return link('#/timetable','查看课表');
  if(job.kind==='sync')return link('#/courses','查看课程');
  return '';
}
function jobsView() {
  const labels={'slides-pdf':'导出去重 PDF',schedule:'更新课表',timetable:'同步学期课表',sync:'同步学期课程',recordings:'同步最新回放',course:'同步课次',materials:'获取课堂材料',analyze:'分析课堂',automation:'课程自动化'};
  const statuses={queued:'等待中',running:'处理中',completed:'已完成',failed:'失败',needs_login:'需要登录',interrupted:'已中断'};
  return state.jobs.map(j=>`<div class="job-item"><strong class="job-name">${esc(labels[j.kind]||j.kind)}</strong><p class="job-message">${esc(j.message)} · ${esc(datetime(j.created_at))}</p><div class="job-status">${badge(statuses[j.status]||j.status,j.status==='completed'?'green':['failed','needs_login','interrupted'].includes(j.status)?'amber':'blue')}</div><div class="job-action">${jobResultLink(j)}${['failed','needs_login','interrupted'].includes(j.status)?smallAction('重试','retry-job',j.id):''}</div></div>`).join('')||empty('暂无任务','','','clock');
}
function jobsPage() {
  return `<div class="page-heading"><div><h1>任务状态</h1></div></div><section class="panel" aria-label="任务记录">${jobsView()}</section>`;
}
function settingsPage() {
  const {model}=state;
  return `<div class="page-heading"><h1>模型与设置</h1></div>
    <section class="panel arc-settings">
      <div class="panel-head"><h2>学习模型</h2>${badge(model.ready?(model.has_key?'已配置':'服务已填写'):'待配置',model.ready?(model.has_key?'green':'blue'):'')}</div>
      <div class="panel-body">
        <form id="model-form" class="form-grid">
          ${arcSlot('input',{label:'服务地址',name:'base_url',type:'url',value:model.base_url,placeholder:'https://你的模型服务地址/v1',required:true,description:'本地模型可使用 localhost HTTP。'})}
          ${arcSlot('input',{label:'模型名称',name:'model',value:model.model,placeholder:'服务商提供的模型 ID',required:true})}
          ${arcSlot('input',{label:'API Key',name:'api_key',type:'password',placeholder:model.key_state==='saved'?'已保存密钥':model.has_key?'已配置密钥':'本地无鉴权模型可留空',description:!model.can_save_key?'设置页输入的密钥仅在当前运行有效。':''})}
          ${model.key_state==='unavailable'?alert('无法读取已保存的密钥','请重新输入或移除。'):''}
          ${alert('模型服务会接收相关课堂文本','分析可能产生费用。PPT 截图仅供本地查看。','info')}
          <div class="actions">
            ${arcSlot('button',{label:'保存配置',type:'submit',variant:'primary'},'', 'span')}
            ${model.can_save_key&&(model.has_key||model.key_state==='unavailable')?arcSlot('button',{label:'移除当前密钥',type:'submit',name:'remove_key','skip-validation':true},'', 'span'):''}
          </div>
        </form>
      </div>
    </section>${window.buaaDesktop?'<div id="desktop-settings"></div>':''}`;
}
let timetableVisitOwner = null;
function refreshTimetableOnEntry(page) {
  if(page!=='timetable'||!state.auth.logged_in){
    timetableVisitOwner=null;
    return;
  }
  const owner=state.auth.profile.owner;
  if(!owner||timetableVisitOwner===owner)return;
  timetableVisitOwner=owner;
  if(state.jobs.some(job=>job.kind==='timetable'&&['queued','running'].includes(job.status)))return;
  void api('/timetable/sync',{method:'POST'}).catch(error=>{
    if(route().page==='timetable'&&state?.auth.profile.owner===owner)toast(error.message);
  });
}
async function renderRoute() {
  const version=++routeVersion, r=route();
  refreshTimetableOnEntry(r.page);
  const wasOpen=$('#reader').open, previousLesson=readerLesson;
  if(wasOpen)rememberReader();
  try {
    if(r.page==='course'&&r.course) {
      const course=await api(`/courses/${enc(r.course)}`);
      if(version!==routeVersion)return;
      const last=readMemory(`buaa-last-lesson:${state.auth.profile.owner||'local'}:${r.course}`, '');
      const clicked=scheduledDate?.id===r.course ? scheduledDate : null;
      scheduledDate=null;
      const dayFormat=new Intl.DateTimeFormat('en-CA',{timeZone:'Asia/Shanghai',year:'numeric',month:'2-digit',day:'2-digit'});
      const scheduled=clicked && course.lessons.find(l=>l.date && dayFormat.format(new Date(l.date))===clicked.date);
      const selected=r.lesson||scheduled?.id||(!clicked&&course.lessons.some(l=>l.id===last)?last:null);
      const lesson=selected?await api(`/lessons/${enc(selected)}`):null;
      if(version!==routeVersion)return;
      currentCourse=course;currentLesson=lesson;
      if(currentLesson&&currentLesson.lesson.course_id!==r.course)throw new Error('课次不属于当前课程');
      if(currentLesson)localStorage.setItem(`buaa-last-lesson:${state.auth.profile.owner||'local'}:${r.course}`,JSON.stringify(currentLesson.lesson.id));
      const recent=JSON.parse(localStorage.getItem('buaa-recent-courses')||'[]');
      localStorage.setItem('buaa-recent-courses',JSON.stringify([r.course,...recent.filter(id=>id!==r.course)].slice(0,5)));
      renderMain(coursePage());
    } else if(r.page==='timetable'||r.page==='courses') renderCourseWorkspace(r.page);
    else renderMain(r.page==='tasks'?tasksPage():r.page==='automations'?automationsPage():r.page==='settings'?settingsPage():jobsPage());
    renderShell();
    if(pendingJobResult&&r.page==='course'&&currentLesson?.lesson.id===pendingJobResult.lesson){const tab=pendingJobResult.tab;pendingJobResult=null;openReader(tab);}
    if(wasOpen){
      if(currentLesson?.lesson.id!==previousLesson||r.page!=='course')closeReader();
      else if(!$('#source-video video'))openReader(readerTab);
    }
  } catch(e) {if(version===routeVersion)renderMain(empty('暂时无法打开',e.message,'<a class="button" href="#/courses">返回课程</a>'));}
}
async function action(name,id,trigger,focusVisible) {

  if(name==='retry-startup'){arc.replace($('#reader-close'),arcSlot('button',{label:'关闭课堂阅读',action:'close-reader','icon-only':true,variant:'ghost'},'', 'span'));
arc.replace($('#reader-top'),smallAction('回到开头','reader-top'));
await initialize();return;}
  if(name==='login'){await api('/auth/login',{method:'POST'});toast('请在新开的 Chrome 窗口完成学校登录');await refresh();return;}
  if(name==='logout'){await api('/auth/logout',{method:'POST'});await refresh();return;}
  if(name==='open-reader'){openReader(id,trigger,focusVisible);return;}
  if(name==='close-reader'){closeReader();return;}
  if(name==='reader-top'){$('#reader-body').scrollTop=0;rememberReader();readerPosition();return;}
  if(name==='evidence'){openSourceRange(id,id,trigger,focusVisible);return;}
  if(name==='source-range'){const [start,end]=id.split('|');openSourceRange(start,end,trigger,focusVisible);return;}
  if(name==='play-source-video'){playSourceVideo();return;}
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
  if(name==='retry-job'){const j=state.jobs.find(x=>x.id===id);if(j.kind==='schedule'){await api('/schedule/sync',{method:'POST'});await refresh(false);return;}return action({'slides-pdf':'slides-pdf',sync:'sync',recordings:'recordings-sync',timetable:'timetable-sync',course:'course-sync',materials:'materials',analyze:'analyze',automation:'run-automation'}[j.kind],j.target);}
  const path=name==='slides-pdf'?`/lessons/${enc(id)}/slides/export`:name==='schedule-sync'?'/schedule/sync':name==='timetable-sync'?'/timetable/sync':name==='sync'?'/sync':name==='recordings-sync'?'/recordings/sync':name==='course-sync'?`/courses/${enc(id)}/sync`:name==='run-automation'?`/courses/${enc(id)}/automation/run`:`/lessons/${enc(id)}/${name==='analyze'?'analyze':'materials'}`;
  await api(path,{method:'POST'});toast('任务已加入队列，可在页面顶部查看进度');await refresh(['recordings-sync','timetable-sync','sync','materials','analyze'].includes(name));
}
document.addEventListener('click', async event=>{
  if(event.target.closest('.skip')){event.preventDefault();$('#main').focus();return;}
  const element=event.target.closest('[data-action]');if(!element)return;
  const {action:name,id}=element.dataset;
  const opensReader=['open-reader','evidence','source-range'].includes(name);
  if(!opensReader)element.disabled=true;
  try{await action(name,id,element,event.detail===0);}catch(e){toast(e.message);}finally{if(element.isConnected)element.disabled=false;}
});
document.addEventListener('input',event=>{
  if(event.target.id==='transcript-search'){readerSearch=event.target.value;$('#transcript-rows').innerHTML=transcriptRows(visibleTranscript());$('#reader-body').scrollTop=0;rememberReader();readerPosition();}
});
document.addEventListener('change',async event=>{
  if(event.target.dataset.task){try{await api(`/tasks/${enc(event.target.dataset.task)}`,{method:'PATCH',body:JSON.stringify({done:event.target.checked})});await refresh();}catch(e){event.target.checked=!event.target.checked;toast(e.message);}}
});
document.addEventListener('submit',async event=>{
  const form=event.target;
  if(form.id!=='model-form')return;
  event.preventDefault();
  if(busy)return;
  const data=new FormData(form), submit=event.submitter;
  const remove=submit?.name==='remove_key';
  if(submit)submit.disabled=true;
  busy=true;
  try {
    const config=remove?{base_url:state.model.base_url,model:state.model.model,api_key:''}:
      {base_url:data.get('base_url'),model:data.get('model'),api_key:data.get('api_key')||null};
    await api('/model',{method:'PUT',body:JSON.stringify(config)});
    toast(remove?'当前密钥已移除':'模型配置已保存');
    await refresh();
  } catch(e) {toast(e.message);}
  finally {busy=false;if(submit)submit.disabled=false;}
});

window.addEventListener('hashchange',()=>{if(state){closeReader();renderRoute();}});
$('#reader').addEventListener('cancel',event=>{event.preventDefault();closeReader();});
$('#reader').addEventListener('click',event=>{if(event.target===$('#reader')){const b=$('#reader').getBoundingClientRect();if(event.clientX<b.left||event.clientX>b.right||event.clientY<b.top||event.clientY>b.bottom)closeReader();}});
$('#reader-body').addEventListener('scroll',()=>{readerPosition();clearTimeout(readerSaveTimer);readerSaveTimer=setTimeout(rememberReader,150);});
window.addEventListener('pagehide',rememberReader);
async function poll() {
  try{
    const old=state, next=await api('/state');state=next;renderShell();
    if(next.auth.logged_in&&!old?.auth.logged_in)toast(next.auth.message);
    const signature=JSON.stringify([next.auth.state,next.auth.message,next.courses,next.timetable?.synced_at,next.jobs.map(x=>[x.id,x.status]),next.tasks,next.automations]);
    const editing=['INPUT','TEXTAREA','SELECT'].includes(document.activeElement.tagName);
    if(signature!==refreshSignature&&!busy&&!editing&&!document.querySelector('.buaa-combobox-popup[data-open]')&&!(route().page==='automations'&&Object.keys(automationDraft?.changes||{}).length)){refreshSignature=signature;await renderRoute();}
  }catch{ /* Transient server restarts keep the current local view intact. */ }
}
async function initialize() {
  try {
    await refresh();
    setInterval(poll,3000);
  } catch (error) {
    state = undefined;
    if (!(error instanceof ApiError)) console.error(error);
    const title = error.status === 0 ? '本地服务未连接' : '暂时无法加载学习空间';
    const message = error instanceof ApiError ? error.message : '页面加载失败，请重试。';
    renderMain(empty(title,message,button('重试','retry-startup')));
  }
}
arc.replace($('#reader-close'),arcSlot('button',{label:'关闭课堂阅读',action:'close-reader','icon-only':true,variant:'ghost'},'', 'span'));
arc.replace($('#reader-top'),smallAction('回到开头','reader-top'));
await initialize();
