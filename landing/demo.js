const content = document.querySelector('#demo-content')
const notes = content.innerHTML
const tabs = document.querySelectorAll('[data-view]')
const sources = [
  ['00:07:34', '分类、检测与分割', '分类回答图里是什么，检测还要指出它在哪里，分割则会标出每个像素属于哪个区域。'],
  ['00:12:56', '多模态如何理解图像', '图像和文字提供不同的信息。做图文检索时，模型把两者映射到同一个空间，让含义相近的图像和描述彼此靠近。'],
  ['00:14:15', '视觉 Transformer', '视觉 Transformer 先把图像切成小块，再用注意力机制判断哪些区域需要结合起来理解。'],
  ['00:21:30', '图文检索练习', '请选三组图文样本做检索。记录每组的匹配结果，再挑一个匹配错误，解释模型可能忽略了哪些信息。'],
]

function show(view, source = 0) {
  for (const tab of tabs) tab.setAttribute('aria-pressed', String(tab.dataset.view === view))
  if (view === 'notes') {
    content.innerHTML = notes
  } else if (view === 'tasks') {
    content.innerHTML = `<div class="detail-view"><h3>图文检索练习</h3><p>选取三组图文样本，对比检索结果，并解释一个匹配错误。</p><button type="button" class="quote-button" data-source="3">查看原话 <span aria-hidden="true">↗</span></button></div>`
  } else {
    const [time, title, quote] = sources[source]
    content.innerHTML = `<div class="detail-view"><time>${time}</time><h3>${title}</h3><blockquote>${quote}</blockquote><button type="button" class="quote-button" data-back>${returnTarget?.view === 'tasks' ? '返回作业与事项' : '返回时间轴'}</button></div>`
  }
}

let returnTarget
for (const tab of tabs) tab.addEventListener('click', () => {
  returnTarget = undefined
  show(tab.dataset.view)
})
content.addEventListener('click', event => {
  const source = event.target.closest('[data-source]')
  if (source) {
    returnTarget = { view: [...tabs].find(tab => tab.getAttribute('aria-pressed') === 'true').dataset.view, index: source.dataset.source }
    show('source', Number(source.dataset.source))
    content.querySelector('[data-back]').focus({ preventScroll: true })
  } else if (event.target.closest('[data-back]')) {
    show(returnTarget?.view ?? 'notes')
    const target = returnTarget ? content.querySelector(`[data-source="${returnTarget.index}"]`) : tabs[0]
    target.focus({ preventScroll: true })
  }
})


const reducedMotion = matchMedia('(prefers-reduced-motion: reduce)')
const film = document.querySelector('.cinema')
const stage = film.querySelector('.film-stage')
const screen = film.querySelector('.film-window')
const canvas = document.querySelector('#film-content')
const play = document.querySelector('#film-play')
const pointer = film.querySelector('.film-pointer')
const clickRing = film.querySelector('.film-click')
const progress = film.querySelector('.film-progress i')
const sceneTabs = [...document.querySelectorAll('[data-scene]')]
const captions = {
  review: '沿着时间轴，找回这一节课的脉络。',
  knowledge: '找到知识点，也找到老师讲到它的那一刻。',
  homework: '错过的作业和提醒，集中补上。',
}
const header = '<h3>图像理解与多模态</h3><p class="lesson-meta">第 4 讲 · 计算机视觉导论</p>'
function timeline(focused = false) {
  return header + '<div class="film-timeline">' + sources.slice(0, 3).map(([time,title],i) => `<div class="film-entry ${focused && i === 1 ? 'active' : ''}"><time>${time}</time><div><strong>${title}</strong><p>${['从识别整张图，到理解每一个像素。','让图像和语言在同一个空间里相遇。','用注意力建立图像区域之间的联系。'][i]}</p>${focused && i === 1 ? '<span class="film-pill" data-target>查看原话 ↗</span>' : ''}</div></div>`).join('') + '</div>'
}
function overview() {
  return header + '<div class="lesson-cover"><div><strong>课堂笔记已整理</strong><small>按课堂顺序，保留每个知识点的时间。</small></div><span class="film-action" data-target>继续阅读</span></div>'
}
function assignments() {
  return '<div class="film-heading"><h3>本节作业与事项</h3><span>已从课堂提取</span></div><p class="lesson-meta">第 4 讲 · 图像理解与多模态</p><div class="film-assignment"><strong>图文检索练习</strong><p>选取三组图文样本，对比检索结果，并解释一个匹配错误。</p><span class="film-pill" data-target>查看原话 ↗</span></div><div class="film-assignment"><strong>下次课带一个例子</strong><p>课堂将讨论检索失败的案例，提前准备一个你观察到的问题。</p></div>'
}
function quote(index) {
  const [time,title,text] = sources[index]
  return `<h3>${title}</h3><p class="lesson-meta">课堂原话</p><div class="film-quote"><time>${time}</time><blockquote>${text}</blockquote></div><p class="source-context">已定位到对应课堂时间</p>`
}
let scene = 'review'
let generation = 0
let running = false
let paused = false
const activeAnimations = new Set()
function animate(element, frames, duration, options = {}) {
  const animation = element.animate(frames, {duration:reducedMotion.matches ? 0 : duration, easing:'cubic-bezier(.22,.8,.25,1)', fill:'both', ...options})
  activeAnimations.add(animation)
  if (paused) animation.pause()
  return animation
}
async function tween(element, frames, duration, options) {
  const animation = animate(element,frames,duration,options)
  await animation.finished
  return animation
}
async function hold(duration) {
  const a = await tween(canvas,[{opacity:1},{opacity:1}],duration)
  a.cancel(); activeAnimations.delete(a)
}
function cancel() {
  generation++
  for (const a of activeAnimations) a.cancel()
  activeAnimations.clear()
  running = false; paused = false
  pointer.style.opacity = '0'
}
function reset() {
  canvas.innerHTML = scene === 'review' ? overview() : scene === 'knowledge' ? timeline(true) : header + '<div class="lesson-cover"><div><strong>课堂材料已就绪</strong><small>笔记、作业与重要事项</small></div><span class="film-action" data-target>查看课次</span></div>'
  document.querySelector('#film-caption').textContent = captions[scene]
  film.id = `${scene}-demo`
  play.innerHTML = '播放演示 <span aria-hidden="true">▷</span>'
}
async function pointAndClick() {
  const target = canvas.querySelector('[data-target]')
  const bounds = stage.getBoundingClientRect()
  const rect = target.getBoundingClientRect()
  const x = rect.left - bounds.left + rect.width * .65
  const y = rect.top - bounds.top + rect.height * .6
  const move = (x,y) => `translate(${x}px,${y}px)`
  await tween(pointer,[{transform:move(x+35,y+48),opacity:0},{transform:move(x,y),opacity:1}],850)
  await hold(180)
  await tween(clickRing,[{transform:move(x-17,y-17)+' scale(.4)',opacity:.6},{transform:move(x-17,y-17)+' scale(1.3)',opacity:0}],340)
  await tween(pointer,[{opacity:1},{opacity:0}],180)
}
async function reveal(html, stagger = false) {
  const out = await tween(canvas,[{opacity:1,transform:'translateY(0)'},{opacity:0,transform:'translateY(-6px)'}],180)
  out.cancel(); activeAnimations.delete(out)
  canvas.innerHTML = html
  await tween(canvas,[{opacity:0,transform:'translateY(10px)'},{opacity:1,transform:'translateY(0)'}],420)
  if(stagger) {
    const rows = [...canvas.querySelectorAll('.film-entry,.film-assignment')]
    await Promise.all(rows.map((row,i) => tween(row,[{opacity:0,transform:'translateY(14px)'},{opacity:1,transform:'translateY(0)'}],480,{delay:i*180})))
  }
}
async function run() {
  cancel(); reset()
  running = true
  const token = generation
  play.innerHTML = '暂停 <span aria-hidden="true">Ⅱ</span>'
  if(reducedMotion.matches) {
    canvas.innerHTML = scene === 'review' ? timeline() : scene === 'knowledge' ? quote(1) : assignments()
    running=false; play.textContent='重播演示'; return
  }
  try {
    animate(progress,[{transform:'scaleX(0)'},{transform:'scaleX(1)'}],scene === 'homework' ? 10500 : 6800,{easing:'linear'})
    await hold(650)
    await pointAndClick()
    await reveal(scene === 'review' ? timeline() : scene === 'knowledge' ? quote(1) : assignments(),scene !== 'knowledge')
    await tween(screen,[{transform:'scale(1)'},{transform:'scale(1.035)'}],950)
    await hold(1200)
    if(scene === 'homework') {
      await pointAndClick()
      await reveal(quote(3))
      await hold(1400)
    }
    await tween(screen,[{transform:'scale(1.035)'},{transform:'scale(1)'}],700)
    await tween(progress,[{transform:getComputedStyle(progress).transform},{transform:'scaleX(1)'}],180)
    running=false
    play.innerHTML='重播演示 <span aria-hidden="true">↻</span>'
  } catch(error) {
    if(token === generation) {
      cancel(); reset()
      console.error('演示播放失败',error)
    }
  }
}
play.addEventListener('click', () => {
  if (!running) { run(); return }
  paused = !paused
  for (const a of activeAnimations) if(a.playState !== 'finished') paused ? a.pause() : a.play()
  play.innerHTML = paused ? '继续播放 <span aria-hidden="true">▷</span>' : '暂停 <span aria-hidden="true">Ⅱ</span>'
})
for(const tab of sceneTabs) tab.addEventListener('click', () => {
  cancel(); scene=tab.dataset.scene
  for(const item of sceneTabs) item.setAttribute('aria-pressed',String(item === tab))
  reset(); run()
})
document.addEventListener('visibilitychange', () => {
  if(document.hidden && running && !paused) play.click()
})
reducedMotion.addEventListener('change', () => { cancel(); reset() })
reset()
