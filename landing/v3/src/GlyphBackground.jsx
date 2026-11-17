import {useEffect, useRef, useState} from 'react';
import GlyphWind from './vendor/glyph-wind.js';

export function GlyphBackground({paused, reduced}) {
 const host=useRef(null), renderer=useRef(null), light=useRef(null);
 const [inView,setInView]=useState(true);
 useEffect(()=>{
  const observer=new IntersectionObserver(([entry])=>setInView(entry.isIntersecting));
  observer.observe(host.current.parentElement);
  return()=>observer.disconnect();
 },[]);
 useEffect(()=>{
  const element=host.current;
  const settings=GlyphWind.defaults();
  settings.seed=20261116;
  settings.look='mixed';
  settings.strength.base.light=.23;
  settings.strength.rest=.035;
  settings.surface.fill=.92;
  settings.glow.color.light=[.22,.55,1];
  settings.touch.stir=2;
  settings.touch.slowPxS=0;
  settings.touch.fastPxS=250;
  settings.grid.maxDevicePixels=4500000;
  settings.power.frameMs=33;
  settings.power.rippleFrameMs=20;
  settings.composition.card={centerY:.32,calmX:520,calmY:220,left:.2};
  const instance=GlyphWind.create({
   element,settings:()=>settings,timing:()=>({fadeIn:1600,fadeOut:300}),
   onReady:()=>{element.dataset.ready='true'},
   onError:()=>{element.dataset.ready='unavailable'},
  });
  renderer.current=instance;
  return()=>{instance.destroy();renderer.current=null};
 },[]);
 useEffect(()=>{renderer.current?.setRunning(!paused&&!reduced&&inView)},[paused,reduced,inView]);
 useEffect(()=>{
  const canvas=light.current, element=host.current, stage=element.parentElement;
  const context=canvas.getContext('2d');
  if(!context)return;
  if(paused||reduced||!inView){context.clearRect(0,0,canvas.width,canvas.height);return;}
  let frame=null, previous=0, width=0, height=0, ratio=1;
  let inside=false, opacity=0, x=0, y=0, targetX=0, targetY=0, trailX=0, trailY=0;
  const paint=(cx,cy,radius,alpha)=>{
   const glow=context.createRadialGradient(cx,cy,0,cx,cy,radius);
   glow.addColorStop(0,`rgba(65,157,255,${alpha})`);
   glow.addColorStop(.2,`rgba(33,111,245,${alpha*.75})`);
   glow.addColorStop(.55,`rgba(27,66,185,${alpha*.3})`);
   glow.addColorStop(1,'rgba(12,31,96,0)');
   context.fillStyle=glow;context.fillRect(cx-radius,cy-radius,radius*2,radius*2);
  };
  const draw=now=>{
   frame=null;
   const delta=previous?Math.min(64,now-previous):16;previous=now;
   const follow=1-Math.exp(-delta/65),tail=1-Math.exp(-delta/210);
   x+=(targetX-x)*follow;y+=(targetY-y)*follow;
   trailX+=(x-trailX)*tail;trailY+=(y-trailY)*tail;
   opacity+=((inside?1:0)-opacity)*(1-Math.exp(-delta/180));
   context.clearRect(0,0,width,height);
   context.globalCompositeOperation='screen';
   paint(trailX,trailY,280,opacity*.25);
   paint(x,y,200,opacity*.48);
   context.globalCompositeOperation='source-over';
   if(inside||opacity>.005)frame=requestAnimationFrame(draw);
  };
  const start=()=>{if(frame===null&&!document.hidden){previous=0;frame=requestAnimationFrame(draw)}};
  const move=event=>{
   if(event.pointerType!=='mouse')return;
   const bounds=stage.getBoundingClientRect();
   targetX=event.clientX-bounds.left;targetY=event.clientY-bounds.top;
   if(!inside){x=trailX=targetX;y=trailY=targetY;}
   inside=true;start();
  };
  const leave=()=>{inside=false;start()};
  const resize=()=>{
   const bounds=element.getBoundingClientRect();width=bounds.width;height=bounds.height;
   ratio=Math.min(devicePixelRatio,1.5);canvas.width=Math.round(width*ratio);canvas.height=Math.round(height*ratio);
   canvas.style.width=`${width}px`;canvas.style.height=`${height}px`;context.setTransform(ratio,0,0,ratio,0,0);start();
  };
  const visibility=()=>{
   if(document.hidden){cancelAnimationFrame(frame);frame=null;inside=false;opacity=0;context.clearRect(0,0,width,height)}
   else start();
  };
  const observer=new ResizeObserver(resize);observer.observe(element);resize();
  stage.addEventListener('pointermove',move,{passive:true});stage.addEventListener('pointerleave',leave);
  window.addEventListener('blur',leave);document.addEventListener('visibilitychange',visibility);
  return()=>{
   cancelAnimationFrame(frame);observer.disconnect();context.clearRect(0,0,width,height);
   stage.removeEventListener('pointermove',move);stage.removeEventListener('pointerleave',leave);
   window.removeEventListener('blur',leave);document.removeEventListener('visibilitychange',visibility);
  };
 },[paused,reduced,inView]);
 return <div ref={host} className="glyph-backdrop" aria-hidden="true"><canvas ref={light} className="pointer-light"/></div>;
}
