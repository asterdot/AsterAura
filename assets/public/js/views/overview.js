import { esc } from '../format.js'

export function createOverviewView(ctx) {
  function renderOverview(main) {
    ctx.paneShell(main, '概览')
    loadOverview()
  }

  async function loadOverview() {
    const body = ctx.$('#pane-body')
    if (!body) return
    const r = await ctx.api('/api/status')
    if (!r.ok) { body.innerHTML = '<div class="empty">' + esc(r.reason || '读不到') + '</div>'; return }
    const cfg = ctx.getCfg()
    const mood = r.mood || {}
    const d = r.diaryStats || {}
    const su = r.speakUp || {}
    const sd = r.selfDrive || {}
    const mem = r.memory || {}
    const hub = r.stats || {}
    const sdCfg = cfg?.selfDrive || {}
    const suCfg = cfg?.speakUp || {}
    body.innerHTML =
      '<div class="ov-grid">' +
      '<div class="ov-card"><div class="ov-t">心情</div><div class="ov-v">' + esc(mood.value ?? '-') + ' <span class="muted" style="font-size:13px">' + esc(mood.label || '') + '</span></div></div>' +
      '<div class="ov-card"><div class="ov-t">动态</div><div class="ov-v">' + (d.total || 0) + ' <span class="muted" style="font-size:13px">篇</span></div></div>' +
      '<div class="ov-card"><div class="ov-t">今天写了</div><div class="ov-v">' + (d.today || 0) + ' <span class="muted" style="font-size:13px">篇</span></div></div>' +
      '<div class="ov-card"><div class="ov-t">今天主动搭话</div><div class="ov-v">' + (su.count || 0) + ' <span class="muted" style="font-size:13px">次</span></div></div>' +
      '<div class="ov-card"><div class="ov-t">自驱</div><div class="ov-v small">' + (sdCfg.enabled ? '开启' : '关闭') + ' · 今天 ' + (sd.count || 0) + ' 次</div></div>' +
      '<div class="ov-card"><div class="ov-t">记忆</div><div class="ov-v">' + (mem.count || 0) + ' <span class="muted" style="font-size:13px">条</span></div></div>' +
      '<div class="ov-card ov-full"><div class="ov-t">状态</div><div class="ov-rows">' +
      '<div class="ov-row"><span>总开关</span><span>' + (cfg?.enabled ? '开' : '关') + '</span></div>' +
      '<div class="ov-row"><span>闲置搭话</span><span>' + (suCfg.enabled ? suCfg.idleMinutes + ' 分钟' : '关') + '</span></div>' +
      '<div class="ov-row"><span>时间段</span><span>' + (suCfg.quietFrom || '23:00') + ' – ' + (suCfg.quietTo || '08:00') + '</span></div>' +
      '<div class="ov-row"><span>聊天轮数</span><span>' + (hub.turns || 0) + '</span></div>' +
      '<div class="ov-row"><span>消息条数</span><span>' + (hub.messages || 0) + '</span></div>' +
      '<div class="ov-row"><span>语音/表情/图片</span><span>' + (hub.voice || 0) + ' 条 · 表情 ' + (hub.stickers || 0) + ' · 图 ' + (hub.images || 0) + '</span></div>' +
      '<div class="ov-row"><span>会话</span><span>' + Object.keys(r.sessions || {}).length + ' 个</span></div>' +
      '</div></div>' +
      (ctx.isOwner() ? '<div class="ov-card ov-full"><div class="ov-t">调试 / 试一下</div><div class="ov-tests">' +
        '<button class="ghost small" data-test="speakup">让Ta现在来找你</button>' +
        '<button class="ghost small" data-test="diary">让Ta写一篇日记</button>' +
        '<button class="ghost small" data-test="selfdrive">让Ta自驱一次</button>' +
        '<button class="ghost small" data-test="social">让Ta回访评论</button>' +
        '</div></div>' : '') +
      '</div>'
    body.onclick = async (e) => {
      const b = e.target.closest('[data-test]')
      if (!b) return
      b.disabled = true
      const r2 = await ctx.api('/api/test', { method: 'POST', body: { what: b.dataset.test } })
      b.disabled = false
      ctx.toast(r2.ok ? (r2.message || '做了') : (r2.reason || '没做成'), r2.ok ? '' : 'err')
    }
  }

  return { renderOverview, loadOverview }
}
