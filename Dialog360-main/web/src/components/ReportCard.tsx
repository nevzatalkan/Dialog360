import { useCallback, useMemo, useState } from 'react'
import type { Cell, ChatResponse, DrillTarget, Report } from '../api'
import { formatPeriodLong, toCsv } from '../format'
import { DataTable } from './DataTable'
import { DrillMenu, UpIcon, type DrillPick } from './DrillMenu'
import { availableKinds, ReportChart, type ChartKind } from './ReportChart'
import { SqlPanel } from './SqlPanel'

/** Drill isteği: value null ise üst seviyeye çıkılır. label kullanıcı mesajı olarak gösterilir. */
export type DrillHandler = (response: ChatResponse, column: string, value: Cell, target: DrillTarget, label: string) => void
export type DrillBackHandler = (messageId: number, label: string) => void

type Tab = 'chart' | 'table' | 'sql'

const KIND_LABELS: Record<ChartKind, string> = { kpi: 'Gösterge', bar: 'Çubuk', line: 'Çizgi', multiline: 'Çizgi', pie: 'Halka' }

export function ReportCard({ response, onRunSql, onDrill, onDrillBack, busy }: {
  response: ChatResponse
  onRunSql: (sql: string) => void
  onDrill?: DrillHandler
  onDrillBack?: DrillBackHandler
  busy?: boolean
}) {
  const report = response.report!
  const kinds = availableKinds(report)
  const [tab, setTab] = useState<Tab>(kinds.length > 0 ? 'chart' : 'table')
  const [kind, setKind] = useState<ChartKind | undefined>(kinds[0])
  const [metric, setMetric] = useState(report.chart.y[0])
  const [pick, setPick] = useState<DrillPick | null>(null)
  const closeMenu = useCallback(() => setPick(null), [setPick])

  // Drill yalnızca kayıtlı (messageId'li) raporlarda ve hedefi olan kolonlarda açılır
  const canDrill = !!onDrill && response.messageId > 0 && !busy
  const drill = useMemo(() => new Map((report.drill ?? []).filter(c => c.targets.length > 0).map(c => [c.column, c])), [report.drill])
  const chartColumn = report.chart.x && drill.has(report.chart.x) ? report.chart.x : null
  const rollUp = (report.drill ?? []).find(c => c.up && c.column === report.chart.x) ?? (report.drill ?? []).find(c => c.up)
  const crumbs = response.crumbs ?? []

  const choose = (target: DrillTarget) => {
    if (!pick) return
    setPick(null)
    onDrill!(response, pick.column, pick.value, target, `${pick.value === null ? '(boş)' : formatPeriodLong(String(pick.value))} → ${target.label}`)
  }

  return (
    <div className="overflow-hidden rounded-2xl border border-slate-200 bg-white shadow-sm">
      <div className="border-b border-slate-100 px-5 pb-3 pt-4">
        {crumbs.length > 1 && (
          <nav aria-label="Detay yolu" className="mb-2 flex flex-wrap items-center gap-1 text-xs text-slate-500">
            {crumbs.map((c, i) => (
              <span key={i} className="flex items-center gap-1">
                {i > 0 && <span className="text-slate-300" aria-hidden="true">›</span>}
                {i < crumbs.length - 1 && c.messageId && onDrillBack ? (
                  <button onClick={() => onDrillBack(c.messageId!, c.label)} disabled={busy} title="Bu seviyeye geri dön"
                    className="max-w-56 truncate rounded px-1 py-0.5 text-indigo-600 hover:bg-indigo-50 hover:text-indigo-800 disabled:opacity-50">
                    {c.label}
                  </button>
                ) : (
                  <span className={`max-w-56 truncate px-1 py-0.5 ${i === crumbs.length - 1 ? 'font-medium text-slate-800' : ''}`} aria-current={i === crumbs.length - 1 ? 'page' : undefined}>{c.label}</span>
                )}
              </span>
            ))}
          </nav>
        )}
        <div className="flex items-start justify-between gap-4">
          <h3 className="text-base font-semibold text-slate-900">{response.title}</h3>
          <Badges response={response} />
        </div>
        <p className="mt-1.5 text-sm leading-relaxed text-slate-600">{response.text}</p>
      </div>

      <div className="flex items-center justify-between gap-2 border-b border-slate-100 px-5">
        <nav className="flex gap-1">
          {kinds.length > 0 && <TabButton active={tab === 'chart'} onClick={() => setTab('chart')}>Grafik</TabButton>}
          <TabButton active={tab === 'table'} onClick={() => setTab('table')}>Tablo ({report.rows.length.toLocaleString('tr-TR')}{report.truncated ? '+' : ''})</TabButton>
          <TabButton active={tab === 'sql'} onClick={() => setTab('sql')}>SQL</TabButton>
        </nav>
        <div className="flex items-center gap-2 py-2">
          {canDrill && rollUp?.up && tab !== 'sql' && (
            <button onClick={() => onDrill!(response, rollUp.column, null, rollUp.up!, `↑ ${rollUp.up!.label}`)}
              className="flex items-center gap-1 rounded-md px-2 py-1 text-xs text-slate-500 hover:bg-slate-100 hover:text-slate-800" title="Bir üst seviyede göster">
              <UpIcon /> {rollUp.up.label}
            </button>
          )}
          {tab === 'chart' && report.chart.type !== 'kpi' && report.chart.y.length > 1 && (
            <select value={metric} onChange={e => setMetric(e.target.value)} className="rounded-md border border-slate-200 px-2 py-1 text-xs">
              {report.chart.y.map(y => <option key={y}>{y}</option>)}
            </select>
          )}
          {tab === 'chart' && kinds.length > 1 && kinds.map(k => (
            <button key={k} onClick={() => setKind(k)}
              className={`rounded-md px-2 py-1 text-xs ${kind === k ? 'bg-indigo-50 font-medium text-indigo-700' : 'text-slate-500 hover:bg-slate-100'}`}>
              {KIND_LABELS[k]}
            </button>
          ))}
          <button onClick={() => download(response.title, report)} className="rounded-md px-2 py-1 text-xs text-slate-500 hover:bg-slate-100" title="CSV olarak indir (Excel)">
            ⤓ CSV
          </button>
        </div>
      </div>

      <div className="p-5">
        {tab === 'chart' && kind && (
          <ReportChart report={report} kind={kind} metric={metric}
            onPick={canDrill && chartColumn ? (value, x, y) => setPick({ column: chartColumn, value, x, y }) : undefined} />
        )}
        {tab === 'table' && (
          <DataTable columns={report.columns} rows={report.rows} pickable={canDrill ? new Set(drill.keys()) : undefined}
            onPick={canDrill ? (column, value, x, y) => setPick({ column, value, x, y }) : undefined} />
        )}
        {canDrill && drill.size > 0 && tab !== 'sql' && (
          <p className="mt-3 flex items-center gap-1.5 text-xs text-slate-400">
            <CursorIcon />
            {tab === 'chart' && chartColumn ? 'Detay için grafikteki bir değere tıklayın' : 'Detay için tablodaki altı çizili değerlere tıklayın'}
            {tab === 'chart' && chartColumn && drill.size > 1 ? ' (diğer kolonlar Tablo sekmesinde)' : ''}
          </p>
        )}
        {pick && drill.get(pick.column) && <DrillMenu pick={pick} column={drill.get(pick.column)!} onSelect={choose} onClose={closeMenu} />}
        {tab === 'sql' && <SqlPanel report={report} meta={response.meta} onRunSql={onRunSql} />}
      </div>
    </div>
  )
}

function Badges({ response }: { response: ChatResponse }) {
  const { meta, report } = response
  return (
    <div className="flex shrink-0 flex-wrap justify-end gap-1.5 text-[11px] font-medium">
      {response.drillSource === 'rewrite' && <span className="rounded-full bg-indigo-50 px-2 py-0.5 text-indigo-700" title="Önceki SQL'e filtre eklenerek, dil modeli kullanılmadan üretildi">detay · LLM'siz</span>}
      {response.drillSource === 'llm' && <span className="rounded-full bg-indigo-50 px-2 py-0.5 text-indigo-700">detay</span>}
      {response.drillSource === 'back' && <span className="rounded-full bg-slate-100 px-2 py-0.5 text-slate-600" title="Kayıtlı rapor yeniden gösterildi">geri dönüldü</span>}
      {meta.fromCache && <span className="rounded-full bg-emerald-50 px-2 py-0.5 text-emerald-700">önbellekten</span>}
      {meta.corrections.length > 0 && <span className="rounded-full bg-amber-50 px-2 py-0.5 text-amber-700">{meta.corrections.length} kez kendini düzeltti</span>}
      {report && report.planWarnings.length > 0 && <span className="rounded-full bg-amber-50 px-2 py-0.5 text-amber-700">performans uyarısı</span>}
      <span className="rounded-full bg-slate-100 px-2 py-0.5 text-slate-500">salt okunur ✓</span>
    </div>
  )
}

function TabButton({ active, onClick, children }: { active: boolean; onClick: () => void; children: React.ReactNode }) {
  return (
    <button onClick={onClick}
      className={`-mb-px border-b-2 px-3 py-2.5 text-sm ${active ? 'border-indigo-600 font-medium text-indigo-700' : 'border-transparent text-slate-500 hover:text-slate-800'}`}>
      {children}
    </button>
  )
}

function download(title: string, report: Report) {
  const blob = new Blob([toCsv(report.columns, report.rows)], { type: 'text/csv;charset=utf-8' })
  const a = document.createElement('a')
  a.href = URL.createObjectURL(blob)
  a.download = `${title.replace(/[^\p{L}\p{N}]+/gu, '_')}.csv`
  a.click()
  URL.revokeObjectURL(a.href)
}

const CursorIcon = () => (
  <svg width={13} height={13} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth={2} strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
    <path d="M4 4l7 17 2.5-7.5L21 11z" />
  </svg>
)
