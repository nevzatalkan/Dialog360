/** Sunucu tarihleri yerel saatte 'YYYY-MM-DD HH:MM:SS' gelir; tarayıcı aynı makinede olduğu için yerel saat olarak yorumlanır. */
export const parseLocal = (s: string) => new Date(s.replace(' ', 'T'))

const startOfDay = (d: Date) => new Date(d.getFullYear(), d.getMonth(), d.getDate())
const dayDiff = (d: Date, now = new Date()) => Math.round((startOfDay(now).getTime() - startOfDay(d).getTime()) / 86_400_000)

const time = new Intl.DateTimeFormat('tr-TR', { hour: '2-digit', minute: '2-digit' })
const dayMonth = new Intl.DateTimeFormat('tr-TR', { day: 'numeric', month: 'short' })
const monthYear = new Intl.DateTimeFormat('tr-TR', { month: 'long', year: 'numeric' })
const full = new Intl.DateTimeFormat('tr-TR', { day: 'numeric', month: 'long', year: 'numeric', hour: '2-digit', minute: '2-digit' })

/** Geçmiş menüsü grubu: Bugün, Dün, Son 7 gün, Son 30 gün, sonra ay ay ("Eylül 2026"). */
export function historyGroup(s: string): string {
  const d = parseLocal(s)
  const diff = dayDiff(d)
  if (diff <= 0) return 'Bugün'
  if (diff === 1) return 'Dün'
  if (diff < 7) return 'Son 7 gün'
  if (diff < 30) return 'Son 30 gün'
  const label = monthYear.format(d)
  return label.charAt(0).toLocaleUpperCase('tr-TR') + label.slice(1)
}

/** Listede kısa zaman: bugün ve dün için saat, daha eskiler için gün ve ay. */
export function shortTime(s: string): string {
  const d = parseLocal(s)
  return dayDiff(d) <= 1 ? time.format(d) : dayMonth.format(d)
}

/** Mesaj üstündeki zaman: bugünse yalnızca saat, değilse tam tarih ve saat. */
export function messageTime(s: string): string {
  const d = parseLocal(s)
  return dayDiff(d) === 0 ? time.format(d) : full.format(d)
}

export const fullTime = (s: string) => full.format(parseLocal(s))

/** Şu an, sunucunun kullandığı biçimde ('YYYY-MM-DD HH:MM:SS', yerel saat). */
export function nowLocal(): string {
  const d = new Date(), p = (n: number) => String(n).padStart(2, '0')
  return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())} ${p(d.getHours())}:${p(d.getMinutes())}:${p(d.getSeconds())}`
}

/** Sunucudaki aramayla aynı katlama: büyük/küçük harf ve Türkçe karakter duyarsız, uzunluğu korur. */
export function fold(s: string): string {
  const lower = s.toLocaleLowerCase('tr-TR')
  const base = lower.length === s.length ? lower : s.toLowerCase()
  return base.replace(/[ışğüöçâîû]/g, c => ({ ı: 'i', ş: 's', ğ: 'g', ü: 'u', ö: 'o', ç: 'c', â: 'a', î: 'i', û: 'u' })[c] ?? c)
}
