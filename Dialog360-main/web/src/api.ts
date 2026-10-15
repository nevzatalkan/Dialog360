export type Cell = string | number | null

export interface QueryColumn { name: string; kind: 'number' | 'date' | 'text' }
export interface ChartSpec { type: 'kpi' | 'bar' | 'line' | 'multiline' | 'table'; x: string | null; y: string[]; series: string | null }

export interface Report {
  sql: string
  explanation: string
  assumptions: string[]
  columns: QueryColumn[]
  rows: Cell[][]
  truncated: boolean
  chart: ChartSpec
  tables: string[]
  usedColumns: string[]
  plan: string[]
  planWarnings: string[]
  dbMs: number
  /** Tıklanabilir kolonlar ve gidilebilecek seviyeler (drill down/up). Eski kayıtlarda yok. */
  drill?: DrillColumn[] | null
}

/** kind: down (aynı hiyerarşide alt seviye), across (başka boyut), up (üst seviye) */
export interface DrillTarget { key: string; label: string; kind: 'down' | 'across' | 'up' }
export interface DrillColumn { column: string; level: string; levelLabel: string; targets: DrillTarget[]; up: DrillTarget | null }
/** Detay yolu adımı; messageId dolu ise o rapora geri dönülebilir. */
export interface Crumb { label: string; messageId: number | null }

export interface Meta {
  attempts: number
  fromCache: boolean
  llmMs: number
  totalMs: number
  corrections: { sql: string; error: string }[]
  model: string
}

export interface ChatResponse {
  chatId: string
  messageId: number
  kind: 'report' | 'clarify' | 'blocked' | 'error'
  title: string
  text: string
  report: Report | null
  clarify: { question: string; options: string[] } | null
  blocked: { reason: string; layer: string; sql: string | null } | null
  meta: Meta
  crumbs?: Crumb[] | null
  /** rewrite: LLM'siz detay, llm: modelle üretilen detay, back: detay yolunda geri dönüş */
  drillSource?: 'rewrite' | 'llm' | 'back' | null
}

export type HistoryItem =
  | { role: 'user'; text: string; createdAt?: string }
  | { role: 'assistant'; response: ChatResponse; createdAt?: string }

/** Geçmiş menüsündeki sohbet özeti. Tarihler sunucunun yerel saatinde 'YYYY-MM-DD HH:MM:SS'. */
/** snippet: arama yapılırken eşleşmenin geçtiği kısa alıntı (başlıkta eşleştiyse boş). */
export interface ChatSummary { id: string; title: string; createdAt: string; updatedAt: string; questions: number; reports: number; snippet?: string | null }

export interface ColumnInfo {
  name: string; type: string; isPk: boolean; fkTable: string | null; fkColumn: string | null
  description: string | null; isPii: boolean; values: string[] | null
}
export interface TableInfo { name: string; description: string | null; rowCount: number; columns: ColumnInfo[] }

/** ZB penceresindeki bir filtre alanı. Metin şablonlarında {min}, {max}, {values} yer tutucuları vardır. */
export interface FilterField {
  key: string; label: string
  kind: 'range' | 'choice' | 'single'
  /** attr: sonuna "olan" gelir; verb: kendi fiiliyle biter; time: cümlenin başına gelir */
  style: 'attr' | 'verb' | 'time'
  unit: string | null; min: number | null; max: number | null; step: number
  values: string[] | null
  presets: { label: string; min: number | null; max: number | null }[] | null
  textRange: string | null; textMin: string | null; textMax: string | null; textChoice: string | null
  hint: string
  /** Bağlama göre her değerdeki müşteri sayısı (seçim alanlarında) */
  counts?: Record<string, number> | null
}
/** Seçenekleri daraltan mevcut kriter; source: soru (önceki ZB seçimi), metin (soruda geçen değer), pencere */
export interface ZbContextItem { field: string; label: string; text: string; source: 'soru' | 'metin' | 'pencere' }
export interface FacetResult { fields: FilterField[]; matching: number; context: ZbContextItem[] }
export interface FilterDefault { field: string; min: number | null; max: number | null; values: string[] }
export interface ZbAnalysis {
  term: string
  /** Terimin filtreye karşılık gelen kısmı; metinde "ZB " + phrase seçilen kriterlerle değiştirilir */
  phrase: string
  title: string; description: string
  primary: string | null; extras: string[]; defaults: FilterDefault[]
  source: 'llm' | 'kural'
  fields: FilterField[]
  /** Seçenekleri daraltan mevcut kriterler (örn. Şehir: Ankara) ve bu bağlamdaki müşteri sayısı */
  context: ZbContextItem[]
  contextFilters: FilterDefault[]
  matching: number
}

/** Oturum düştüğünde (401) uygulama giriş ekranına döner. */
export const UNAUTHORIZED_EVENT = 'd360:unauthorized'

async function json<T>(res: Response): Promise<T> {
  if (res.status === 401 && !res.url.endsWith('/api/auth/login')) window.dispatchEvent(new Event(UNAUTHORIZED_EVENT))
  if (!res.ok) {
    const body = await res.json().catch(() => null)
    throw new Error(body?.error ?? `Sunucu hatası (${res.status})`)
  }
  return res.json() as Promise<T>
}

const post = (url: string, body: unknown) =>
  fetch(url, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) })

export const auth = {
  me: () => fetch('/api/auth/me').then(r => (r.ok ? (r.json() as Promise<{ username: string }>) : null)),
  login: (username: string, password: string) => post('/api/auth/login', { username, password }).then(r => json<{ username: string }>(r)),
  logout: () => post('/api/auth/logout', {}),
}

const send = (method: 'PATCH' | 'DELETE', url: string, body?: unknown) =>
  fetch(url, { method, headers: body ? { 'Content-Type': 'application/json' } : undefined, body: body ? JSON.stringify(body) : undefined })

async function ok(res: Response) {
  if (res.status === 401) window.dispatchEvent(new Event(UNAUTHORIZED_EVENT))
  if (!res.ok) {
    const body = await res.json().catch(() => null)
    throw new Error(body?.error ?? `Sunucu hatası (${res.status})`)
  }
}

export const chats = {
  list: (q?: string) => fetch(`/api/chats${q ? `?q=${encodeURIComponent(q)}` : ''}`).then(r => json<ChatSummary[]>(r)),
  rename: (id: string, title: string) => send('PATCH', `/api/chats/${id}`, { title }).then(ok),
  remove: (id: string) => send('DELETE', `/api/chats/${id}`).then(ok),
}

export const api = {
  chat: (chatId: string | null, message: string) => post('/api/chat', { chatId, message }).then(r => json<ChatResponse>(r)),
  runSql: (chatId: string | null, sql: string) => post('/api/sql', { chatId, sql }).then(r => json<ChatResponse>(r)),
  /** value null → üst seviyeye çık (roll-up) */
  drill: (chatId: string, messageId: number, column: string, value: Cell, target: string) =>
    post('/api/drill', { chatId, messageId, column, value, target }).then(r => json<ChatResponse>(r)),
  /** context: sorudaki önceki ZB seçimleri; soruda geçen değerleri (örn. "Ankara") sunucu ayrıca bulur */
  zbAnalyze: (term: string, text: string, context: FilterDefault[]) => post('/api/zb/analyze', { term, text, context }).then(r => json<ZbAnalysis>(r)),
  zbFacets: (context: FilterDefault[]) => post('/api/zb/facets', { context }).then(r => json<FacetResult>(r)),
  drillBack: (chatId: string, messageId: number) => post('/api/drill/back', { chatId, messageId }).then(r => json<ChatResponse>(r)),
  history: (chatId: string) => fetch(`/api/history/${chatId}`).then(r => json<HistoryItem[]>(r)),
  schema: () => fetch('/api/schema').then(r => json<TableInfo[]>(r)),
  health: () => fetch('/api/health').then(r => json<{ llm: boolean; model: string; tables: number }>(r)),
}
