/* SPDX-License-Identifier: MIT
 * Original implementation for the public Background Lab.
 * Numeric design parameters were informed by the REA study; no recovered
 * Anthropic JavaScript, shader source, or fonts are included in this file.
 */
const GlyphWind = (() => {
  const clamp = (value, min=0, max=1) => Math.max(min,Math.min(max,value));
  const smooth = value => { const x=clamp(value); return x*x*(3-2*x); };
  const charsets = {
    mixed:'abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789',
    lettered:'abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ',
    figured:'0123456789ABCDEFabcdef'
  };
  function defaults() {
    return {
      seed:undefined, look:undefined,
      strength:{scale:1,base:{light:.26,dark:.26},cap:{light:.9,dark:.9},rest:.03},
      surface:{fill:1,weight:300,glyphCss:9,skipUnseen:true},
      wind:{energy:1,friction:.6,flick:1.2,lull:.5,streak:.4,detail:.6,edge:.75,curl:.65,wisp:.5,tail:.35,sweep:.3,morph:3,revealS:4},
      glow:{amount:1,headroom:3,color:{light:[0,.25,1],dark:[.1,.68,1]}},
      touch:{stir:1,ripple:1,slowPxS:60,fastPxS:800},
      composition:{card:{centerY:.45,calmX:250,calmY:80,left:.3}},
      grid:{cellCss:6.5,minCols:24,minRows:8,maxDevicePixels:21000000},
      power:{frameMs:40,rippleFrameMs:10,budgetS:180,settleS:8,resumeS:3}
    };
  }
  function random(seed) {
    let state=(seed>>>0)||0x19a4c637;
    return () => {
      state^=state<<13;state^=state>>>17;state^=state<<5;
      return (state>>>0)/4294967296;
    };
  }
  function pattern(seed,look='mixed') {
    const rng=random(seed), between=(low,high)=>low+(high-low)*rng();
    const angle=between(Math.PI/10,Math.PI/4.3);
    return {seed:seed>>>0,key:look,heading:rng()<.5?angle:Math.PI-angle,
      speed:between(42,64),gustLength:between(380,560),gustWidth:between(120,190),
      bend:between(60,110),bendScale:between(300,440)};
  }
  function grid(bounds,options) {
    const {deviceWidth:w,deviceHeight:h,devicePixelRatio:scale}=bounds;
    if(w<=0||h<=0||scale<=0)return null;
    const cellWidth=Math.max(5,Math.round(options.cellCss*scale));
    const cellHeight=cellWidth*2;
    let columns=Math.ceil(w/cellWidth),rows=Math.ceil(h/cellHeight);
    if(columns<options.minCols||rows<options.minRows)return null;
    const factor=Math.max(1,Math.sqrt(columns*rows*cellWidth*cellHeight/options.maxDevicePixels));
    columns=Math.floor(columns/factor);rows=Math.floor(rows/factor);
    return {columns,rows,cellWidth,cellHeight,scale,left:Math.round((w-columns*cellWidth)/2),top:Math.round((h-rows*cellHeight)/2)};
  }
  function chooseLook(seed,history=[]) {
    const keys=Object.keys(charsets), available=keys.filter(key=>!history.slice(0,2).includes(key));
    const pool=available.length?available:keys;
    return pool[(seed>>>0)%pool.length];
  }
  const vertex = `#version 300 es
precision highp float;
precision highp int;
uniform vec2 viewport, cellSize, gridOrigin, pane;
uniform int columns, sequence[64], sequenceLength;
uniform float seed, clockTime, wallTime, reveal, fillRate, changeRate;
uniform vec4 flow, bending, textureShape, activity;
uniform vec4 quiet;
uniform float tail, sweep, evolve;
uniform int tapCount, wakeCount;
uniform vec4 taps[6], wakes[48];
out vec2 atlasUV;
out float luminance, blue;
const vec2 corners[6]=vec2[6](vec2(0,0),vec2(1,0),vec2(1,1),vec2(0,0),vec2(1,1),vec2(0,1));
float hash(vec2 p){return fract(sin(dot(p,vec2(127.13,311.77))+seed*.017)*43758.31);}
float noise(vec2 p){
  vec2 base=floor(p), q=fract(p);q=q*q*(3.-2.*q);
  return mix(mix(hash(base),hash(base+vec2(1,0)),q.x),mix(hash(base+vec2(0,1)),hash(base+vec2(1)),q.x),q.y);
}
float cloud(vec2 p,float age){
  float sum=0., weight=.5, total=0.;
  for(int k=0;k<4;k++){
    vec2 drift=age*vec2(.019+float(k)*.007,-.013-float(k)*.004);
    sum+=weight*noise(p+drift+float(k)*13.37);total+=weight;
    p=mat2(1.74,-.58,.58,1.74)*p+vec2(7.9,21.2);weight*=mix(.35,.65,textureShape.x);
  }
  return sum/total;
}
float heartDistance(vec2 p,float size){
  vec2 q=vec2(p.x,-p.y)/max(size,1.);q.y+=.22;
  float b=dot(q,q)-1.;
  float value=b*b*b-q.x*q.x*q.y*q.y*q.y;
  vec2 gradient=vec2(6.*q.x*b*b-2.*q.x*q.y*q.y*q.y,6.*q.y*b*b-3.*q.x*q.x*q.y*q.y);
  return abs(value)/max(.05,length(gradient))*size;
}
void main(){
  int row=gl_InstanceID/columns, col=gl_InstanceID-row*columns;
  vec2 cell=vec2(col,row);
  vec2 pixel=gridOrigin+(cell+.5)*cellSize;
  vec2 p=pixel*pane/viewport;
  if(hash(cell+19.)>fillRate){gl_Position=vec4(2,2,0,1);atlasUV=vec2(0);luminance=0.;blue=0.;return;}
  float age=clockTime*evolve;
  vec2 dir=vec2(cos(flow.x),sin(flow.x));
  vec2 across=vec2(-dir.y,dir.x);
  vec2 bend=vec2(noise(p/bending.y+age*.012),noise(p/bending.y+vec2(21.3,-age*.009)))-.5;
  vec2 shifted=p+bend*bending.x*2.;
  vec2 q=vec2(dot(shifted,dir)-clockTime*flow.y,dot(shifted,across));
  vec2 eddy=vec2(noise(q/(flow.w*1.2)+age*.014),noise(q/(flow.w*.8)+vec2(7.2,-age*.01)))-.5;
  q+=eddy*flow.w*textureShape.z*1.2;
  float field=cloud(q/flow.zw,age);
  float threads=1.-smoothstep(0.,.28,abs(noise(q/vec2(140.,30.)+age*.008)-.5));
  field+=textureShape.w*.1*(threads-.5)+sweep*.08*cos(q.x/max(pane.x*.17,120.));
  float threshold=.28+bending.z*.4-tail*.035;
  float band=mix(.19,.045,textureShape.y);
  float lit=pow(smoothstep(threshold-band,threshold+band,field),2.);
  lit*=mix(1.,.32+.68*threads,bending.w);
  float warmth=0.;
  for(int j=0;j<48;j++){
    if(j>=wakeCount)break;
    float t=max(0.,wallTime-wakes[j].z),radius=50.+40.*t;
    float heat=(1.-exp(-t/.05))*exp(-t/.65);
    warmth+=wakes[j].w*heat*exp(-dot(p-wakes[j].xy,p-wakes[j].xy)/(radius*radius));
  }
  warmth=1.-exp(-1.5*warmth);
  lit=mix(lit,max(lit,.7*cloud(q/vec2(35.,22.),age)),warmth);
  float glow=0.,push=0.;
  for(int j=0;j<6;j++){
    if(j>=tapCount)break;
    float t=max(0.,wallTime-taps[j].z),radius=620.*(1.-exp(-t/1.3)),width=85.+60.*t;
    vec2 offset=p-taps[j].xy;
    float distance=taps[j].w<0.?heartDistance(offset,radius*.72):abs(length(offset)-radius);
    float ring=exp(-4.*distance*distance/(width*width));
    if(taps[j].w<0.&&length(offset)>radius*1.5+width)ring=0.;
    float envelope=(1.-exp(-t/.05))*exp(-t/1.0)*abs(taps[j].w);
    glow+=ring*envelope;push+=ring*envelope*.75;
  }
  vec2 center=(p-vec2(pane.x*.5,pane.y*quiet.w))/quiet.xy;
  float calm=1.-(1.-quiet.z)*exp(-.9*dot(center,center));
  float shown=max(reveal,clamp(warmth+push,0.,1.));
  luminance=(min(1.,lit+push)+activity.x)*calm*shown;
  blue=(glow+warmth*.95)*activity.y;
  int stepIndex=int(floor(clockTime*changeRate*(.6+.8*hash(cell+9.))+hash(cell)*50.+push*5.));
  int glyph=sequence[(int(hash(cell+77.)*997.)+stepIndex)%sequenceLength];
  vec2 corner=corners[gl_VertexID];
  atlasUV=(vec2(glyph%16,glyph/16)+corner)/vec2(16.,6.);
  vec2 location=gridOrigin+(cell+corner)*cellSize;
  gl_Position=vec4(location/viewport*vec2(2.,-2.)+vec2(-1.,1.),0,1);
}`;
  const fragment = `#version 300 es
precision highp float;
uniform sampler2D atlas;
uniform vec3 inkColor,glowColor;
uniform float inkOpacity,friction;
in vec2 atlasUV;
in float luminance,blue;
out vec4 color;
void main(){
  float coverage=texture(atlas,atlasUV).a;
  if(coverage<.001)discard;
  float shading=mix(1.,.7+.3*coverage,friction);
  float alpha=coverage*clamp(luminance*inkOpacity*shading/3.+blue*.85,0.,1.);
  vec3 rgb=mix(inkColor,glowColor,smoothstep(.04,.3,blue));
  color=vec4(rgb*alpha,alpha);
}`;
  function compile(gl,type,text) {
    const shader=gl.createShader(type);gl.shaderSource(shader,text);gl.compileShader(shader);
    if(!gl.getShaderParameter(shader,gl.COMPILE_STATUS)){const error=gl.getShaderInfoLog(shader);gl.deleteShader(shader);throw new Error(error);}
    return shader;
  }
  function create({element,settings,timing,onReady,onError}) {
    const canvas=document.createElement('canvas');canvas.className='absolute block';
    canvas.style.opacity='0';element.append(canvas);
    let gl,program,atlasTexture,resizeObserver,view=null,destroyed=false,started=false,running=false;
    let timeout,frame,lastTick=performance.now(),elapsed=0,deadline=settings().power.budgetS;
    let animationTime=(settings().seed>>>0)/4294967296*64,revealStart=0,resumeStart=-3;
    let frozen=false,taps=[],wakes=[],lastPointer=null,lastWake=null,velocity=0,tapNumber=0;
    const origin=performance.now(),now=()=> (performance.now()-origin)/1000;
    const uniforms=new Map();
    const inkParser=document.createElement('canvas').getContext('2d');
    let lastInk='',lastCharset='';
    const location=name=>{if(!uniforms.has(name))uniforms.set(name,gl.getUniformLocation(program,name));return uniforms.get(name);};
    const one=(name,value)=>gl.uniform1f(location(name),value);
    const integer=(name,value)=>gl.uniform1i(location(name),value);
    const two=(name,x,y)=>gl.uniform2f(location(name),x,y);
    const four=(name,...values)=>gl.uniform4f(location(name),...values);
    function ink() {
      const css=getComputedStyle(element).color;
      if(css===lastInk)return;lastInk=css;
      inkParser.fillStyle=css;inkParser.fillRect(0,0,1,1);
      const bytes=inkParser.getImageData(0,0,1,1).data;
      gl.uniform3f(location('inkColor'),bytes[0]/255,bytes[1]/255,bytes[2]/255);one('inkOpacity',bytes[3]/255);
    }
    function visible() {return !document.hidden&&document.hasFocus();}
    function opacity(instant=false) {
      const config=settings(),strength=Math.min(config.strength.cap.light,config.strength.base.light*config.strength.scale);
      canvas.style.transition=instant?'none':`opacity ${visible()?timing().fadeIn:timing().fadeOut}ms ease-out`;
      canvas.style.opacity=visible()?String(Math.min(1,strength*3)):'0';
      canvas.dataset.state=!visible()?'unseen':!running?'still':frozen?'calm':'moving';
    }
    function resize() {
      const bounds=element.getBoundingClientRect(),dpr=devicePixelRatio;
      view=grid({deviceWidth:Math.round(bounds.width*dpr),deviceHeight:Math.round(bounds.height*dpr),devicePixelRatio:dpr},settings().grid);
      canvas.style.visibility=view?'visible':'hidden';if(!view)return;
      // The grid cap also caps the backing buffer, preserving coverage on very large displays.
      const budgetScale=Math.min(1,Math.sqrt(settings().grid.maxDevicePixels/Math.max(1,bounds.width*bounds.height*dpr*dpr)));
      if(budgetScale<1)view=grid({deviceWidth:Math.round(bounds.width*dpr*budgetScale),deviceHeight:Math.round(bounds.height*dpr*budgetScale),devicePixelRatio:dpr*budgetScale},settings().grid);
      if(!view){canvas.style.visibility='hidden';return;}
      canvas.width=Math.round(bounds.width*view.scale);canvas.height=Math.round(bounds.height*view.scale);
      canvas.style.width=bounds.width+'px';canvas.style.height=bounds.height+'px';
      const atlas=document.createElement('canvas');atlas.width=view.cellWidth*16;atlas.height=view.cellHeight*6;
      const context=atlas.getContext('2d'),config=settings();
      const family=getComputedStyle(element).fontFamily,weight=config.surface.weight+(view.scale<1.5?100:0);
      context.font=`${weight} 100px ${family}`;
      const size=Math.min(Math.round(config.surface.glyphCss*view.scale),Math.floor((view.cellWidth-2)/(context.measureText('0').width/100)));
      context.font=`${weight} ${size}px ${family}`;context.fillStyle='#fff';context.textAlign='center';context.textBaseline='middle';
      for(let slot=0;slot<96;slot++)context.fillText(slot===95?'·':String.fromCharCode(slot+32),(slot%16+.5)*view.cellWidth,(Math.floor(slot/16)+.5)*view.cellHeight);
      gl.bindTexture(gl.TEXTURE_2D,atlasTexture);gl.texImage2D(gl.TEXTURE_2D,0,gl.RGBA,gl.RGBA,gl.UNSIGNED_BYTE,atlas);
      draw();
    }
    function draw() {
      if(!started||!view||destroyed)return;
      const config=settings(),field=config.field||pattern(config.seed,config.look),card=config.composition.card,time=now();
      taps=taps.filter(tap=>time-tap[2]<3.6);wakes=wakes.filter(wake=>time-wake[2]<2.4);
      gl.useProgram(program);gl.viewport(0,0,canvas.width,canvas.height);gl.clearColor(0,0,0,0);gl.clear(gl.COLOR_BUFFER_BIT);
      two('viewport',canvas.width,canvas.height);two('pane',canvas.width/view.scale,canvas.height/view.scale);
      two('cellSize',view.cellWidth,view.cellHeight);two('gridOrigin',view.left,view.top);integer('columns',view.columns);
      one('seed',config.seed%65521);one('clockTime',animationTime);one('wallTime',time);
      const settling=1-smooth((elapsed-deadline)/config.power.settleS);
      one('reveal',running?smooth((elapsed-revealStart)/config.wind.revealS)*settling:1);
      one('fillRate',config.surface.fill);one('changeRate',config.wind.flick);
      four('flow',field.heading,field.speed,field.gustLength,field.gustWidth);
      four('bending',field.bend,field.bendScale,config.wind.lull,config.wind.streak);
      four('textureShape',config.wind.detail,config.wind.edge,config.wind.curl,config.wind.wisp);
      four('quiet',card.calmX,card.calmY,card.left,card.centerY);
      four('activity',config.strength.rest,config.glow.amount,0,0);
      one('tail',config.wind.tail);one('sweep',config.wind.sweep);one('evolve',config.wind.morph);one('friction',config.wind.friction);
      integer('tapCount',taps.length);integer('wakeCount',wakes.length);
      if(taps.length)gl.uniform4fv(location('taps'),new Float32Array(taps.flat()));
      if(wakes.length)gl.uniform4fv(location('wakes'),new Float32Array(wakes.flat()));
      const charset=charsets[config.look]||charsets.mixed;
      if(charset!==lastCharset){
        lastCharset=charset;const sequence=new Int32Array(64);
        [...charset].forEach((character,index)=>sequence[index]=character.charCodeAt(0)-32);
        gl.uniform1iv(location('sequence'),sequence);integer('sequenceLength',charset.length);
      }
      ink();gl.uniform3fv(location('glowColor'),config.glow.color.light);
      gl.drawArraysInstanced(gl.TRIANGLES,0,6,view.columns*view.rows);
    }
    function cancel() {clearTimeout(timeout);cancelAnimationFrame(frame);timeout=frame=undefined;}
    function schedule() {
      cancel();if(!started||destroyed||!running||!visible()||frozen)return;
      const config=settings();timeout=setTimeout(()=>{frame=requestAnimationFrame(tick);},taps.length?config.power.rippleFrameMs:config.power.frameMs);
    }
    function tick() {
      const tickTime=performance.now(),delta=clamp((tickTime-lastTick)/1000,0,.25);lastTick=tickTime;elapsed+=delta;
      const config=settings(),settling=1-smooth((elapsed-deadline)/config.power.settleS);
      animationTime+=delta*config.wind.energy*settling*smooth((elapsed-resumeStart)/config.power.resumeS);
      draw();frozen=settling===0&&taps.length===0&&wakes.length===0;opacity();schedule();
    }
    function wake() {
      const config=settings();deadline=elapsed+config.power.budgetS;
      if(frozen){frozen=false;resumeStart=elapsed;revealStart=elapsed;lastTick=performance.now();schedule();}
    }
    function position(event) {const bounds=element.getBoundingClientRect();return [event.clientX-bounds.left,event.clientY-bounds.top];}
    function move(event) {
      if(!running||event.pointerType!=='mouse')return;
      wake();const [x,y]=position(event),t=now(),sample={x,y,t};
      if(!lastPointer||t-lastPointer.t>.25){lastPointer=lastWake=sample;velocity=0;return;}
      const dt=Math.max(1/240,t-lastPointer.t),distance=Math.hypot(x-lastPointer.x,y-lastPointer.y);
      velocity+=(distance/dt-velocity)*Math.min(1,dt/.08);lastPointer=sample;
      const span=Math.hypot(x-lastWake.x,y-lastWake.y);
      if(span<7||t-lastWake.t<.02)return;
      const config=settings().touch,force=config.stir*(.3+.7*smooth((velocity-config.slowPxS)/(config.fastPxS-config.slowPxS)))*span/(span+35);
      if(force>.04)wakes.push([(x+lastWake.x)/2,(y+lastWake.y)/2,t,force]);
      wakes=wakes.slice(-48);lastWake=sample;
    }
    function tap(event) {
      if(!running||event.button!==0||event.target.closest('button,a,input,select,textarea,summary,[contenteditable]'))return;
      const [x,y]=position(event);tapNumber++;taps.push([x,y,now(),settings().touch.ripple]);
      taps=taps.slice(-6);wake();schedule();
    }
    function focusChanged() {lastTick=performance.now();opacity();schedule();}
    const host=element.parentElement,leave=()=>{lastPointer=lastWake=null;};
    function cleanup() {
      cancel();resizeObserver?.disconnect();host?.removeEventListener('pointermove',move);host?.removeEventListener('pointerdown',tap);host?.removeEventListener('pointerleave',leave);host?.removeEventListener('keydown',wake);
      window.removeEventListener('focus',focusChanged);window.removeEventListener('blur',focusChanged);document.removeEventListener('visibilitychange',focusChanged);
      if(gl){gl.deleteTexture(atlasTexture);gl.deleteProgram(program);gl.getExtension('WEBGL_lose_context')?.loseContext();}canvas.remove();
    }
    Promise.resolve().then(()=>{
      if(destroyed)return;
      try {
        gl=canvas.getContext('webgl2',{alpha:true,antialias:false,premultipliedAlpha:true,powerPreference:'low-power'});
        if(!gl)throw new Error('WebGL2 is unavailable');
        const vs=compile(gl,gl.VERTEX_SHADER,vertex),fs=compile(gl,gl.FRAGMENT_SHADER,fragment);
        program=gl.createProgram();gl.attachShader(program,vs);gl.attachShader(program,fs);gl.linkProgram(program);gl.deleteShader(vs);gl.deleteShader(fs);
        if(!gl.getProgramParameter(program,gl.LINK_STATUS))throw new Error(gl.getProgramInfoLog(program));
        gl.useProgram(program);atlasTexture=gl.createTexture();gl.bindTexture(gl.TEXTURE_2D,atlasTexture);
        gl.texParameteri(gl.TEXTURE_2D,gl.TEXTURE_MIN_FILTER,gl.NEAREST);gl.texParameteri(gl.TEXTURE_2D,gl.TEXTURE_MAG_FILTER,gl.NEAREST);gl.texParameteri(gl.TEXTURE_2D,gl.TEXTURE_WRAP_S,gl.CLAMP_TO_EDGE);gl.texParameteri(gl.TEXTURE_2D,gl.TEXTURE_WRAP_T,gl.CLAMP_TO_EDGE);integer('atlas',0);
        started=true;resize();resizeObserver=new ResizeObserver(resize);resizeObserver.observe(element);
        canvas.dataset.look=`${settings().look}/${(settings().seed>>>0).toString(16)}`;
        host?.addEventListener('pointermove',move,{passive:true});host?.addEventListener('pointerdown',tap,{passive:true});host?.addEventListener('pointerleave',leave,{passive:true});host?.addEventListener('keydown',wake);
        window.addEventListener('focus',focusChanged);window.addEventListener('blur',focusChanged);document.addEventListener('visibilitychange',focusChanged);
        opacity();onReady();schedule();
      }catch(error){cleanup();onError?.(error);}
    });
    return {
      setRunning(value){running=value;lastTick=performance.now();if(started){opacity();schedule();}},
      refresh(){if(!started)return;if(frozen){frozen=false;resumeStart=elapsed;lastTick=performance.now();}deadline=elapsed+settings().power.budgetS;draw();opacity(true);schedule();},
      destroy(){if(destroyed)return;destroyed=true;cleanup();}
    };
  }
  return {defaults,pattern,grid,chooseLook,create};
})();
export default GlyphWind;
