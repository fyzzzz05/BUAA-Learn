import {useEffect, useRef, useState} from 'react';
import {ArrowCounterClockwise, ArrowUpRight, BookOpen, CalendarBlank, CaretRight, Check, CheckSquare, Cursor, MagnifyingGlass, Pause, Play, Quotes} from '@phosphor-icons/react';
import './product-demo.css';

const DURATION = 28000;
const chapters = [
 {title:'回顾课堂', description:'先看清这节课的脉络。', start:0, end:9000, still:5000, Icon:BookOpen},
 {title:'定位知识点', description:'找到想复习的一段，连同老师原话一起回看。', start:9000, end:20000, still:17000, Icon:MagnifyingGlass},
 {title:'补看作业', description:'课堂留下的任务，一项也不遗漏。', start:20000, end:DURATION, still:26500, Icon:CheckSquare},
];
const notes = [
 {time:'02:10', title:'图像如何变成特征', text:'把像素转化为可计算的特征，建立图像表示。'},
 {time:'07:34', title:'分类、检测与分割', text:'识别图像中的对象，进一步确定位置与边界。'},
 {time:'12:56', title:'多模态如何理解图像', text:'让图像和语言在同一个语义空间里相遇。'},
 {time:'14:15', title:'视觉 Transformer', text:'通过注意力机制，建立图像区域之间的联系。'},
];
const homework = [
 {title:'阅读 Vision Transformer 论文', detail:'选读 · 关注图像分块与位置编码', time:'23:08'},
 {title:'对比三组图文检索结果', detail:'实践 · 记录匹配结果并解释差异', time:'24:32'},
 {title:'解释一个检索匹配错误', detail:'思考 · 从图像与文本的语义关系入手', time:'25:46'},
];

function useDemoClock(root, reduced) {
 const elapsed = useRef(0);
 const [time,setTime] = useState(0);
 const [playing,setPlaying] = useState(true);
 const [visible,setVisible] = useState(false);
 const [pageVisible,setPageVisible] = useState(!document.hidden);
 useEffect(()=>{
  const observer = new IntersectionObserver(([entry])=>setVisible(entry.isIntersecting),{threshold:.25});
  observer.observe(root.current);
  const visibility=()=>setPageVisible(!document.hidden);
  document.addEventListener('visibilitychange',visibility);
  return()=>{observer.disconnect();document.removeEventListener('visibilitychange',visibility)};
 },[root]);
 useEffect(()=>{
  if(!playing||!visible||!pageVisible||reduced)return;
  let frame, previous=null, published=0;
  const tick=now=>{
   if(previous!==null)elapsed.current=Math.min(DURATION,elapsed.current+Math.min(now-previous,100));
   previous=now;
   if(now-published>=80||elapsed.current===DURATION){setTime(elapsed.current);published=now}
   if(elapsed.current<DURATION)frame=requestAnimationFrame(tick);else setPlaying(false);
  };
  frame=requestAnimationFrame(tick);
  return()=>cancelAnimationFrame(frame);
 },[playing,visible,pageVisible,reduced]);
 const seek=(value,play=true)=>{elapsed.current=value;setTime(value);setPlaying(play)};
 const toggle=()=>{if(elapsed.current>=DURATION)seek(0);else setPlaying(value=>!value)};
 return {time,seek,toggle,playing:playing&&!reduced,active:playing&&visible&&pageVisible&&!reduced};
}

function DemoPointer({stage,target,layoutKey,clickKey,hidden}) {
 const pointer=useRef(null);
 useEffect(()=>{
  const position=()=>{
   const anchor=stage.current?.querySelector(`[data-cursor="${target}"]`);
   if(!pointer.current)return;
   const visibleAnchor=anchor&&anchor.getClientRects().length>0;
   pointer.current.style.visibility=visibleAnchor?'visible':'hidden';
   if(!visibleAnchor)return;
   const box=anchor.getBoundingClientRect(),parent=stage.current.getBoundingClientRect();
   pointer.current.style.transform=`translate(${box.left-parent.left+box.width*.72}px,${box.top-parent.top+box.height*.6}px)`;
  };
  position();
  const observer=new ResizeObserver(position);
  observer.observe(stage.current);
  window.addEventListener('resize',position);
  return()=>{observer.disconnect();window.removeEventListener('resize',position)};
 },[stage,target,layoutKey]);
 return <div ref={pointer} className={`demo-cursor ${hidden?'is-hidden':''}`} aria-hidden="true"><span className="demo-click" key={clickKey}/><Cursor size={29} weight="fill"/></div>;
}

export function ProductDemo({reduced}) {
 const root=useRef(null),stage=useRef(null);
 const clock=useDemoClock(root,reduced);
 const time=clock.time;
 const chapter=time<9000?0:time<20000?1:2;
 const query=chapter===1?'多模态'.slice(0,Math.max(0,Math.min(3,Math.floor((time-10100)/400)))):'';
 const found=time>=12100&&chapter===1;
 const sourceOpen=time>=15100&&chapter===1;
 const reviewed=time>=25500;
 const checked=time>=22800;
 const activeNote=chapter===0?(time<2600?0:time<4900?1:2):2;
 const target=chapter===0?(time<1300?'lesson':time<4000?'note-1':'note-2'):
  chapter===1?(time<12200?'search':time<14700?'result':'source'):
  time<21700?'work':time<24800?'task-0':'task-1';
 const clickKey=chapter===0?(time<1800?'rest':time<4500?'first':'second'):
  chapter===1?(time<10000?'search':time<15100?'result':'source'):
  time<22800?'work':time<25500?'check-1':'check-2';

 return <section ref={root} id="scenes" className="shell product-demo" aria-labelledby="scenes-title">
  <div className="demo-heading"><h2 id="scenes-title">复习，<br/>从你需要的地方开始。</h2><p>打开 BUAA Learn，<br/>把一节课变成自己的复习材料。</p></div>
  <div className={`demo-theater ${clock.active?'is-running':''}`}>
   <div ref={stage} className={`demo-app scene-${chapter} ${sourceOpen?'source-open':''}`} aria-hidden="true">
    <div className="demo-titlebar"><span><img src="/images/buaa-learn-icon.svg" alt=""/>BUAA Learn</span><span>示例课堂 · 计算机视觉导论</span><span className="demo-window-dots"><i/><i/><i/></span></div>
    <div className="demo-workspace">
     <aside className="demo-sidebar">
      <div className="demo-side-item"><CalendarBlank size={17}/><span>课程表</span></div>
      <div className={`demo-side-item ${chapter!==2?'current':''}`}><BookOpen size={17}/><span>本学期课程</span></div>
      <div className={`demo-side-item ${chapter===2?'current':''}`} data-cursor="work"><CheckSquare size={17}/><span>作业与事项</span></div>
      <div className="demo-courses"><span>计算机视觉导论</span><div>第 3 讲<small>图像特征与表示</small></div><div className="current" data-cursor="lesson">第 4 讲<small>图像理解与多模态</small></div><div>第 5 讲<small>视觉模型的训练</small></div></div>
      <div className="demo-account"><span>同</span>我的学习空间</div>
     </aside>
     <div className="demo-main">
      <div className="demo-breadcrumb">计算机视觉导论<CaretRight size={12}/>{chapter===2?'作业与事项':'第 4 讲'}</div>
      <div className="demo-page-heading"><div><p>{chapter===2?'本节作业':'学习笔记'}</p><h3>{chapter===2?'留给自己的下一步。':'图像理解与多模态'}</h3></div><span className="demo-ready">{chapter===2?`${Number(checked)+Number(reviewed)} / 3 已完成`:<><Check size={13}/>笔记已生成</>}</span></div>
      {chapter!==2?<>
       <div className="demo-reader-toolbar"><span className="demo-reader-tab">课堂时间轴</span><div className={`demo-search ${chapter===1?'focused':''}`} data-cursor="search"><MagnifyingGlass size={15}/><span>{query||'查找知识点'}{chapter===1&&!found&&<i/>}</span>{found&&<small>1 个结果</small>}</div></div>
       <div className="demo-reading-area">
        <div className="demo-note-list">
         {notes.map((note,i)=><div key={note.time} data-cursor={found&&i===2?'result':`note-${i}`} className={`demo-note ${activeNote===i?'selected':''} ${found&&i!==2?'muted':''}`}>
          <time>{note.time}</time><div><h4>{found&&i===2?<><mark>多模态</mark>如何理解图像</>:note.title}</h4><p>{note.text}</p>{activeNote===i&&<div className="demo-note-extra"><span className="demo-source-button" data-cursor="source"><Play size={10} weight="fill"/>查看原话<ArrowUpRight size={11}/></span>{chapter===0&&<span className="demo-reading-label">正在回顾</span>}</div>}</div>
         </div>)}
        </div>
        {sourceOpen&&<div className="demo-transcript"><div className="demo-transcript-heading"><Quotes size={20} weight="fill"/><span>课堂原话</span><time>12:56</time></div><div className="demo-quote-context"><time>12:48</time><p>接下来，我们把图像与语言放在一起看。</p></div><blockquote>图像与文字被映射到同一个语义空间，让含义相近的内容彼此靠近。</blockquote><div className="demo-quote-context"><time>13:16</time><p>因此，我们可以用一句话去检索相关的图像。</p></div><div className="demo-source-location"><span/><span>已定位到课堂原话</span></div></div>}
       </div>
      </>:<div className="demo-homework-scene"><div className="demo-work-list">{homework.map((task,i)=>{
       const done=i===0?checked:i===1?reviewed:false;
       return <div key={task.title} className={`demo-task ${done?'done':''}`}><span data-cursor={`task-${i}`} className="demo-task-check">{done&&<Check size={15} weight="bold"/>}</span><div><h4>{task.title}</h4><p>{task.detail}</p><span className="demo-task-source"><Play size={9} weight="fill"/>{task.time} 查看原话</span></div></div>;
      })}</div><div className={`demo-complete ${reviewed?'visible':''}`}><Check size={17} weight="bold"/><span>又完成了两项，复习继续。</span></div></div>}
     </div>
    </div>
    <DemoPointer stage={stage} target={target} layoutKey={`${chapter}-${activeNote}-${found}-${sourceOpen}`} clickKey={clickKey} hidden={reduced||time>=DURATION}/>
   </div>
   <div className="demo-transport"><span>一节课的复习过程<span className="demo-duration"> · 28 秒</span></span><div><button onClick={()=>clock.seek(0)} aria-label="重播复习流程动画" title="重播"><ArrowCounterClockwise size={17}/></button><button onClick={clock.toggle} disabled={reduced} aria-label={clock.playing?'暂停复习流程动画':'播放复习流程动画'} title={reduced?'已遵循减少动态效果设置':clock.playing?'暂停':'播放'}>{clock.playing?<Pause size={17} weight="fill"/>:<Play size={17} weight="fill"/>}</button></div></div>
  </div>
  <div className="demo-chapters" role="group" aria-label="选择演示步骤">{chapters.map(({title,description,start,end,still,Icon},i)=><button key={title} aria-pressed={chapter===i} onClick={()=>clock.seek(reduced?still:start,!reduced)}><span className="demo-chapter-line"><i style={{transform:`scaleX(${Math.max(0,Math.min(1,(time-start)/(end-start)))})`}}/></span><span className="demo-chapter-title"><Icon size={20}/>{title}</span><span className="demo-chapter-description">{description}</span></button>)}</div>
 </section>;
}
