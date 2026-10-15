import { useEffect, useLayoutEffect, useRef, useState } from 'react'
import type { Cell, DrillColumn, DrillTarget } from '../api'
import { formatPeriodLong } from '../format'

export interface DrillPick { column: string; value: Cell; x: number; y: number }

const WIDTH = 256

/** Tıklanan değerin üstünde açılan menü: "Detaya in" (aynı hiyerarşide alt seviyeler) ve "Farklı kırılım" (başka boyutlar). */
export function DrillMenu({ pick, column, onSelect, onClose }: {
  pick: DrillPick
  column: DrillColumn
  onSelect: (target: DrillTarget) => void
  onClose: () => void
}) {
  const ref = useRef<HTMLDivElement>(null)
  const [pos, setPos] = useState({ left: pick.x, top: pick.y })

  // Ekrandan taşmasın: sağa/alta sığmıyorsa tıklanan noktanın soluna/üstüne aç
  useLayoutEffect(() => {
    const h = ref.current?.offsetHeight ?? 0
    setPos({
      left: Math.max(8, pick.x + WIDTH + 8 > window.innerWidth ? pick.x - WIDTH : pick.x),
      top: Math.max(8, pick.y + h + 8 > window.innerHeight ? pick.y - h : pick.y + 6),
    })
  }, [pick])

  useEffect(() => {
    const onDown = (e: MouseEvent) => { if (!ref.current?.contains(e.target as Node)) onClose() }
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') onClose() }
    window.addEventListener('mousedown', onDown)
    window.addEventListener('keydown', onKey)
    window.addEventListener('resize', onClose)
    document.querySelector('main')?.addEventListener('scroll', onClose, { passive: true })
    ref.current?.querySelector<HTMLButtonElement>('button')?.focus()
    return () => {
      window.removeEventListener('mousedown', onDown)
      window.removeEventListener('keydown', onKey)
      window.removeEventListener('resize', onClose)
      document.querySelector('main')?.removeEventListener('scroll', onClose)
    }
  }, [onClose])

  const down = column.targets.filter(t => t.kind === 'down')
  const across = column.targets.filter(t => t.kind === 'across')
  const value = pick.value === null ? '(boş)' : formatPeriodLong(String(pick.value))

  return (
    <div ref={ref} role="menu" aria-label={`${column.levelLabel}: ${value} için detay`} style={{ left: pos.left, top: pos.top, width: WIDTH }}
      className="fixed z-50 rounded-xl border border-slate-200 bg-white p-1.5 text-sm shadow-xl ring-1 ring-slate-900/5">
      <div className="flex items-baseline gap-1.5 border-b border-slate-100 px-2.5 pb-2 pt-1">
        <span className="text-xs text-slate-400">{column.levelLabel}</span>
        <span className="truncate font-semibold text-slate-900">{value}</span>
      </div>
      {down.length > 0 && <Group title="Detaya in" icon={<DownIcon />} targets={down} onSelect={onSelect} />}
      {across.length > 0 && <Group title="Farklı kırılım" icon={<SplitIcon />} targets={across} onSelect={onSelect} />}
    </div>
  )
}

function Group({ title, icon, targets, onSelect }: { title: string; icon: React.ReactNode; targets: DrillTarget[]; onSelect: (t: DrillTarget) => void }) {
  return (
    <div className="pt-1.5">
      <div className="px-2.5 pb-1 text-[11px] font-semibold uppercase tracking-wide text-slate-400">{title}</div>
      {targets.map(t => (
        <button key={t.key} role="menuitem" onClick={() => onSelect(t)}
          className="flex w-full items-center gap-2.5 rounded-lg px-2.5 py-1.5 text-left text-slate-700 hover:bg-indigo-50 hover:text-indigo-700 focus-visible:bg-indigo-50 focus-visible:text-indigo-700 focus-visible:outline-none">
          <span className="text-slate-400">{icon}</span>{t.label}
        </button>
      ))}
    </div>
  )
}

const icon = { width: 14, height: 14, viewBox: '0 0 24 24', fill: 'none', stroke: 'currentColor', strokeWidth: 2, strokeLinecap: 'round' as const, strokeLinejoin: 'round' as const, 'aria-hidden': true }
export const DownIcon = () => <svg {...icon}><path d="M12 5v14M6 13l6 6 6-6" /></svg>
export const UpIcon = () => <svg {...icon}><path d="M12 19V5M6 11l6-6 6 6" /></svg>
const SplitIcon = () => <svg {...icon}><path d="M6 3v6a6 6 0 0 0 6 6h6M14 11l4 4-4 4" /></svg>
