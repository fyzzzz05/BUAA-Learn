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

const track = document.querySelector('#case-track')
const controls = document.querySelector('.gallery-controls')
const [previousCase, nextCase] = controls.querySelectorAll('button')
const reducedMotion = matchMedia('(prefers-reduced-motion: reduce)')

function updateGalleryControls() {
  const end = track.scrollWidth - track.clientWidth
  controls.hidden = end <= 1
  previousCase.disabled = track.scrollLeft <= 1
  nextCase.disabled = track.scrollLeft >= end - 1
}

controls.addEventListener('click', event => {
  const button = event.target.closest('[data-case-step]')
  if (!button) return
  const start = track.children[0].offsetLeft
  const end = track.scrollWidth - track.clientWidth
  const positions = Array.from(track.children, card => Math.min(end, card.offsetLeft - start))
  const target = button === nextCase
    ? positions.find(position => position > track.scrollLeft + 1)
    : positions.findLast(position => position < track.scrollLeft - 1)
  if (target === undefined) return
  track.scrollTo({
    left: target,
    behavior: reducedMotion.matches ? 'instant' : 'smooth',
  })
})
track.addEventListener('scroll', updateGalleryControls, { passive: true })
new ResizeObserver(updateGalleryControls).observe(track)
updateGalleryControls()

for (const motion of document.querySelectorAll('.motion-demo')) {
  const stage = motion.querySelector('.motion-stage')
  const lessonWindow = motion.querySelector('.motion-window')
  const motionNotes = motion.querySelector('.motion-notes')
  const motionSource = motion.querySelector('.motion-source')
  const motionTarget = motion.querySelector('.motion-target')
  const cursor = motion.querySelector('.motion-cursor')
  const ring = motion.querySelector('.motion-ring')
  const playMotion = motion.querySelector('.motion-play')

  playMotion.hidden = false
  playMotion.addEventListener('click', async () => {
    if (playMotion.getAttribute('aria-disabled') === 'true') return
    playMotion.setAttribute('aria-disabled', 'true')
    playMotion.textContent = '演示播放中'
    motionNotes.hidden = false
    motionSource.hidden = true

    if (!reducedMotion.matches) {
      const stageRect = stage.getBoundingClientRect()
      const targetRect = motionTarget.getBoundingClientRect()
      const x = targetRect.left - stageRect.left + targetRect.width / 2
      const y = targetRect.top - stageRect.top + targetRect.height / 2
      const pointer = (left, top) => `translate(${left}px, ${top}px)`
      const end = pointer(x, y)
      const start = pointer(Math.max(12, x - 110), Math.min(stageRect.height - 28, y + 65))
      const windowRect = lessonWindow.getBoundingClientRect()
      lessonWindow.style.transformOrigin = `${x + stageRect.left - windowRect.left}px ${y + stageRect.top - windowRect.top}px`
      cursor.hidden = false
      ring.hidden = false
      const camera = lessonWindow.animate([
        { transform: 'scale(1)', offset: 0 },
        { transform: 'scale(1)', offset: 0.48, easing: 'ease-in-out' },
        { transform: 'scale(1.1)', offset: 0.8 },
        { transform: 'scale(1.1)', offset: 1 },
      ], { duration: 2400, fill: 'both' })
      const pointerMotion = cursor.animate([
        { transform: start, opacity: 0, offset: 0 },
        { transform: start, opacity: 1, offset: 0.4, easing: 'ease-in-out' },
        { transform: end, opacity: 1, offset: 0.86 },
        { transform: end, opacity: 1, offset: 1 },
      ], { duration: 2400, fill: 'both' })
      const pulse = ring.animate([
        { transform: pointer(x - 16, y - 16) + ' scale(.45)', opacity: 0.9 },
        { transform: pointer(x - 16, y - 16) + ' scale(1.25)', opacity: 0 },
      ], { delay: 2070, duration: 330, fill: 'forwards' })
      await camera.finished
      cursor.hidden = true
      ring.hidden = true
      for (const animation of [camera, pointerMotion, pulse]) animation.cancel()
    }

    motionNotes.hidden = true
    motionSource.hidden = false
    if (!reducedMotion.matches) {
      await motionSource.animate([{ opacity: 0 }, { opacity: 1 }], { duration: 180 }).finished
    }
    playMotion.removeAttribute('aria-disabled')
    playMotion.textContent = '重播演示'
  })
}
