import type { FilterDefault, FilterField } from './api'

/** Soru metnindeki bir "ZB" işareti: konumu ve ardından gelen ifade. */
export interface ZbMarker { index: number; start: number; term: string }

/** "ZB" ancak ardından boşluk geldiğinde işarete dönüşür (yazarken "ZB" henüz düz metindir). */
const MARKER = /(^|\s)(zb)(?=\s)/gi

export function findMarkers(text: string): ZbMarker[] {
  const out: ZbMarker[] = []
  const starts: number[] = []
  for (const m of text.matchAll(MARKER)) starts.push(m.index! + m[1].length)
  starts.forEach((start, i) => {
    const end = i + 1 < starts.length ? starts[i + 1] : text.length
    const term = text.slice(start + 2, end).trim().replace(/[\s,.;:!?]+$/, '')
    out.push({ index: i, start, term })
  })
  return out
}

export type Selection = { min?: number | null; max?: number | null; values?: string[] }

const fmt = (f: FilterField, v: number) =>
  f.unit === 'TL' ? new Intl.NumberFormat('tr-TR', { maximumFractionDigits: 0 }).format(v) : String(v)

/** Bir alanın seçimini Türkçe ifadeye çevirir ("yaşı 25-40 arası", "cinsiyeti Kadın"); seçim yoksa null. */
export function phraseOf(f: FilterField, s: Selection | undefined): string | null {
  if (!s) return null
  if (f.kind === 'range') {
    let { min, max } = s
    if (min != null && max != null && min > max) [min, max] = [max, min]
    if (min != null && max != null) return f.textRange!.replace('{min}', fmt(f, min)).replace('{max}', fmt(f, max))
    if (min != null) return f.textMin!.replace('{min}', fmt(f, min))
    if (max != null) return f.textMax!.replace('{max}', fmt(f, max))
    return null
  }
  const values = s.values ?? []
  return values.length ? f.textChoice!.replace('{values}', values.join(' veya ')) : null
}

/**
 * Seçimleri soruya eklenecek tek cümleye çevirir:
 * "son 3 ay içinde yaşı 25-40 arası, cinsiyeti Kadın olan, Market sektöründe kart harcaması yapan"
 */
export function buildCriteria(fields: FilterField[], selections: Record<string, Selection>): string {
  const time: string[] = [], attr: string[] = [], verb: string[] = []
  for (const f of fields) {
    const p = phraseOf(f, selections[f.key])
    if (p) (f.style === 'time' ? time : f.style === 'verb' ? verb : attr).push(p)
  }
  const core = [attr.length ? `${attr.join(', ')} olan` : '', ...verb].filter(Boolean).join(', ')
  return [time.join(' ve '), core].filter(Boolean).join(' ')
}

/**
 * Metindeki n'inci "ZB <ifade>" bölümünü kriter cümlesiyle değiştirir. Arkasında bir şey yoksa "müşteriler",
 * "müşteri..." ile devam etmiyorsa "müşterilerin" eklenir ki cümle anlamlı kalsın. ZB, daha önce eklenmiş bir kriter
 * cümlesinin hemen arkasındaysa ikisi birleştirilir: "Şehri Ankara olan müşteriler ZB ilçe" →
 * "Şehri Ankara, şubesi Çankaya olan müşteriler" (replaced: birleştirilen eski cümle).
 */
export function applyCriteria(text: string, marker: ZbMarker, phrase: string, criteria: string, previous: string[] = []): { text: string; inserted: string; replaced?: string } {
  const afterZb = text.slice(marker.start + 2).replace(/^\s+/, '')
  const consumed = afterZb.toLocaleLowerCase('tr-TR').startsWith(phrase.toLocaleLowerCase('tr-TR')) ? phrase.length : 0
  const rest = afterZb.slice(consumed).replace(/^\s+/, '')
  let before = text.slice(0, marker.start)
  const noun = rest === '' ? ' müşteriler' : /^müşteri/i.test(rest) ? '' : ' müşterilerin'
  const capital = (s: string) => s.charAt(0).toLocaleUpperCase('tr-TR') + s.slice(1)

  const prev = previous.filter(Boolean).find(p => before.trimEnd().endsWith(p.trimEnd()))
  if (prev) {
    const trimmed = before.trimEnd()
    const head = trimmed.slice(0, trimmed.length - prev.trimEnd().length)
    const prevNoun = prev.trimEnd().match(/\s+(müşteriler(?:in)?)$/i)
    const prevCore = prev.trimEnd().slice(0, prev.trimEnd().length - (prevNoun?.[0].length ?? 0))
    const joined = /\solan$/.test(prevCore) && /\solan(,|$)/.test(criteria) && !/\siçinde\s/.test(criteria.split(',')[0])
      ? `${prevCore.slice(0, -5)}, ${criteria}`
      : `${prevCore}, ${criteria}`
    const tail = rest === '' ? (prevNoun ? ` ${prevNoun[1]}` : ' müşteriler') : noun
    const inserted = (head.trim() === '' ? capital(joined) : joined) + tail
    return { text: head + inserted + (rest ? ' ' + rest : ' '), inserted, replaced: prev }
  }

  let inserted = criteria + noun
  if (before.trim() === '') inserted = capital(inserted)
  before = before.replace(/\s*$/, before.trim() === '' ? '' : ' ')
  return { text: before + inserted + (rest ? ' ' + rest : ' '), inserted }
}

/** Pencere seçimlerini sunucunun anladığı kriter listesine çevirir (boş seçimler atılır). */
export function toDefaults(selections: Record<string, Selection>): FilterDefault[] {
  return Object.entries(selections)
    .map(([field, s]) => ({ field, min: s.min ?? null, max: s.max ?? null, values: s.values ?? [] }))
    .filter(d => d.values.length > 0 || d.min != null || d.max != null)
}

/** Aynı alan iki listede varsa sonraki (daha yeni) seçim geçerlidir. */
export function mergeContext(...lists: FilterDefault[][]): FilterDefault[] {
  const map = new Map<string, FilterDefault>()
  for (const list of lists) for (const d of list) map.set(d.field, d)
  return [...map.values()]
}
