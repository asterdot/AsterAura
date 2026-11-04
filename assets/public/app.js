// 同一份前端会被挂在 DSH 的 /aurachat/ 下，也会被固定端口挂在根路径，所以顺着当前地址推，别写死。
import { esc, pad2, fmtClock, fmtDay, dayKey, dayLabel, BARS, fmtDur, greet } from './js/format.js'
import { bindTheme, bgUrl, bustBackground, applyTheme, watchSystemDark } from './js/theme.js'
import { TONES, playSound as playTone } from './js/sound.js'
import { encodeWav } from './js/audio.js'
import { createMoodView } from './js/views/mood.js'
import { createOverviewView } from './js/views/overview.js'
import { createLogsView } from './js/views/logs.js'

const BASE = (location.pathname.replace(/\/?$/, '').replace(/\/index\.html$/, ''))
const TOKEN_KEY = 'aurachat_token'
let token = localStorage.getItem(TOKEN_KEY) || ''
let botState = null      // /api/state 的结果
let me = null            // 当前登录的人
let cfg = null           // 完整配置（主人）
let models = []          // [{provider, model}]
let personaList = []     // [{id, name, tags}]
let currentView = 'chat'
let stream = null
// 聊天记录分页状态：服务端一片 100 条，往上翻一页取比 oldestAt 更早的 100 条
let historyState = { oldestAt: 0, hasMore: false, loading: false }

const $ = (s, el) => (el || document).querySelector(s)
const $$ = (s, el) => Array.from((el || document).querySelectorAll(s))

function api(path, opts = {}) {
  const headers = Object.assign({}, opts.headers || {})
  if (token) headers.authorization = 'Bearer ' + token
  if (opts.body !== undefined && !(opts.body instanceof Blob)) {
    headers['content-type'] = 'application/json'
    opts.body = JSON.stringify(opts.body)
  }
  return fetch(BASE + path, { ...opts, headers }).then(async (r) => {
    if (r.status === 401) { setToken(''); }
    const t = await r.text()
    try { return JSON.parse(t) } catch (e) { return { ok: false, reason: t.slice(0, 200) || ('HTTP ' + r.status) } }
  }).catch((e) => ({ ok: false, reason: e.message }))
}

function setToken(t) { token = t || ''; if (t) localStorage.setItem(TOKEN_KEY, t); else localStorage.removeItem(TOKEN_KEY) }

function toast(msg, kind = '') {
  const box = $('#toasts')
  const el = document.createElement('div')
  el.className = 'toast ' + kind
  el.textContent = msg
  box.appendChild(el)
  setTimeout(() => el.remove(), 3200)
}

const bot = () => (botState && botState.botName) || cfg?.botName || '大肥鱼'
const botTag = () => (botState && botState.botTag) || cfg?.botTag || ''
const ownerName = () => (botState && botState.ownerName) || cfg?.ownerName || '主人'
const isOwner = () => Boolean(me && me.role === 'owner')

bindTheme({ base: BASE, cfg: () => cfg, state: () => botState })
watchSystemDark()

// 自动化测试用的把手
window.__aura = {
  get cfg() { return cfg },
  get me() { return me },
  get state() { return botState },
  applyTheme,
  playSound,
  bustBackground,
}

function setAvatar(el, hasImg) {
  if (!el) return
  el.innerHTML = '<img src="' + (hasImg ? BASE + '/api/avatar?ts=' + Date.now() : BASE + '/icon.svg') + '" alt="" />'
}
function refreshAvatar() {
  const el = $('#rail-avatar')
  if (!el) return
  const img = new Image()
  img.onload = () => setAvatar(el, true)
  img.onerror = () => setAvatar(el, false)
  img.src = BASE + '/api/avatar?ts=' + Date.now()
}

function showGate(mode, msg = '') {
  $('#gate').classList.remove('hidden')
  $('#app').classList.add('hidden')
  const wiz = $('#wizard'), form = $('#gate-form')
  wiz.classList.toggle('hidden', mode !== 'setup')
  form.classList.toggle('hidden', mode !== 'login')
  $('#gate-sub').textContent = mode === 'setup' ? '第一次来，先把 Ta 安顿好。' : ''
  $('#gate-hello').textContent = mode === 'login' ? greet() : ''
  if (msg) { (mode === 'setup' ? $('#wizard-msg') : $('#gate-msg')).textContent = msg }
  setAvatar($('#gate-logo'), false)
  applyTitle()
}

const WIZ_LAST = 5
let wizStep = 1
let wizAvatar = null
let wizPersona = 'dafeiyu'

function initWizard() {
  wizStep = 1
  showWizStep(1)
  api('/api/personas').then((r) => {
    personaList = (r.ok && r.personas) || []
    renderWizPersonas()
  })
  api('/api/models').then((r) => {
    models = flattenModels(r.ok ? r.models : [])
    const sel = $('#w-model')
    sel.innerHTML = '<option value="__default__">默认（跟随 DSH）</option>' + models.map((m) => '<option value="' + esc(m.provider + '::' + m.model) + '">' + esc(m.provider + ' / ' + m.model) + '</option>').join('')
  })
  setAvatar($('#w-avatar-preview'), false)

  const form = $('#wizard')
  form.addEventListener('click', (e) => {
    const next = e.target.closest('[data-next]')
    const prev = e.target.closest('[data-prev]')
    if (next && wizStep < WIZ_LAST) showWizStep(wizStep + 1)
    if (prev && wizStep > 1) showWizStep(wizStep - 1)
  })
  $('#w-avatar').onchange = (e) => {
    const f = e.target.files && e.target.files[0]
    if (!f) return
    const rd = new FileReader()
    rd.onload = () => {
      wizAvatar = { data: String(rd.result).split(',')[1] || '', mime: f.type || 'image/png' }
      const box = $('#w-avatar-preview')
      box.innerHTML = '<img src="' + rd.result + '" alt="" />'
    }
    rd.readAsDataURL(f)
  }
  $('#w-avatar-clear').onclick = () => { wizAvatar = null; setAvatar($('#w-avatar-preview'), false) }
  form.onsubmit = async (e) => {
    e.preventDefault()
    const username = $('#w-username').value.trim()
    const password = $('#w-password').value
    if (password.length < 6) { $('#wizard-msg').textContent = '密码至少 6 位'; return }
    const mv = $('#w-model').value
    const mm = mv && mv !== '__default__' ? mv.split('::') : null
    let personaId = wizPersona
    if (personaId === '__new') {
      const pn = ($('#w-pn-name')?.value || '').trim()
      if (!pn) { $('#wizard-msg').textContent = '先给人设起个名字'; $('#w-pn-name')?.focus(); return }
      const saved = await api('/api/personas/save', {
        method: 'POST',
        body: { name: pn, tags: ($('#w-pn-tags')?.value || '').trim(), body: ($('#w-pn-body')?.value || '').trim() },
      })
      if (!saved.ok) { $('#wizard-msg').textContent = saved.reason || '人设没存上'; return }
      personaId = saved.persona?.id || pn
      personaList = [{id: personaId, name: pn, tags: []}, ...personaList.filter((p) => p.id !== personaId)]
      wizPersona = personaId
    }
    const body = {
      username, password,
      botName: $('#w-name').value.trim() || undefined,
      botTag: $('#w-tag').value.trim() || undefined,
      ownerName: $('#w-owner').value.trim() || undefined,
      persona: wizPersona,
      themeMode: 'follow',
      model: mm ? { provider: mm[0], model: mm.slice(1).join('::'), reasoningEffort: $('#w-effort').value } : undefined,
      avatar: wizAvatar || undefined,
    }
    const r = await api('/api/setup', { method: 'POST', body })
    if (!r.ok) { $('#wizard-msg').textContent = r.reason || '没建成，再试一次'; return }
    await doLogin(username, password)
  }
}

function renderWizPersonas() {
  const box = $('#w-persona-list')
  if (!box) return
  box.innerHTML = (personaList.length ? personaList.map((p) => (
    '<div class="persona-opt' + (p.id === wizPersona ? ' on' : '') + '" data-pid="' + esc(p.id) + '">' +
    '<b>' + esc(p.name || p.id) + '</b>' +
    '<span>' + esc((p.tags || []).join(' · ')) + '</span></div>'
  )).join('') : '') +
    '<div class="persona-opt new' + (wizPersona === '__new' ? ' on' : '') + '" data-pid="__new">' +
    '<b>＋ 自己写一个</b><span>不用预设，现场告诉Ta该是什么样的人</span></div>'
  box.onclick = (e) => {
    const el = e.target.closest('[data-pid]')
    if (!el) return
    wizPersona = el.dataset.pid
    const custom = $('#w-persona-new')
    if (custom) custom.classList.toggle('hidden', wizPersona !== '__new')
    if (wizPersona === '__new' && $('#w-pn-name')) setTimeout(() => $('#w-pn-name').focus(), 30)
    renderWizPersonas()
  }
}

function showWizStep(n) {
  wizStep = Math.max(1, Math.min(WIZ_LAST, n))
  $$('#wizard .w-step').forEach((s) => s.classList.toggle('hidden', Number(s.dataset.step) !== wizStep))
}

async function doLogin(username, password) {
  const r = await api('/api/login', { method: 'POST', body: { username, password } })
  if (!r.ok) {
    const msg = r.reason || '登录失败'
    if ($('#wizard') && !$('#wizard').classList.contains('hidden')) $('#wizard-msg').textContent = msg
    else $('#gate-msg').textContent = msg
    return false
  }
  setToken(r.token)
  me = r.me || null
  await boot()
  return true
}

async function enterApp() {
  $('#gate').classList.add('hidden')
  $('#app').classList.remove('hidden')
  applyTitle()
  applyFavicon()
  buildNav()
  refreshAvatar()
  await loadConfig()
  await loadModels()
  await api('/api/personas').then((r) => { personaList = (r.ok && r.personas) || [] })
  if (!queryOf('nosse')) connectStream()
  switchView(currentView)
}

const NAVS = [
  { id: 'chat', label: '聊天' },
  { id: 'diary', label: '空间' },
  { id: 'mood', label: '心情' },
  { id: 'overview', label: '概览', owner: true },
  { id: 'memory', label: '记忆', owner: true },
  { id: 'settings', label: '设置' },
  { id: 'logs', label: '日志', owner: true },
]

function buildNav() {
  const nav = $('#nav')
  nav.innerHTML = NAVS.filter((n) => !n.owner || isOwner()).map((n) => '<button data-view="' + n.id + '">' + n.label + '</button>').join('')
  nav.onclick = (e) => { const b = e.target.closest('[data-view]'); if (b) switchView(b.dataset.view) }
  syncNav()
}
function syncNav() { $$('#nav [data-view]').forEach((b) => b.classList.toggle('on', b.dataset.view === currentView)) }

const views = {
  mood: createMoodView({
    $, $$, api, esc, isOwner, toast, paneShell, refreshMoodPill,
    getCfg: () => cfg,
    setCfg: (c) => { cfg = c },
  }),
  overview: createOverviewView({ $, api, esc, isOwner, toast, paneShell, getCfg: () => cfg }),
  logs: createLogsView({ $, api, esc, toast, paneShell }),
}

function switchView(id) {
  currentView = id
  syncNav()
  const main = $('#main')
  main.innerHTML = ''
  if (id === 'chat') { renderChat(main); markReadSoon() }
  else if (id === 'diary') renderDiary(main)
  else if (id === 'mood') views.mood.renderMood(main)
  else if (id === 'overview') views.overview.renderOverview(main)
  else if (id === 'memory') renderMemory(main)
  else if (id === 'settings') renderSettings(main)
  else if (id === 'logs') views.logs.renderLogs(main)
}

// D3 已读回执：进入聊天页就把"我读到了"发出去；收到对端 read 事件时刷新角标
let readTimer = null
function markReadSoon() {
  if (readTimer) clearTimeout(readTimer)
  readTimer = setTimeout(async () => {
    try { await api('/api/read', { method: 'POST' }) } catch { /* 不影响聊天 */ }
    refreshReadMarks()
  }, 400)
}
async function refreshReadMarks() {
  try {
    const r = await api('/api/read')
    if (!r.ok) return
    const peer = Number(r.peerReadAt || 0)
    const box = $('#msgs')
    if (!box) return
    // 对方已读时间之后我发的消息标"已读"
    box.querySelectorAll('.msg.me').forEach((el) => {
      const mark = el.querySelector('.read-mark')
      if (!mark) return
      const mine = Number(el.dataset.t || 0) <= peer
      mark.classList.toggle('on', mine)
      mark.title = mine ? '已读' : '未读'
    })
  } catch { /* 静默 */ }
}

async function loadConfig() {
  if (!isOwner()) return
  const r = await api('/api/config')
  if (r.ok) { cfg = r.config; applyTheme() }
}
async function loadModels() {
  const r = await api('/api/models')
  models = flattenModels(r.ok ? r.models : [])
}
function flattenModels(raw) {
  const out = []
  const push = (x) => { if (x && x.model) out.push({ provider: x.provider || '', model: x.model }) }
  if (!Array.isArray(raw)) return out
  for (const x of raw) {
    if (!x) continue
    if (Array.isArray(x.models)) { for (const m of x.models) push({ provider: x.provider, model: typeof m === 'string' ? m : m.model || m.id }) }
    else push(x)
  }
  return out
}
function splitModelValue(v) {
  const s = String(v || '')
  const i = s.indexOf('::')
  if (i < 0) return { provider: '', model: '' }
  return { provider: s.slice(0, i), model: s.slice(i + 2) }
}

function renderChat(main) {
  main.innerHTML =
    '<div class="chat">' +
    '<div class="chat-head"><span class="name" id="head-name"></span><span class="tag" id="head-tag"></span>' +
    '<button class="mood-pill" id="head-mood"><span id="head-mood-l"></span> <b id="head-mood-v"></b></button></div>' +
    '<div class="msgs" id="msgs"></div>' +
    '<div class="typing" id="typing"></div>' +
    '<div class="composer">' +
    '<div class="composer-tools"><button class="tool" id="tool-image">图片</button>' +
    '<button class="tool" id="tool-sticker">表情</button>' +
    '<button class="tool" id="tool-voice">语音</button></div>' +
    '<div class="composer-row"><textarea id="input" rows="1" placeholder="说点什么…（回车发送，Shift+回车换行）"></textarea>' +
    '<button class="primary" id="send">发送</button></div>' +
    '<input type="file" id="image-file" accept="image/*" hidden />' +
    '</div></div>'
  $('#head-name').textContent = bot()
  $('#head-tag').textContent = botTag()
  $('#head-mood').onclick = () => switchView('mood')
  refreshMoodPill()
  $('#send').onclick = sendText
  $('#input').addEventListener('keydown', (e) => { if (e.key === 'Enter' && !e.shiftKey) { e.preventDefault(); sendText() } })
  let pingTimer = null
  let lastPing = 0
  $('#input').addEventListener('input', () => {
    const now = Date.now()
    if (now - lastPing < 2000) return
    lastPing = now
    api('/api/typing', { method: 'POST', body: { typing: true } }).catch(() => {})
    clearTimeout(pingTimer)
    pingTimer = setTimeout(() => { lastPing = 0 }, 3000)
  })
  $('#tool-image').onclick = () => $('#image-file').click()
  $('#image-file').onchange = sendImage
  $('#tool-sticker').onclick = (e) => openStickerPicker(e.currentTarget)
  setupVoiceButton()
  loadMessages()
}

async function refreshMoodPill() {
  const el = $('#head-mood-v')
  if (!el) return
  const r = await api('/api/mood')
  if (r.ok && r.state) {
    // 胶囊上不放分数：分数是给人看的仪表，不是她的心情。
    // 只用一个字概括，点进去才看三个轴。
    const d = Number(r.state.dopamine) || 50
    const c = Number(r.state.cortisol) || 20
    const s = Number(r.state.serotonin) || 50
    const word = c >= 70 ? '紧' : (c >= 55 ? '烦' : (d >= 70 ? '兴' : (s <= 30 ? '躁' : (d < 30 ? '淡' : '常'))))
    el.textContent = word
    const l = $('#head-mood-l'); if (l) l.textContent = '心情'
  }
}

async function loadMessages() {
  // 一个分片文件 = 一页：首屏只渲染最新那片，往上翻才取上一片
  const r = await api('/api/messages?file=last')
  const box = $('#msgs')
  if (!box) return
  box.innerHTML = ''
  historyState = { oldestAt: 0, hasMore: false, loading: false, index: 0, total: 0 }
  if (!r.ok) { box.innerHTML = '<div class="empty">读不到消息：' + esc(r.reason || '') + '</div>'; return }
  const list = r.messages || []
  if (r.file) { historyState.index = Number(r.file.index) || 0; historyState.total = Number(r.file.total) || 0 }
  if (!list.length) { box.innerHTML = '<div class="empty">还没说过话。打个招呼吧。</div>'; return }
  for (const m of list) appendMessage(m, false)
  historyState.oldestAt = r.oldestAt || (list[0] && list[0].t) || 0
  historyState.hasMore = Boolean(r.hasMore)
  renderHistoryMore()
  scrollDown()
  refreshReadMarks()
}

// 「查看更早的记录」按钮：只在还有更早的消息时出现，位置固定在列表最上面
function renderHistoryMore() {
  const box = $('#msgs')
  if (!box) return
  const old = box.querySelector('.more-history')
  if (old) old.remove()
  if (!historyState.hasMore) return
  const btn = document.createElement('button')
  btn.className = 'more-history'
  // 一个文件就是一页
  btn.textContent = '查看更早的记录'
  btn.onclick = () => loadOlder(btn)
  box.insertBefore(btn, box.firstChild)
}

async function loadOlder(btn) {
  if (historyState.loading) return
  historyState.loading = true
  if (btn) { btn.textContent = '正在取…'; btn.disabled = true }
  const box = $('#msgs')
  const keep = box ? box.scrollHeight - box.scrollTop : 0
  const r = await api('/api/messages?file=prev&before=' + encodeURIComponent(historyState.oldestAt || 0))
  historyState.loading = false
  if (!r || !r.ok) {
    if (btn) { btn.textContent = '查看更早的记录'; btn.disabled = false }
    return toast(r?.reason || '没取到更早的记录', 'err')
  }
  const list = r.messages || []
  if (r.file) { historyState.index = Number(r.file.index) || 0; historyState.total = Number(r.file.total) || 0 }
  if (r.oldestAt) historyState.oldestAt = r.oldestAt
  if (!list.length) historyState.hasMore = false
  else historyState.hasMore = Boolean(r.hasMore)
  if (box) {
    const more = box.querySelector('.more-history')
    if (more) more.remove()
    // 从后往前插到最前面，保证 DOM 顺序和原来的列表一致
    for (let i = list.length - 1; i >= 0; i--) prependMessage(list[i])
    reflowDaySeparators()
    box.scrollTop = box.scrollHeight - keep
  }
  renderHistoryMore()
}

function mediaUrl(m) {
  if (!m || !m.mediaId) return ''
  return BASE + '/api/media/' + encodeURIComponent(m.mediaId) + '?token=' + encodeURIComponent(token)
}

// 表情消息里存的历史值有两种：裸 md5，和旧写入路径存下来的文件名（manual-<md5>.jpg）。
// 后者拼进 /api/sticker/ 永远取不到，所以两种都统一抽成 md5。
const MD5_RE = /[0-9a-f]{32}/i
function stickerMd5(v) {
  const s = String(v == null ? '' : v).trim()
  if (/^[0-9a-f]{32}$/i.test(s)) return s.toLowerCase()
  const hit = s.match(MD5_RE)
  return hit ? hit[0].toLowerCase() : ''
}

// 图片取不到时的兜底。服务端对不存在的表情/媒体回的是 200 + application/json，
// <img> 只会解码失败、画成破图（人类看到的「图片已损坏」），这里换成一句占位文字。
function guardMediaImg(img, label) {
  const swap = () => {
    if (!img.isConnected) return
    const span = document.createElement('span')
    span.className = 'expired'
    span.textContent = label
    img.replaceWith(span)
  }
  img.addEventListener('error', swap, { once: true })
  if (img.complete && img.naturalWidth === 0) swap()
}

function buildMessageNode(m) {
  const mine = m.who === 'user'
  const wrap = document.createElement('div')
  wrap.className = 'msg ' + (mine ? 'me' : 'her')
  wrap.dataset.t = String(m.t || '')
  if (m.id) wrap.dataset.id = String(m.id)
  let inner = ''
  // D4 引用块：[引用 …] 开头的消息渲染成引用样式
  const refMatch = (m.kind === 'text' && typeof m.text === 'string') ? m.text.match(/^\[引用 (.+?)\]\n([\s\S]*)$/) : null
  if (refMatch) {
    inner += '<div class="quote-block">引用：' + esc(refMatch[1]) + '</div>'
  }
  if (m.kind === 'voice') {
    const src = m.mediaId ? mediaUrl(m) : (m.voice ? 'data:' + (m.voiceMime || 'audio/mpeg') + ';base64,' + m.voice : '')
    inner = src
      ? '<div class="voice-bar" data-vid="' + esc(m.id || '') + '">' +
        '<button class="vb-play" data-voice-play aria-label="播放"></button>' +
        '<span class="vb-bars">' + BARS + '</span>' +
        '<span class="vb-dur">' + (m.dur ? fmtDur(m.dur) : '') + '</span>' +
        '<audio preload="metadata" src="' + src + '"></audio></div>' +
        (m.heard ? '<div class="heard">听成了：' + esc(m.heard) + '</div>' : '')
      : (m.heard ? '<div class="heard">听成了：' + esc(m.heard) + '</div>' : esc(m.text || '[语音已过期]'))
  } else if (m.kind === 'sticker') {
    const md5 = stickerMd5(m.sticker)
    inner += md5
      ? '<img data-media-fallback="sticker" src="' + BASE + '/api/sticker/' + md5 + '" alt="' + esc(m.name || '表情') + '" />'
      : '<span class="expired">表情已经不在了</span>'
  } else if (m.kind === 'image') {
    const src = m.mediaId ? mediaUrl(m) : (m.image ? 'data:' + (m.imageMime || 'image/png') + ';base64,' + m.image : '')
    inner += src ? '<img data-media-fallback="image" src="' + src + '" alt="图片" />' : '<span class="expired">图片已经过期了</span>'
  } else if (refMatch) {
    inner += esc(refMatch[2])
  } else {
    inner += esc(m.text || '')
  }
  if (mine && m.kind === 'image' && cfg?.stickers?.steal !== false) inner += '<button class="collect" data-collect="' + esc(m.id || '') + '">收进表情库</button>'
  wrap.innerHTML = (mine ? '' : '<div class="sender">' + esc(bot()) + '</div>') +
    '<div class="bubble"><div class="body">' + inner + '</div><span class="meta">' + esc(fmtDayClock(m.t)) + (mine ? '<i class="read-mark" data-r="me"></i>' : '<i class="read-mark" data-r="her"></i>') + '</span></div>'
  wrap.querySelectorAll('img[data-media-fallback]').forEach((img) => {
    guardMediaImg(img, img.dataset.mediaFallback === 'sticker' ? '表情已经不在了' : '图片已经过期了')
  })
  wrap.addEventListener('contextmenu', (e) => showMsgMenu(e, m, wrap, refMatch))
  return wrap
}

// D1：气泡时间——今天只显示 HH:mm，隔天带日期
function fmtDayClock(t) {
  if (!t) return ''
  const d = new Date(Number(t))
  const now = new Date()
  const sameDay = d.getFullYear() === now.getFullYear() && d.getMonth() === now.getMonth() && d.getDate() === now.getDate()
  const hm = pad2(d.getHours()) + ':' + pad2(d.getMinutes())
  return sameDay ? hm : (d.getMonth() + 1) + '/' + d.getDate() + ' ' + hm
}

// D4/D5：右键/长按消息 → 引用整句；选中文字 → 浮出"引用选中/复制"
function showMsgMenu(e, m, wrap, refMatch) {
  e.preventDefault()
  const sel = String(window.getSelection() || '')
  const menu = document.createElement('div')
  menu.className = 'msg-menu'
  const items = []
  if (sel && sel.length > 0 && m.kind === 'text') {
    items.push(['引用选中', () => startReply(m, sel)])
    items.push(['复制选中', () => navigator.clipboard?.writeText(sel).then(() => toast('复制了'))])
  }
  if (m.kind === 'text') items.push(['引用整句', () => startReply(m, refMatch ? refMatch[2] : m.text)])
  items.push(['复制整句', () => navigator.clipboard?.writeText(refMatch ? refMatch[2] : (m.text || '')).then(() => toast('复制了'))])
  menu.innerHTML = items.map((it, i) => '<button data-i="' + i + '">' + esc(it[0]) + '</button>').join('')
  menu.style.left = Math.min(e.clientX, window.innerWidth - 140) + 'px'
  menu.style.top = Math.min(e.clientY, window.innerHeight - items.length * 36 - 12) + 'px'
  document.body.appendChild(menu)
  const close = () => { menu.remove(); document.removeEventListener('click', close) }
  setTimeout(() => document.addEventListener('click', close), 0)
  menu.onclick = (ev) => {
    const b = ev.target.closest('button')
    if (b) items[Number(b.dataset.i)][1]()
    close()
  }
}

// 引用状态：输入框上方显示一条引用条
let replyCtx = null
function startReply(m, quoted) {
  replyCtx = { id: m.id || '', text: quoted || m.text || '' }
  renderReplyBar()
}
function cancelReply() {
  replyCtx = null
  renderReplyBar()
}
function renderReplyBar() {
  let bar = $('#reply-bar')
  const inp = $('#input')
  if (!replyCtx) { bar && bar.remove(); return }
  if (!bar) {
    bar = document.createElement('div')
    bar.id = 'reply-bar'
    bar.innerHTML = '<span class="rb-text"></span><button class="rb-cancel">×</button>'
    ;(inp && inp.parentElement ? inp.parentElement : document.body).appendChild(bar)
    bar.querySelector('.rb-cancel').onclick = cancelReply
  }
  bar.querySelector('.rb-text').textContent = '引用：' + (replyCtx.text || '').slice(0, 60)
}

// 日期分隔条：同一天的消息只标一个「今天 / 昨天 / 日期」
function daySepNode(t) {
  const el = document.createElement('div')
  el.className = 'day-sep'
  el.textContent = dayLabel(t)
  return el
}

// 按 DOM 里消息的实际顺序重排日期分隔条（追加 / 前插 / 过期裁剪后都调一次）
function reflowDaySeparators() {
  const box = $('#msgs')
  if (!box) return
  box.querySelectorAll('.day-sep').forEach((el) => el.remove())
  let prev = ''
  box.querySelectorAll('.msg').forEach((el) => {
    const k = dayKey(Number(el.dataset.t || 0))
    if (k !== prev) { box.insertBefore(daySepNode(Number(el.dataset.t || 0)), el); prev = k }
  })
}

function appendMessage(m, scroll = true) {
  const box = $('#msgs')
  if (!box) return
  if (m?.id && box.querySelector('[data-id="' + cssId(m.id) + '"]')) return
  const empty = box.querySelector('.empty')
  if (empty) empty.remove()
  box.appendChild(buildMessageNode(m))
  reflowDaySeparators()
  if (scroll) scrollDown()
}

// 消息 id 里可能有特殊字符，塞进属性选择器前先转义
function cssId(id) { return String(id).replace(/["\\]/g, '\\$&') }

// 往前翻页时插到「查看更早」按钮的后面（按钮永远在最上面）
function prependMessage(m) {
  const box = $('#msgs')
  if (!box) return
  if (m?.id && box.querySelector('[data-id="' + cssId(m.id) + '"]')) return
  const more = box.querySelector('.more-history')
  box.insertBefore(buildMessageNode(m), more ? more.nextSibling : box.firstChild)
}

function scrollDown() { const box = $('#msgs'); if (box) box.scrollTop = box.scrollHeight }
function setTyping(on) { const t = $('#typing'); if (t) t.textContent = on ? bot() + ' 正在输入…' : '' }

async function sendText() {
  const inp = $('#input')
  const text = inp.value.trim()
  if (!text) return
  inp.value = ''
  inp.style.height = 'auto'
  api('/api/typing', { method: 'POST', body: { typing: false } }).catch(() => {})
  const body = { text }
  if (replyCtx) { body.replyTo = replyCtx; cancelReply() }
  const r = await api('/api/messages', { method: 'POST', body })
  if (!r.ok) toast(r.reason || '没发出去', 'err')
  else playSound('out')
}

async function sendImage(e) {
  const f = e.target.files && e.target.files[0]
  e.target.value = ''
  if (!f) return
  if (f.size > 10 * 1024 * 1024) return toast('图片太大了（最多 10MB）', 'err')
  const rd = new FileReader()
  rd.onload = async () => {
    const data = String(rd.result).split(',')[1] || ''
    const r = await api('/api/messages', { method: 'POST', body: { text: '', images: [{ data, mediaType: f.type || 'image/png', name: f.name }] } })
    if (!r.ok) toast(r.reason || '没发出去', 'err')
  }
  rd.readAsDataURL(f)
}

function connectStream() {
  if (stream) stream.close()
  stream = new EventSource(BASE + '/api/stream?token=' + encodeURIComponent(token))
  stream.onmessage = (e) => {
    let ev
    try { ev = JSON.parse(e.data) } catch (err) { return }
    if (ev.type === 'message') {
      const m = ev.message || {}
      if (m.who === 'bot') playSound('in')
      if (!ev.chatId || !me || String(ev.chatId) === String(me.subject)) {
        if (currentView === 'chat') appendMessage(m)
      }
    } else if (ev.type === 'typing') {
      if (currentView === 'chat') setTyping(Boolean(ev.on))
    } else if (ev.type === 'read') {
      // D3：对端读了 → 刷新"已读"角标
      if (currentView === 'chat') refreshReadMarks()
    } else if (ev.type === 'history') {
      // 服务端过期清理 / 分片边界变了：把客户端里比 droppedBefore 更早的气泡也删掉
      if (!ev.chatId || !me || String(ev.chatId) === String(me.subject)) {
        const before = Number(ev.droppedBefore || 0)
        const box = $('#msgs')
        if (before > 0 && box) {
          box.querySelectorAll('.msg[data-t]').forEach((el) => { if (Number(el.dataset.t) < before) el.remove() })
          reflowDaySeparators()
          historyState.oldestAt = Math.max(historyState.oldestAt || 0, before)
        }
        if (typeof ev.hasMore === 'boolean') historyState.hasMore = ev.hasMore
        if (currentView === 'chat') renderHistoryMore()
      }
    }
  }
  stream.onerror = () => {}
}

async function startPcmRecording(btn) {
  const s = await navigator.mediaDevices.getUserMedia({ audio: { channelCount: 1, echoCancellation: true, noiseSuppression: true, autoGainControl: true } })
  const AC = window.AudioContext || window.webkitAudioContext
  if (!AC) { s.getTracks().forEach((t) => t.stop()); throw Object.assign(new Error('这个浏览器不支持录音'), { name: 'NotSupportedError' }) }
  const ctx = new AC()
  const rate = ctx.sampleRate
  const src = ctx.createMediaStreamSource(s)
  const proc = ctx.createScriptProcessor(4096, 1, 1)
  const parts = []
  let frames = 0
  proc.onaudioprocess = (e) => { const d = e.inputBuffer.getChannelData(0); parts.push(new Float32Array(d)); frames += d.length }
  src.connect(proc)
  proc.connect(ctx.destination)
  const started = Date.now()
  let done = false
  let auto = null
  btn.classList.add('recording'); btn.textContent = '停止'
  const stop = async () => {
    if (done) return
    done = true
    clearTimeout(auto)
    btn.classList.remove('recording'); btn.textContent = '语音'
    try {
      proc.onaudioprocess = null
      src.disconnect(); proc.disconnect()
      s.getTracks().forEach((t) => t.stop())
      await ctx.close()
    } catch (e) {}
    const dur = Math.min(60, Math.max(1, Math.round((Date.now() - started) / 1000)))
    if (frames < rate * 0.2) { toast('太短了，再按一次重说'); return }
    const blob = encodeWav(parts, frames, rate)
    const rd = new FileReader()
    rd.onload = async () => {
      const data = String(rd.result).split(',')[1] || ''
      const r = await api('/api/messages', { method: 'POST', body: { text: '', voice: { data, mime: 'audio/wav', dur } } })
      if (!r.ok) toast(r.reason || '语音没发出去', 'err')
    }
    rd.readAsDataURL(blob)
  }
  auto = setTimeout(stop, 60_000)
  return { stop }
}

function pickAudioFile() {
  const inp = document.createElement('input')
  inp.type = 'file'
  inp.accept = 'audio/*'
  inp.onchange = () => {
    const f = inp.files && inp.files[0]
    if (!f) return
    if (f.size > 12 * 1024 * 1024) return toast('音频太大了（最多 12MB）', 'err')
    const url = URL.createObjectURL(f)
    const a = new Audio()
    a.preload = 'metadata'
    a.onloadedmetadata = () => {
      const dur = Math.min(60, Math.max(1, Math.round(a.duration || 1)))
      const rd = new FileReader()
      rd.onload = async () => {
        const data = String(rd.result).split(',')[1] || ''
        const r = await api('/api/messages', { method: 'POST', body: { text: '', voice: { data, mime: f.type || 'audio/wav', dur } } })
        toast(r.ok ? '发出去了' : (r.reason || '没发出去'), r.ok ? '' : 'err')
      }
      rd.readAsDataURL(f)
      URL.revokeObjectURL(url)
    }
    a.onerror = () => toast('这个音频读不出来', 'err')
    a.src = url
  }
  inp.click()
}

function openInBrowser() {
  const url = location.origin + location.pathname + '?token=' + encodeURIComponent(token || '')
  let w = null
  try { w = window.open(url, '_blank') } catch (e) {}
  if (!w && navigator.clipboard) {
    navigator.clipboard.writeText(url).then(() => toast('弹窗被拦了，地址已复制，粘到浏览器里打开'), () => toast('弹窗被拦了', 'err'))
  }
  return w
}

function setupVoiceButton() {
  const btn = $('#tool-voice')
  if (!btn) return
  if (!navigator.mediaDevices || !navigator.mediaDevices.getUserMedia) { btn.title = '这个环境不支持录音'; return }
  let rec = null
  btn.onclick = async () => {
    if (rec) { await rec.stop(); rec = null; return }
    try {
      rec = await startPcmRecording(btn)
    } catch (e) {
      rec = null
      const name = (e && e.name) || ''
      if (name === 'NotAllowedError' || name === 'PermissionDeniedError' || name === 'SecurityError') {
        toast('当前窗口没有麦克风权限，请在浏览器中打开后再录音', 'err')
        setTimeout(() => confirmAction('用新窗口打开 AuraChat 再录？（这个窗口留着不动）', openInBrowser), 250)
      } else if (name === 'NotFoundError' || name === 'DevicesNotFoundError') {
        toast('没找到麦克风', 'err')
      } else {
        toast('麦克风打不开：' + ((e && e.message) || name || '未知原因'), 'err')
        pickAudioFile()
      }
    }
  }
}

function confirmAction(text, onOk) {
  const box = document.createElement('div')
  box.className = 'mini-confirm'
  box.innerHTML = '<div>' + esc(text) + '</div><div class="mc-row"><button class="primary small" data-ok>好</button><button class="ghost small" data-no>算了</button></div>'
  document.body.appendChild(box)
  const close = () => box.remove()
  box.querySelector('[data-no]').onclick = close
  box.querySelector('[data-ok]').onclick = () => { close(); onOk() }
  return box
}

async function openStickerPicker(anchor) {
  const old = $('.sticker-picker')
  if (old) { old.remove(); return }
  const r = await api('/api/stickers')
  const items = (r.ok && r.items) || []
  const box = document.createElement('div')
  box.className = 'sticker-picker'
  const empty = !items.length
  if (empty) {
    box.classList.add('is-empty')
    box.style.width = '236px'
    box.innerHTML = '<div class="empty sticker-empty"><div>表情库还是空的</div>' +
      '<button class="btn-sm" id="go-sticker-settings">去设置里传几张</button></div>'
  } else {
    box.innerHTML = items.map((i) => '<button class="sticker-opt" data-md5="' + esc(i.md5) + '" title="' + esc(i.name || '还没起名') + '">' +
      '<img src="' + BASE + '/api/sticker/' + esc(i.md5) + '" alt="" />' +
      (i.name ? '<i class="sticker-opt-name">' + esc(i.name) + '</i>' : '') + '</button>').join('')
    box.querySelectorAll('.sticker-opt img').forEach((img) => guardMediaImg(img, '图没了'))
  }
  box.style.visibility = 'hidden'
  box.style.left = '0px'
  box.style.top = '0px'
  document.body.appendChild(box)
  const rect = anchor.getBoundingClientRect()
  const bw = box.offsetWidth || 256
  const bh = box.offsetHeight || 200
  let left = rect.left + rect.width / 2 - bw / 2
  left = Math.max(8, Math.min(left, window.innerWidth - bw - 8))
  let top = empty ? rect.bottom + 8 : rect.top - bh - 10
  if (top + bh > window.innerHeight - 8) top = Math.max(8, Math.min(rect.bottom + 8, window.innerHeight - bh - 8))
  if (top < 8) top = Math.max(8, Math.min(rect.bottom + 10, window.innerHeight - bh - 8))
  box.style.left = left + 'px'
  box.style.top = top + 'px'
  box.style.visibility = ''
  const go = $('#go-sticker-settings', box)
  if (go) go.onclick = () => {
    box.remove()
    switchView('settings')
    setTimeout(() => {
      const grid = $('#sticker-grid')
      grid?.closest('.section')?.scrollIntoView({ block: 'center', behavior: 'smooth' })
    }, 60)
  }
  box.onclick = async (e) => {
    const b = e.target.closest('[data-md5]')
    if (!b) return
    box.remove()
    const res = await api('/api/messages', { method: 'POST', body: { text: '', sticker: b.dataset.md5 } })
    if (!res.ok) toast(res.reason || '没发出去', 'err')
  }
  setTimeout(() => document.addEventListener('click', function h(ev) { if (!box.contains(ev.target)) { box.remove(); document.removeEventListener('click', h) } }), 0)
}

document.addEventListener('click', async (e) => {
  const b = e.target.closest('[data-collect]')
  if (!b) return
  const img = b.closest('.bubble')?.querySelector('img')
  if (!img) return
  const src = img.getAttribute('src') || ''
  const data = await imageBase64(src)
  if (!data) return toast('这张图存不下来', 'err')
  const r = await api('/api/stickers/upload', { method: 'POST', body: { data, from: 'steal', hint: '' } })
  toast(r.ok ? '收进表情库了' : (r.reason || '没收成'), r.ok ? '' : 'err')
})

// 图片可能是内联 data:，也可能是 /api/media/... 的地址，统一转成 base64 再传
async function imageBase64(src) {
  if (!src) return ''
  if (src.startsWith('data:')) { const i = src.indexOf(','); return i >= 0 ? src.slice(i + 1) : '' }
  try {
    const buf = await (await fetch(src)).arrayBuffer()
    const bytes = new Uint8Array(buf)
    let bin = ''
    for (let i = 0; i < bytes.length; i += 0x8000) bin += String.fromCharCode.apply(null, bytes.subarray(i, i + 0x8000))
    return btoa(bin)
  } catch { return '' }
}

function paneShell(main, title, headRight) {
  main.innerHTML = '<div class="pane"><div class="pane-head"><h2>' + esc(title) + '</h2><span class="spacer"></span>' + (headRight || '') + '</div><div class="pane-body" id="pane-body"></div></div>'
  return $('#pane-body')
}

function renderDiary(main) {
  paneShell(main, '空间', '<span class="count" id="diary-count"></span>')
  loadDiary()
}

async function loadDiary() {
  const r = await api('/api/diary')
  const body = $('#pane-body')
  if (!body) return
  const st = (r.ok && r.stats) || {}
  const cnt = $('#diary-count')
  if (cnt) cnt.textContent = '共 ' + (st.total || 0) + ' 篇 · 今天 ' + (st.today || 0) + ' 篇' + (st.planned ? ' · 计划 ' + st.planned + ' 篇' : '')
  const list = (r.ok && r.entries) || []
  if (!list.length) { body.innerHTML = '<div class="empty">还没有动态。</div>'; return }
  body.innerHTML = list.map(renderEntry).join('')
  bindDiaryEvents(body)
}

function renderEntry(e) {
  const comments = (e.comments || []).map((c) => renderComment(c, e.id)).join('')
  return '<div class="entry" data-eid="' + esc(e.id) + '">' +
    '<div class="entry-head"><span class="who">' + esc(fmtDay(e.at)) + '</span>' +
    (e.visibility === 'self' ? '<span class="visibility-tag">仅主人可见</span>' : '') +
    '<span class="spacer"></span>' + (isOwner() ? '<button class="small ghost" data-del-entry="' + esc(e.id) + '">删除</button>' : '') + '</div>' +
    '<div class="entry-text">' + esc(e.text) + '</div>' +
    '<div class="comments">' + (comments || '<div class="muted" style="font-size:12px">还没有评论</div>') +
    '<div class="c-input"><input type="text" placeholder="说点什么…" data-cinput="' + esc(e.id) + '" /><button class="primary small" data-csend="' + esc(e.id) + '">评论</button></div>' +
    '</div></div>'
}

function renderComment(c, entryId) {
  const replies = (c.replies || []).map((r2) => (
    '<div class="comment" data-cid="' + esc(c.id) + '" data-rid="' + esc(r2.id) + '">' +
    '<span class="c-who">' + esc(r2.name || r2.from) + '</span><span class="c-text">' + esc(r2.text) + '</span>' +
    '<span class="c-time">' + esc(fmtClock(r2.at)) + '</span>' +
    (canDelComment(r2) ? '<span class="c-acts"><button data-del-comment="' + esc(entryId) + '" data-cid="' + esc(c.id) + '" data-rid="' + esc(r2.id) + '">删除</button></span>' : '') +
    '</div>'
  )).join('')
  return '<div class="comment" data-cid="' + esc(c.id) + '">' +
    '<span class="c-who">' + esc(c.name || c.from) + '</span><span class="c-text">' + esc(c.text) + '</span>' +
    '<span class="c-time">' + esc(fmtClock(c.at)) + '</span>' +
    (canDelComment(c) ? '<span class="c-acts"><button data-del-comment="' + esc(entryId) + '" data-cid="' + esc(c.id) + '">删除</button></span>' : '') +
    '</div>' + (replies ? '<div class="replies">' + replies + '</div>' : '')
}

function canDelComment(c) { return isOwner() || (me && c.from === me.subject) }

function bindDiaryEvents(body) {
  body.onclick = async (e) => {
    const del = e.target.closest('[data-del-entry]')
    if (del) {
      const r = await api('/api/diary/delete', { method: 'POST', body: { entryId: del.dataset.delEntry } })
      toast(r.ok ? '删了' : (r.reason || '没删掉'), r.ok ? '' : 'err')
      loadDiary()
      return
    }
    const dc = e.target.closest('[data-del-comment]')
    if (dc) {
      const r = await api('/api/diary/comment/delete', { method: 'POST', body: { entryId: dc.dataset.delComment, commentId: dc.dataset.cid, replyId: dc.dataset.rid || '' } })
      toast(r.ok ? '删了' : (r.reason || '没删掉'), r.ok ? '' : 'err')
      loadDiary()
      return
    }
    const cs = e.target.closest('[data-csend]')
    if (cs) {
      const eid = cs.dataset.csend
      const inp = body.querySelector('[data-cinput="' + eid + '"]')
      const text = (inp?.value || '').trim()
      if (!text) return
      const r = await api('/api/diary/comment', { method: 'POST', body: { entryId: eid, text } })
      toast(r.ok ? '评论好了' : (r.reason || '没发出去'), r.ok ? '' : 'err')
      loadDiary()
    }
  }
  body.onkeydown = (e) => {
    if (e.key !== 'Enter') return
    const inp = e.target.closest('[data-cinput]')
    if (!inp) return
    const btn = body.querySelector('[data-csend="' + inp.dataset.cinput + '"]')
    btn?.click()
  }
}

let memTab = 'profiles'
function renderMemory(main) {
  paneShell(main, '记忆',
    '<div class="mem-tabs" id="mem-tabs">' +
    ['profiles|档案', 'insights|相处经验', 'styles|说话方式', 'items|全部记忆', 'settings|设置'].map((x) => {
      const [id, label] = x.split('|')
      return '<button data-mtab="' + id + '" class="' + (memTab === id ? 'on' : '') + '">' + label + '</button>'
    }).join('') + '</div>')
  $('#mem-tabs').onclick = (e) => {
    const b = e.target.closest('[data-mtab]')
    if (!b) return
    memTab = b.dataset.mtab
    $$('#mem-tabs button').forEach((x) => x.classList.toggle('on', x.dataset.mtab === memTab))
    loadMemory()
  }
  loadMemory()
}

async function loadMemory() {
  const body = $('#pane-body')
  if (!body) return
  if (memTab === 'settings') { body.innerHTML = '<div id="mem-settings"></div>'; renderMemorySettings(); return }
  const r = await api('/api/memory')
  if (!r.ok) { body.innerHTML = '<div class="empty">' + esc(r.reason || '读不到') + '</div>'; return }
  if (memTab === 'items') {
    const items = r.items || []
    const byId = new Map(items.map((it) => [String(it.id), it]))
    body.innerHTML = '<div class="mem-head"><span class="muted" style="font-size:12px">共 ' + (r.stats?.count ?? items.length) + ' 条</span>' +
      '<button class="ghost small" id="mem-add">＋ 加一条</button></div>' +
      (items.length ? items.map((it) => '<div class="mem-row" data-mid="' + esc(it.id) + '"><div class="mem-text">' + esc(it.text) +
        '<div class="mem-meta">' + esc(fmtDay(it.at)) + ' · 重要度 ' + (Math.round((it.importance || 0) * 100) + '%') + ' · ' + esc(it.source || '') +
        (it.editedAt ? ' · 手改过' : '') +
        (it.tags || []).map((t) => '<span class="mem-tag">' + esc(t) + '</span>').join('') + '</div></div>' +
        '<span class="c-acts"><button data-edit-mem="' + esc(it.id) + '">修改</button><button data-del-mem="' + esc(it.id) + '">删除</button></span></div>').join('') : '<div class="empty">还没有记忆。</div>')
    body.onclick = async (e) => {
      const d = e.target.closest('[data-del-mem]')
      if (d) {
        const r2 = await api('/api/memory/delete', { method: 'POST', body: { id: d.dataset.delMem } })
        toast(r2.ok ? '删了' : (r2.reason || '没删掉'), r2.ok ? '' : 'err'); loadMemory(); return
      }
      const b = e.target.closest('[data-edit-mem]')
      if (b) {
        const it = byId.get(String(b.dataset.editMem)) || {}
        inlineEditor(b.closest('.mem-row'), it.text || '', '这条记的是什么', async (next) => {
          if (!next) return
          const r2 = await api('/api/memory/update', { method: 'POST', body: { id: it.id, text: next } })
          toast(r2.ok ? '改好了' : (r2.reason || '没改成'), r2.ok ? '' : 'err'); loadMemory()
        }); return
      }
      const a = e.target.closest('#mem-add')
      if (a) {
        inlineEditor(body.querySelector('.mem-head'), '', '要记住的一件事（一句话）', async (next) => {
          if (!next) return
          const r2 = await api('/api/memory', { method: 'POST', body: { text: next, source: 'manual' } })
          toast(r2.ok ? '记下了' : (r2.reason || '没记下'), r2.ok ? '' : 'err'); loadMemory()
        }); return
      }
    }
  } else if (memTab === 'profiles') {
    const ps = r.profiles || []
    body.innerHTML = '<div class="mem-head"><span class="muted" style="font-size:12px">共 ' + ps.length + ' 个</span>' +
      '<button class="ghost small" id="prof-add">＋ 记一个人</button></div>' +
      (ps.length ? ps.map((p) => {
        const name = p.name || p.id || '某人'
        return '<div class="mem-profile" data-pid="' + esc(p.id || '') + '"><div class="mp-name">' + esc(name) + (p.name && p.id ? ' <span class="muted" style="font-size:12px">' + esc(p.id) + '</span>' : '') + '</div>' +
          '<div class="mp-text" id="mp-' + esc(p.id || '') + '">' + esc(p.text || '（还没有印象）') + '</div>' +
          '<div class="ov-tests"><button class="ghost small" data-edit-profile="' + esc(p.id || '') + '">修改</button></div></div>'
      }).join('') : '<div class="empty">还没有人物档案，多聊聊就有了。</div>')
    body.onclick = (e) => {
      const b = e.target.closest('[data-edit-profile]')
      if (b) {
        const about = b.dataset.editProfile
        const el = document.getElementById('mp-' + about)
        const cur = el ? el.textContent : ''
        const nm = (ps.find((p) => String(p.id) === String(about)) || {}).name || ''
        inlineEditor(b.closest('.mem-profile'), cur, '写点你对这个人的印象', async (next) => {
          const r2 = await api('/api/memory/profile', { method: 'POST', body: { about, text: next, name: nm } })
          toast(r2.ok ? '改好了' : (r2.reason || '没改成'), r2.ok ? '' : 'err'); loadMemory()
        })
        return
      }
      const a = e.target.closest('#prof-add')
      if (a) {
        inlineEditor(body.querySelector('.mem-head'), '', '这个人的 ID（主人写 owner，客人写他的用户名）', async (who) => {
          if (!who) return
          inlineEditor(body.querySelector('.mem-head'), '', '怎么称呼 Ta / 名字', async (name) => {
            inlineEditor(body.querySelector('.mem-head'), '', '写点你对 ' + (name || who) + ' 的印象', async (text) => {
              if (!text) return
              const r2 = await api('/api/memory/profile', { method: 'POST', body: { about: who, text, name: name || '' } })
              toast(r2.ok ? '记下了' : (r2.reason || '没记下'), r2.ok ? '' : 'err'); loadMemory()
            })
          })
        })
      }
    }
  } else if (memTab === 'insights') {
    const gs = r.insights || []
    const save = async (g, lines) => {
      const r2 = await api('/api/memory/insight', { method: 'POST', body: { key: g.key, name: g.name || '', lines } })
      toast(r2.ok ? '改好了' : (r2.reason || '没改成'), r2.ok ? '' : 'err'); loadMemory()
    }
    body.innerHTML = gs.length ? gs.map((g) => '<div class="mem-group" data-key="' + esc(g.key) + '"><div class="mem-group-name">' + esc(g.name || g.key) +
      ' <button class="ghost small" data-add-ins="' + esc(g.key) + '">＋ 加一条</button></div><div class="mem-body">' +
      ((g.list || []).length ? g.list.map((x, i) => '<div class="mem-row"><div class="mem-text">' + esc(x.text) + '<div class="mem-meta">' + esc(fmtDay(x.at)) + '</div></div>' +
        '<span class="c-acts"><button data-edit-ins="' + esc(g.key) + '" data-i="' + i + '">修改</button><button data-del-ins="' + esc(g.key) + '" data-i="' + i + '">删除</button></span></div>').join('') : '<div class="muted">还没有。</div>') +
      '</div></div>').join('') : '<div class="empty">还没有相处经验。</div>'
    body.onclick = (e) => {
      const keyOf = (el) => el.dataset.editIns || el.dataset.delIns || el.dataset.addIns
      const g = (el) => gs.find((x) => String(x.key) === String(keyOf(el)))
      const texts = (gg) => (gg.list || []).map((x) => x.text)
      const ed = e.target.closest('[data-edit-ins]')
      if (ed) {
        const gg = g(ed); if (!gg) return
        const line = gg.list[Number(ed.dataset.i)]
        inlineEditor(ed.closest('.mem-row'), line ? line.text : '', '这条经验', (next) => {
          if (!next) return
          const lines = texts(gg); lines[Number(ed.dataset.i)] = next
          save(gg, lines)
        }); return
      }
      const de = e.target.closest('[data-del-ins]')
      if (de) {
        const gg = g(de); if (!gg) return
        save(gg, texts(gg).filter((_, i) => i !== Number(de.dataset.i))); return
      }
      const ad = e.target.closest('[data-add-ins]')
      if (ad) {
        const gg = g(ad); if (!gg) return
        inlineEditor(ad.closest('.mem-group-name'), '', '新的一条相处经验', (next) => {
          if (!next) return
          save(gg, texts(gg).concat(next))
        })
      }
    }
  } else {
    const styles = r.styles || {}
    const keys = Object.keys(styles)
    const save = async (who, lines) => {
      const r2 = await api('/api/memory/style', { method: 'POST', body: { who, lines } })
      toast(r2.ok ? '改好了' : (r2.reason || '没改成'), r2.ok ? '' : 'err'); loadMemory()
    }
    const listOf = (k) => (styles[k] || []).map((x) => (typeof x === 'string' ? x : x.text))
    body.innerHTML = keys.length ? keys.map((k) => '<div class="mem-group" data-style="' + esc(k) + '"><div class="mem-group-name">' + esc(k === 'self' ? 'Ta自己养成的说法' : '和 ' + k + ' 说话的方式') +
      ' <button class="ghost small" data-add-style="' + esc(k) + '">＋ 加一条</button></div><div class="mem-body">' +
      (styles[k] || []).map((x, i) => '<div class="mem-row"><div class="mem-text">' + esc(typeof x === 'string' ? x : x.text) + '</div>' +
        '<span class="c-acts"><button data-edit-style="' + esc(k) + '" data-i="' + i + '">修改</button><button data-del-style="' + esc(k) + '" data-i="' + i + '">删除</button></span></div>').join('') +
      '</div></div>').join('') : '<div class="empty">还没有形成说话方式。</div>'
    body.onclick = async (e) => {
      const d = e.target.closest('[data-del-style]')
      if (d) { save(d.dataset.delStyle, listOf(d.dataset.delStyle).filter((_, i) => i !== Number(d.dataset.i))); return }
      const ed = e.target.closest('[data-edit-style]')
      if (ed) {
        const k = ed.dataset.editStyle
        const cur = listOf(k)[Number(ed.dataset.i)] || ''
        inlineEditor(ed.closest('.mem-row'), cur, '一句话说法', (next) => {
          if (!next) return
          const lines = listOf(k); lines[Number(ed.dataset.i)] = next
          save(k, lines)
        }); return
      }
      const ad = e.target.closest('[data-add-style]')
      if (ad) {
        const k = ad.dataset.addStyle
        inlineEditor(ad.closest('.mem-group-name'), '', '新的一句说话方式', (next) => {
          if (!next) return
          save(k, listOf(k).concat(next))
        })
      }
    }
  }
}

function openSettings() { switchView('settings') }

function renderSettings(main) {
  const owner = isOwner()
  const body = paneShell(main, '设置', owner ? '<button class="primary small" id="cfg-save">保存设置</button>' : '')
  body.innerHTML = '<div id="cfg-body"></div>'
  const box = $('#cfg-body')
  const parts = owner
    ? [
        sectionSwitch('总开关', 'enabled'),
        sectionIdentity(),
        sectionAppearance(),
        sectionModel(),
        sectionReply(),
        sectionSound(),
        sectionDiary(),
        sectionVoice(),
        sectionListen(),
        sectionImageGen(),
        sectionStickers(),
        sectionHistory(),
        sectionLogs(),
        sectionSpeakUp(),
        sectionSelfDrive(),
        sectionWeb(),
        sectionGuests(),
        sectionDebug(),
        sectionAccount(),
      ]
    : [sectionAccount()]
  box.innerHTML = parts.join('')
  const saveBtn = $('#cfg-save')
  if (saveBtn) saveBtn.onclick = () => saveConfig()
  box.onclick = (e) => {
    const cp = e.target.closest('#pw-save')
    if (cp) return changePassword()
    const lo = e.target.closest('#logout')
    if (lo) return logout()
  }
  bindSectionEvents(box)
  applyWhen(box)
}

function saveConfig() {
  const b = collectConfig()
  const mv = $('#model-sel')?.value
  if (mv !== undefined) {
    const mm = splitModelValue(mv)
    b.model = b.model || {}
    b.model.provider = mm.provider
    b.model.model = mm.model
  }
  const keep = scrollState()
  api('/api/config', { method: 'PATCH', body: b }).then(async (r) => {
    toast(r.ok ? '存好了' : (r.reason || '没存上'), r.ok ? '' : 'err')
    if (!r.ok) return
    cfg = r.config
    applyTheme()
    applyTitle()
    await boot({ silent: true })
    restoreScroll(keep)
  })
}

function scrollState() {
  return { win: window.scrollY || 0, pane: $('.pane')?.scrollTop || 0, body: $('#pane-body')?.scrollTop || 0 }
}

function restoreScroll(s) {
  if (!s) return
  const put = (el, v) => { if (el && v) el.scrollTop = v }
  put($('.pane'), s.pane)
  put($('#pane-body'), s.body)
  if (s.win) window.scrollTo(0, s.win)
}

async function changePassword() {
  const oldPw = $('#pw-old')?.value || ''
  const newPw = $('#pw-new')?.value || ''
  if (newPw.length < 6) return toast('新密码至少 6 位', 'err')
  const r = await api('/api/account/password', { method: 'POST', body: { oldPassword: oldPw, newPassword: newPw } })
  toast(r.ok ? '密码改好了' : (r.reason || '没改成'), r.ok ? '' : 'err')
  if (r.ok) { $('#pw-old').value = ''; $('#pw-new').value = '' }
}
async function logout() {
  await api('/api/logout', { method: 'POST' })
  setToken('')
  me = null
  if (stream) { stream.close(); stream = null }
  showGate('login')
}

function sectionSwitch(title, key, hint) {
  return '<div class="section"><h3>' + esc(title) + '</h3><div class="body"><div class="row">' +
    '<label>' + esc(title) + '</label><div class="ctl"><label class="switch"><input type="checkbox" data-key="' + key + '" /><span class="slider"></span></label>' +
    '<span class="muted" style="font-size:12px">' + esc(hint || '') + '</span></div></div></div></div>'
}

function sectionIdentity() {
  return '<div class="section"><h3>身份</h3><div class="body">' +
    rowInput('名字', 'botName', 'Ta的名字，一般跟着人设走') +
    rowInput('个性签名', 'botTag', '可留空；显示在聊天页名字后面') +
    rowInput('怎么称呼我', 'ownerName', 'Ta对你的称呼') +
    rowInput('网页名称', 'siteName', '登录页和入口显示的名字；留空就是 AuraChat') +
    rowInput('网页标题', 'pageTitle', '浏览器标签页上的标题；留空就是 AuraChat') +
    '<div class="row"><label>人设</label><div class="ctl"><select id="persona-sel" data-key="persona.current">' +
    (personaList.length ? personaList.map((p) => '<option value="' + esc(p.id) + '">' + esc(p.name || p.id) + '</option>').join('') : '<option value="dafeiyu">大肥鱼</option>') +
    '<option value="__new__">＋ 新建一个人设…</option></select>' +
    '<button class="ghost small" id="pe-edit">编辑当前人设</button></div></div>' +
    '<div id="persona-editor" class="persona-editor hidden">' +
    '<div class="row"><label>人设名字</label><div class="ctl"><input type="text" id="pe-name" placeholder="例如 小满" /></div></div>' +
    '<div class="row"><label>标签</label><div class="ctl"><input type="text" id="pe-tags" placeholder="逗号分隔，例如 温柔,话少" /></div></div>' +
    '<div class="row"><label>人设正文</label><div class="ctl" style="flex-direction:column;align-items:stretch">' +
    '<textarea id="pe-body" rows="10" placeholder="你是谁、性格、说话方式、在意什么…写成一小段话就行"></textarea></div></div>' +
    '<div class="row"><div class="ctl"><button class="primary small" id="pe-save">保存人设</button>' +
    '<button class="ghost small" id="pe-del">删掉这个人设</button>' +
    '<button class="ghost small" id="pe-cancel">收起</button>' +
    '<span class="muted" style="font-size:12px">存好后会自动切到Ta</span></div></div>' +
    '</div>' +
    '<div class="row"><label>头像</label><div class="ctl"><label class="file-btn">选张图<input type="file" id="avatar-file" accept="image/*" hidden /></label>' +
    '<button class="ghost small" id="avatar-reset">用默认图标</button></div></div>' +
    '</div></div>'
}

function rowInput(label, key, hint, type = 'text') {
  return '<div class="row"><label>' + esc(label) + '</label><div class="ctl"><input type="' + type + '" data-key="' + key + '" />' +
    (hint ? '<span class="muted" style="font-size:12px">' + esc(hint) + '</span>' : '') + '</div></div>'
}
function rowNum(label, key, hint) {
  return '<div class="row"><label>' + esc(label) + '</label><div class="ctl"><input type="number" data-key="' + key + '" />' +
    (hint ? '<span class="muted" style="font-size:12px">' + esc(hint) + '</span>' : '') + '</div></div>'
}
function rowSelect(label, key, opts) {
  return '<div class="row"><label>' + esc(label) + '</label><div class="ctl"><select data-key="' + key + '">' +
    opts.map((o) => '<option value="' + esc(o[0]) + '">' + esc(o[1]) + '</option>').join('') + '</select></div></div>'
}
function rowTime(label, key, hint) {
  return '<div class="row"><label>' + esc(label) + '</label><div class="ctl"><input type="text" data-key="' + key + '" placeholder="HH:MM" maxlength="5" style="max-width:110px" />' +
    '<span class="muted" style="font-size:12px">' + esc(hint || 'HH:MM') + '</span></div></div>'
}

function rowWhen(when, html) {
  return '<div class="when-wrap" data-when="' + esc(when) + '">' + html + '</div>'
}
function applyWhen(root) {
  $$('[data-when]', root || document).forEach((w) => {
    const [key, vals] = String(w.dataset.when || '').split(':')
    const el = root ? root.querySelector('[data-key="' + key + '"]') : $('[data-key="' + key + '"]')
    const want = String(vals || '').split('|')
    w.style.display = !el || want.includes(String(el.value)) ? '' : 'none'
  })
}
document.addEventListener('change', (e) => {
  const t = e.target
  if (t && t.dataset && t.dataset.key) applyWhen(t.closest('#cfg-body') || document.body)
})

document.addEventListener('click', (e) => {
  const b = e.target.closest('[data-voice-play]')
  if (!b) return
  const bar = b.closest('.voice-bar')
  const a = bar && bar.querySelector('audio')
  if (!a) return
  if (!a.paused) { a.pause(); bar.classList.remove('on'); return }
  document.querySelectorAll('.voice-bar audio').forEach((x) => {
    if (x !== a) { try { x.pause() } catch (err) {} x.closest('.voice-bar')?.classList.remove('on') }
  })
  const p = a.play()
  if (p && p.then) p.then(() => bar.classList.add('on')).catch(() => {})
  else bar.classList.add('on')
})
document.addEventListener('ended', (e) => {
  if (e.target && e.target.tagName === 'AUDIO') e.target.closest('.voice-bar')?.classList.remove('on')
}, true)
document.addEventListener('pause', (e) => {
  if (e.target && e.target.tagName === 'AUDIO') e.target.closest('.voice-bar')?.classList.remove('on')
}, true)
document.addEventListener('loadedmetadata', (e) => {
  if (!e.target || e.target.tagName !== 'AUDIO') return
  const bar = e.target.closest('.voice-bar')
  const d = bar && bar.querySelector('.vb-dur')
  if (d && !d.textContent) d.textContent = fmtDur(e.target.duration)
}, true)

function sectionAppearance() {
  return '<div class="section"><h3>外观</h3><div class="body">' +
    rowSelect('主题', 'theme.mode', [['follow', '跟随 DSH / 系统'], ['light', '浅色'], ['dark', '深色'], ['acrylic', '亚克力'], ['frosted', '毛玻璃']]) +
    '<p class="hint">仅 亚克力/毛玻璃 支持背景图</p>' +
    '<div class="row"><label>背景图</label><div class="ctl"><label class="file-btn">上传<input type="file" id="bg-file" accept="image/*" hidden /></label>' +
    '<button class="ghost small" id="bg-clear">清除</button></div></div>' +
    '</div></div>'
}

function sectionSave() {
  return ''
}

function sectionReply() {
  return '<div class="section"><h3>回复节奏</h3><div class="body">' +
    rowSwitch('连发合并', 'merge.enabled', '你连着发几条，Ta攒一起回，不会一句一句打断') +
    rowNum('停顿几秒再回', 'merge.waitSec', '你停下来这么久之后Ta才开始想') +
    rowNum('最多等几秒', 'merge.maxSec', '你一直不停手也不会无限等') +
    rowNum('你在打字时先等几秒', 'merge.waitTypingSec', '检测到输入框里还有字就压着不回；0 = 不管这个') +
    '</div></div>'
}

function sectionSound() {
  return '<div class="section"><h3>提示音</h3><div class="body">' +
    rowSwitch('Ta来消息时响', 'sound.onIncoming') +
    rowSelect('来消息的音', 'sound.incoming', TONES) +
    rowWhen('sound.incoming:custom', rowInput('来消息的音频地址', 'sound.incomingUrl', 'http(s) 地址，或者本机的 /xxx.mp3')) +
    rowSwitch('我发消息时响', 'sound.onSend') +
    rowSelect('我发消息的音', 'sound.outgoing', TONES) +
    rowWhen('sound.outgoing:custom', rowInput('我发消息的音频地址', 'sound.outgoingUrl', '同上')) +
    '<div class="row"><label>来消息音量</label><div class="ctl"><input type="number" step="0.1" min="0" max="1" data-key="sound.inVolume" />' +
    '<button class="ghost small" data-tone="in">试听</button></div></div>' +
    '<div class="row"><label>发消息音量</label><div class="ctl"><input type="number" step="0.1" min="0" max="1" data-key="sound.outVolume" />' +
    '<button class="ghost small" data-tone="out">试听</button></div></div>' +
    '</div></div>'
}

function playSound(kind) { playTone(kind, cfg) }

function sectionModel() {
  const cur = cfg?.model || {}
  return '<div class="section"><h3>模型</h3><div class="body">' +
    '<div class="row"><label>模型</label><div class="ctl"><select id="model-sel">' +
    '<option value="__default__">默认（跟随 DSH）</option>' +
    models.map((m) => '<option value="' + esc(m.provider + '::' + m.model) + '">' + esc(m.provider + ' / ' + m.model) + '</option>').join('') +
    '</select></div></div>' +
    rowSelect('思考强度', 'model.reasoningEffort', [['off', '关'], ['low', '低'], ['medium', '中'], ['high', '高']]) +
    rowInput('API Key', 'model.apiKey', '自定义模型专用；留空保持不变', 'password') +
    rowInput('API 地址', 'model.baseUrl', '自定义模型的接口地址，如 https://api.deepseek.com/v1') +
    '</div></div>'
}

function sectionWeb() {
  return '<div class="section"><h3>网页入口</h3><div class="body">' +
    rowInput('监听地址', 'web.host') +
    rowNum('监听端口', 'web.port') +
    '</div></div>'
}

function sectionDiary() {
  return '<div class="section"><h3>空间日记</h3><div class="body">' +
    rowSwitch('每天写', 'diary.enabled', 'Ta自己按计划写动态') +
    rowNum('每天几篇', 'diary.timesPerDay', '0~6') +
    rowInput('时间段开始', 'diary.windowStart', 'HH:MM') +
    rowInput('时间段结束', 'diary.windowEnd', 'HH:MM') +
    rowNum('两篇至少隔（分钟）', 'diary.minGapMinutes') +
    rowNum('最少字数', 'diary.minChars') +
    rowNum('最多字数', 'diary.maxChars', '想写长一点就调大，最多 3000') +
    rowSelect('谁能看', 'diary.visibility', [['all', '任何人'], ['self', '仅主人']]) +
    '</div></div>'
}

function rowSwitch(label, key, hint) {
  return '<div class="row"><label>' + esc(label) + '</label><div class="ctl"><label class="switch"><input type="checkbox" data-key="' + key + '" /><span class="slider"></span></label>' +
    (hint ? '<span class="muted" style="font-size:12px">' + esc(hint) + '</span>' : '') + '</div></div>'
}

const TTS_CARDS = [
  { id: 'auto', title: '自动', desc: 'edge-tts 优先，不行用 Windows 语音' },
  { id: 'edge', title: '微软 edge-tts', desc: '在线，音色自然', badge: true },
  { id: 'cosyvoice', title: '本地 CosyVoice', desc: '自己部署的服务，可克隆音色' },
  { id: 'gptsovits', title: '本地 GPT-SoVITS', desc: 'GPT-SoVITS api_v2，用参考音频' },
  { id: 'openai', title: 'OpenAI 兼容', desc: '/audio/speech，最通用' },
  { id: 'http', title: '其他 HTTP 接口', desc: '任意返回音频的地址' },
  { id: 'windows', title: 'Windows 自带', desc: '离线可用，机械感重一些' },
]

const EDGE_VOICES = [
  ['zh-CN-XiaoxiaoNeural', '晓晓 · 温暖女声'],
  ['zh-CN-XiaoyiNeural', '晓伊 · 活泼女声'],
  ['zh-CN-XiaochenNeural', '晓辰 · 清亮女声'],
  ['zh-CN-XiaohanNeural', '晓涵 · 知性女声'],
  ['zh-CN-XiaomengNeural', '晓梦 · 少女'],
  ['zh-CN-XiaomoNeural', '晓墨 · 低柔女声'],
  ['zh-CN-YunxiNeural', '云希 · 少年'],
  ['zh-CN-YunjianNeural', '云健 · 浑厚男声'],
  ['zh-CN-YunxiaNeural', '云夏 · 少年'],
  ['zh-CN-YunyangNeural', '云扬 · 播报男声'],
  ['zh-CN-liaoning-XiaobeiNeural', '晓北 · 东北女声'],
  ['zh-CN-shaanxi-XiaoniNeural', '晓妮 · 陕西女声'],
  ['zh-HK-HiuMaanNeural', '曉曼 · 粤语女声'],
  ['zh-HK-WanLungNeural', '雲龍 · 粤语男声'],
  ['zh-TW-HsiaoChenNeural', '曉臻 · 台湾女声'],
  ['zh-TW-YunJheNeural', '雲哲 · 台湾男声'],
]

const IMG_CARDS = [
  { id: 'openai', title: 'OpenAI 兼容', desc: '/images/generations' },
  { id: 'sdwebui', title: 'SD WebUI', desc: '/sdapi/v1/txt2img' },
  { id: 'comfyui', title: 'ComfyUI', desc: '/prompt 工作流' },
  { id: 'code', title: '让 Ta 写代码画', desc: '不连任何服务' },
]

function rowSlider(label, key, min, max, step, unit, hint) {
  return '<div class="row"><label>' + esc(label) + '</label><div class="ctl">' +
    '<input type="range" min="' + min + '" max="' + max + '" step="' + step + '" data-key="' + key + '" data-unit="' + esc(unit) + '" />' +
    '<span class="pill" data-out="' + esc(key) + '"></span>' +
    '</div>' + (hint ? '<p class="hint">' + esc(hint) + '</p>' : '') + '</div>'
}

function sectionListen() {
  return '<div class="section"><h3>听语音（实验性）</h3><div class="body">' +
    rowSwitch('听语音（STT）', 'voice.asr', '把Ta收到的语音转成文字再回') +
    '<p class="hint">识别走 SenseVoice（dsh 自带），本地推理，不用填地址，也不用联网；只在 DSH 桌面版里可用。</p>' +
    '<div class="row"><label>识别服务</label><div class="ctl"><span class="muted" id="asr-state">正在查…</span></div></div>' +
    rowInput('识别语言', 'voice.asrLanguage', 'zh / en，留空自动') +
    '</div></div>'
}

async function loadAsrState() {
  const el = $('#asr-state')
  if (!el) return
  const r = await api('/api/status')
  if (!r || r.ok === false) { el.textContent = '查不到（要主人登录）'; return }
  const a = r.asr || {}
  if (!a.host) el.textContent = '宿主没有提供语音识别（网页版没有；桌面版要启用语音输入）'
  else if (a.providers && a.providers.length) el.textContent = '可用：' + a.providers.join('、')
  else el.textContent = '宿主有服务，但识别器还没注册（模型可能没下载完）'
}

function sectionVoice() {
  const cur = cfg?.voice?.provider || 'auto'
  const cards = TTS_CARDS.map((c) =>
    '<button type="button" class="tts-card' + (c.id === cur ? ' on' : '') + '" data-tts="' + c.id + '">' +
    '<b>' + esc(c.title) + '</b><small>' + esc(c.desc) + '</small>' +
    (c.badge ? '<em class="tts-badge" data-edge-badge>还没装</em>' : '') + '</button>').join('')
  return '<div class="section"><h3>语音（实验性）</h3><div class="body">' +
    rowSwitch('发语音（TTS）', 'voice.enabled', 'Ta能把话念出来发语音条') +
    '<div class="tts-cards">' + cards + '</div>' +
    '<input type="hidden" data-key="voice.provider" />' +
    rowWhen('voice.provider:edge|auto', '<div class="row"><label>edge-tts</label><div class="ctl">' +
      '<span class="muted" id="edge-state">正在查…</span><button class="ghost small" id="edge-install">安装</button></div></div>') +
    rowWhen('voice.provider:edge|auto', rowSelect('音色', 'voice.voice', EDGE_VOICES)) +
    rowWhen('voice.provider:windows', rowInput('音色', 'voice.voice', 'Windows 里装的语音名，例如 Microsoft Huihui Desktop')) +
    rowWhen('voice.provider:edge|windows|auto', rowSelect('语速', 'voice.rate', [['-30%', '很慢'], ['-15%', '慢'], ['+0%', '正常'], ['+15%', '快'], ['+30%', '很快']])) +
    rowWhen('voice.provider:cosyvoice', rowInput('服务地址', 'voice.cosyvoice.url', 'CosyVoice 自带 runtime/python/fastapi/server.py')) +
    rowWhen('voice.provider:cosyvoice', rowSelect('模式', 'voice.cosyvoice.mode', [['sft', '预置音色（sft）'], ['instruct', '自然语言控制（instruct）'], ['zero_shot', '零样本克隆（zero_shot）'], ['cross_lingual', '跨语种（cross_lingual）']])) +
    rowWhen('voice.provider:cosyvoice', rowInput('音色 ID', 'voice.cosyvoice.spkId', '例如 中文女、中文男')) +
    rowWhen('voice.provider:cosyvoice', rowSelect('采样率', 'voice.cosyvoice.sampleRate', [['22050', '22050 Hz'], ['24000', '24000 Hz'], ['16000', '16000 Hz']])) +
    rowWhen('voice.provider:cosyvoice', rowInput('控制指令', 'voice.cosyvoice.instruct', 'instruct 模式下用，例如「用四川话说」')) +
    rowWhen('voice.provider:gptsovits', rowInput('服务地址', 'voice.gptsovits.url', 'GPT-SoVITS 的 api_v2.py')) +
    rowWhen('voice.provider:gptsovits', rowInput('参考音频路径', 'voice.gptsovits.refAudio', '服务那台电脑上的路径')) +
    rowWhen('voice.provider:gptsovits', rowInput('参考音频的文字', 'voice.gptsovits.promptText')) +
    rowWhen('voice.provider:openai', rowInput('服务地址', 'voice.ttsBaseUrl', '例如 http://127.0.0.1:9880/v1')) +
    rowWhen('voice.provider:openai', rowInput('模型', 'voice.ttsModel')) +
    rowWhen('voice.provider:openai', rowInput('音色', 'voice.ttsVoice', '看服务支持哪些')) +
    rowWhen('voice.provider:openai', rowInput('密钥', 'voice.ttsApiKey', '不需要就留空；填过留空表示不改')) +
    rowWhen('voice.provider:http', rowInput('地址模板', 'voice.http.urlTemplate', '{text} 会换成要念的话，返回 wav/mp3')) +
    rowSlider('短回复变语音的概率', 'voice.autoChance', 0, 1, 0.05, '', '每次回完，按这个概率把短的转成语音条') +
    rowSlider('多短算短回复', 'voice.autoMaxChars', 20, 300, 10, ' 字') +
    '<div class="row"><label>试听</label><div class="ctl">' +
    '<input type="text" id="tts-test-text" value="晚安啦，今天也辛苦了，明天见～" style="max-width:320px" />' +
    '<button class="ghost small" id="tts-test">生成</button><span class="muted" id="tts-test-state" style="font-size:12px"></span>' +
    '</div><p class="hint">先保存设置再试听；试听不影响真正发消息的开关。</p></div>' +
    '<audio id="tts-test-audio" style="display:none"></audio>' +
    '</div></div>'
}

function sectionImageGen() {
  const cur = cfg?.imageGen?.provider || 'openai'
  const cards = IMG_CARDS.map((c) =>
    '<button type="button" class="tts-card' + (c.id === cur ? ' on' : '') + '" data-img="' + c.id + '">' +
    '<b>' + esc(c.title) + '</b><small>' + esc(c.desc) + '</small></button>').join('')
  return '<div class="section"><h3>生图（实验性）</h3><div class="body">' +
    rowSwitch('允许画图', 'imageGen.enabled') +
    '<div class="tts-cards">' + cards + '</div>' +
    '<input type="hidden" data-key="imageGen.provider" />' +
    rowWhen('imageGen.provider:openai|sdwebui|comfyui', rowInput('地址', 'imageGen.baseUrl', '例如 http://127.0.0.1:7860')) +
    rowWhen('imageGen.provider:openai', rowInput('密钥', 'imageGen.apiKey', '本地服务一般不用填')) +
    rowWhen('imageGen.provider:openai|sdwebui|comfyui', rowInput('模型 / 检查点', 'imageGen.model', 'SD WebUI 可留空，ComfyUI 填 ckpt 名')) +
    rowWhen('imageGen.provider:openai|sdwebui', rowInput('画面尺寸', 'imageGen.size', '例如 1024x1024')) +
    rowWhen('imageGen.provider:openai|sdwebui|comfyui', rowInput('风格补充', 'imageGen.stylePrompt', '每次画图都会拼在提示词前面')) +
    rowWhen('imageGen.provider:sdwebui|comfyui', rowNum('步数', 'imageGen.steps')) +
    rowWhen('imageGen.provider:sdwebui', rowNum('CFG Scale', 'imageGen.cfgScale')) +
    rowWhen('imageGen.provider:sdwebui', rowInput('采样器', 'imageGen.sampler', '例如 DPM++ 2M Karras')) +
    rowWhen('imageGen.provider:openai|sdwebui|comfyui', rowInput('负面提示词', 'imageGen.negativePrompt', '不想让Ta画出来的东西')) +
    rowWhen('imageGen.provider:code', '<p class="hint">代码画：Ta 会直接写一段绘图代码把图画出来，不用连接任何服务。</p>') +
    '</div></div>'
}

function sectionLogs() {
  return '<div class="section"><h3>日志</h3><div class="body">' +
    rowNum('占用空间（MB）', 'logs.maxMB', '日志不落盘') +
    '</div></div>'
}

function sectionDebug() {
  return '<div class="section"><h3>上下文调试</h3><div class="body">' +
    '<div class="row"><label>看看她在想什么</label><div class="ctl">' +
    '<button class="ghost small" id="ctx-load">拉取当前上下文</button>' +
    '<button class="ghost small" id="ctx-compress">手动压一次</button></div></div>' +
    '<div id="ctx-view" class="ctx-view hidden"></div>' +
    '</div></div>'
}

async function loadContextDebug() {
  const view = $('#ctx-view')
  if (!view) return
  view.classList.remove('hidden')
  view.textContent = '拉取中…'
  const r = await api('/api/debug/context')
  if (!r.ok) { view.textContent = r.reason || '拉取失败'; return }
  const sec = (title, body) => '<div class="ctx-sec"><b>' + esc(title) + '</b><pre>' + esc(body || '（空）') + '</pre></div>'
  let html = '<div class="muted" style="font-size:12px;margin-bottom:6px">约 ' + (r.approxTokens || 0) + ' tokens · 窗口 ' + (r.recentCount || 0) + '/' + (r.keepRecent || 30) + ' 条 · 工具 ' + (r.toolCount || 0) + ' 个</div>'
  html += sec('压缩摘要', r.compress && r.compress.summary ? r.compress.summary : '还没有压缩摘要')
  for (const p of (r.systemParts || [])) html += sec(p.name || '段', p.text)
  html += '<div class="ctx-sec"><b>最近原文（' + (r.recentCount || 0) + ' 条）</b>'
  html += '<pre>' + (r.recentWindow || []).map((m) =>
    (m.who === 'bot' ? '她' : '对方') + ': ' + (m.hasMedia ? '[带图]' : '') + (m.text || '[' + m.kind + ']')
  ).join('\n') + '</pre></div>'
  html += '<div class="ctx-sec"><b>她手里的工具</b><pre>' + (r.tools || []).map((t) => t.name).join('\n') + '</pre></div>'
  view.innerHTML = html
}

function sectionHistory() {
  return '<div class="section"><h3>聊天记录</h3><div class="body">' +
    rowNum('媒体保留天数', 'history.keepDays', '0 = 永不清理；聊天文字永远保留，这里只管图片语音') +
    '<div class="row"><label>分片</label><div class="ctl"><span class="muted" style="font-size:12px">' +
    '聊完（空闲 2 分钟）后按每 100 条自动切成一个文件，一个文件就是界面的一页；' +
    '翻到顶可以点「查看更早的记录」取上一页。图片和语音单独存放，超过保留天数才清，文字永远保留。' +
    '</span></div></div>' +
    '</div></div>'
}

function sectionStickers() {
  return '<div class="section"><h3>表情库</h3><div class="body">' +
    rowSwitch('自动配表情', 'stickers.enabled') +
    rowSlider('自动配表情的概率', 'stickers.autoChance', 0.1, 1, 0.05, '', '每次回完，按这个概率补一张表情') +
    rowSwitch('偷表情', 'stickers.steal', 'Ta收到你发的图/表情，会自己挑着收进库') +
    '<div class="row"><label>上传</label><div class="ctl"><label class="file-btn">选图（可多选）<input type="file" id="sticker-file" accept="image/*" multiple hidden /></label>' +
    '<span class="muted" style="font-size:12px">支持动图；也可以直接把图拖到输入框</span></div></div>' +
    '<div id="sticker-grid" class="sticker-grid-lg"></div></div></div>'
}

function sectionSpeakUp() {
  return '<div class="section"><h3>闲置搭话</h3><div class="body">' +
    rowSwitch('开启', 'speakUp.enabled', '你一阵子没说话，Ta会自己开口') +
    rowNum('闲置搭话（分钟）', 'speakUp.idleMinutes', '多久没说话就让Ta开口，从你最后一条消息算起') +
    rowNum('两次至少隔（分钟）', 'speakUp.minGapMinutes') +
    rowNum('每天最多几次', 'speakUp.maxPerDay') +
    rowTime('时间段开始', 'speakUp.quietFrom') +
    rowTime('时间段结束', 'speakUp.quietTo') +
    '</div></div>'
}

function sectionSelfDrive() {
  return '<div class="section"><h3>自驱</h3><div class="body">' +
    rowSwitch('开启', 'selfDrive.enabled', 'Ta会照着这张便签自己找点事做') +
    rowNum('多久看一次（分钟）', 'selfDrive.intervalMinutes') +
    rowNum('每天最多几次', 'selfDrive.maxPerDay') +
    rowTime('时间段开始', 'selfDrive.quietFrom') +
    rowTime('时间段结束', 'selfDrive.quietTo') +
    '<div class="row"><label>共享便签</label><div class="ctl" style="flex-direction:column;align-items:stretch">' +
    '<textarea id="sd-text" placeholder="随手写点什么，想到哪写到哪。就是一张便签，不用列待办，你和Ta都能改。"></textarea>' +
    '<div><button class="ghost small" id="sd-save">保存便签</button></div></div></div>' +
    '</div></div>'
}

function sectionGuests() {
  return '<div class="section"><h3>客人</h3><div class="body">' +
    '<div class="row"><label>创建客人</label><div class="ctl">' +
    '<input type="text" id="g-u" placeholder="用户名（区分大小写）" style="max-width:190px" />' +
    '<input type="text" id="g-d" placeholder="称呼" style="max-width:100px" />' +
    '<select id="g-level"><option value="social">只聊天日记</option><option value="full">完全权限</option></select>' +
    '<button class="primary small" id="g-create">创建</button></div></div>' +
    '<div id="guest-list"></div></div></div>'
}

function sectionAccount() {
  return '<div class="section"><h3>账号</h3><div class="body">' +
    '<div class="row"><label>修改当前账户密码</label><div class="ctl">' +
    '<input type="password" id="pw-old" placeholder="当前密码" style="max-width:150px" />' +
    '<input type="password" id="pw-new" placeholder="新密码（至少 6 位）" style="max-width:180px" />' +
    '<button class="ghost small" id="pw-save">改密码</button></div></div>' +
    '<div class="row"><label>退出登录</label><div class="ctl"><button class="danger small" id="logout">退出登录</button></div></div>' +
    '</div></div>'
}

function collectConfig() {
  const out = {}
  $$('#cfg-body [data-key]').forEach((el) => {
    const wrap = el.closest('.when-wrap')
    if (wrap && getComputedStyle(wrap).display === 'none') return
    const key = el.dataset.key
    let v
    if (el.type === 'checkbox') v = el.checked
    else if (el.type === 'number') v = el.value === '' ? undefined : Number(el.value)
    else v = el.value
    if (v === undefined) return
    const parts = key.split('.')
    let o = out
    for (let i = 0; i < parts.length - 1; i++) { o[parts[i]] = o[parts[i]] || {}; o = o[parts[i]] }
    o[parts[parts.length - 1]] = v
  })
  return out
}

async function loadEdgeState() {
  const el = $('#edge-state')
  const btn = $('#edge-install')
  if (!el) return
  const r = await api('/api/voice/edge')
  const badge = document.querySelector('[data-edge-badge]')
  const mark = (t) => { if (badge) badge.textContent = t }
  if (!r.ok) { el.textContent = '查不到'; mark('查不到'); return }
  el.textContent = r.installed ? '已装好，可以直接用' : '还没装'
  mark(r.installed ? '已装' : '还没装')
  if (btn) {
    btn.textContent = r.installed ? '重装' : '安装'
    btn.onclick = async () => {
      btn.disabled = true
      el.textContent = '正在装，可能要几分钟…'
      const r2 = await api('/api/voice/edge', { method: 'POST' })
      btn.disabled = false
      toast(r2.ok ? '装好了' : (r2.reason || '没装上'), r2.ok ? '' : 'err')
      loadEdgeState()
    }
  }
}

function fmtSlider(el) {
  const v = Number(el.value)
  const unit = el.dataset.unit || ''
  if (!unit && Number(el.max) <= 1) return Math.round(v * 100) + '%'
  return String(Math.round(v)) + unit
}
function refreshSliders(root) {
  const box = root || document
  $$('input[type="range"][data-key]', box).forEach((el) => {
    const out = box.querySelector('[data-out="' + el.dataset.key + '"]')
    if (out) out.textContent = fmtSlider(el)
  })
}
document.addEventListener('input', (e) => {
  const t = e.target
  if (!t || t.type !== 'range' || !t.dataset.key) return
  const box = t.closest('#cfg-body') || document
  const out = box.querySelector('[data-out="' + t.dataset.key + '"]')
  if (out) out.textContent = fmtSlider(t)
})

function bindCards(root, attr, key) {
  const hidden = root.querySelector('[data-key="' + key + '"]')
  root.querySelectorAll('[' + attr + ']').forEach((b) => {
    b.onclick = () => {
      const id = b.getAttribute(attr)
      if (hidden) hidden.value = id
      root.querySelectorAll('[' + attr + ']').forEach((x) => x.classList.toggle('on', x === b))
      applyWhen(root)
      if (key === 'voice.provider' && (id === 'edge' || id === 'auto')) loadEdgeState()
    }
  })
}

function bindTtsCards(root) {
  bindCards(root, 'data-tts', 'voice.provider')
  const btn = root.querySelector('#tts-test')
  if (btn) btn.onclick = async () => {
    const st = $('#tts-test-state')
    const box = $('#tts-test-text')
    btn.disabled = true
    if (st) st.textContent = '正在生成…'
    const r = await api('/api/voice/test', { method: 'POST', body: { text: box ? box.value : '' } })
    btn.disabled = false
    if (!r.ok) { if (st) st.textContent = r.reason || '没成'; return }
    if (st) st.textContent = '用的 ' + r.engine
    const a = $('#tts-test-audio')
    if (a) { a.src = 'data:' + r.mime + ';base64,' + r.data; const p = a.play(); if (p && p.catch) p.catch(() => {}) }
  }
}

function bindSectionEvents(box) {
  if (cfg) {
    $$('#cfg-body [data-key]').forEach((el) => {
      const key = el.dataset.key
      const v = key.split('.').reduce((o, k) => (o == null ? o : o[k]), cfg)
      if (el.type === 'checkbox') el.checked = Boolean(v)
      else if (v != null) el.value = v
    })
    const ms = $('#model-sel')
    if (ms) {
      const cur = cfg.model || {}
      ms.value = cur.provider && cur.model ? cur.provider + '::' + cur.model : '__default__'
    }
  }
  loadStickerGrid()
  loadGuests()
  loadSelfDriveText()
  loadEdgeState()
  loadAsrState()
  document.querySelectorAll('[data-tone]').forEach((b) => {
    b.onclick = () => {
      const kind = b.dataset.tone
      const key = kind === 'in' ? 'inVolume' : 'outVolume'
      const el = document.querySelector('[data-key="sound.' + key + '"]')
      if (el && cfg && cfg.sound) cfg.sound[key] = Number(el.value)
      playSound(kind)
    }
  })
  bindPersonaEditor()
  bindTtsCards(box)
  bindCards(box, 'data-img', 'imageGen.provider')
  refreshSliders(box)
  $('#avatar-file') && ($('#avatar-file').onchange = uploadAvatar)
  $('#avatar-reset') && ($('#avatar-reset').onclick = () => { /* 用默认图标=删掉头像文件 */ api('/api/avatar', { method: 'POST', body: { data: '' } }).then(() => { refreshAvatar(); toast('用回默认图标了') }) })
  $('#bg-file') && ($('#bg-file').onchange = uploadBackground)
  $('#bg-clear') && ($('#bg-clear').onclick = async () => { await api('/api/background', { method: 'POST', body: { data: '' } }); await loadConfig(); applyTheme(); toast('背景图清掉了') })
  $('#sticker-file') && ($('#sticker-file').onchange = uploadSticker)
  $('#sd-save') && ($('#sd-save').onclick = async () => {
    const r = await api('/api/selfdrive', { method: 'POST', body: { text: $('#sd-text').value } })
    toast(r.ok ? '备忘录存好了' : (r.reason || '没存上'), r.ok ? '' : 'err')
  })
  $('#ctx-load') && ($('#ctx-load').onclick = loadContextDebug)
  $('#ctx-compress') && ($('#ctx-compress').onclick = async () => {
    toast('正在压…')
    const r = await api('/api/debug/compress', { method: 'POST' })
    toast(r.ok ? (r.summarized ? '压完了：' + r.summarized + ' 条进摘要' : '没有要压的') : (r.reason || r.message || '没压成'), r.ok ? '' : 'err')
    if (r.ok) loadContextDebug()
  })
}

function bindPersonaEditor() {
  const sel = $('#persona-sel')
  const box = $('#persona-editor')
  if (!sel || !box) return
  let editingId = '' // 空 = 新建
  const open = () => box.classList.remove('hidden')
  const close = () => { box.classList.add('hidden'); editingId = '' }
  const fill = (p) => {
    $('#pe-name').value = p ? p.name || '' : ''
    $('#pe-tags').value = p ? (p.tags || []).join(',') : ''
    $('#pe-body').value = p ? p.body || '' : ''
  }
  sel.onchange = async () => {
    if (sel.value === '__new__') {
      editingId = ''
      fill(null)
      open()
      $('#pe-name').focus()
      return
    }
    const r = await api('/api/config', { method: 'POST', body: { persona: { current: sel.value } } })
    if (r.ok) {
      cfg = r.config || cfg
      toast('换上「' + (sel.options[sel.selectedIndex].text) + '」了')
    }
    close()
  }
  $('#pe-edit').onclick = async () => {
    const id = sel.value
    if (!id || id === '__new__') return toast('先选一个要改的人设', 'err')
    const r = await api('/api/personas/detail?id=' + encodeURIComponent(id))
    if (!r.ok || !r.persona) return toast(r.reason || '没读到这个人设', 'err')
    editingId = r.persona.id
    fill(r.persona)
    open()
  }
  $('#pe-cancel').onclick = close
  $('#pe-save').onclick = async () => {
    const name = $('#pe-name').value.trim()
    const body = $('#pe-body').value.trim()
    if (!name) return toast('给Ta起个名字', 'err')
    if (!body) return toast('人设正文不能空', 'err')
    const r = await api('/api/personas/save', {
      method: 'POST',
      body: {
        id: editingId || '',
        name,
        tags: $('#pe-tags').value.split(',').map((s) => s.trim()).filter(Boolean),
        body,
      },
    })
    if (!r.ok) return toast(r.reason || '没存上', 'err')
    await api('/api/config', { method: 'POST', body: { persona: { current: r.persona.id } } })
    personaList = (await api('/api/personas')).personas || []
    await loadConfig()
    switchView('settings')
    toast('人设存好了，已经换上')
  }
  $('#pe-del').onclick = async () => {
    if (!editingId) return toast('这是还没存的新人设，直接收起就行', 'err')
    const r = await api('/api/personas/delete', { method: 'POST', body: { id: editingId } })
    if (!r.ok) return toast(r.reason || '没删掉', 'err')
    personaList = (await api('/api/personas')).personas || []
    close()
    switchView('settings')
    toast('删掉了')
  }
}

async function uploadAvatar(e) {
  const f = e.target.files && e.target.files[0]
  e.target.value = ''
  if (!f) return
  const rd = new FileReader()
  rd.onload = async () => {
    const r = await api('/api/avatar', { method: 'POST', body: { data: String(rd.result), mime: f.type } })
    toast(r.ok ? '头像换好了' : (r.reason || '没换'), r.ok ? '' : 'err')
    if (r.ok) refreshAvatar()
  }
  rd.readAsDataURL(f)
}

async function uploadBackground(e) {
  const f = e.target.files && e.target.files[0]
  e.target.value = ''
  if (!f) return
  const rd = new FileReader()
  rd.onload = async () => {
    const r = await api('/api/background', { method: 'POST', body: { data: String(rd.result), mime: f.type } })
    if (r.ok) {
      bustBackground()
      const mode = $('#cfg-body [data-key="theme.mode"]')
      if (mode && mode.value !== 'acrylic' && mode.value !== 'frosted') mode.value = 'acrylic'
      await saveConfig()
      toast('背景图设好了（已切到亚克力模式）')
    } else toast(r.reason || '没设上', 'err')
  }
  rd.readAsDataURL(f)
}

async function uploadSticker(e) {
  const files = Array.from((e.target.files || []))
  e.target.value = ''
  if (!files.length) return
  let ok = 0
  let skip = 0
  let last = ''
  const names = []
  for (const f of files) {
    if (f.size > 8 * 1024 * 1024) { skip++; continue }
    const data = await new Promise((resolve) => {
      const rd = new FileReader()
      rd.onload = () => resolve(String(rd.result).split(',')[1] || '')
      rd.onerror = () => resolve('')
      rd.readAsDataURL(f)
    })
    if (!data) { skip++; continue }
    const r = await api('/api/stickers/upload', { method: 'POST', body: { data, from: 'panel' } })
    if (r.ok) { ok++; if (r.item?.name) names.push(r.item.name) }
    else { skip++; last = r.reason || '' }
  }
  toast(ok ? (files.length > 1 ? `收进表情库 ${ok} 张` + (skip ? `，跳过 ${skip} 张` : '') : '收进表情库了') : (last || '没收成'), ok ? '' : 'err')
  const refresh = () => { if ($('#sticker-grid')) loadStickerGrid(); if ($('.sticker-picker')) { $('.sticker-picker').remove() } }
  refresh()
  setTimeout(refresh, 3000)
  setTimeout(refresh, 7000)
}

async function loadStickerGrid() {
  const grid = $('#sticker-grid')
  if (!grid) return
  const r = await api('/api/stickers')
  const items = (r.ok && r.items) || []
  if (!items.length) { grid.innerHTML = '<div class="muted" style="font-size:12px">还没有表情。上传几张，或者看到别人发的图点「收进表情库」。</div>'; return }
  grid.innerHTML = items.map((i) => '<div class="sticker-item"><img src="' + BASE + '/api/sticker/' + esc(i.md5) + '" alt="" />' +
    '<div class="sticker-name">' + esc(i.name || '没起名') + '</div>' +
    '<div class="sticker-acts"><button data-rename="' + esc(i.md5) + '">改名</button><button data-del-sticker="' + esc(i.md5) + '">删除</button></div></div>').join('')
  grid.querySelectorAll('.sticker-item img').forEach((img) => guardMediaImg(img, '图没了'))
  grid.onclick = async (e) => {
    const rn = e.target.closest('[data-rename]')
    if (rn) {
      const md5 = rn.dataset.rename
      const cur = items.find((x) => x.md5 === md5)
      inlineEditor(rn.closest('.sticker-item'), cur?.name || '', '起个名字', async (name) => {
        const r2 = await api('/api/stickers/' + md5, { method: 'PATCH', body: { name } })
        toast(r2.ok ? '改好了' : (r2.reason || '没改成'), r2.ok ? '' : 'err'); loadStickerGrid()
      })
      return
    }
    const dl = e.target.closest('[data-del-sticker]')
    if (dl) {
      const r2 = await api('/api/stickers/' + dl.dataset.delSticker, { method: 'DELETE' })
      toast(r2.ok ? '删了' : (r2.reason || '没删掉'), r2.ok ? '' : 'err'); loadStickerGrid()
    }
  }
}

function inlineEditor(host, value, placeholder, onSave) {
  const old = host.querySelector('.inline-edit')
  if (old) old.remove()
  const box = document.createElement('div')
  box.className = 'inline-edit'
  box.innerHTML = '<input type="text" /><button class="primary small">保存</button><button class="ghost small">取消</button>'
  host.appendChild(box)
  const input = box.querySelector('input')
  input.value = value || ''
  input.placeholder = placeholder || ''
  input.focus()
  input.select()
  const close = () => box.remove()
  box.querySelector('.ghost').onclick = close
  box.querySelector('.primary').onclick = async () => {
    const v = input.value.trim()
    close()
    await onSave(v)
  }
  input.onkeydown = (e) => { if (e.key === 'Enter') box.querySelector('.primary').click(); if (e.key === 'Escape') close() }
}

async function loadGuests() {
  const box = $('#guest-list')
  if (!box) return
  const create = $('#g-create')
  if (create) create.onclick = async () => {
    const username = ($('#g-u')?.value || '').trim()
    if (!username) return toast('先填个用户名', 'err')
    const r2 = await api('/api/guests', { method: 'POST', body: { username, displayName: ($('#g-d')?.value || '').trim() || username, level: $('#g-level')?.value || 'social' } })
    if (r2.ok) {
      toast('创建好了，一次性密码：' + r2.password)
      if (navigator.clipboard) navigator.clipboard.writeText(r2.password).catch(() => {})
      if ($('#g-u')) $('#g-u').value = ''
      if ($('#g-d')) $('#g-d').value = ''
      loadGuests()
    } else toast(r2.reason || '没建成', 'err')
  }
  const r = await api('/api/guests')
  const list = (r.ok && r.guests) || []
  if (!list.length) { box.innerHTML = '<div class="muted" style="font-size:12px;margin-top:8px">还没有客人。</div>'; return }
  box.innerHTML = list.map((g) => '<div class="guest-row"><span class="g-name">' + esc(g.displayName || g.username) + '</span>' +
    '<span class="muted" style="font-size:12px">@' + esc(g.username) + '</span>' +
    '<span class="spacer"></span>' +
    '<select data-glevel="' + esc(g.subject) + '"><option value="social"' + (g.level === 'social' ? ' selected' : '') + '>只聊天日记</option><option value="full"' + (g.level === 'full' ? ' selected' : '') + '>完全权限</option></select>' +
    '<button class="ghost small" data-gpw="' + esc(g.subject) + '">重置密码</button>' +
    '<button class="danger small" data-gdel="' + esc(g.subject) + '">删除</button></div>').join('')
  box.onclick = async (e) => {
    const gp = e.target.closest('[data-gpw]')
    if (gp) {
      inlineEditor(gp.closest('.guest-row'), '', '新密码（至少 6 位）', async (pw) => {
        if (!pw) return
        const r2 = await api('/api/guests/' + gp.dataset.gpw, { method: 'PATCH', body: { password: pw } })
        toast(r2.ok ? '密码换了' : (r2.reason || '没换'), r2.ok ? '' : 'err')
      })
      return
    }
    const gd = e.target.closest('[data-gdel]')
    if (gd) {
      const r2 = await api('/api/guests/' + gd.dataset.gdel, { method: 'DELETE' })
      toast(r2.ok ? '删了' : (r2.reason || '没删掉'), r2.ok ? '' : 'err'); loadGuests()
    }
  }
  box.onchange = async (e) => {
    const sel = e.target.closest('[data-glevel]')
    if (!sel) return
    const r2 = await api('/api/guests/' + sel.dataset.glevel, { method: 'PATCH', body: { level: sel.value } })
    toast(r2.ok ? '改好了' : (r2.reason || '没改成'), r2.ok ? '' : 'err')
  }
}

async function loadSelfDriveText() {
  const box = $('#sd-text')
  if (!box) return
  const r = await api('/api/selfdrive')
  if (r.ok) box.value = r.text || ''
}

function renderMemorySettings() {
  const box = $('#mem-settings')
  if (!box) return
  const times = (cfg?.memory?.consolidateTimes || ['04:00', '16:00']).join(',')
  box.innerHTML = '<div class="section"><h3>记忆设置</h3><div class="body">' +
    rowSwitch('开启记忆', 'memory.enabled') +
    rowNum('每次翻几条相关的', 'memory.recallCount') +
    rowSwitch('每天自动整理记忆', 'memory.dailyConsolidate') +
    '<div class="row"><label>整理时间</label><div class="ctl">' +
    '<input type="text" data-mem="times" value="' + esc(times) + '" placeholder="04:00,16:00" style="max-width:220px" />' +
    '<button class="ghost small" id="mem-consolidate">现在整理一次</button></div></div>' +
    '<p class="hint">整理时间写 24 小时制，多个用逗号隔开，例如 04:00,16:00</p>' +
    '<div class="row"><div class="ctl"><button class="primary" id="mem-save">保存记忆设置</button></div></div>' +
    '</div></div>'
  if (cfg) {
    $$('#mem-settings [data-key], #pane-body [data-key]').forEach((el) => {
      const v = el.dataset.key.split('.').reduce((o, k) => (o == null ? o : o[k]), cfg)
      if (el.type === 'checkbox') el.checked = Boolean(v)
      else if (v != null) el.value = v
    })
  }
  const save = box.querySelector('#mem-save')
  if (save) save.onclick = async () => {
    const body = {}
    box.querySelectorAll('[data-key]').forEach((el) => {
      const key = el.dataset.key
      const v = el.type === 'checkbox' ? el.checked : el.type === 'number' ? Number(el.value) : el.value
      const parts = key.split('.')
      let o = body
      for (let i = 0; i < parts.length - 1; i++) { o[parts[i]] = o[parts[i]] || {}; o = o[parts[i]] }
      o[parts[parts.length - 1]] = v
    })
    const timeInput = box.querySelector('[data-mem="times"]')
    if (timeInput) {
      const list = String(timeInput.value || '').split(/[,，\s]+/).map((s) => s.trim()).filter(Boolean)
        .filter((s) => /^([01]?\d|2[0-3]):[0-5]\d$/.test(s))
        .map((s) => (s.length === 4 ? '0' + s : s))
      if (!list.length) return toast('整理时间格式不对，像 04:00 这样写', 'err')
      body.memory = { ...(body.memory || {}), consolidateTimes: list }
    }
    const r = await api('/api/config', { method: 'PATCH', body })
    toast(r.ok ? '存好了' : (r.reason || '没存上'), r.ok ? '' : 'err')
    if (r.ok) cfg = r.config
  }
  const btn = box.querySelector('#mem-consolidate')
  if (btn) btn.onclick = async () => {
    btn.disabled = true
    btn.textContent = '整理中…'
    const r = await api('/api/memory/consolidate', { method: 'POST' })
    btn.disabled = false
    btn.textContent = '现在整理一次'
    toast(r.ok ? '整理完了' : (r.reason || '没整理成'), r.ok ? '' : 'err')
    if (r.ok && currentView === 'memory') switchView('memory')
  }
}

function queryOf(name) {
  try { return new URL(location.href).searchParams.get(name) } catch { return null }
}

function applyTitle() {
  const site = (botState?.siteName || '').trim() || 'AuraChat'
  document.title = botState?.title || site
  const h1 = $('#gate-title')
  if (h1) h1.textContent = site
}

function applyFavicon() {
  const el = $('#favicon')
  if (el) el.href = BASE + '/icon.svg'
}

async function boot({ silent = false } = {}) {
  const deep = queryOf('token')
  if (deep) localStorage.setItem(TOKEN_KEY, deep)
  botState = await api('/api/state')
  if (!botState.ok) { showGate('login', '连不上服务'); return }
  me = botState.me
  applyTheme()
  applyTitle()
  applyFavicon()
  setAvatar($('#gate-logo'), false)
  if (botState.needSetup) { showGate('setup'); initWizard(); return }
  if (!me) { showGate('login'); bindLogin(); return }
  await enterApp()
  const view = queryOf('view')
  if (view && NAVS.some((n) => n.id === view)) switchView(view)
  if (!silent) toast('欢迎回来' + (me.role === 'owner' ? '，' + (cfg?.ownerName || '主人') : me.name ? '，' + me.name : ''))
}

function bindLogin() {
  const form = $('#gate-form')
  form.onsubmit = async (e) => {
    e.preventDefault()
    await doLogin($('#g-username').value.trim(), $('#g-password').value)
  }
}

document.addEventListener('input', (e) => {
  const t = e.target
  if (t && t.tagName === 'TEXTAREA' && t.id === 'input') {
    t.style.height = 'auto'
    t.style.height = Math.min(140, t.scrollHeight) + 'px'
  }
})

boot()
