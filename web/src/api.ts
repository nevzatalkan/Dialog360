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
}

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
}

export type HistoryItem = { role: 'user'; text: string } | { role: 'assistant'; response: ChatResponse }

export interface ColumnInfo {
  name: string; type: string; isPk: boolean; fkTable: string | null; fkColumn: string | null
  description: string | null; isPii: boolean; values: string[] | null
}
export interface TableInfo { name: string; description: string | null; rowCount: number; columns: ColumnInfo[] }

async function json<T>(res: Response): Promise<T> {
  if (!res.ok) {
    const body = await res.json().catch(() => null)
    throw new Error(body?.error ?? `Sunucu hatası (${res.status})`)
  }
  return res.json() as Promise<T>
}

const post = (url: string, body: unknown) =>
  fetch(url, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) })

export const api = {
  chat: (chatId: string | null, message: string) => post('/api/chat', { chatId, message }).then(r => json<ChatResponse>(r)),
  runSql: (chatId: string | null, sql: string) => post('/api/sql', { chatId, sql }).then(r => json<ChatResponse>(r)),
  history: (chatId: string) => fetch(`/api/history/${chatId}`).then(r => json<HistoryItem[]>(r)),
  schema: () => fetch('/api/schema').then(r => json<TableInfo[]>(r)),
  health: () => fetch('/api/health').then(r => json<{ llm: boolean; model: string; tables: number }>(r)),
}
