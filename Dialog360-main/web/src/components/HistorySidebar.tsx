import { useEffect, useMemo, useRef, useState, type KeyboardEvent } from 'react'
import type { ChatSummary } from '../api'
import { fold, fullTime, historyGroup, shortTime } from '../format-date'

interface Props {
  open: boolean
  chats: ChatSummary[]
  loading: boolean
  activeId: string | null
  busy: boolean
  search: string
  onSearch: (q: string) => void
  onSelect: (id: string) => void
  onNew: () => void
  onRename: (id: string, title: string) => Promise<void>
  onDelete: (id: string) => Promise<void>
  onClose: () => void
}

/** Sol taraftaki geçmiş menüsü: sohbetler son etkinliğe göre yeniden eskiye, tarih gruplarıyla. Mobilde çekmece olarak açılır. */
export function HistorySidebar({ open, chats, loading, activeId, busy, search, onSearch, onSelect, onNew, onRename, onDelete, onClose }: Props) {
  const groups = useMemo(() => {
    const out: { label: string; items: ChatSummary[] }[] = []
    for (const c of chats) {
      const label = historyGroup(c.updatedAt)
      if (out.at(-1)?.label !== label) out.push({ label, items: [] })
      out.at(-1)!.items.push(c)
    }
    return out
  }, [chats])

  useEffect(() => {
    if (!open) return
    const onKey = (e: globalThis.KeyboardEvent) => { if (e.key === 'Escape') onClose() }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [open, onClose])

  return (
    <>
      {open && <div className="fixed inset-0 z-30 bg-slate-900/30 lg:hidden" onClick={onClose} aria-hidden="true" />}
      <aside id="history-sidebar" aria-label="Sohbet geçmişi" hidden={!open}
        className="fixed inset-y-0 left-0 z-40 flex w-80 max-w-[85vw] flex-col border-r border-slate-200 bg-white shadow-xl lg:static lg:z-auto lg:w-72 lg:max-w-none lg:shadow-none">
        <div className="flex items-center justify-between gap-2 px-4 pb-3 pt-4">
          <h2 className="text-sm font-semibold text-slate-900">Sohbet geçmişi</h2>
          <button onClick={onNew} disabled={busy}
            className="flex items-center gap-1.5 rounded-lg border border-slate-200 px-2.5 py-1.5 text-sm text-slate-700 hover:bg-slate-50 disabled:opacity-40">
            <PlusIcon /> Yeni sohbet
          </button>
        </div>

        <div className="px-4 pb-3">
          <label htmlFor="history-search" className="sr-only">Konuşmalarda ara: sorular, rapor özetleri, SQL ve tablo değerleri</label>
          <input id="history-search" type="search" value={search} onChange={e => onSearch(e.target.value)} placeholder="Konuşmalarda ara"
            className="w-full rounded-lg border border-slate-200 bg-slate-50 px-3 py-2 text-sm outline-none placeholder:text-slate-400 focus:border-indigo-500 focus:bg-white focus:ring-2 focus:ring-indigo-100" />
        </div>

        <nav className="flex-1 overflow-y-auto px-2 pb-4">
          {loading && chats.length === 0 && <p className="px-3 py-2 text-sm text-slate-400">Sohbetler yükleniyor…</p>}
          {!loading && chats.length === 0 && (
            <p className="px-3 py-2 text-sm leading-relaxed text-slate-500">
              {search ? `"${search}" geçen bir konuşma bulunamadı.` : 'Henüz sohbet yok. İlk sorunuzu sorduğunuzda burada görünecek.'}
            </p>
          )}
          {groups.map(g => (
            <section key={g.label} className="mt-3 first:mt-0">
              <h3 className="px-3 pb-1 text-xs font-medium text-slate-400">{g.label}</h3>
              <ul className="space-y-0.5">
                {g.items.map(c => (
                  <ChatRow key={c.id} chat={c} query={search.trim()} active={c.id === activeId} busy={busy}
                    onSelect={() => onSelect(c.id)} onRename={t => onRename(c.id, t)} onDelete={() => onDelete(c.id)} />
                ))}
              </ul>
            </section>
          ))}
        </nav>
      </aside>
    </>
  )
}

function ChatRow({ chat, query, active, busy, onSelect, onRename, onDelete }: {
  chat: ChatSummary; query: string; active: boolean; busy: boolean; onSelect: () => void; onRename: (t: string) => Promise<void>; onDelete: () => Promise<void>
}) {
  const [mode, setMode] = useState<'view' | 'rename' | 'confirm'>('view')
  const [draft, setDraft] = useState(chat.title)
  const inputRef = useRef<HTMLInputElement>(null)

  useEffect(() => { if (mode === 'rename') inputRef.current?.select() }, [mode])

  const save = async () => {
    const t = draft.trim()
    if (t && t !== chat.title) await onRename(t)
    setMode('view')
  }
  const onKey = (e: KeyboardEvent<HTMLInputElement>) => {
    if (e.key === 'Enter') save()
    if (e.key === 'Escape') { setDraft(chat.title); setMode('view') }
  }

  if (mode === 'rename') {
    return (
      <li className="px-1 py-1">
        <label htmlFor={`rename-${chat.id}`} className="sr-only">Sohbet başlığı</label>
        <input id={`rename-${chat.id}`} ref={inputRef} value={draft} maxLength={120} onChange={e => setDraft(e.target.value)} onKeyDown={onKey} onBlur={save}
          className="w-full rounded-md border border-indigo-400 px-2.5 py-1.5 text-sm outline-none ring-2 ring-indigo-100" />
      </li>
    )
  }

  if (mode === 'confirm') {
    return (
      <li className="rounded-lg bg-rose-50 px-3 py-2.5 text-sm">
        <p className="text-rose-800">Bu sohbet ve içindeki raporlar silinsin mi?</p>
        <div className="mt-2 flex gap-2">
          <button onClick={() => { onDelete(); setMode('view') }} className="rounded-md bg-rose-600 px-2.5 py-1 text-white hover:bg-rose-700">Sil</button>
          <button onClick={() => setMode('view')} autoFocus className="rounded-md px-2.5 py-1 text-rose-800 hover:bg-rose-100">Vazgeç</button>
        </div>
      </li>
    )
  }

  return (
    <li className={`group relative rounded-lg ${active ? 'bg-indigo-50' : 'hover:bg-slate-50'}`}>
      <button onClick={onSelect} disabled={busy && !active} aria-current={active ? 'page' : undefined} title={`${chat.title}\nSon etkinlik: ${fullTime(chat.updatedAt)}`}
        className={`block w-full rounded-lg px-3 py-2 text-left focus-visible:outline-2 ${active ? 'pr-16' : 'pr-3 group-hover:pr-16 group-focus-within:pr-16'} focus-visible:outline-indigo-600 disabled:cursor-not-allowed disabled:opacity-50`}>
        <span className={`block truncate text-sm ${active ? 'font-medium text-indigo-900' : 'text-slate-800'}`}><Highlight text={chat.title} query={query} /></span>
        {query && chat.snippet && (
          <span className="mt-1 line-clamp-2 block text-xs leading-snug text-slate-500"><Highlight text={chat.snippet} query={query} /></span>
        )}
        <span className="mt-0.5 block text-xs text-slate-400">
          {shortTime(chat.updatedAt)}, {chat.questions} soru{chat.reports > 0 ? `, ${chat.reports} rapor` : ''}
        </span>
      </button>
      <div className={`absolute right-1.5 top-1/2 flex -translate-y-1/2 gap-0.5 ${active ? 'flex' : 'hidden group-hover:flex group-focus-within:flex'}`}>
        <IconButton label="Yeniden adlandır" onClick={() => { setDraft(chat.title); setMode('rename') }}><PencilIcon /></IconButton>
        <IconButton label="Sil" onClick={() => setMode('confirm')} danger><TrashIcon /></IconButton>
      </div>
    </li>
  )
}

/** Aramadaki ifadeyi metin içinde vurgular; eşleşme Türkçe karakter ve büyük/küçük harf duyarsızdır. */
function Highlight({ text, query }: { text: string; query: string }) {
  if (!query) return <>{text}</>
  const i = fold(text).indexOf(fold(query))
  if (i < 0) return <>{text}</>
  return <>{text.slice(0, i)}<mark className="rounded-sm bg-amber-100 px-0.5 text-inherit">{text.slice(i, i + query.length)}</mark>{text.slice(i + query.length)}</>
}

function IconButton({ label, onClick, danger, children }: { label: string; onClick: () => void; danger?: boolean; children: React.ReactNode }) {
  return (
    <button onClick={onClick} aria-label={label} title={label}
      className={`rounded-md p-1.5 text-slate-400 hover:bg-white focus-visible:outline-2 focus-visible:outline-indigo-600 ${danger ? 'hover:text-rose-600' : 'hover:text-slate-700'}`}>
      {children}
    </button>
  )
}

const icon = { width: 16, height: 16, viewBox: '0 0 24 24', fill: 'none', stroke: 'currentColor', strokeWidth: 2, strokeLinecap: 'round' as const, strokeLinejoin: 'round' as const, 'aria-hidden': true }
const PlusIcon = () => <svg {...icon}><path d="M12 5v14M5 12h14" /></svg>
const PencilIcon = () => <svg {...icon}><path d="M4 20h4L19 9l-4-4L4 16v4z" /><path d="M14 6l4 4" /></svg>
const TrashIcon = () => <svg {...icon}><path d="M4 7h16M10 11v6M14 11v6M6 7l1 13h10l1-13M9 7V4h6v3" /></svg>
export const MenuIcon = () => <svg {...icon} width={20} height={20}><path d="M4 6h16M4 12h16M4 18h16" /></svg>
