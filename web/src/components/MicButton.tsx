import { useEffect, useRef, useState } from 'react'
import { api } from '../api'
import { EmptyRecordingError, startRecording, type Recording } from '../recorder'
import { MicIcon, StopIcon } from './icons'

const MAX_SECONDS = 30

interface Props {
  available: boolean
  disabled: boolean
  onText: (text: string) => void
  onError: (message: string) => void
}

/** Bas-konuş-bas: kayıt WAV olarak /api/speech'e gider, tanınan metin soru kutusuna yazılır (otomatik gönderilmez). */
export function MicButton({ available, disabled, onText, onError }: Props) {
  const [state, setState] = useState<'idle' | 'recording' | 'transcribing'>('idle')
  const [seconds, setSeconds] = useState(0)
  const recording = useRef<Recording | null>(null)

  useEffect(() => () => recording.current?.cancel(), [])

  const stop = async () => {
    const rec = recording.current
    if (!rec) return
    recording.current = null
    setState('transcribing')
    try {
      const { text } = await api.transcribe(await rec.stop())
      if (text) onText(text)
      else onError('Konuşma anlaşılamadı, tekrar deneyin.')
    } catch (e) {
      onError(e instanceof Error ? e.message : 'Ses tanınamadı.')
    } finally {
      setState('idle')
    }
  }

  useEffect(() => {
    if (state !== 'recording') return
    const t = setInterval(() => setSeconds(s => s + 1), 1000)
    return () => clearInterval(t)
  }, [state])

  useEffect(() => {
    if (state === 'recording' && seconds >= MAX_SECONDS) void stop()
  }, [seconds, state]) // eslint-disable-line react-hooks/exhaustive-deps -- yalnızca süre dolunca

  const start = async () => {
    try {
      recording.current = await startRecording()
      setSeconds(0)
      setState('recording')
    } catch (e) {
      onError(e instanceof EmptyRecordingError ? e.message : 'Mikrofona erişilemedi. Tarayıcının mikrofon iznini kontrol edin (Claude uygulamasının içindeki tarayıcı mikrofonu engeller; Chrome/Edge/Safari kullanın).')
    }
  }

  if (state === 'recording') {
    return (
      <button type="button" onClick={stop} aria-label="Kaydı bitir" title="Kaydı bitir"
        className="flex items-center gap-1.5 rounded-xl bg-rose-600 px-3 text-white hover:bg-rose-700">
        <span className="h-2 w-2 animate-pulse rounded-full bg-white" />
        <span className="tabular-nums text-sm">{seconds} sn</span>
        <StopIcon />
      </button>
    )
  }

  const busy = state === 'transcribing'
  return (
    <button type="button" onClick={start} disabled={disabled || busy || !available}
      aria-label="Sesle sor" title={available ? 'Sesle sor (konuşun, bitince tekrar basın)' : 'Ses tanıma servisi kapalı'}
      className={`flex w-12 items-center justify-center rounded-xl border ${busy ? 'border-indigo-200 bg-indigo-50 text-indigo-500' : 'border-slate-300 text-slate-600 hover:border-indigo-300 hover:bg-indigo-50 hover:text-indigo-700'} disabled:cursor-not-allowed disabled:opacity-40 disabled:hover:bg-transparent`}>
      {busy ? <span className="h-4 w-4 animate-spin rounded-full border-2 border-indigo-300 border-t-indigo-600" /> : <MicIcon />}
    </button>
  )
}
