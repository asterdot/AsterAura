export function encodeWav(parts, frames, rate) {
  const buf = new ArrayBuffer(44 + frames * 2)
  const v = new DataView(buf)
  const str = (off, s) => { for (let i = 0; i < s.length; i++) v.setUint8(off + i, s.charCodeAt(i)) }
  str(0, 'RIFF'); v.setUint32(4, 36 + frames * 2, true); str(8, 'WAVE'); str(12, 'fmt ')
  v.setUint32(16, 16, true); v.setUint16(20, 1, true); v.setUint16(22, 1, true)
  v.setUint32(24, rate, true); v.setUint32(28, rate * 2, true); v.setUint16(32, 2, true); v.setUint16(34, 16, true)
  str(36, 'data'); v.setUint32(40, frames * 2, true)
  let off = 44
  for (const p of parts) {
    for (let i = 0; i < p.length; i++) {
      const x = Math.max(-1, Math.min(1, p[i]))
      v.setInt16(off, x < 0 ? x * 0x8000 : x * 0x7fff, true)
      off += 2
    }
  }
  return new Blob([buf], { type: 'audio/wav' })
}
