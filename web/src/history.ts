import type { HistoryItem } from './api'
import { MANUAL_SQL_PREFIX } from './components/MessageView'

/**
 * "Geçmiş Sorgularım": konuşmalar tarayıcıda (localStorage) saklanır; açılınca anında gösterilir.
 * Konuşmanın devam edebilmesi için gereken bağlam (önceki SQL'ler) sunucuda chatId ile tutulur,
 * bu yüzden konuşma açılınca tam veri sunucudan da tazelenir.
 */
export interface Conversation {
  id: string
  title: string
  createdAt: number
  updatedAt: number
  items: HistoryItem[]
}

const KEY = 'diyalog360.conversations.v1' // gitleaks:allow (localStorage anahtar adı)
const ACTIVE_KEY = 'diyalog360.activeChat'
/** localStorage ~5 MB; rapor başına saklanan satır sınırlanır (tam sonuç sunucudan gelir). */
const MAX_ROWS_STORED = 200

export function loadConversations(): Conversation[] {
  try {
    const list = JSON.parse(localStorage.getItem(KEY) ?? '[]') as Conversation[]
    return Array.isArray(list) ? list.sort((a, b) => b.updatedAt - a.updatedAt) : []
  } catch {
    return []
  }
}

export function titleOf(items: HistoryItem[]): string {
  const first = items.find(i => i.role === 'user')
  if (!first || first.role !== 'user') return 'Yeni sohbet'
  return first.text.startsWith(MANUAL_SQL_PREFIX) ? 'Elle düzenlenen SQL' : first.text
}

function compact(items: HistoryItem[]): HistoryItem[] {
  return items.map(i =>
    i.role === 'assistant' && i.response.report && i.response.report.rows.length > MAX_ROWS_STORED
      ? { ...i, response: { ...i.response, report: { ...i.response.report, rows: i.response.report.rows.slice(0, MAX_ROWS_STORED), truncated: true } } }
      : i)
}

function persist(list: Conversation[]) {
  // Kota dolarsa en eski konuşmalardan başlayarak yer aç
  const sorted = [...list].sort((a, b) => b.updatedAt - a.updatedAt)
  while (sorted.length > 0) {
    try {
      localStorage.setItem(KEY, JSON.stringify(sorted))
      return
    } catch {
      sorted.pop()
    }
  }
  localStorage.removeItem(KEY)
}

export function saveConversation(id: string, items: HistoryItem[]): Conversation[] {
  const list = loadConversations()
  const now = Date.now()
  const existing = list.find(c => c.id === id)
  const updated: Conversation = {
    id,
    title: titleOf(items),
    createdAt: existing?.createdAt ?? now,
    updatedAt: now,
    items: compact(items),
  }
  persist([updated, ...list.filter(c => c.id !== id)])
  return loadConversations()
}

export function removeConversation(id: string): Conversation[] {
  persist(loadConversations().filter(c => c.id !== id))
  if (getActiveId() === id) setActiveId(null)
  return loadConversations()
}

export const getActiveId = () => localStorage.getItem(ACTIVE_KEY)

export function setActiveId(id: string | null) {
  if (id) localStorage.setItem(ACTIVE_KEY, id)
  else localStorage.removeItem(ACTIVE_KEY)
}
