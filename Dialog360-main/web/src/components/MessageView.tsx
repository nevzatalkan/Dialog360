import type { ChatResponse } from '../api'
import { ReportCard, type DrillBackHandler, type DrillHandler } from './ReportCard'
import { SqlBlock } from './SqlPanel'
import { fullTime, messageTime } from '../format-date'

export const MANUAL_SQL_PREFIX = '[Elle düzenlenen SQL]\n'
/** Rapordaki bir değere tıklanarak istenen detay adımları ("Marmara → Şehirlere in", "↑ Bölgelere çık"). */
export const DRILL_PREFIX = '[Detay] '

export function UserMessage({ text, createdAt }: { text: string; createdAt?: string }) {
  const when = createdAt && <time dateTime={createdAt.replace(' ', 'T')} title={fullTime(createdAt)}>{messageTime(createdAt)}</time>
  if (text.startsWith(DRILL_PREFIX)) {
    const step = text.slice(DRILL_PREFIX.length)
    return (
      <div className="ml-auto flex max-w-[80%] flex-col items-end">
        {when && <div className="mb-1 text-xs text-slate-400">{when}</div>}
        <div className="relative flex items-center gap-1.5 rounded-full border border-indigo-200 bg-indigo-50 px-3 py-1.5 text-sm text-indigo-800">
          <span className="text-indigo-400" aria-hidden="true">{step.startsWith('↑') || step.startsWith('↩') ? '' : '↓'}</span>
          <span className="sr-only">Detay: </span>{step}
        </div>
      </div>
    )
  }
  if (text.startsWith(MANUAL_SQL_PREFIX)) {
    return (
      <div className="ml-auto max-w-[80%]">
        <div className="mb-1 text-right text-xs text-slate-400">Elle düzenlenen SQL{when && <>, {when}</>}</div>
        <SqlBlock sql={text.slice(MANUAL_SQL_PREFIX.length)} />
      </div>
    )
  }
  return (
    <div className="ml-auto flex max-w-[80%] flex-col items-end">
      {when && <div className="mb-1 text-xs text-slate-400">{when}</div>}
      <div className="rounded-2xl rounded-br-md bg-indigo-600 px-4 py-2.5 text-white shadow-sm">{text}</div>
    </div>
  )
}

interface Props {
  response: ChatResponse
  isLast: boolean
  onSend: (text: string) => void
  onRunSql: (sql: string) => void
  onDrill?: DrillHandler
  onDrillBack?: DrillBackHandler
  busy?: boolean
}

export function AssistantMessage({ response, isLast, onSend, onRunSql, onDrill, onDrillBack, busy }: Props) {
  switch (response.kind) {
    case 'report':
      return <ReportCard response={response} onRunSql={onRunSql} onDrill={onDrill} onDrillBack={onDrillBack} busy={busy} />

    case 'clarify':
      return (
        <div className="max-w-2xl rounded-2xl border border-indigo-100 bg-white p-5 shadow-sm">
          <div className="mb-1 text-xs font-semibold uppercase tracking-wide text-indigo-500">Netleştirme</div>
          <p className="text-slate-800">{response.clarify!.question}</p>
          <div className="mt-3 flex flex-wrap gap-2">
            {response.clarify!.options.map(o => (
              <button key={o} disabled={!isLast} onClick={() => onSend(o)}
                className="rounded-full border border-indigo-200 px-3.5 py-1.5 text-sm text-indigo-700 hover:bg-indigo-50 disabled:cursor-default disabled:opacity-50 disabled:hover:bg-transparent">
                {o}
              </button>
            ))}
          </div>
        </div>
      )

    case 'blocked':
      return (
        <div className="max-w-2xl rounded-2xl border border-rose-200 bg-rose-50/60 p-5">
          <div className="flex items-center gap-2 text-sm font-semibold text-rose-700">
            <span className="flex h-5 w-5 items-center justify-center rounded-full bg-rose-600 text-[11px] text-white">✕</span>
            {response.title}
          </div>
          <p className="mt-2 text-sm text-rose-900">{response.text}</p>
          {response.blocked?.sql && <div className="mt-3"><SqlBlock sql={response.blocked.sql} /></div>}
          <div className="mt-3 text-xs text-rose-700/80">
            Durduran katman: <span className="font-medium">{response.blocked?.layer}</span>.
            Veritabanı bağlantısı zaten salt okunur; LLM ne üretirse üretsin veri değiştirilemez.
          </div>
        </div>
      )

    default:
      return (
        <div className="max-w-2xl rounded-2xl border border-amber-200 bg-amber-50/60 p-5">
          <div className="text-sm font-semibold text-amber-800">{response.title}</div>
          <p className="mt-1 text-sm text-amber-900">{response.text}</p>
          {response.meta.corrections.length > 0 && (
            <div className="mt-2 text-xs text-amber-700">Son hata: {response.meta.corrections.at(-1)!.error}</div>
          )}
        </div>
      )
  }
}
