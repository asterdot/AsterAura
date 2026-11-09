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
const REMEMBER_KEY = 'aurachat_remember'
// 记住我：勾选 → token 存 localStorage（关掉浏览器也在）；不勾 → 只存 sessionStorage（关掉浏览器就掉）
let token = localStorage.getItem(TOKEN_KEY) || sessionStorage.getItem(TOKEN_KEY) || ''
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
    // 会话失效（401）一律回登录页，不留半认证状态、也不留空白主界面
    if (r.status === 401 && me) { me = null; showGate('login', '登录状态已失效，请重新登录') }
    const t = await r.text()
    try { return JSON.parse(t) } catch (e) { return { ok: false, reason: t.slice(0, 200) || ('HTTP ' + r.status) } }
  }).catch((e) => ({ ok: false, reason: e.message }))
}

function setToken(t, remember) {
  token = t || ''
  const keep = remember === undefined ? (localStorage.getItem(REMEMBER_KEY) === '1') : !!remember
  if (remember !== undefined) localStorage.setItem(REMEMBER_KEY, keep ? '1' : '0')
  localStorage.removeItem(TOKEN_KEY)
  sessionStorage.removeItem(TOKEN_KEY)
  if (t) {
    if (keep) localStorage.setItem(TOKEN_KEY, t)
    else sessionStorage.setItem(TOKEN_KEY, t)
  }
}

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
// owner 判定统一：新版本没有访客，凡账号密码登录者皆为主人
const isOwner = () => Boolean(me && (me.role === 'owner' || me.role === 'account'))

bindTheme({ base: BASE, cfg: () => cfg, state: () => botState, fetchLocalBg: () => fetchLocalBg() })
watchSystemDark()

// 自动化测试用的把手
window.__aura = {
  get cfg() { return cfg },
  get me() { return me },
  get state() { return botState },
  get myReadAt() { return myReadAt },
  get seenAt() { return seenAt },
  get unreadCount() { return document.querySelectorAll('#msgs .msg.her.unread').length },
  applyTheme,
  playSound,
  bustBackground,
  reconcileUnread,
}

// 头像也走授权取回（服务端 /api/avatar 已要求登录）：blob object URL，取不到就显示公共 icon.svg
let avatarBlob = null
const iconSrc = () => BASE + '/icon.svg'
function setAvatar(el, src) {
  if (!el) return
  el.innerHTML = '<img src="' + src + '" alt="" />'
}
function disposeAvatarBlob() {
  if (avatarBlob) { try { URL.revokeObjectURL(avatarBlob) } catch (e) { /* 已回收就算了 */ } avatarBlob = null }
}
async function refreshAvatar() {
  const el = $('#rail-avatar')
  if (!el) return
  if (!avatarBlob) setAvatar(el, iconSrc())          // 先给占位，别闪破图
  try {
    const r = await fetch(BASE + '/api/avatar?ts=' + Date.now(), {
      headers: token ? { authorization: 'Bearer ' + token } : {},
      cache: 'no-store',
    })
    if (!r.ok) throw new Error('HTTP ' + r.status)
    if (!(r.headers.get('content-type') || '').startsWith('image/')) throw new Error('not an image')
    const u = URL.createObjectURL(await r.blob())
    disposeAvatarBlob()
    avatarBlob = u
    if (el.isConnected) setAvatar(el, u)
  } catch (e) { /* 没设过头像（401/404）：保持默认图标 */ }
}

function showGate(mode, msg = '') {
  $('#gate').classList.remove('hidden')
  $('#app').classList.add('hidden')
  const wiz = $('#wizard'), form = $('#gate-form'), reg = $('#reg-form'), comp = $('#comp-form'), links = $('#gate-links')
  const loginish = mode === 'login' || mode === 'legacy'
  wiz.classList.toggle('hidden', mode !== 'setup')
  form.classList.toggle('hidden', !loginish)
  reg.classList.toggle('hidden', mode !== 'register')
  comp.classList.toggle('hidden', mode !== 'companion')
  if (links) {
    links.classList.toggle('hidden', mode === 'setup' || mode === 'companion')
    const tog = $('#link-toggle')
    if (tog) {
      tog.dataset.to = mode === 'register' ? 'login' : 'register'
      tog.textContent = mode === 'register' ? '已有账号？去登录' : '没有账号？注册一个'
    }
    const leg = $('#link-legacy')
    if (leg) {
      leg.classList.toggle('hidden', mode === 'register')
      leg.textContent = mode === 'legacy' ? '用新账号登录' : '本机旧账号登录'
    }
    const wlink = $('#link-wizard')
    // 旧版本机初始化只在登录页显式露出，不是公开注册；
    // t28：匿名 state 不再携带 needSetup，显式链接不再依赖该字段（服务端 /api/setup 仍会拒绝已有主人的重复初始化）
    if (wlink) wlink.classList.toggle('hidden', mode !== 'login')
  }
  $('#gate-sub').textContent =
    mode === 'setup' ? '第一次来，先把 Ta 安顿好。' :
    mode === 'register' ? '注册一个本地账号。' :
    mode === 'companion' ? '给账号建一个陪伴角色。' :
    mode === 'legacy' ? '旧账号兼容入口：登录本机原有账号。' : ''
  $('#gate-hello').textContent = loginish ? greet() : ''
  if (msg) {
    const m = mode === 'setup' ? $('#wizard-msg') : mode === 'register' ? $('#reg-msg') : mode === 'companion' ? $('#comp-msg') : $('#gate-msg')
    if (m) m.textContent = msg
  }
  setAvatar($('#gate-logo'), iconSrc())
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
  setAvatar($('#w-avatar-preview'), iconSrc())

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
  $('#w-avatar-clear').onclick = () => { wizAvatar = null; setAvatar($('#w-avatar-preview'), iconSrc()) }
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
    // 旧版本机初始化完成后只校验旧凭据（t25：旧会话清凭据回登录，不进主界面），引导用户注册/登录账号
    await doLogin(username, password, 'legacy')
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

async function doLogin(username, password, mode = 'account') {
  const path = mode === 'legacy' ? '/api/login' : '/api/v2/auth/login'
  const r = await api(path, { method: 'POST', body: { username, password } })
  if (!r.ok) {
    const msg = r.reason || '登录失败'
    if ($('#wizard') && !$('#wizard').classList.contains('hidden')) $('#wizard-msg').textContent = msg
    else $('#gate-msg').textContent = msg
    return false
  }
  const remember = !($('#g-remember') && $('#g-remember').checked === false)
  setToken(r.token, remember)
  if (mode === 'legacy') {
    // 旧账号兼容登录：只作迁移期校验，不再进入主界面——立即清凭据回登录，
    // 要求账号认证 + 角色分流（t25：所有登录模式都必须走 account 认证；旧会话清凭据回登录）
    setToken('')
    me = null
    showGate('login', '旧版账号仅用于迁移，请使用账号登录或注册')
    return false
  }
  me = r.account ? { subject: r.account.accountId, role: r.account.role || 'account', name: r.account.displayName || r.account.username, level: 'full' } : null
  await routeAfterAccountAuth()
  return true
}

// 账号认证成功后的角色分流（t13：无角色 → 创建页；有角色 → 主界面；失效 → 回登录）
async function routeAfterAccountAuth() {
  const s = await api('/api/v2/companions/current')
  if (s.ok && s.companion) { await enterApp(); return }
  if (s.ok && !s.companion) {
    $('#comp-hello').textContent = (me && me.name ? me.name : '这个账号') + '，还没有陪伴角色。'
    showGate('companion')
    return
  }
  // 任何无法验证角色状态的失败（401/403/503/网络）都清凭据回登录，不留半认证状态（t28）
  setToken('')
  me = null
  showGate('login', s.reason || '登录状态已失效，请重新登录')
}

function bindRegister() {
  const form = $('#reg-form')
  if (!form) return
  form.onsubmit = async (e) => {
    e.preventDefault()
    const r = await api('/api/v2/auth/register', { method: 'POST', body: { username: $('#r-username').value.trim(), password: $('#r-password').value } })
    if (!r.ok) { $('#reg-msg').textContent = r.reason || '没注册上，再试一次'; return }
    setToken(r.token, localStorage.getItem(REMEMBER_KEY) === '1')
    me = { subject: r.account.accountId, role: r.account.role || 'account', name: r.account.displayName || r.account.username, level: 'full' }
    await routeAfterAccountAuth()   // 注册成功 → 按同一流程取角色状态
  }
}

function bindCompanionCreate() {
  const form = $('#comp-form')
  if (!form) return
  form.onsubmit = async (e) => {
    e.preventDefault()
    // Idempotency-Key：网络重试/重复提交不会建出第二个角色
    const key = (typeof crypto !== 'undefined' && crypto.randomUUID) ? crypto.randomUUID() : 'comp-' + Date.now() + '-' + Math.random().toString(36).slice(2)
    const r = await api('/api/v2/companions', { method: 'POST', headers: { 'Idempotency-Key': key }, body: { name: $('#c-name').value.trim() } })
    if (r.ok && r.companion) { toast('角色创建好了'); await enterApp(); return }
    if (r.companion) { await enterApp(); return }   // 幂等重放：返回同一角色
    $('#comp-msg').textContent = r.reason || '没建成，再试一次'
  }
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
  { id: 'mood', label: '心情与生命' },
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
  if (id !== 'chat' && currentView === 'chat') {
    // 离开聊天页：DOM 马上清空，把还挂着未读点的她消息折进红点计数；
    // 已读到没来得及上报的水位在这儿收尾（规则内：读到时条件都满足过）
    offviewUnread = document.querySelectorAll('#msgs .msg.her.unread').length
    flushReadPush()
  }
  currentView = id
  syncNav()
  const main = $('#main')
  main.innerHTML = ''
  if (id === 'chat') renderChat(main)
  else if (id === 'diary') renderDiary(main)
  else if (id === 'mood') renderMoodAndLife(main)
  else if (id === 'overview') views.overview.renderOverview(main)
  else if (id === 'memory') renderMemory(main)
  else if (id === 'settings') renderSettings(main)
  else if (id === 'logs') views.logs.renderLogs(main)
}

// ---------- #1 已读（A 半）：「我读到哪了」 ----------
// 只渲染服务器确认的事实：未读 = 她消息的 t > 打开聊天页那一刻的水位快照（seenAt），
// 不因为水位推进而中途消失。水位推进规则写死（别放宽）：只有「页面可见 + 窗口有焦点 +
// 当前是聊天视图 + 消息渲染进视口」才消点并推进；切视图 / 最小化 / 切到别的程序一律不推进
// ——否则又变回「重开一次聊天页就全变已读」那个自欺。
let myReadAt = 0        // 我的已读水位（服务端 GET api/read 的 myReadAt）
let seenAt = 0          // 打开聊天页那一刻的水位快照：t > seenAt 的她的消息算「没看过」
let offviewUnread = 0   // 不在聊天视图时进来的她的消息数（只用于红点；进聊天页后按服务端水位重算）
let readUiReady = false // 本页水位快照是否已就位（没就位时不判未读，避免拿陈旧 seenAt 乱标）
let lastReadHerT = 0    // 本会话已读到的最新一条她的 t（POST api/read 的 upToT，契约 §4）
let readTimer = null
let msgIo = null        // IntersectionObserver：未读点滚进视口才读掉
let lineIo = null       // IntersectionObserver：分隔线「滚过即消失」

function canAdvanceRead() {
  return document.visibilityState === 'visible' && document.hasFocus() && currentView === 'chat'
}

async function fetchReadState() {
  try {
    const r = await api('/api/read')
    if (r && r.ok) myReadAt = Number(r.myReadAt || 0)
  } catch { /* 静默 */ }
  return myReadAt
}

function setRailBadge(n) {
  const el = $('#rail-avatar')
  if (!el) return
  if (n > 0) el.dataset.unread = n > 9 ? '9+' : String(n)
  else delete el.dataset.unread
}

function refreshUnreadBadge() {
  const box = $('#msgs')
  const n = (box ? box.querySelectorAll('.msg.her.unread').length : 0) + offviewUnread
  setRailBadge(n)
}

function hasUnread() {
  const box = $('#msgs')
  return Boolean(box && box.querySelector('.msg.her.unread'))
}

// 推进水位：先把本地水位抬到看见的最大 t，再延迟上报。
// 本地还有未读没看全就不 POST——服务端 POST api/read 是「标到此刻」，提前打会把还没
// 进视口的旧未读一并标成已读（契约 ④ 的 upToT 到位前先这样兜住）。
function advanceReadTo(t) {
  if (!canAdvanceRead()) return
  if (t) {
    myReadAt = Math.max(myReadAt, Number(t) || 0)
    lastReadHerT = Math.max(lastReadHerT, Number(t) || 0)   // upToT = 已读到的最新一条她的 t（契约 §4）
  }
  refreshUnreadBadge()
  if (readTimer) clearTimeout(readTimer)
  readTimer = setTimeout(async () => {
    readTimer = null
    if (!canAdvanceRead() || hasUnread() || offviewUnread > 0) return
    try { await api('/api/read', { method: 'POST', body: lastReadHerT > 0 ? { upToT: lastReadHerT } : {} }) } catch { /* 不影响聊天 */ }
  }, 400)
}

// 消掉一批未读（调用方先判定这批确实该读：IO 相交，或 reconcile 的视口判定）
function markReadEls(els) {
  if (!canAdvanceRead()) return
  let maxT = 0, hit = false
  for (const el of els) {
    if (!el.classList.contains('unread')) continue
    el.classList.remove('unread')
    if (msgIo) msgIo.unobserve(el)
    maxT = Math.max(maxT, Number(el.dataset.t) || 0)
    hit = true
  }
  if (hit) advanceReadTo(maxT)
}

function watchUnread() {
  if (msgIo) msgIo.disconnect()
  msgIo = new IntersectionObserver((entries) => {
    if (!canAdvanceRead()) return   // 不可见 / 没焦点 / 不在聊天视图：不消点、不推进
    markReadEls(entries.filter((e) => e.isIntersecting).map((e) => e.target))
  })
  const box = $('#msgs')
  if (box) box.querySelectorAll('.msg.her.unread').forEach((el) => msgIo.observe(el))
}

// 事件驱动的补账（IO 只在相交变化时回调，页面重新可见/聚焦时相交没变、不会重发）：
// 载入完成、页面重新可见、窗口重新聚焦时跑一遍——条件不满足时整个函数直接返回。
function reconcileUnread() {
  if (!canAdvanceRead()) return
  const box = $('#msgs'); if (!box) return
  const vh = window.innerHeight
  markReadEls(Array.from(box.querySelectorAll('.msg.her.unread')).filter((el) => {
    const r = el.getBoundingClientRect()
    return r.bottom > 0 && r.top < vh
  }))
}

document.addEventListener('visibilitychange', () => { if (document.visibilityState === 'visible') reconcileUnread() })
window.addEventListener('focus', reconcileUnread)

// 离开聊天页前，把已读到却还没上报的水位补上——读到时条件都满足过，上报只是收尾；
// 未读没看全就照规则不打。
function flushReadPush() {
  const pending = readTimer != null
  if (readTimer) { clearTimeout(readTimer); readTimer = null }
  if (!pending || !canAdvanceRead() || hasUnread() || offviewUnread > 0) return
  api('/api/read', { method: 'POST', body: lastReadHerT > 0 ? { upToT: lastReadHerT } : {} }).catch(() => {})
}

// 「上次看到这里」分隔线：只在打开聊天页时插一次，跟着第一条未读；
// 见过之后从上方滚出视口（真滚过了）才撤，往回翻旧记录从下方滚出不撤。
function unreadLineNode() {
  const el = document.createElement('div')
  el.className = 'unread-line'
  el.textContent = seenAt ? '上次看到这里 · ' + fmtDayClock(seenAt) : '新消息'
  return el
}
function placeUnreadLine() {
  const box = $('#msgs'); if (!box) return
  if (lineIo) { lineIo.disconnect(); lineIo = null }
  const old = box.querySelector('.unread-line'); if (old) old.remove()
  let first = null
  box.querySelectorAll('.msg.her').forEach((el) => { if (!first && Number(el.dataset.t || 0) > seenAt) first = el })
  if (!first) return
  const node = unreadLineNode()
  box.insertBefore(node, first)
  lineIo = new IntersectionObserver((entries) => {
    for (const e of entries) {
      if (e.isIntersecting) { e.target.dataset.lineSeen = '1'; continue }
      if (e.target.dataset.lineSeen && e.boundingClientRect.top < 0) {
        e.target.remove(); lineIo.disconnect(); lineIo = null; break
      }
    }
  })
  lineIo.observe(node)
}

// ---------- #1 已读（B 半）：我发的消息三段态 ----------
// 已送出 → 她收到了（received 水位，契约 §1）→ 她正在回复（typing，契约 §2）→ 她回复了（状态消失）。
// 只渲染服务器确认的事实；30s 没等到「她收到了」只诚实标「她还没收到」，不重发（Leader 裁定）。
const RSTAT_TEXT = { sent: '已送出', got: '她收到了', typing: '她正在回复', late: '她还没收到' }
const RSTAT_TIMEOUT = 30000
let receivedWm = 0        // 「她收到了」水位：SSE received.receivedAt，只向前
let typingOffTimer = null // 极短 off→on 视为同一段「正在回复」，防闪断（契约 §2b）

function clearStatTimer(el) { if (el && el._statTimer) { clearTimeout(el._statTimer); el._statTimer = null } }

function setMyStat(el, key) {
  if (!el || !el.isConnected) return
  const meta = el.querySelector('.meta')
  if (!meta) return
  let span = meta.querySelector('.rstat')
  if (!key) {
    if (span) span.remove()
    delete el.dataset.rstat
    clearStatTimer(el)
    return
  }
  if (!span) { span = document.createElement('span'); span.className = 'rstat'; meta.appendChild(span) }
  span.textContent = '· ' + RSTAT_TEXT[key]
  span.dataset.k = key
  el.dataset.rstat = key
}

// 我的消息回显到达：亮「已送出」；30s 没等到 received → 「她还没收到」（只标，不重发）
function onMyMessageEcho(el) {
  setMyStat(el, 'sent')
  clearStatTimer(el)
  el._statTimer = setTimeout(() => {
    el._statTimer = null
    if (el.dataset.rstat === 'sent') setMyStat(el, 'late')
  }, RSTAT_TIMEOUT)
}

// received 水位推进：t <= receivedAt 的我方消息 → 「她收到了」（迟到的 received 也把 late 升级回 got）
function markMyReceived() {
  const box = $('#msgs'); if (!box) return
  box.querySelectorAll('.msg.me').forEach((el) => {
    const k = el.dataset.rstat
    if (!k || k === 'got' || k === 'typing') return
    if (Number(el.dataset.t || 0) <= receivedWm) { clearStatTimer(el); setMyStat(el, 'got') }
  })
}

// typing：on → 最后一条还没收束的我方消息升「她正在回复」；off → 收束（状态消失）
function onTypingStatus(on) {
  if (on) {
    if (typingOffTimer) { clearTimeout(typingOffTimer); typingOffTimer = null }
    const box = $('#msgs'); if (!box) return
    const mine = box.querySelectorAll('.msg.me')
    for (let i = mine.length - 1; i >= 0; i--) {
      const k = mine[i].dataset.rstat
      if (k === 'sent' || k === 'got' || k === 'late') { setMyStat(mine[i], 'typing'); break }
    }
  } else if (!typingOffTimer) {
    typingOffTimer = setTimeout(() => {
      typingOffTimer = null
      const box = $('#msgs'); if (!box) return
      box.querySelectorAll('.msg.me[data-rstat="typing"]').forEach((el) => setMyStat(el, null))
    }, 400)
  }
}

// 她的回复到达 = 这一轮结束：我方消息上的三段态整段消失（④ 她回复了）
function clearMyStats() {
  if (typingOffTimer) { clearTimeout(typingOffTimer); typingOffTimer = null }
  const box = $('#msgs'); if (!box) return
  box.querySelectorAll('.msg.me[data-rstat]').forEach((el) => setMyStat(el, null))
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
  offviewUnread = 0   // 进聊天页后未读一律以服务端水位重算，红点不再叠着算
  readUiReady = false
  const r = await api('/api/messages?file=last')
  const box = $('#msgs')
  if (!box) return
  box.innerHTML = ''
  historyState = { oldestAt: 0, hasMore: false, loading: false, index: 0, total: 0 }
  if (!r.ok) { box.innerHTML = '<div class="empty">读不到消息：' + esc(r.reason || '') + '</div>'; refreshUnreadBadge(); return }
  const list = r.messages || []
  if (r.file) { historyState.index = Number(r.file.index) || 0; historyState.total = Number(r.file.total) || 0 }
  if (!list.length) { box.innerHTML = '<div class="empty">还没说过话。打个招呼吧。</div>'; refreshUnreadBadge(); return }
  for (const m of list) appendMessage(m, false)
  historyState.oldestAt = r.oldestAt || (list[0] && list[0].t) || 0
  historyState.hasMore = Boolean(r.hasMore)
  renderHistoryMore()
  scrollDown()
  // 打开聊天页这一刻取水位快照，t > 快照的她的消息标未读；分隔线也以它定位
  await fetchReadState()
  if (!box.isConnected || currentView !== 'chat') return
  seenAt = myReadAt
  box.querySelectorAll('.msg.her').forEach((el) => {
    if (Number(el.dataset.t || 0) > seenAt) el.classList.add('unread')
  })
  refreshUnreadBadge()
  watchUnread()
  placeUnreadLine()
  reconcileUnread()   // 视口里的立刻算读到（条件不满足就一条都不消）
  readUiReady = true
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
    let newUnread = 0
    for (let i = list.length - 1; i >= 0; i--) {
      const el = prependMessage(list[i])
      // 旧分页也可能比水位新（水位停在最早那页之前），新插进来的她消息按快照补判未读
      if (el && readUiReady && list[i].who !== 'user' && Number(list[i].t || 0) > seenAt) {
        el.classList.add('unread')
        if (msgIo) msgIo.observe(el)
        newUnread++
      }
    }
    reflowDaySeparators()
    box.scrollTop = box.scrollHeight - keep
    if (newUnread) { placeUnreadLine(); refreshUnreadBadge() }
  }
  renderHistoryMore()
}

// 媒体取回统一走 Authorization 头 + blob：URL 里不出现 token（不落日志/历史/Referer）。
// <img>/<audio>/CSS 带不了自定义头，所以先渲染占位（data-media-id / data-sticker-md5），
// 再用带 Bearer 的 fetch 取回 blob，把 object URL 赋给 src。blob 按 URL 缓存：
// 切视图重渲染同一批消息时不重复下载；登出/换号统一 revoke，防对象 URL 泄漏。
const mediaBlobCache = new Map()
async function authedBlobUrl(url) {
  if (mediaBlobCache.has(url)) return mediaBlobCache.get(url)
  const r = await fetch(url, { headers: token ? { authorization: 'Bearer ' + token } : {}, cache: 'no-store' })
  if (!r.ok) throw new Error('HTTP ' + r.status)
  // 服务端对「没有这个资源」回 200 + JSON（ok:false），不验类型会把 JSON 塞给 <img>/<audio>
  const type = r.headers.get('content-type') || ''
  if (!type.startsWith('image/') && !type.startsWith('audio/')) throw new Error('HTTP ' + r.status + ' ' + type)
  const obj = URL.createObjectURL(await r.blob())
  mediaBlobCache.set(url, obj)
  return obj
}
async function mediaBlobUrl(mediaId) { return authedBlobUrl(BASE + '/api/media/' + encodeURIComponent(mediaId)) }
async function stickerBlobUrl(md5) { return authedBlobUrl(BASE + '/api/sticker/' + encodeURIComponent(md5)) }
function disposeMediaBlobs() {
  for (const u of mediaBlobCache.values()) { try { URL.revokeObjectURL(u) } catch (e) { /* 已回收就算了 */ } }
  mediaBlobCache.clear()
  disposeAvatarBlob()
  disposeLocalBg()
}

function hydrateMedia(el) {
  mediaBlobUrl(el.dataset.mediaId).then((url) => {
    if (el.isConnected) el.src = url
  }).catch(() => {
    if (!el.isConnected) return
    if (el.tagName === 'AUDIO') {
      const bar = el.closest('.voice-bar')
      if (bar) bar.innerHTML = '<span class="expired">语音已经过期了</span>'
    } else {
      el.dispatchEvent(new Event('error'))   // guardMediaImg 换占位文字
    }
  })
}

// 表情文件和聊天媒体同一套授权取回；img 上先不挂 src，等 blob 回来再填
function hydrateSticker(el) {
  stickerBlobUrl(el.dataset.stickerMd5).then((url) => {
    if (el.isConnected) el.src = url
  }).catch(() => {
    if (el.isConnected) el.dispatchEvent(new Event('error'))   // guardMediaImg 换占位文字
  })
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
  // 没挂 src 的（等 blob 水合的）不算坏图，等 hydrateMedia 的结果
  if (img.getAttribute('src') && img.complete && img.naturalWidth === 0) swap()
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
    const src = m.voice ? 'data:' + (m.voiceMime || 'audio/mpeg') + ';base64,' + m.voice : ''
    inner = (m.mediaId || src)
      ? '<div class="voice-bar" data-vid="' + esc(m.id || '') + '">' +
        '<button class="vb-play" data-voice-play aria-label="播放"></button>' +
        '<span class="vb-bars">' + BARS + '</span>' +
        '<span class="vb-dur">' + (m.dur ? fmtDur(m.dur) : '') + '</span>' +
        '<audio preload="metadata"' + (src ? ' src="' + src + '"' : ' data-media-id="' + esc(m.mediaId) + '"') + '></audio></div>' +
        (m.heard ? '<div class="heard">听成了：' + esc(m.heard) + '</div>' : '')
      : (m.heard ? '<div class="heard">听成了：' + esc(m.heard) + '</div>' : esc(m.text || '[语音已过期]'))
  } else if (m.kind === 'sticker') {
    const md5 = stickerMd5(m.sticker)
    inner += md5
      ? '<img data-media-fallback="sticker" data-sticker-md5="' + esc(md5) + '" alt="' + esc(m.name || '表情') + '" />'
      : '<span class="expired">表情已经不在了</span>'
  } else if (m.kind === 'image') {
    inner += m.mediaId
      ? '<img data-media-fallback="image" data-media-id="' + esc(m.mediaId) + '" alt="图片" />'
      : (m.image
        ? '<img data-media-fallback="image" src="data:' + (m.imageMime || 'image/png') + ';base64,' + m.image + '" alt="图片" />'
        : '<span class="expired">图片已经过期了</span>')
  } else if (refMatch) {
    inner += esc(refMatch[2])
  } else {
    inner += esc(m.text || '')
  }
  if (mine && m.kind === 'image' && cfg?.stickers?.steal !== false) inner += '<button class="collect" data-collect="' + esc(m.id || '') + '">收进表情库</button>'
  wrap.innerHTML = (mine ? '' : '<div class="sender">' + esc(bot()) + '</div>') +
    '<div class="bubble"><div class="body">' + inner + '</div><span class="meta">' + esc(fmtDayClock(m.t)) + '</span></div>'
  wrap.querySelectorAll('img[data-media-fallback]').forEach((img) => {
    guardMediaImg(img, img.dataset.mediaFallback === 'sticker' ? '表情已经不在了' : '图片已经过期了')
  })
  wrap.querySelectorAll('[data-media-id]').forEach(hydrateMedia)
  wrap.querySelectorAll('img[data-sticker-md5]').forEach(hydrateSticker)
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
    if (k !== prev) {
      // 未读分隔线正好卡在换天处时，日期条排它前面：日期 → 上次看到这里 → 消息
      const ref = (el.previousElementSibling && el.previousElementSibling.classList.contains('unread-line')) ? el.previousElementSibling : el
      box.insertBefore(daySepNode(Number(el.dataset.t || 0)), ref)
      prev = k
    }
  })
}

function appendMessage(m, scroll = true) {
  const box = $('#msgs')
  if (!box) return null
  if (m?.id && box.querySelector('[data-id="' + cssId(m.id) + '"]')) return null
  const empty = box.querySelector('.empty')
  if (empty) empty.remove()
  const node = buildMessageNode(m)
  box.appendChild(node)
  reflowDaySeparators()
  if (scroll) scrollDown()
  return node
}

// 消息 id 里可能有特殊字符，塞进属性选择器前先转义
function cssId(id) { return String(id).replace(/["\\]/g, '\\$&') }

// 往前翻页时插到「查看更早」按钮的后面（按钮永远在最上面）
function prependMessage(m) {
  const box = $('#msgs')
  if (!box) return null
  if (m?.id && box.querySelector('[data-id="' + cssId(m.id) + '"]')) return null
  const more = box.querySelector('.more-history')
  const node = buildMessageNode(m)
  box.insertBefore(node, more ? more.nextSibling : box.firstChild)
  return node
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
  stream = openStream(onStreamEvent)
}

// SSE 不用 EventSource：它带不了 Authorization 头，会话凭证不能进 URL。
// 改用 fetch 流 + Bearer 头自己读，断线按退避重连；返回的句柄带 close()，
// 和原来 EventSource 的用法（logout / 重连时 close）兼容。
function openStream(onEvent) {
  let closed = false
  let ctl = null
  const handle = {
    close() {
      closed = true
      try { if (ctl) ctl.abort() } catch (e) { /* 已断就不管 */ }
    },
  }
  ;(async () => {
    let wait = 1
    while (!closed) {
      ctl = new AbortController()
      try {
        const r = await fetch(BASE + '/api/stream', {
          headers: token ? { authorization: 'Bearer ' + token } : {},
          signal: ctl.signal,
        })
        if (!r.ok || !r.body) throw new Error('HTTP ' + r.status)
        wait = 1
        const reader = r.body.getReader()
        const dec = new TextDecoder()
        let buf = ''
        for (;;) {
          const { done, value } = await reader.read()
          if (done) break
          buf += dec.decode(value, { stream: true })
          let i
          while ((i = buf.indexOf('\n')) >= 0) {
            const line = buf.slice(0, i).replace(/\r$/, '')
            buf = buf.slice(i + 1)
            if (!line.startsWith('data:')) continue   // “: ping” 心跳之类的注释帧直接略过
            const json = line.slice(5).trim()
            if (!json) continue
            try {
              const ev = JSON.parse(json)
              if (ev && ev.type) onEvent(ev)
            } catch (err) { /* 单条坏帧跳过 */ }
          }
        }
      } catch (e) {
        if (closed || (ctl && ctl.signal.aborted)) break
      }
      if (closed) break
      try {
        await new Promise((res, rej) => {
          const t = setTimeout(res, wait * 1000)
          if (ctl) ctl.signal.addEventListener('abort', () => { clearTimeout(t); rej(new Error('closed')) }, { once: true })
        })
      } catch (e) { break }
      wait = Math.min(wait * 2, 15)
    }
  })()
  return handle
}

function onStreamEvent(ev) {
  if (ev.type === 'message') {
      const m = ev.message || {}
      if (m.who === 'bot') playSound('in')
      if (!ev.chatId || !me || String(ev.chatId) === String(me.subject)) {
        if (currentView === 'chat') {
          const el = appendMessage(m)
          if (m.who === 'user') {
            if (el) onMyMessageEcho(el)        // 我的回显 → 三段态起点（已送出）
          } else {
            clearMyStats()                      // 她回了 → 我方消息的三段态整段消失（④）
            if (el) {
              if (canAdvanceRead()) advanceReadTo(m.t)   // 聊天页且可见有焦点，append 已把它滚进视口 → 直接算读到
              else { el.classList.add('unread'); if (msgIo) msgIo.observe(el); refreshUnreadBadge() }
            }
          }
        } else if (m.who !== 'user') {
          offviewUnread++      // 不在聊天页：DOM 不渲染，只进红点（进聊天页后按水位重算）
          refreshUnreadBadge()
        }
      }
    } else if (ev.type === 'typing') {
      if (currentView === 'chat') setTyping(Boolean(ev.on))
      onTypingStatus(Boolean(ev.on))           // 她正在回复（③）：on 升级、off 收束（契约 §2）
    } else if (ev.type === 'received') {
      // 她收到了（契约 §1）：只向前的水位，t <= receivedAt 的我方消息置「她收到了」
      if (!ev.chatId || !me || String(ev.chatId) === String(me.subject)) {
        receivedWm = Math.max(receivedWm, Number(ev.receivedAt) || 0)
        markMyReceived()
      }
    } else if (ev.type === 'read') {
      // A 半不消费：单主人场景它只可能是自己 POST 后的回声，水位以 GET api/read 为准；B 半再接
    } else if (ev.type === 'history') {
      // 服务端过期清理 / 分片边界变了：把客户端里比 droppedBefore 更早的气泡也删掉
      if (!ev.chatId || !me || String(ev.chatId) === String(me.subject)) {
        const before = Number(ev.droppedBefore || 0)
        const box = $('#msgs')
        if (before > 0 && box) {
          box.querySelectorAll('.msg[data-t]').forEach((el) => { if (Number(el.dataset.t) < before) el.remove() })
          reflowDaySeparators()
          // 被裁掉的可能正好是分隔线的锚点：未读还在就重新定位，没未读就把线撤了
          if (readUiReady && (box.querySelector('.unread-line') || box.querySelector('.msg.her.unread'))) placeUnreadLine()
          refreshUnreadBadge()
          historyState.oldestAt = Math.max(historyState.oldestAt || 0, before)
        }
        if (typeof ev.hasMore === 'boolean') historyState.hasMore = ev.hasMore
        if (currentView === 'chat') renderHistoryMore()
      }
    }
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

async function openInBrowser() {
  // 会话凭证不进地址栏：先换一张 60 秒一次性桥接票，新窗口用它换回自己的会话；
  // 地址栏/历史/Referer 里只有那张短命票，没有长期 token
  let q = ''
  const r = await api('/api/bridge', { method: 'POST' })
  if (r.ok && r.ticket) q = '?bridge=' + encodeURIComponent(r.ticket)
  else toast('没拿到桥接票（' + (r.reason || '未知原因') + '），新窗口要重新登录', 'err')
  const url = location.origin + location.pathname + q
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
      '<img data-sticker-md5="' + esc(i.md5) + '" alt="" />' +
      (i.name ? '<i class="sticker-opt-name">' + esc(i.name) + '</i>' : '') + '</button>').join('')
    box.querySelectorAll('.sticker-opt img').forEach((img) => { guardMediaImg(img, '图没了'); hydrateSticker(img) })
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
  const data = await imageBase64(src, img)
  if (!data) return toast('这张图存不下来', 'err')
  const r = await api('/api/stickers/upload', { method: 'POST', body: { data, from: 'steal', hint: '' } })
  toast(r.ok ? '收进表情库了' : (r.reason || '没收成'), r.ok ? '' : 'err')
})

// 图片可能是内联 data:、blob:（已授权取回的媒体），也可能是还没水合的媒体占位——
// 占位时按 data-media-id 走授权通道取回，统一转成 base64 再传
async function imageBase64(src, el) {
  if ((!src || src === '') && el && el.dataset && el.dataset.mediaId) {
    try { src = await mediaBlobUrl(el.dataset.mediaId) } catch { return '' }
  }
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
        inlineEditor(body.querySelector('.mem-head'), '', '这个人的 ID（主人写 owner，其他写对方用户名）', async (who) => {
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
  disposeMediaBlobs()   // 上一账号的媒体/头像/背景 object URL 全部回收
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
  grid.innerHTML = items.map((i) => '<div class="sticker-item"><img data-sticker-md5="' + esc(i.md5) + '" alt="" />' +
    '<div class="sticker-name">' + esc(i.name || '没起名') + '</div>' +
    '<div class="sticker-acts"><button data-rename="' + esc(i.md5) + '">改名</button><button data-del-sticker="' + esc(i.md5) + '">删除</button></div></div>').join('')
  grid.querySelectorAll('.sticker-item img').forEach((img) => { guardMediaImg(img, '图没了'); hydrateSticker(img) })
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

// 本地背景图也走授权取回（服务端 /api/background 已要求登录），主题层通过 refs.fetchLocalBg 拿 object URL
let localBgBlob = null
async function fetchLocalBg() {
  try {
    const r = await fetch(BASE + '/api/background?v=' + Date.now(), {
      headers: token ? { authorization: 'Bearer ' + token } : {},
      cache: 'no-store',
    })
    if (!r.ok) throw new Error('HTTP ' + r.status)
    if (!(r.headers.get('content-type') || '').startsWith('image/')) throw new Error('not an image')
    const u = URL.createObjectURL(await r.blob())
    disposeLocalBg()
    localBgBlob = u
    return u
  } catch (e) { return null }   // 没登录/没设背景：主题层保持无图
}
function disposeLocalBg() {
  if (localBgBlob) { try { URL.revokeObjectURL(localBgBlob) } catch (e) { /* 已回收就算了 */ } localBgBlob = null }
}

async function boot({ silent = false } = {}) {
  // 桥接票一次性换回会话，然后立刻把票从地址栏抹掉（不进历史/不进 Referer）
  const bridge = queryOf('bridge')
  if (bridge) {
    const r = await api('/api/bridge/use', { method: 'POST', body: { ticket: bridge } })
    try {
      const u = new URL(location.href)
      u.searchParams.delete('bridge')
      history.replaceState(null, '', u.origin + u.pathname + (u.searchParams.toString() ? '?' + u.searchParams.toString() : ''))
    } catch (e) { /* 地址栏抹不掉也不影响功能 */ }
    if (r.ok && r.token) setToken(r.token)
    else toast(r.reason || '桥接票无效，请重新登录', 'err')
  }
  // 旧版深链把长期 token 放在 ?token= 里，现在一律不认：只把残留参数从地址栏清掉
  try {
    const u = new URL(location.href)
    if (u.searchParams.has('token')) {
      u.searchParams.delete('token')
      history.replaceState(null, '', u.origin + u.pathname + (u.searchParams.toString() ? '?' + u.searchParams.toString() : ''))
    }
  } catch (e) { }
  botState = await api('/api/state')
  // t32：state 401（显式凭证已失效）也走到这里——api() 已清 token，展示服务端真实原因，
  // 别把「登录过期」误报成「连不上服务」；两条路都回登录页
  if (!botState.ok) { showGate('login', botState.reason || '连不上服务'); return }
  me = botState.me
  applyTheme()
  applyTitle()
  applyFavicon()
  setAvatar($('#gate-logo'), iconSrc())
  // 会话内重入（设置保存后刷新等）：只认账号会话——重走 v2 角色分流回主界面；
  // 旧账号会话不再静默进入，清凭据回登录（t18 MED-003）
  if (silent && me) {
    if (me.role === 'account' || me.role === 'owner') { await routeAfterAccountAuth(); return }
    setToken('')
    me = null
  }
  // 记住我：本地还留着凭据就直接进主界面（凭据失效时 routeAfterAccountAuth 会清掉并回登录页）
  if (token && me) { await routeAfterAccountAuth(); return }
  // 登录优先（t13 合同）：冷启动一律先到登录/注册页；
  // 本地是否已有旧 owner / setupDone / 历史角色都不改变该入口，needSetup 只作旧版本机初始化的显式链接提示
  showGate('login')
  bindLogin()
}

let loginMode = 'account'

function bindLogin() {
  const form = $('#gate-form')
  form.onsubmit = async (e) => {
    e.preventDefault()
    await doLogin($('#g-username').value.trim(), $('#g-password').value, loginMode)
  }
  const tog = $('#link-toggle')
  if (tog) tog.onclick = (e) => {
    e.preventDefault()
    loginMode = 'account'
    if (tog.dataset.to === 'register') { bindRegister(); showGate('register') }
    else showGate('login')
  }
  const leg = $('#link-legacy')
  if (leg) leg.onclick = (e) => {
    e.preventDefault()
    if (loginMode === 'legacy') { loginMode = 'account'; showGate('login') }
    else { loginMode = 'legacy'; showGate('legacy') }
  }
  const wlink = $('#link-wizard')
  if (wlink) wlink.onclick = (e) => { e.preventDefault(); showGate('setup'); initWizard() }
  bindRegister()
  bindCompanionCreate()
}

document.addEventListener('input', (e) => {
  const t = e.target
  if (t && t.tagName === 'TEXTAREA' && t.id === 'input') {
    t.style.height = 'auto'
    t.style.height = Math.min(140, t.scrollHeight) + 'px'
  }
})

boot()

// ---- 心情与生命状态（合并页） ----
async function renderMoodAndLife(main) {
  paneShell(main, '心情与生命状态', '')
  const body = document.getElementById('pane-body')
  if (!body) return
  body.innerHTML = '<div id="mood-box"><div class="loading">加载中…</div></div>' +
    '<div id="life-box"><div class="loading">加载中…</div></div>'
  await views.mood.renderMoodInto(document.getElementById('mood-box'))
  await renderLife(document.getElementById('life-box'))
}

async function renderLife(box) {
  if (!box) return
  box.innerHTML = '<div class="loading">加载中…</div>'

  const r = await api('/api/life/state')
  if (!r || !r.ok) {
    box.innerHTML = '<div class="empty">获取生命状态失败：' + esc((r && r.reason) || '未知错误') + '</div>'
    return
  }

  const s = r.state || {}
  const h = r.hormones || {}

  const pct = (v) => Math.round(Math.max(0, Math.min(100, v || 0)))
  const bar = (v, color) => `<div style="background:#2a2a2a;border-radius:4px;height:8px;width:100%;margin:4px 0">
    <div style="background:${color};border-radius:4px;height:8px;width:${pct(v)}%"></div></div>`

  box.innerHTML = `
    <div class="pane-section" style="padding:16px">
      <h3 style="margin:0 0 16px">身体状态</h3>
      <div style="display:grid;grid-template-columns:1fr 1fr;gap:12px">
        <div><div style="color:#aaa;font-size:12px">精力</div>${bar(s.energy,'#4caf50')}<div style="font-size:12px">${pct(s.energy)}%</div></div>
        <div><div style="color:#aaa;font-size:12px">饥饿</div>${bar(s.hunger,'#ff9800')}<div style="font-size:12px">${pct(s.hunger)}%</div></div>
        <div><div style="color:#aaa;font-size:12px">口渴</div>${bar(s.thirst,'#2196f3')}<div style="font-size:12px">${pct(s.thirst)}%</div></div>
        <div><div style="color:#aaa;font-size:12px">困倦</div>${bar(s.sleepiness,'#9c27b0')}<div style="font-size:12px">${pct(s.sleepiness)}%</div></div>
      </div>

      <h3 style="margin:20px 0 16px">给她来点什么</h3>
      <div style="display:flex;gap:8px;flex-wrap:wrap">
        <button class="primary small" onclick="lifeEvent('drink')">喝水</button>
        <button class="primary small" onclick="lifeEvent('eat')">吃东西</button>
        <button class="primary small" onclick="lifeEvent('comfort')">安慰她</button>
        <button class="primary small" onclick="lifeEvent('nudge')">戳一戳</button>
        <button class="ghost small" onclick="refreshLife()">刷新</button>
      </div>
      <div id="life-msg" style="margin-top:12px;font-size:13px;color:#aaa"></div>
    </div>`
}

async function refreshLife() {
  await renderLife(document.getElementById('life-box'))
}

async function lifeEvent(type) {
  const msg = document.getElementById('life-msg')
  if (msg) msg.textContent = '发送中…'
  const r = await api('/api/life/event', { method: 'POST', body: { type } })
  if (msg) msg.textContent = r && r.ok ? ('✓ ' + (r.message || '好的')) : ('✗ ' + ((r && r.reason) || '失败了'))
  setTimeout(() => refreshLife(), 1200)
}

// 内联 onclick 只能看见 window 上的东西：ES module 里的函数默认不在 window，
// 少了这两行，生命状态面板的按钮点了就是 ReferenceError。
window.lifeEvent = lifeEvent
window.refreshLife = refreshLife
