import { useEffect } from 'react'
import type { Conversation } from '../history'
import { CloseIcon, PlusIcon, TrashIcon } from './icons'

interface Props {
  open: boolean
  conversations: Conversation[]
  activeId: string | null
  onSelect: (c: Conversation) => void
  onDelete: (c: Conversation) => void
  onNew: () => void
  onClose: () => void
}

const dateFormat = new Intl.DateTimeFormat('tr-TR', { day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit' })

export function HistoryDrawer({ open, conversations, activeId, onSelect, onDelete, onNew, onClose }: Props) {
  useEffect(() => {
    if (!open) return
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') onClose() }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [open, onClose])

  const confirmDelete = (c: Conversation) => {
    if (window.confirm(`"${c.title}" konuşmasını silmek istediğinize emin misiniz?\nBu işlem geri alınamaz.`)) onDelete(c)
  }

  return (
    <>
      <div onClick={onClose} aria-hidden="true"
        className={`fixed inset-0 z-30 bg-slate-900/30 transition-opacity ${open ? 'opacity-100' : 'pointer-events-none opacity-0'}`} />
      <aside aria-label="Geçmiş Sorgularım" inert={!open}
        className={`fixed inset-y-0 left-0 z-40 flex w-80 flex-col border-r border-slate-200 bg-white shadow-xl transition-transform ${open ? 'translate-x-0' : '-translate-x-full'}`}>
        <div className="flex items-center justify-between border-b border-slate-100 px-4 py-3">
          <h2 className="font-semibold text-slate-900">Geçmiş Sorgularım</h2>
          <button onClick={onClose} className="rounded-md p-1 text-slate-400 hover:bg-slate-100" aria-label="Menüyü kapat"><CloseIcon /></button>
        </div>
        <div className="p-3">
          <button onClick={onNew} className="flex w-full items-center justify-center gap-2 rounded-lg border border-slate-200 py-2 text-sm text-slate-700 hover:bg-slate-50">
            <PlusIcon /> Yeni sohbet
          </button>
        </div>
        <nav className="flex-1 overflow-y-auto px-2 pb-3">
          {conversations.length === 0 && <p className="px-3 py-6 text-center text-sm text-slate-400">Henüz kayıtlı konuşma yok.</p>}
          <ul className="space-y-1">
            {conversations.map(c => {
              const questions = c.items.filter(i => i.role === 'user').length
              return (
                <li key={c.id} className={`group flex items-center rounded-lg ${c.id === activeId ? 'bg-indigo-50' : 'hover:bg-slate-50'}`}>
                  <button onClick={() => onSelect(c)} className="min-w-0 flex-1 px-3 py-2 text-left">
                    <div className={`truncate text-sm ${c.id === activeId ? 'font-medium text-indigo-800' : 'text-slate-800'}`}>{c.title}</div>
                    <div className="text-xs text-slate-400">{dateFormat.format(c.updatedAt)} · {questions} soru</div>
                  </button>
                  <button onClick={() => confirmDelete(c)} title="Konuşmayı sil" aria-label={`"${c.title}" konuşmasını sil`}
                    className="mr-2 rounded-md p-1.5 text-slate-400 hover:bg-rose-50 hover:text-rose-600">
                    <TrashIcon />
                  </button>
                </li>
              )
            })}
          </ul>
        </nav>
        <p className="border-t border-slate-100 px-4 py-2 text-[11px] text-slate-400">Konuşmalar bu tarayıcıda saklanır.</p>
      </aside>
    </>
  )
}
