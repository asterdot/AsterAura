import { esc, fmtDay } from '../format.js'

// 三激素的界面名。这里可以有名字——界面上是给人看的，
// 对话里她永远看不到这些名字，所以不会念出来。
const AXES = [
  { key: 'dopamine', name: '兴致', desc: '被勾起兴趣会涨，闲着会慢慢落回平常' },
  { key: 'cortisol', name: '紧绷', desc: '被凶、被催、出错会涨，松下来会落回平常' },
  { key: 'serotonin', name: '踏实', desc: '被重视会涨，长期紧绷会掉，都会慢慢回平常' },
]

const clampPct = (v) => Math.max(0, Math.min(100, Math.round(Number(v) || 0)))

export function createMoodView(ctx) {
  function renderMood(main) {
    ctx.paneShell(main, '心情')
    loadMood()
  }

  function applyKeys(el, patch, key) {
    const parts = key.split('.')
    let o = patch
    for (let i = 0; i < parts.length - 1; i++) { o[parts[i]] = o[parts[i]] || {}; o = o[parts[i]] }
    o[parts[parts.length - 1]] = el.type === 'checkbox' ? el.checked : Number(el.value)
  }

  function axisBar(a) {
    const v = clampPct(a.value)
    return '<div class="mem-row" style="display:block">' +
      '<div style="display:flex;justify-content:space-between;align-items:baseline">' +
      '<b>' + esc(a.name) + '</b><span class="muted" style="font-size:12px">' + v + '</span></div>' +
      '<div class="mood-bar" style="margin:6px 0 4px"><i style="left:0;width:' + v + '%"></i></div>' +
      '<div class="muted" style="font-size:12px">' + esc(a.desc) + '</div>' +
      '</div>'
  }

  async function loadMood() {
    const body = ctx.$('#pane-body')
    if (!body) return
    const r = await ctx.api('/api/mood')
    if (!r.ok) { body.innerHTML = '<div class="empty">' + esc(r.reason || '读不到') + '</div>'; return }
    const cfg = ctx.getCfg()
    const s = r.state || {}
    const hist = r.history || []
    const axes = AXES.map((a) => ({ ...a, value: s[a.key] }))

    body.innerHTML =
      '<div class="entry">' +
      axes.map(axisBar).join('') +
      (s.reason ? '<div class="muted" style="font-size:12px;margin-top:8px">最近一次变化：' + esc(s.reason) + '</div>' : '') +
      '</div>' +
      (ctx.isOwner() ? '<div class="section"><h3>怎么算的</h3><div class="body">' +
        '<div class="muted" style="font-size:12px;line-height:1.7">' +
        '三个数各自按半衰期回落到平常值：兴致 45 分钟、紧绷 30 分钟、踏实 120 分钟。' +
        '她说话的样子由这三个数决定，但她看不到数字，也不知道这些名字——她只会话变短、话变少，或者兴致上来多说两句。' +
        '</div>' +
        '<div class="row"><label>判断情绪</label><div class="ctl"><label class="switch"><input type="checkbox" data-mkey="mood.judge" /><span class="slider"></span></label>' +
        '<span class="muted" style="font-size:12px">每轮结束后让模型读一遍</span></div></div>' +
        '<div class="row"><label>自动回落</label><div class="ctl"><label class="switch"><input type="checkbox" data-mkey="mood.decay" /><span class="slider"></span></label>' +
        '<span class="muted" style="font-size:12px">关掉就停在当前值不动</span></div></div>' +
        '<div class="row"><div class="ctl"><button class="ghost small" data-mood-reset>重置到平常</button></div></div>' +
        '</div></div>' : '') +
      '<div class="section"><h3>变化记录</h3><div class="body">' +
      (hist.length ? hist.map((h) => {
        const from = h.from || {}
        const to = h.to || {}
        const delta = ['dopamine', 'cortisol', 'serotonin']
          .map((k) => {
            const d = Math.round((to[k] || 0) - (from[k] || 0))
            return d ? (AXES.find((a) => a.key === k)?.name || k) + (d > 0 ? ' +' : ' ') + d : ''
          })
          .filter(Boolean)
          .join('，')
        return '<div class="mem-row"><div class="mem-text">' + esc(h.reason || '') +
          '<div class="mem-meta">' + esc(fmtDay(h.at)) + (delta ? ' · ' + esc(delta) : '') + '</div></div></div>'
      }).join('') : '<div class="muted">还没有记录。</div>') +
      '</div></div>'

    if (ctx.isOwner() && cfg) {
      ctx.$$('#pane-body [data-mkey]').forEach((el) => {
        const v = el.dataset.mkey.split('.').reduce((o, k) => (o == null ? o : o[k]), cfg)
        if (el.type === 'checkbox') el.checked = Boolean(v)
        else if (v != null) el.value = v
      })
    }
    body.onclick = async (e) => {
      if (e.target.closest('#mood-save')) {
        const patch = {}
        ctx.$$('#pane-body [data-mkey]').forEach((el) => applyKeys(el, patch, el.dataset.mkey))
        const r2 = await ctx.api('/api/config', { method: 'PATCH', body: patch })
        ctx.toast(r2.ok ? '存好了' : (r2.reason || '没存上'), r2.ok ? '' : 'err')
        if (r2.ok) { ctx.setCfg(r2.config); loadMood() }
        return
      }
      if (e.target.closest('[data-mood-reset]')) {
        await ctx.api('/api/mood', { method: 'POST', body: { reset: true } })
        loadMood()
        ctx.refreshMoodPill()
      }
    }
  }

  return { renderMood, loadMood }
}
