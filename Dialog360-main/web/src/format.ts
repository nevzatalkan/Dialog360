import type { Cell, QueryColumn } from './api'

const MONEY = /tutar|harcama|bakiye|mevduat|ciro|limit|sepet|gelir/i
const PERCENT = /oran|yüzde|pay/i

const integer = new Intl.NumberFormat('tr-TR', { maximumFractionDigits: 0 })
const decimal = new Intl.NumberFormat('tr-TR', { minimumFractionDigits: 2, maximumFractionDigits: 2 })
const compact = new Intl.NumberFormat('tr-TR', { notation: 'compact', maximumFractionDigits: 1 })

export function formatNumber(value: number, column = ''): string {
  if (PERCENT.test(column)) return '%' + new Intl.NumberFormat('tr-TR', { maximumFractionDigits: 1 }).format(value)
  const text = Number.isInteger(value) || Math.abs(value) >= 1000 ? integer.format(value) : decimal.format(value)
  return MONEY.test(column) ? `${text} TL` : text
}

export const formatCompact = (value: number) => compact.format(value)

export function formatCell(value: Cell, column: QueryColumn): string {
  if (value === null) return '—'
  if (typeof value === 'number' && column.kind === 'number') return formatNumber(value, column.name)
  return String(value)
}

const MONTHS = ['Oca', 'Şub', 'Mar', 'Nis', 'May', 'Haz', 'Tem', 'Ağu', 'Eyl', 'Eki', 'Kas', 'Ara']

/** '2026-09' → 'Eyl 2026' */
export function formatPeriod(value: string): string {
  const m = /^(\d{4})-(\d{2})$/.exec(value)
  return m ? `${MONTHS[Number(m[2]) - 1]} ${m[1]}` : value
}

const MONTHS_LONG = ['Ocak', 'Şubat', 'Mart', 'Nisan', 'Mayıs', 'Haziran', 'Temmuz', 'Ağustos', 'Eylül', 'Ekim', 'Kasım', 'Aralık']

/** '2026-09' → 'Eylül 2026' (detay yolu ve drill adımları için; sunucudaki biçimle aynı) */
export function formatPeriodLong(value: string): string {
  const m = /^(\d{4})-(\d{2})$/.exec(value)
  return m ? `${MONTHS_LONG[Number(m[2]) - 1]} ${m[1]}` : value
}

export const formatMs = (ms: number) => (ms >= 1000 ? `${(ms / 1000).toLocaleString('tr-TR', { maximumFractionDigits: 1 })} sn` : `${ms} ms`)

const KEYWORDS = /\s+(FROM|LEFT JOIN|INNER JOIN|JOIN|WHERE|GROUP BY|HAVING|ORDER BY|LIMIT|UNION ALL|UNION)\s+/gi

/** Okunurluk için SQL'i ana yan cümlelerden satırlara böler. */
export function prettySql(sql: string): string {
  return sql
    .replace(/\s+/g, ' ')
    .replace(KEYWORDS, (_, kw: string) => `\n${kw.toUpperCase()} `)
    .replace(/\s+AND\s+(?![^()]*\))/gi, '\n  AND ')
    .trim()
}

export function toCsv(columns: QueryColumn[], rows: Cell[][]): string {
  const esc = (v: Cell) => {
    const s = v === null ? '' : typeof v === 'number' ? String(v).replace('.', ',') : String(v)
    return /[";\n]/.test(s) ? `"${s.replace(/"/g, '""')}"` : s
  }
  return '\uFEFF' + [columns.map(c => esc(c.name)).join(';'), ...rows.map(r => r.map(esc).join(';'))].join('\n')
}
