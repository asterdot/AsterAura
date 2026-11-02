let refs = { base: '', cfg: () => null, state: () => null }
let bgStamp = String(Date.now())
let lastDark = null
let wired = false

export function bindTheme(next) { refs = next }

export function bgUrl() { return refs.base + '/api/background?v=' + bgStamp }
export function bustBackground() { bgStamp = String(Date.now()) }

export function themeCfg() {
  const cfg = refs.cfg()
  const state = refs.state()
  return (cfg && cfg.theme) || (state && state.theme) || { mode: 'follow', background: '' }
}

export function dshIsDark() {
  try {
    const doc = window.parent && window.parent !== window ? window.parent.document : null
    if (doc && doc.body) {
      const c = window.parent.getComputedStyle(doc.body).backgroundColor || ''
      const m = c.match(/(\d+),\s*(\d+),\s*(\d+)/)
      if (m) { const lum = (Number(m[1]) * 299 + Number(m[2]) * 587 + Number(m[3]) * 114) / 1000; return lum < 128 }
      if (doc.documentElement.dataset.theme) return doc.documentElement.dataset.theme === 'dark'
    }
  } catch {}
  return window.matchMedia && window.matchMedia('(prefers-color-scheme: dark)').matches
}

export function applyTheme() {
  const t = themeCfg()
  const mode = t.mode || 'follow'
  const wall = mode === 'acrylic' || mode === 'frosted'
  // 无背景图的玻璃模式：固定深色黑灰，别跟系统跑成白底糊白
  let dark
  if (mode === 'light') dark = false
  else if (mode === 'dark') dark = true
  else if (wall && !t.background) dark = true
  else dark = dshIsDark()
  const theme = dark ? 'dark' : 'light'
  if (document.body.dataset.theme !== theme) document.body.dataset.theme = theme
  if (wall) { if (document.body.dataset.wall !== mode) document.body.dataset.wall = mode }
  else if (document.body.dataset.wall) delete document.body.dataset.wall
  // 玻璃模式没背景图时打纯色黑灰底，跟主题深浅无关
  const plain = wall && !t.background
  if (plain) { if (document.body.dataset.wallplain !== '1') document.body.dataset.wallplain = '1' }
  else if (document.body.dataset.wallplain) delete document.body.dataset.wallplain
  let img = 'none'
  if (wall && t.background) img = t.background === 'local' ? 'url("' + bgUrl() + '")' : 'url("' + t.background + '")'
  if (document.body.style.getPropertyValue('--bg-image') !== img) document.body.style.setProperty('--bg-image', img)
  if (window.matchMedia && !wired) {
    wired = true
    window.matchMedia('(prefers-color-scheme: dark)').addEventListener?.('change', applyTheme)
  }
}

export function watchSystemDark() {
  setInterval(() => {
    const cur = dshIsDark()
    if (lastDark === null) { lastDark = cur; return }
    if (cur !== lastDark) { lastDark = cur; applyTheme() }
  }, 2000)
}
