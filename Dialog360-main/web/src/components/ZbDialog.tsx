import { useEffect, useMemo, useRef, useState } from 'react'
import { api, type FilterDefault, type FilterField, type ZbAnalysis } from '../api'
import { buildCriteria, mergeContext, toDefaults, type Selection } from '../zb'

/**
 * ZB kriter penceresi. Soru kutusunun üstünde açılır: ana alan (örn. yaş aralığı) vurgulu kartta, altında modelin
 * önerdiği ek filtreler (cinsiyet, segment, şehir...) ve "Filtre ekle" ile diğer tüm alanlar. Tamam'a basınca seçimler
 * Türkçe bir kriter cümlesine çevrilip soruya eklenir.
 */
export function ZbDialog({ term, analysis, error, onCancel, onConfirm }: {
  term: string
  analysis: ZbAnalysis | null
  error: string | null
  onCancel: () => void
  onConfirm: (criteria: string, filters: FilterDefault[]) => void
}) {
  // Başlangıç: ana alan + önerilen ek alanlar, varsayılan değerlerle (analiz gelince bileşen key ile yeniden kurulur)
  const [active, setActive] = useState<string[]>(() =>
    analysis ? [...(analysis.primary ? [analysis.primary] : []), ...analysis.extras.filter(k => k !== analysis.primary)] : [])
  const [sel, setSel] = useState<Record<string, Selection>>(() =>
    Object.fromEntries((analysis?.defaults ?? []).map(d => [d.field, d.values.length ? { values: d.values } : { min: d.min, max: d.max }])))
  const box = useRef<HTMLDivElement>(null)
  // Pencerede seçim değiştikçe diğer alanların seçenekleri yeniden daraltılır (Şehir: Ankara → yalnızca Ankara şubeleri)
  const [live, setLive] = useState<{ key: string; fields: FilterField[]; matching: number } | null>(null)
  const chosen = useMemo(() => toDefaults(sel), [sel])
  const liveKey = JSON.stringify(chosen)
  useEffect(() => {
    if (!analysis || chosen.length === 0) return
    const t = setTimeout(() => {
      api.zbFacets(mergeContext(analysis.contextFilters, chosen))
        .then(r => setLive({ key: liveKey, fields: r.fields, matching: r.matching }))
        .catch(() => { /* sayılar güncellenemezse mevcut seçenekler kalır */ })
    }, 250)
    return () => clearTimeout(t)
  }, [analysis, chosen, liveKey])
  const current = chosen.length > 0 && live ? live : null

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') { e.preventDefault(); onCancel() } }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [onCancel])

  // Odak ana kriterin ilk seçeneğine (kapat düğmesine değil)
  useEffect(() => { if (analysis) box.current?.querySelector<HTMLElement>('[data-zb-body] button, [data-zb-body] input')?.focus() }, [analysis])

  const fields = useMemo(() => new Map((current?.fields ?? analysis?.fields ?? []).map(f => [f.key, f])), [analysis, current])
  const matching = current?.matching ?? analysis?.matching
  const stale = chosen.length > 0 && live?.key !== liveKey
  const shown = active.map(k => fields.get(k)).filter((f): f is FilterField => !!f)
  const criteria = analysis ? buildCriteria(shown, sel) : ''
  const update = (key: string, s: Selection) => setSel(prev => ({ ...prev, [key]: s }))
  const remove = (key: string) => {
    setActive(a => a.filter(k => k !== key))
    setSel(prev => Object.fromEntries(Object.entries(prev).filter(([k]) => k !== key)))
  }
  const addable = (analysis?.fields ?? []).filter(f => !active.includes(f.key))
  const primary = analysis?.primary ? fields.get(analysis.primary) : undefined

  return (
    <div ref={box} role="dialog" aria-modal="false" aria-labelledby="zb-title"
      className="absolute inset-x-0 bottom-full z-40 mb-3 overflow-hidden rounded-2xl border border-slate-200 bg-white shadow-2xl ring-1 ring-slate-900/5"
      onKeyDown={e => { if (e.key === 'Enter' && (e.ctrlKey || e.metaKey) && criteria) { e.preventDefault(); onConfirm(criteria, chosen) } }}>
      <div className="flex items-start justify-between gap-4 border-b border-slate-100 bg-gradient-to-r from-amber-50 via-white to-white px-5 py-4">
        <div className="min-w-0">
          <div className="flex items-center gap-2">
            <span className="rounded-md bg-amber-100 px-1.5 text-sm font-bold tracking-wide text-amber-900 ring-1 ring-amber-300">ZB</span>
            <span className="truncate font-semibold text-amber-800">{term}</span>
            {analysis && (
              <span className={`rounded-full px-2 py-0.5 text-[11px] font-medium ${analysis.source === 'llm' ? 'bg-indigo-50 text-indigo-700' : 'bg-slate-100 text-slate-600'}`}
                title={analysis.source === 'llm' ? 'Alanları dil modeli seçti' : 'Model kapalı; alanlar kelime eşleştirmesiyle seçildi'}>
                {analysis.source === 'llm' ? 'Model önerisi' : 'Kural tabanlı'}
              </span>
            )}
          </div>
          <h2 id="zb-title" className="mt-1.5 text-base font-semibold text-slate-900">{analysis?.title ?? 'İfade analiz ediliyor…'}</h2>
          {analysis && <p className="mt-0.5 text-sm text-slate-500">{analysis.description}</p>}
          {analysis && analysis.context.length > 0 && (
            <div className="mt-2.5 flex flex-wrap items-center gap-1.5 text-xs" title="Seçenekler sorudaki bu kriterlere göre daraltıldı">
              <span className="font-medium text-slate-500">Sorudaki kriterler:</span>
              {analysis.context.map(c => (
                <span key={c.field} className="inline-flex items-center gap-1 rounded-full bg-white px-2 py-0.5 text-slate-700 ring-1 ring-slate-200">
                  <FilterIcon /> <span className="text-slate-400">{c.label}:</span> {c.text}
                </span>
              ))}
              <span className="text-slate-400">· seçenekler bunlara göre daraltıldı</span>
            </div>
          )}
        </div>
        <button onClick={onCancel} aria-label="Kapat" className="rounded-md p-1.5 text-slate-400 hover:bg-slate-100 hover:text-slate-700">✕</button>
      </div>

      <div data-zb-body className="max-h-[48vh] space-y-4 overflow-y-auto px-5 py-4">
        {!analysis && !error && (
          <div className="space-y-3" aria-busy="true">
            <div className="flex items-center gap-2 text-sm text-slate-500">
              <span className="h-4 w-4 animate-spin rounded-full border-2 border-indigo-200 border-t-indigo-600" />
              “{term}” ifadesi dil modeliyle analiz ediliyor…
            </div>
            {[0, 1, 2].map(i => <div key={i} className="h-12 animate-pulse rounded-xl bg-slate-100" />)}
          </div>
        )}
        {error && <p className="rounded-lg bg-rose-50 px-3.5 py-2.5 text-sm text-rose-700">{error}</p>}

        {primary && (
          <section className="rounded-xl border border-indigo-200 bg-indigo-50/40 p-4">
            <FieldHeader field={primary} badge="Ana kriter" />
            <FieldControl field={primary} value={sel[primary.key]} onChange={s => update(primary.key, s)} large />
          </section>
        )}

        {analysis && (
          <section>
            <div className="mb-2 flex items-center justify-between">
              <h3 className="text-xs font-semibold uppercase tracking-wide text-slate-400">{primary ? 'Ek filtreler' : 'Filtreler'}</h3>
              {addable.length > 0 && (
                <label className="flex items-center gap-1.5 text-sm text-indigo-600">
                  <span aria-hidden="true">＋</span>
                  <select value="" onChange={e => e.target.value && setActive(a => [...a, e.target.value])}
                    className="cursor-pointer rounded-md bg-transparent py-1 pr-1 text-sm font-medium text-indigo-600 outline-none hover:text-indigo-800 focus-visible:ring-2 focus-visible:ring-indigo-200">
                    <option value="">Filtre ekle</option>
                    {addable.map(f => <option key={f.key} value={f.key}>{f.label}</option>)}
                  </select>
                </label>
              )}
            </div>
            <div className="grid gap-2.5 lg:grid-cols-2">
              {shown.filter(f => f.key !== primary?.key).map(f => (
                <div key={f.key} className="rounded-xl border border-slate-200 p-3">
                  <div className="flex items-start justify-between gap-2">
                    <FieldHeader field={f} />
                    <button onClick={() => remove(f.key)} aria-label={`${f.label} filtresini kaldır`} title="Kaldır"
                      className="-mr-1 -mt-1 rounded-md p-1 text-slate-300 hover:bg-slate-100 hover:text-slate-600">✕</button>
                  </div>
                  <FieldControl field={f} value={sel[f.key]} onChange={s => update(f.key, s)} />
                </div>
              ))}
            </div>
          </section>
        )}
      </div>

      {analysis && (
        <div className="border-t border-slate-100 bg-slate-50/70 px-5 py-3.5">
          <div className="mb-3 text-sm">
            <div className="flex items-baseline justify-between gap-2">
              <span className="text-xs font-semibold uppercase tracking-wide text-slate-400">Soruya eklenecek</span>
              {matching != null && (
                <span className={`text-xs tabular-nums transition-opacity ${stale ? 'opacity-50' : ''}`} aria-live="polite">
                  Bu kriterlere uyan <span className="font-semibold text-slate-700">{fmt(matching)}</span> müşteri
                </span>
              )}
            </div>
            <p className={`mt-1 rounded-lg px-3 py-2 ${criteria ? 'bg-indigo-50 text-indigo-900 ring-1 ring-indigo-100' : 'bg-white text-slate-400 ring-1 ring-slate-200'}`}>
              {criteria || 'Henüz kriter seçilmedi.'}
            </p>
          </div>
          <div className="flex items-center justify-end gap-2">
            <span className="mr-auto hidden text-xs text-slate-400 sm:block">Ctrl+Enter: Tamam · Esc: Vazgeç</span>
            <button onClick={onCancel} className="rounded-lg px-4 py-2 text-sm text-slate-600 hover:bg-slate-100">Vazgeç</button>
            <button onClick={() => onConfirm(criteria, chosen)} disabled={!criteria}
              className="rounded-lg bg-indigo-600 px-5 py-2 text-sm font-medium text-white hover:bg-indigo-700 disabled:cursor-not-allowed disabled:opacity-40">
              Tamam
            </button>
          </div>
        </div>
      )}
    </div>
  )
}

function FieldHeader({ field, badge }: { field: FilterField; badge?: string }) {
  return (
    <div className="mb-2.5 flex flex-wrap items-baseline gap-x-2">
      <span className="text-sm font-semibold text-slate-800">{field.label}</span>
      {badge && <span className="rounded-full bg-indigo-600 px-2 py-0.5 text-[10px] font-semibold uppercase tracking-wide text-white">{badge}</span>}
      {field.kind === 'range' && field.min != null && field.max != null && (
        <span className="text-xs text-slate-400">{fmt(field.min)}–{fmt(field.max)} {field.unit}</span>
      )}
    </div>
  )
}

const fmt = (v: number) => new Intl.NumberFormat('tr-TR', { maximumFractionDigits: 0 }).format(v)

function FieldControl({ field, value, onChange, large }: { field: FilterField; value: Selection | undefined; onChange: (s: Selection) => void; large?: boolean }) {
  if (field.kind === 'range') {
    const min = value?.min ?? null, max = value?.max ?? null
    const num = (s: string) => (s.trim() === '' ? null : Number(s.replace(/\./g, '').replace(',', '.')))
    return (
      <div className="space-y-2.5">
        {field.presets && field.presets.length > 0 && (
          <div className="flex flex-wrap gap-1.5">
            {field.presets.map(p => {
              const on = min === p.min && max === p.max
              return (
                <button key={p.label} type="button" onClick={() => onChange(on ? {} : { min: p.min, max: p.max })} aria-pressed={on}
                  className={`rounded-full px-3 py-1 text-sm transition-colors ${on ? 'bg-indigo-600 text-white' : 'border border-slate-200 bg-white text-slate-700 hover:border-indigo-300 hover:bg-indigo-50'}`}>
                  {p.label}
                </button>
              )
            })}
          </div>
        )}
        <div className="flex items-center gap-2">
          {(['min', 'max'] as const).map((k, i) => (
            <label key={k} className="flex flex-1 items-center gap-2">
              {i === 1 && <span className="text-slate-300">—</span>}
              <span className="sr-only">{k === 'min' ? 'En az' : 'En çok'}</span>
              <div className={`flex flex-1 items-center rounded-lg border border-slate-300 bg-white focus-within:border-indigo-500 focus-within:ring-2 focus-within:ring-indigo-100 ${large ? 'py-2' : 'py-1.5'}`}>
                <input inputMode="numeric" value={(k === 'min' ? min : max) ?? ''} placeholder={k === 'min' ? 'En az' : 'En çok'}
                  onChange={e => onChange({ min, max, [k]: num(e.target.value) })}
                  className="w-full min-w-0 bg-transparent px-3 text-sm tabular-nums outline-none" />
                {field.unit && <span className="pr-3 text-xs text-slate-400">{field.unit}</span>}
              </div>
            </label>
          ))}
        </div>
      </div>
    )
  }
  const selected = value?.values ?? []
  const toggle = (v: string) => onChange({
    values: field.kind === 'single' ? (selected.includes(v) ? [] : [v]) : selected.includes(v) ? selected.filter(x => x !== v) : [...selected, v],
  })
  return (
    <div className="flex flex-wrap gap-1.5" role={field.kind === 'single' ? 'radiogroup' : 'group'} aria-label={field.label}>
      {[...(field.values ?? []), ...selected.filter(v => !(field.values ?? []).includes(v))].map(v => {
        const on = selected.includes(v)
        return (
          <button key={v} type="button" onClick={() => toggle(v)} aria-pressed={on}
            className={`rounded-full px-3 py-1 text-sm transition-colors ${on ? 'bg-indigo-600 text-white shadow-sm' : 'border border-slate-200 bg-white text-slate-700 hover:border-indigo-300 hover:bg-indigo-50'}`}>
            {on && <span aria-hidden="true">✓ </span>}{v}
            {field.counts && <span className={`ml-1.5 text-xs tabular-nums ${on ? 'text-indigo-200' : 'text-slate-400'}`}>{fmt(field.counts[v] ?? 0)}</span>}
          </button>
        )
      })}
    </div>
  )
}

const FilterIcon = () => (
  <svg width={11} height={11} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth={2.2} strokeLinecap="round" strokeLinejoin="round" aria-hidden="true" className="text-indigo-500">
    <path d="M3 5h18l-7 8v6l-4 2v-8z" />
  </svg>
)
