import { useState } from 'react'
import type { Meta, Report } from '../api'
import { formatMs, prettySql } from '../format'

export function SqlBlock({ sql }: { sql: string }) {
  const [copied, setCopied] = useState(false)
  const copy = () => navigator.clipboard.writeText(sql).then(() => { setCopied(true); setTimeout(() => setCopied(false), 1500) })
  return (
    <div className="relative">
      <pre className="overflow-x-auto rounded-lg bg-slate-900 p-4 pr-20 font-mono text-[13px] leading-relaxed text-slate-100">{prettySql(sql)}</pre>
      <button onClick={copy} className="absolute right-2 top-2 rounded-md bg-slate-700 px-2 py-1 text-xs text-slate-100 hover:bg-slate-600">
        {copied ? 'Kopyalandı' : 'Kopyala'}
      </button>
    </div>
  )
}

export function SqlPanel({ report, meta, onRunSql }: { report: Report; meta: Meta; onRunSql: (sql: string) => void }) {
  const [editing, setEditing] = useState(false)
  const [draft, setDraft] = useState(report.sql)

  return (
    <div className="space-y-4 text-sm">
      <div>
        <div className="mb-1 font-medium text-slate-700">Bu sorgu ne yapıyor?</div>
        <p className="text-slate-600">{report.explanation}</p>
        {report.assumptions.length > 0 && (
          <ul className="mt-2 space-y-1">
            {report.assumptions.map(a => (
              <li key={a} className="flex gap-2 text-slate-600"><span className="text-amber-500">◆</span>{a}</li>
            ))}
          </ul>
        )}
      </div>

      {editing ? (
        <div className="space-y-2">
          <textarea value={draft} onChange={e => setDraft(e.target.value)} rows={8} spellCheck={false}
            className="w-full rounded-lg border border-slate-300 bg-slate-900 p-3 font-mono text-[13px] text-slate-100 outline-none focus:border-indigo-500" />
          <div className="flex gap-2">
            <button onClick={() => { onRunSql(draft); setEditing(false) }} className="rounded-md bg-indigo-600 px-3 py-1.5 text-white hover:bg-indigo-700">Çalıştır</button>
            <button onClick={() => { setDraft(report.sql); setEditing(false) }} className="rounded-md px-3 py-1.5 text-slate-600 hover:bg-slate-100">Vazgeç</button>
            <span className="self-center text-xs text-slate-400">Düzenlenen sorgu da aynı güvenlik doğrulamasından geçer.</span>
          </div>
        </div>
      ) : (
        <div className="space-y-2">
          <SqlBlock sql={report.sql} />
          <button onClick={() => setEditing(true)} className="text-xs font-medium text-indigo-600 hover:underline">SQL'i düzenle ve çalıştır</button>
        </div>
      )}

      <div className="grid gap-3 sm:grid-cols-2">
        <Info title="Kullanılan tablolar">
          <div className="flex flex-wrap gap-1.5">
            {report.tables.map(t => <span key={t} className="rounded bg-slate-100 px-2 py-0.5 font-mono text-xs">{t}</span>)}
          </div>
          <div className="mt-2 text-xs text-slate-400">{report.usedColumns.length} kolon okundu (SQLite authorizer ile tespit edildi)</div>
        </Info>
        <Info title="Süreler">
          <div className="grid grid-cols-2 gap-x-4 gap-y-1 text-xs text-slate-600">
            <span>Dil modeli</span><span className="text-right tabular-nums">{meta.fromCache ? 'önbellekten' : formatMs(meta.llmMs)}</span>
            <span>Veritabanı</span><span className="text-right tabular-nums">{formatMs(report.dbMs)}</span>
            <span>Toplam</span><span className="text-right tabular-nums">{formatMs(meta.totalMs)}</span>
            <span>Deneme</span><span className="text-right tabular-nums">{meta.attempts}</span>
          </div>
        </Info>
      </div>

      {meta.corrections.length > 0 && (
        <Info title="Kendini düzeltme">
          <ol className="space-y-2">
            {meta.corrections.map((c, i) => (
              <li key={i} className="text-xs">
                <div className="font-medium text-rose-600">{i + 1}. deneme reddedildi: {c.error}</div>
                <code className="mt-1 block truncate font-mono text-slate-400" title={c.sql}>{c.sql}</code>
              </li>
            ))}
          </ol>
          <div className="mt-2 text-xs text-emerald-600">Hata mesajı modele geri verildi, {meta.attempts}. denemede düzeltildi.</div>
        </Info>
      )}

      <Info title="Sorgu planı">
        {report.planWarnings.map(w => <div key={w} className="mb-1 text-xs font-medium text-amber-600">⚠ {w}</div>)}
        <ul className="space-y-0.5 font-mono text-xs text-slate-500">
          {report.plan.map((p, i) => <li key={i}>{p}</li>)}
        </ul>
      </Info>
    </div>
  )
}

function Info({ title, children }: { title: string; children: React.ReactNode }) {
  return (
    <div className="rounded-lg border border-slate-200 p-3">
      <div className="mb-2 text-xs font-semibold uppercase tracking-wide text-slate-400">{title}</div>
      {children}
    </div>
  )
}
