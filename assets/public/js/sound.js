export const TONES = [['ding', '叮'], ['pop', '啵'], ['bubble', '咕噜'], ['chime', '和弦'], ['custom', '自定义音频'], ['none', '不出声']]

const toneAudio = new Map()
let toneCtx = null

function playCustomTone(url, vol) {
  if (!url) return false
  try {
    let a = toneAudio.get(url)
    if (!a) { a = new Audio(url); a.preload = 'auto'; toneAudio.set(url, a) }
    a.volume = Math.max(0, Math.min(1, vol))
    a.currentTime = 0
    const p = a.play()
    if (p && p.catch) p.catch(() => {})
    return true
  } catch { return false }
}

export function playSound(kind, cfg) {
  const s = cfg?.sound
  if (!s) return
  if (kind === 'in' && !s.onIncoming) return
  if (kind === 'out' && !s.onSend) return
  const name = kind === 'in' ? (s.incoming || 'ding') : (s.outgoing || 'pop')
  const vol = Number((kind === 'in' ? s.inVolume : s.outVolume) ?? 0.5)
  if (name === 'none' || vol <= 0) return
  let tone = name
  if (name === 'custom') {
    const url = kind === 'in' ? s.incomingUrl : s.outgoingUrl
    if (playCustomTone(url, vol)) return
    tone = kind === 'in' ? 'ding' : 'pop'
  }
  try {
    const AC = window.AudioContext || window.webkitAudioContext
    if (!AC) return
    toneCtx = toneCtx || new AC()
    if (toneCtx.state === 'suspended') toneCtx.resume().catch(() => {})
    const t0 = toneCtx.currentTime
    const spec = {
      ding: [[880, 0.18, 'sine'], [1320, 0.12, 'sine']],
      pop: [[420, 0.09, 'triangle']],
      bubble: [[560, 0.08, 'sine'], [720, 0.08, 'sine']],
      chime: [[523, 0.22, 'sine'], [659, 0.22, 'sine'], [784, 0.3, 'sine']],
    }[tone] || [[880, 0.18, 'sine']]
    spec.forEach(([freq, len, type], i) => {
      const o = toneCtx.createOscillator()
      const g = toneCtx.createGain()
      o.type = type
      o.frequency.value = freq
      const start = t0 + i * 0.07
      g.gain.setValueAtTime(0, start)
      g.gain.linearRampToValueAtTime(vol * 0.35, start + 0.012)
      g.gain.exponentialRampToValueAtTime(0.0001, start + len)
      o.connect(g).connect(toneCtx.destination)
      o.start(start)
      o.stop(start + len + 0.02)
    })
  } catch {}
}
