import React, {useState, useEffect, useRef} from 'react';
import {GlyphBackground} from './GlyphBackground.jsx';
import {ProductDemo} from './features/product-demo/ProductDemo.jsx';
import {DownloadSimple, Play, Pause, ArrowCounterClockwise, ArrowUpRight, AppleLogo, WindowsLogo, Quotes, BookOpen, X} from '@phosphor-icons/react';

const topics = [
 {time:'07:34', title:'分类、检测与分割', note:'从识别整张图，到理解每一个像素。', quote:'分类判断图像中有什么，检测进一步找到目标的位置，分割则明确每个像素所属的区域。'},
 {time:'12:56', title:'多模态如何理解图像', note:'让图像和语言在同一个空间里相遇。', quote:'图像与文字被映射到同一个语义空间，让含义相近的内容彼此靠近。'},
 {time:'14:15', title:'视觉 Transformer', note:'用注意力建立图像区域之间的联系。', quote:'将图像划分为小块，再通过注意力机制关联不同区域，模型就能从局部信息理解整体。'},
];
const DEMO_DURATION = 30000;
const formatTime = seconds => `${String(Math.floor(seconds / 60)).padStart(2, '0')}:${String(seconds % 60).padStart(2, '0')}`;
const base='https://github.com/fyzzzz05/BUAA-Learn/releases/download/desktop-v0.2.4/';
function WindowBar({lesson=false}) {return <div className="window-bar"><span className="window-brand"><BookOpen size={13}/> {lesson?'示例课堂 · 计算机视觉导论':'BUAA Learn'}</span>{lesson&&<span>图像理解与多模态</span>}</div>}
export function App(){
 const [index,setIndex]=useState(1),[playing,setPlaying]=useState(false),[reduced,setReduced]=useState(false),[modal,setModal]=useState(null);
 const [backgroundPaused,setBackgroundPaused]=useState(false);
 const playhead=useRef(null), progress=useRef(1/3), dialog=useRef(null),lastFocus=useRef(null);
 const [elapsed,setElapsed]=useState(10);
 const showProgress=value=>{
  progress.current=value;
  if(playhead.current)playhead.current.style.left=`${value*100}%`;
  setIndex(Math.min(topics.length-1,Math.floor(value*topics.length)));
  setElapsed(Math.floor(value*DEMO_DURATION/1000));
 };
 useEffect(()=>{const mq=matchMedia('(prefers-reduced-motion: reduce)');const update=()=>{setReduced(mq.matches);if(mq.matches)setPlaying(false)};update();mq.addEventListener('change',update);return()=>mq.removeEventListener('change',update)},[]);
 useEffect(()=>{
  if(!playing)return;
  let frame;
  let previous=null;
  const tick=now=>{
   const next=Math.min(1,progress.current+(previous===null?0:Math.max(0,now-previous))/DEMO_DURATION);
   previous=now;
   progress.current=next;
   if(playhead.current)playhead.current.style.left=`${next*100}%`;
   setIndex(Math.min(topics.length-1,Math.floor(next*topics.length)));
   setElapsed(Math.floor(next*DEMO_DURATION/1000));
   if(next===1)setPlaying(false);else frame=requestAnimationFrame(tick);
  };
  frame=requestAnimationFrame(tick);
  return()=>cancelAnimationFrame(frame);
 },[playing]);
 useEffect(()=>{const stop=()=>{if(document.hidden)setPlaying(false)};document.addEventListener('visibilitychange',stop);return()=>document.removeEventListener('visibilitychange',stop)},[]);
 useEffect(()=>{if(modal!==null){lastFocus.current=document.activeElement;dialog.current.showModal()}else if(dialog.current.open){dialog.current.close();lastFocus.current?.focus()}},[modal]);
 const select=i=>{setPlaying(false);showProgress(i/topics.length)};
 const togglePlayback=()=>{if(!playing&&progress.current>=1)showProgress(0);setPlaying(v=>!v)};
 const t=topics[index];
 return <>
 <main>
 <div className="first-screen">
 <div className="hero-backdrop" aria-hidden="true"/>
 <GlyphBackground paused={backgroundPaused} reduced={reduced}/>
 <header className="shell nav"><a className="brand" href="#"><img className="brand-icon" src="/images/buaa-learn-icon.svg" width="34" height="34" alt=""/>BUAA Learn</a><nav><a href="#scenes">课堂演示</a><a href="#download">下载</a><button className="background-toggle" onClick={()=>setBackgroundPaused(v=>!v)} disabled={reduced} aria-label={backgroundPaused?"播放背景动效":"暂停背景动效"} title={reduced?"已遵循系统减少动态效果设置":backgroundPaused?"播放背景动效":"暂停背景动效"}>{backgroundPaused||reduced?<Play size={14}/>:<Pause size={14}/>}</button></nav></header>
 <section className="hero" aria-labelledby="hero-title"><div className="shell intro"><h1 id="hero-title">把整节课，<br/>变成你的复习路径。</h1><p>按时间轴回顾课堂，找到知识点与老师原话。<br/>错过的作业，也能及时补上。</p><a className="button primary" href="#download"><DownloadSimple size={22}/>下载桌面版</a></div>
 <div className={'interactive-lesson '+(reduced?'reduce':'')}><div className="shell"><div className="product hero-note" aria-live="polite"><WindowBar lesson/><div className="hero-note-body" key={index}><div className="topic-detail"><button className="time-link" onClick={()=>setModal(index)} aria-label={`查看 ${t.time} 的课堂原话`}><Play size={18} weight="fill"/><time>{t.time}</time></button><div><h2>{t.title}</h2><p>{t.note}</p></div></div><blockquote><Quotes weight="fill" size={22}/><span>{t.quote}</span></blockquote></div></div></div>
 <div className="timeline"><div className="ruler" aria-hidden="true">{Array.from({length:101},(_,i)=><i key={i} className={i%5===0?'major':''}/>)}</div><div ref={playhead} className="playhead" style={{left:`${progress.current*100}%`}} aria-hidden="true"/><div className="chapters">{topics.map((topic,i)=><button key={topic.time} aria-pressed={index===i} onClick={()=>select(i)}><time>{topic.time}</time><span>{topic.title}</span></button>)}</div></div>
 <div className="shell playback"><div><button className="round" onClick={togglePlayback} aria-label={playing?'暂停演示':'播放演示'}>{playing?<Pause size={19} weight="fill"/>:<Play size={19} weight="fill"/>}</button><button className="replay" onClick={()=>{showProgress(0);setPlaying(!reduced)}} aria-label="重播演示"><ArrowCounterClockwise size={21}/></button><span className="demo-clock"><time>{formatTime(elapsed)}</time><span className="divider"> / </span>00:30</span></div><span className="course-caption">计算机视觉导论</span></div></div>
 </section>
 </div>
 <ProductDemo reduced={reduced}/>
 <section id="download" className="shell download"><div className="download-top"><h2>把下一节课，也留下来。</h2><div className="download-links"><a className="button primary" href={base+'BUAA-Learn-0.2.4-mac-arm64.dmg'}><AppleLogo size={21} weight="fill"/>macOS <small>Apple Silicon</small></a><a className="button primary" href={base+'BUAA-Learn-0.2.4-win-x64.exe'}><WindowsLogo size={20} weight="fill"/>Windows</a></div></div><div className="download-details"><span>桌面测试版 0.2.4</span><a href={base+'BUAA-Learn-0.2.4-mac-x64.dmg'}>使用 Intel Mac？下载 Intel 版 <ArrowUpRight size={13}/></a></div><p className="conditions">北航 SSO 登录 · 自动获取材料需保持应用运行。生成笔记会使用你配置的模型服务，可能产生调用费用。</p></section>
 </main><footer className="shell"><span>BUAA Learn · 非北航官方应用</span><div className="footer-tools"><a href="https://github.com/fyzzzz05/BUAA-Learn" target="_blank" rel="noreferrer">GitHub <ArrowUpRight size={14}/></a></div></footer>
 <dialog ref={dialog} onCancel={()=>setModal(null)} onClick={e=>{if(e.target===dialog.current)setModal(null)}}><button className="close" aria-label="关闭原话" onClick={()=>setModal(null)}><X size={22}/></button>{modal!==null&&<><p className="dialog-label">示例课堂 · 课堂原话</p><h2>{topics[modal].title}</h2><time>{topics[modal].time}</time><blockquote>{topics[modal].quote}</blockquote></>}</dialog>
 </>
}
