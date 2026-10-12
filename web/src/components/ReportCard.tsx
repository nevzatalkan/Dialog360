import { useState } from 'react'
import type { ChatResponse, Report } from '../api'
import { toCsv } from '../format'
import { DataTable } from './DataTable'
import { availableKinds, ReportChart, type ChartKind } from './ReportChart'
import { SqlPanel } from './SqlPanel'

type Tab = 'chart' | 'table' | 'sql'

const KIND_LABELS: Record<ChartKind, string> = { kpi: 'Gösterge', bar: 'Çubuk', line: 'Çizgi', multiline: 'Çizgi', pie: 'Halka' }

export function ReportCard({ response, onRunSql }: { response: ChatResponse; onRunSql: (sql: string) => void }) {
  const report = response.report!
  const kinds = availableKinds(report)
  const [tab, setTab] = useState<Tab>(kinds.length > 0 ? 'chart' : 'table')
  const [kind, setKind] = useState<ChartKind | undefined>(kinds[0])
  const [metric, setMetric] = useState(report.chart.y[0])

  return (
    <div className="overflow-hidden rounded-2xl border border-slate-200 bg-white shadow-sm">
      <div className="border-b border-slate-100 px-5 pb-3 pt-4">
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
        {tab === 'chart' && kind && <ReportChart report={report} kind={kind} metric={metric} />}
        {tab === 'table' && <DataTable columns={report.columns} rows={report.rows} />}
        {tab === 'sql' && <SqlPanel report={report} meta={response.meta} onRunSql={onRunSql} />}
      </div>
    </div>
  )
}

function Badges({ response }: { response: ChatResponse }) {
  const { meta, report } = response
  return (
    <div className="flex shrink-0 flex-wrap justify-end gap-1.5 text-[11px] font-medium">
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
