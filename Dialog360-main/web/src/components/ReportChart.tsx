import {
  Bar, BarChart, CartesianGrid, Cell as PieCell, Legend, Line, LineChart, Pie, PieChart,
  ResponsiveContainer, Tooltip, XAxis, YAxis,
} from 'recharts'
import type { Cell, QueryColumn, Report } from '../api'
import { formatCompact, formatNumber, formatPeriod } from '../format'

export type ChartKind = 'kpi' | 'bar' | 'line' | 'multiline' | 'pie'

const COLORS = ['#4f46e5', '#0891b2', '#f59e0b', '#10b981', '#ef4444', '#8b5cf6', '#ec4899', '#64748b', '#14b8a6', '#f97316', '#84cc16', '#0ea5e9']

const idx = (cols: QueryColumn[], name: string | null) => cols.findIndex(c => c.name === name)
const num = (v: Cell) => (typeof v === 'number' ? v : Number(v) || 0)
const label = (v: Cell) => formatPeriod(v === null ? '(boş)' : String(v))

/** Rapor için hangi grafik türleri anlamlı? İlk eleman sunucunun önerisidir. */
export function availableKinds(report: Report): ChartKind[] {
  const { type } = report.chart
  if (type === 'table') return []
  if (type === 'kpi') return ['kpi']
  if (type === 'bar') {
    const positive = report.rows.every(r => num(r[idx(report.columns, report.chart.y[0])]) >= 0)
    return positive && report.rows.length <= 8 ? ['bar', 'pie'] : ['bar']
  }
  if (type === 'line') return ['line', 'bar']
  return ['multiline']
}

/** Grafikte x eksenindeki bir değere tıklanınca çağrılır (drill down). Verilmezse grafik tıklanamaz. */
export type PickHandler = (value: Cell, x: number, y: number) => void

type Datum = { x: string; y: number; raw: Cell }

/** Recharts tıklama olayından ham değeri ve ekran konumunu alır. */
const pickFrom = (onPick: PickHandler | undefined) => onPick && ((d: { payload?: Datum } | undefined, _i: number, e: React.MouseEvent) => {
  if (d?.payload) onPick(d.payload.raw, e.clientX, e.clientY)
})

export function ReportChart({ report, kind, metric, onPick }: { report: Report; kind: ChartKind; metric: string; onPick?: PickHandler }) {
  const { columns, rows, chart } = report
  const tooltip = (v: unknown) => formatNumber(Number(v), metric)
  const pointer = onPick ? { cursor: 'pointer' } : undefined
  /** Çizgi grafiklerde tıklanan x etiketini ham değere çevirir. */
  const onLineClick = (labels: Map<string, Cell>) => onPick && ((state: { activeLabel?: string | number }, e: React.SyntheticEvent) => {
    const raw = state?.activeLabel === undefined ? undefined : labels.get(String(state.activeLabel))
    const m = e as unknown as React.MouseEvent
    if (raw !== undefined) onPick(raw, m.clientX, m.clientY)
  })

  if (kind === 'kpi') {
    return (
      <div className="flex flex-wrap gap-4 py-2">
        {chart.y.map(y => (
          <div key={y} className="min-w-48 rounded-xl border border-indigo-100 bg-indigo-50/60 px-6 py-5">
            <div className="text-sm font-medium text-indigo-700">{y}</div>
            <div className="mt-1 text-4xl font-semibold tracking-tight text-slate-900">
              {formatNumber(num(rows[0][idx(columns, y)]), y)}
            </div>
          </div>
        ))}
      </div>
    )
  }

  if (kind === 'multiline') {
    const xi = idx(columns, chart.x), si = idx(columns, chart.series), yi = idx(columns, metric)
    const periods = [...new Set(rows.map(r => String(r[xi])))].sort()
    const totals = new Map<string, number>()
    rows.forEach(r => totals.set(String(r[si]), (totals.get(String(r[si])) ?? 0) + num(r[yi])))
    const series = [...totals.entries()].sort((a, b) => b[1] - a[1]).map(([s]) => s)
    const labels = new Map<string, Cell>(periods.map(p => [formatPeriod(p), rows.find(r => String(r[xi]) === p)![xi]]))
    const data = periods.map(p => {
      const point: Record<string, string | number> = { x: formatPeriod(p) }
      rows.filter(r => String(r[xi]) === p).forEach(r => { point[String(r[si])] = num(r[yi]) })
      return point
    })
    return (
      <ResponsiveContainer width="100%" height={340}>
        <LineChart data={data} margin={{ top: 8, right: 16, left: 8, bottom: 0 }} onClick={onLineClick(labels)} style={pointer}>
          <CartesianGrid strokeDasharray="3 3" stroke="#e2e8f0" />
          <XAxis dataKey="x" tick={{ fontSize: 12 }} />
          <YAxis tickFormatter={formatCompact} tick={{ fontSize: 12 }} width={56} />
          <Tooltip formatter={tooltip} cursor={{ fill: "#eef2ff" }} />
          <Legend wrapperStyle={{ fontSize: 12 }} />
          {series.map((s, i) => (
            <Line key={s} type="monotone" dataKey={s} stroke={COLORS[i % COLORS.length]} strokeWidth={2} dot={false} />
          ))}
        </LineChart>
      </ResponsiveContainer>
    )
  }

  const xi = idx(columns, chart.x), yi = idx(columns, metric)
  const xIsId = columns[xi]?.kind === 'text' && /(no|id|numara)$/i.test(chart.x ?? '')
  const data: Datum[] = rows.map(r => ({ x: (xIsId ? `#${r[xi]}` : label(r[xi])), y: num(r[yi]), raw: r[xi] }))
  const onBar = pickFrom(onPick)

  if (kind === 'pie') {
    return (
      <ResponsiveContainer width="100%" height={320}>
        <PieChart>
          <Pie data={data} dataKey="y" nameKey="x" innerRadius={70} outerRadius={120} paddingAngle={2} onClick={onBar} style={pointer}>
            {data.map((_, i) => <PieCell key={i} fill={COLORS[i % COLORS.length]} />)}
          </Pie>
          <Tooltip formatter={tooltip} cursor={{ fill: "#eef2ff" }} />
          <Legend wrapperStyle={{ fontSize: 12 }} />
        </PieChart>
      </ResponsiveContainer>
    )
  }

  if (kind === 'line') {
    return (
      <ResponsiveContainer width="100%" height={320}>
        <LineChart data={data} margin={{ top: 8, right: 16, left: 8, bottom: 0 }} onClick={onLineClick(new Map(data.map(d => [d.x, d.raw])))} style={pointer}>
          <CartesianGrid strokeDasharray="3 3" stroke="#e2e8f0" />
          <XAxis dataKey="x" tick={{ fontSize: 12 }} />
          <YAxis tickFormatter={formatCompact} tick={{ fontSize: 12 }} width={56} />
          <Tooltip formatter={tooltip} cursor={{ fill: "#eef2ff" }} />
          <Line type="monotone" dataKey="y" name={metric} stroke={COLORS[0]} strokeWidth={2.5} dot={{ r: 3 }} activeDot={{ r: onPick ? 6 : 4 }} />
        </LineChart>
      </ResponsiveContainer>
    )
  }

  const horizontal = data.length > 6
  return (
    <ResponsiveContainer width="100%" height={horizontal ? Math.max(260, data.length * 34) : 320}>
      {horizontal ? (
        <BarChart data={data} layout="vertical" margin={{ top: 4, right: 24, left: 8, bottom: 0 }}>
          <CartesianGrid strokeDasharray="3 3" stroke="#e2e8f0" horizontal={false} />
          <XAxis type="number" tickFormatter={formatCompact} tick={{ fontSize: 12 }} />
          <YAxis type="category" dataKey="x" width={130} tick={{ fontSize: 12 }} />
          <Tooltip formatter={tooltip} cursor={{ fill: "#eef2ff" }} />
          <Bar dataKey="y" name={metric} fill={COLORS[0]} radius={[0, 4, 4, 0]} onClick={onBar} style={pointer} />
        </BarChart>
      ) : (
        <BarChart data={data} margin={{ top: 8, right: 16, left: 8, bottom: 0 }}>
          <CartesianGrid strokeDasharray="3 3" stroke="#e2e8f0" vertical={false} />
          <XAxis dataKey="x" tick={{ fontSize: 12 }} />
          <YAxis tickFormatter={formatCompact} tick={{ fontSize: 12 }} width={56} />
          <Tooltip formatter={tooltip} cursor={{ fill: "#eef2ff" }} />
          <Bar dataKey="y" name={metric} fill={COLORS[0]} radius={[4, 4, 0, 0]} onClick={onBar} style={pointer} />
        </BarChart>
      )}
    </ResponsiveContainer>
  )
}
