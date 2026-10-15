import { useMemo, useState } from 'react'
import type { Cell, QueryColumn } from '../api'
import { formatCell } from '../format'

const PAGE = 50

/** pickable: tıklanınca detaya inilebilen kolonlar (drill). Bu kolonların hücreleri tıklanabilir görünür. */
export function DataTable({ columns, rows, pickable, onPick }: {
  columns: QueryColumn[]; rows: Cell[][]
  pickable?: Set<string>; onPick?: (column: string, value: Cell, x: number, y: number) => void
}) {
  const [sort, setSort] = useState<{ col: number; dir: 1 | -1 } | null>(null)
  const [limit, setLimit] = useState(PAGE)

  const sorted = useMemo(() => {
    if (!sort) return rows
    return [...rows].sort((a, b) => {
      const x = a[sort.col], y = b[sort.col]
      if (x === y) return 0
      if (x === null) return 1
      if (y === null) return -1
      return (typeof x === 'number' && typeof y === 'number' ? x - y : String(x).localeCompare(String(y), 'tr')) * sort.dir
    })
  }, [rows, sort])

  const toggle = (col: number) =>
    setSort(s => (s?.col === col ? (s.dir === -1 ? { col, dir: 1 } : null) : { col, dir: -1 }))

  return (
    <div className="overflow-x-auto rounded-lg border border-slate-200">
      <table className="w-full text-sm">
        <thead className="bg-slate-50 text-left text-xs font-semibold uppercase tracking-wide text-slate-500">
          <tr>
            {columns.map((c, i) => (
              <th key={c.name} onClick={() => toggle(i)}
                className={`cursor-pointer select-none whitespace-nowrap px-3 py-2 hover:text-slate-800 ${c.kind === 'number' ? 'text-right' : ''}`}>
                {c.name} {sort?.col === i ? (sort.dir === -1 ? '↓' : '↑') : ''}
              </th>
            ))}
          </tr>
        </thead>
        <tbody className="divide-y divide-slate-100">
          {sorted.slice(0, limit).map((r, ri) => (
            <tr key={ri} className="hover:bg-slate-50/70">
              {r.map((v, ci) => (
                <td key={ci} className={`whitespace-nowrap px-3 py-1.5 ${columns[ci].kind === 'number' ? 'text-right tabular-nums' : ''}`}>
                  {onPick && pickable?.has(columns[ci].name) && v !== null ? (
                    <button type="button" onClick={e => onPick(columns[ci].name, v, e.clientX, e.clientY)} title="Detay için tıklayın"
                      className="rounded text-left text-indigo-700 underline decoration-indigo-300 decoration-dotted underline-offset-4 hover:decoration-solid focus-visible:outline-2 focus-visible:outline-indigo-600">
                      {formatCell(v, columns[ci])}
                    </button>
                  ) : formatCell(v, columns[ci])}
                </td>
              ))}
            </tr>
          ))}
        </tbody>
      </table>
      {rows.length > limit && (
        <button onClick={() => setLimit(l => l + PAGE)} className="w-full border-t border-slate-200 py-2 text-sm text-indigo-600 hover:bg-indigo-50">
          {rows.length - limit} satır daha göster
        </button>
      )}
    </div>
  )
}
