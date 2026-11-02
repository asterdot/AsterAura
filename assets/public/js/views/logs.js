import { esc, pad2 } from '../format.js'

export function createLogsView(ctx) {
  async function loadLogs() {
    const body = ctx.$('#pane-body')
    if (!body) return
    const r = await ctx.api('/api/logs')
    if (!r.ok) { body.innerHTML = '<div class="empty">' + esc(r.reason || '读不到') + '</div>'; return }
    const logs = (r.logs || []).slice().reverse()
    const st = r.stats || {}
    const stat = ctx.$('#logs-stat')
    if (stat) {
      const mb = (Number(st.bytes || 0) / 1024 / 1024).toFixed(2)
      stat.textContent = (r.version ? 'v' + r.version + ' · ' : '') + logs.length + ' 条 · 约 ' + mb + ' MB / 上限 ' + (r.maxMB || 2) + ' MB'
    }
    body.innerHTML = '<div class="logs">' + (logs.length ? logs.map((l) => {
      const t = new Date(l.t || l.time || Date.now())
      return '<div class="log-line"><span class="t">' + esc(pad2(t.getHours()) + ':' + pad2(t.getMinutes()) + ':' + pad2(t.getSeconds())) + '</span>' +
        '<span class="lv ' + esc(l.level || '') + '">' + esc(l.level || 'info') + '</span>' +
        '<span class="a">' + esc(l.area || '') + '</span>' +
        '<span class="m">' + esc(l.text || l.message || '') + '</span></div>'
    }).join('') : '<div class="empty">还没有日志。</div>') + '</div>'
  }

  function renderLogs(main) {
    ctx.paneShell(main, '日志', '<span class="count" id="logs-stat"></span>' +
      '<button class="ghost small" id="logs-refresh">刷新</button>' +
      '<button class="ghost small" id="logs-clear">清除</button>')
    loadLogs()
    const b = ctx.$('#logs-refresh')
    if (b) b.onclick = loadLogs
    const c = ctx.$('#logs-clear')
    if (c) c.onclick = async () => {
      const r = await ctx.api('/api/logs/clear', { method: 'POST' })
      ctx.toast(r.ok ? '清掉了 ' + (r.cleared || 0) + ' 条' : (r.reason || '没清掉'), r.ok ? '' : 'err')
      loadLogs()
    }
  }

  return { renderLogs, loadLogs }
}
