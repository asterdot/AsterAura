export const esc = (s) => String(s == null ? '' : s).replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]))
export const pad2 = (n) => String(n).padStart(2, '0')

export function fmtClock(t) {
  const d = new Date(t || Date.now())
  return pad2(d.getHours()) + ':' + pad2(d.getMinutes())
}

export function fmtDay(t) {
  const d = new Date(t || Date.now())
  const now = new Date()
  const same = d.toDateString() === now.toDateString()
  return (same ? '今天 ' : (d.getMonth() + 1) + '月' + d.getDate() + '日 ') + fmtClock(t)
}

// 聊天里的日期分隔条：同一天只归成一组，返回「今天 / 昨天 / M月d日 / yyyy年M月d日」
export function dayKey(t) {
  const d = new Date(t || Date.now())
  return d.getFullYear() + '-' + d.getMonth() + '-' + d.getDate()
}

export function dayLabel(t) {
  const d = new Date(t || Date.now())
  const now = new Date()
  const startToday = new Date(now.getFullYear(), now.getMonth(), now.getDate()).getTime()
  const startThat = new Date(d.getFullYear(), d.getMonth(), d.getDate()).getTime()
  const diff = Math.round((startToday - startThat) / 86400000)
  if (diff === 0) return '今天'
  if (diff === 1) return '昨天'
  if (d.getFullYear() === now.getFullYear()) return (d.getMonth() + 1) + '月' + d.getDate() + '日'
  return d.getFullYear() + '年' + (d.getMonth() + 1) + '月' + d.getDate() + '日'
}

export const BARS = Array.from({ length: 16 }, (_, i) => '<i style="height:' + (5 + ((i * 7) % 11)) + 'px"></i>').join('')

export function fmtDur(sec) {
  const s = Math.max(0, Math.round(Number(sec) || 0))
  return Math.floor(s / 60) + ':' + pad2(s % 60)
}

export function greet() {
  const h = new Date().getHours()
  return h < 5 ? '夜深了' : h < 11 ? '上午好' : h < 13 ? '中午好' : h < 18 ? '下午好' : h < 23 ? '晚上好' : '夜深了'
}
