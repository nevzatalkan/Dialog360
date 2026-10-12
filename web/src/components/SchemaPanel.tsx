import { useEffect, useState } from 'react'
import { api, type TableInfo } from '../api'

export function SchemaPanel({ onClose }: { onClose: () => void }) {
  const [tables, setTables] = useState<TableInfo[] | null>(null)
  const [open, setOpen] = useState<string | null>('Islem')
  const [error, setError] = useState<string | null>(null)

  useEffect(() => { api.schema().then(setTables).catch(e => setError(e.message)) }, [])

  return (
    <aside className="flex h-full w-96 shrink-0 flex-col border-l border-slate-200 bg-white">
      <div className="flex items-center justify-between border-b border-slate-100 px-4 py-3">
        <div>
          <div className="font-semibold text-slate-900">Veri kataloğu</div>
          <div className="text-xs text-slate-500">Veritabanından otomatik okunur, açıklamalar elle eklenir</div>
        </div>
        <button onClick={onClose} className="rounded-md px-2 py-1 text-slate-400 hover:bg-slate-100" aria-label="Kapat">✕</button>
      </div>
      <div className="flex-1 overflow-y-auto p-3">
        {error && <div className="text-sm text-rose-600">{error}</div>}
        {tables?.map(t => (
          <div key={t.name} className="mb-1.5 rounded-lg border border-slate-100">
            <button onClick={() => setOpen(open === t.name ? null : t.name)} className="flex w-full items-start justify-between gap-2 px-3 py-2 text-left hover:bg-slate-50">
              <div>
                <div className="font-mono text-sm font-medium text-slate-800">{t.name}</div>
                <div className="text-xs text-slate-500">{t.description}</div>
              </div>
              <span className="shrink-0 text-xs tabular-nums text-slate-400">{t.rowCount.toLocaleString('tr-TR')} satır</span>
            </button>
            {open === t.name && (
              <ul className="space-y-1.5 border-t border-slate-100 px-3 py-2">
                {t.columns.map(c => (
                  <li key={c.name} className="text-xs">
                    <div className="flex flex-wrap items-center gap-1.5">
                      <span className="font-mono font-medium text-slate-700">{c.name}</span>
                      <span className="text-slate-400">{c.type}</span>
                      {c.isPk && <Tag className="bg-slate-100 text-slate-600">PK</Tag>}
                      {c.fkTable && <Tag className="bg-sky-50 text-sky-700">→ {c.fkTable}</Tag>}
                      {c.isPii && <Tag className="bg-rose-50 text-rose-700">KVKK · erişim kapalı</Tag>}
                    </div>
                    {c.description && <div className="text-slate-500">{c.description}</div>}
                    {c.values && <div className="text-slate-400">{c.values.join(' · ')}</div>}
                  </li>
                ))}
              </ul>
            )}
          </div>
        ))}
      </div>
    </aside>
  )
}

function Tag({ className, children }: { className: string; children: React.ReactNode }) {
  return <span className={`rounded px-1.5 py-px text-[10px] font-medium ${className}`}>{children}</span>
}
