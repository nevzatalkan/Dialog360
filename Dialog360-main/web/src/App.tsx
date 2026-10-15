import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { api, chats as chatsApi, type ChatSummary, type HistoryItem } from './api'
import { HistorySidebar, MenuIcon } from './components/HistorySidebar'
import { nowLocal } from './format-date'
import { AssistantMessage, DRILL_PREFIX, MANUAL_SQL_PREFIX, UserMessage } from './components/MessageView'
import type { DrillBackHandler, DrillHandler } from './components/ReportCard'
import { ZbDialog } from './components/ZbDialog'
import { ZbEditor, type ZbEditorHandle } from './components/ZbEditor'
import { applyCriteria, findMarkers, mergeContext } from './zb'
import type { FilterDefault, ZbAnalysis } from './api'
import { SchemaPanel } from './components/SchemaPanel'

const SUGGESTIONS = [
  'Geçen ay sektörlere göre toplam kart harcaması',
  'En iyi müşterilerimiz kimler?',
  'Son 90 günde hiç işlem yapmayan Platinum kart sahibi sayısı',
  'Alsancak şubesinin son 6 aylık harcama trendi',
  'Yıllara göre e-ticaret harcaması',
  'Bölgelere göre aktif TL mevduat toplamı',
]

const STORAGE_KEY = 'diyalog360.chatId'
const SIDEBAR_KEY = 'diyalog360.sidebar'
const isDesktop = () => window.matchMedia('(min-width: 1024px)').matches

function readSidebarPref(): boolean {
  try {
    const v = localStorage.getItem(SIDEBAR_KEY)
    return v === null ? isDesktop() : v === '1' && isDesktop()
  } catch { return isDesktop() }
}

export default function App({ username, onLogout }: { username: string; onLogout: () => void }) {
  const [chatId, setChatId] = useState<string | null>(() => localStorage.getItem(STORAGE_KEY))
  const [items, setItems] = useState<HistoryItem[]>([])
  const [input, setInput] = useState('')
  // ZB: "ZB <ifade>" yazılınca kriter penceresi açılır; seçimler kriter cümlesi olarak soruya eklenir
  // Soruya eklenmiş kriter cümleleri ve seçimleri; sonraki ZB pencereleri bu seçimlere göre daraltılır (Ankara → Ankara şubeleri)
  const [criteria, setCriteria] = useState<{ text: string; filters: FilterDefault[] }[]>([])
  const [zb, setZb] = useState<{ index: number; term: string; analysis: ZbAnalysis | null; error: string | null } | null>(null)
  const zbDismissed = useRef<string | null>(null)
  const zbTimer = useRef<ReturnType<typeof setTimeout> | null>(null)
  const zbRequest = useRef(0)
  const editor = useRef<ZbEditorHandle>(null)
  const [pending, setPending] = useState<{ text: string; started: number } | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [health, setHealth] = useState<{ llm: boolean; model: string } | null>(null)
  const [showSchema, setShowSchema] = useState(false)
  const bottomRef = useRef<HTMLDivElement>(null)
  const [sidebarOpen, setSidebarOpen] = useState(readSidebarPref)
  const [chatList, setChatList] = useState<ChatSummary[]>([])
  const [chatsLoading, setChatsLoading] = useState(true)
  const [search, setSearch] = useState('')
  const searchRef = useRef('')

  const loadChats = useCallback(async () => {
    try { setChatList(await chatsApi.list(searchRef.current.trim() || undefined)) }
    catch { /* liste yüklenemezse mevcut liste kalır */ }
    finally { setChatsLoading(false) }
  }, [])

  // Aramada her tuşta değil, yazmaya ara verince sorgula
  useEffect(() => {
    searchRef.current = search
    const t = setTimeout(loadChats, search ? 250 : 0)
    return () => clearTimeout(t)
  }, [search, loadChats])

  const toggleSidebar = () => setSidebarOpen(o => {
    const next = !o
    if (isDesktop()) { try { localStorage.setItem(SIDEBAR_KEY, next ? '1' : '0') } catch { /* tercih saklanamazsa önemli değil */ } }
    return next
  })
  const closeSidebarOnMobile = () => { if (!isDesktop()) setSidebarOpen(false) }

  useEffect(() => {
    if (chatId) api.history(chatId).then(setItems).catch(() => setItems([]))
  }, []) // eslint-disable-line react-hooks/exhaustive-deps -- yalnızca açılışta geçmişi yükle

  useEffect(() => {
    const check = () => api.health().then(setHealth).catch(() => setHealth(null))
    check()
    const t = setInterval(check, 15000)
    return () => clearInterval(t)
  }, [])

  useEffect(() => { bottomRef.current?.scrollIntoView({ behavior: 'smooth' }) }, [items, pending])

  const submit = useCallback(async (userText: string, call: () => ReturnType<typeof api.chat>) => {
    setError(null)
    setItems(prev => [...prev, { role: 'user', text: userText, createdAt: nowLocal() }])
    setPending({ text: userText, started: Date.now() })
    try {
      const response = await call()
      setChatId(response.chatId)
      localStorage.setItem(STORAGE_KEY, response.chatId)
      setItems(prev => [...prev, { role: 'assistant', response }])
      loadChats()
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Beklenmeyen hata')
    } finally {
      setPending(null)
    }
  }, [loadChats])

  const openZb = useCallback((index: number, text: string) => {
    const marker = findMarkers(text)[index]
    if (!marker || !marker.term) return
    if (zbTimer.current) clearTimeout(zbTimer.current)
    const request = ++zbRequest.current
    setZb({ index, term: marker.term, analysis: null, error: null })
    const context = criteria.filter(c => text.includes(c.text)).flatMap(c => c.filters)
    api.zbAnalyze(marker.term, text, context)
      .then(analysis => { if (zbRequest.current === request) setZb(z => z && { ...z, analysis }) })
      .catch(e => { if (zbRequest.current === request) setZb(z => z && { ...z, error: e instanceof Error ? e.message : 'İfade analiz edilemedi' }) })
  }, [criteria])

  // Yazmaya ara verilince, imleç son ZB ifadesinin sonundaysa pencereyi kendiliğinden aç
  const onInputChange = useCallback((text: string, caret: number | null) => {
    setInput(text)
    if (zbTimer.current) clearTimeout(zbTimer.current)
    const markers = findMarkers(text)
    const lastMarker = markers.at(-1)
    if (!lastMarker || lastMarker.term.length < 2 || caret !== text.trimEnd().length && caret !== text.length) return
    if (zbDismissed.current === lastMarker.term) return
    zbTimer.current = setTimeout(() => openZb(lastMarker.index, text), 1100)
  }, [openZb])

  const closeZb = useCallback(() => {
    zbRequest.current++
    setZb(z => { if (z) zbDismissed.current = z.term; return null })
    setTimeout(() => editor.current?.focus(), 0)
  }, [])

  const confirmZb = (text: string, filters: FilterDefault[]) => {
    if (!zb?.analysis) return
    const marker = findMarkers(input)[zb.index]
    if (!marker) { setZb(null); return }
    const result = applyCriteria(input, marker, zb.analysis.phrase, text, criteria.map(c => c.text))
    setCriteria(c => {
      const old = c.find(x => x.text === result.replaced)
      return [...c.filter(x => x !== old), { text: result.inserted, filters: mergeContext(old?.filters ?? [], filters) }]
    })
    setInput(result.text)
    zbDismissed.current = null
    setZb(null)
    setTimeout(() => editor.current?.focus(), 0)
  }

  const send = (text: string) => {
    if (pending || zb) return
    const markers = findMarkers(text)
    // Netleştirilmemiş ZB varsa önce pencereyi aç; kullanıcı vazgeçtiyse ZB işaretini atıp soruyu olduğu gibi gönder
    if (markers.length > 0 && zbDismissed.current !== markers[0].term && markers[0].term) { openZb(0, text); return }
    const message = text.replace(/(^|\s)zb(?=\s)/gi, '$1').replace(/\s+/g, ' ').trim()
    if (!message) return
    setInput('')
    setCriteria([])
    zbDismissed.current = null
    submit(message, () => api.chat(chatId, message))
  }

  const runSql = (sql: string) => {
    if (pending) return
    submit(MANUAL_SQL_PREFIX + sql, () => api.runSql(chatId, sql))
  }

  // Drill down / across / up: rapordaki bir değere tıklanınca o raporun SQL'i detaylandırılır
  const drill: DrillHandler = (response, column, value, target, label) => {
    if (pending) return
    submit(DRILL_PREFIX + label, () => api.drill(response.chatId, response.messageId, column, value, target.key))
  }

  const drillBack: DrillBackHandler = (messageId, label) => {
    if (pending || !chatId) return
    submit(DRILL_PREFIX + '↩ ' + label, () => api.drillBack(chatId, messageId))
  }

  const newChat = () => {
    if (pending) return
    localStorage.removeItem(STORAGE_KEY)
    setChatId(null)
    setItems([])
    setError(null)
    closeSidebarOnMobile()
  }

  const openChat = async (id: string) => {
    if (pending) return
    closeSidebarOnMobile()
    if (id === chatId) return
    setError(null)
    try {
      const history = await api.history(id)
      setChatId(id)
      localStorage.setItem(STORAGE_KEY, id)
      setItems(history)
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Sohbet açılamadı')
      loadChats()
    }
  }

  const renameChat = async (id: string, title: string) => {
    try { await chatsApi.rename(id, title) } catch (e) { setError(e instanceof Error ? e.message : 'Sohbet yeniden adlandırılamadı') }
    loadChats()
  }

  const deleteChat = async (id: string) => {
    try {
      await chatsApi.remove(id)
      if (id === chatId) { localStorage.removeItem(STORAGE_KEY); setChatId(null); setItems([]) }
    } catch (e) { setError(e instanceof Error ? e.message : 'Sohbet silinemedi') }
    loadChats()
  }

  const criteriaTexts = useMemo(() => criteria.map(c => c.text), [criteria])
  const lastAssistant = items.findLastIndex(i => i.role === 'assistant')

  return (
    <div className="flex h-full">
      <HistorySidebar open={sidebarOpen} chats={chatList} loading={chatsLoading} activeId={chatId} busy={!!pending}
        search={search} onSearch={setSearch} onSelect={openChat} onNew={newChat} onRename={renameChat} onDelete={deleteChat}
        onClose={() => setSidebarOpen(false)} />
      <div className="flex min-w-0 flex-1 flex-col">
        <header className="flex items-center justify-between border-b border-slate-200 bg-white px-4 py-3 sm:px-6">
          <div className="flex items-center gap-3">
            <button onClick={toggleSidebar} aria-label={sidebarOpen ? 'Sohbet geçmişini kapat' : 'Sohbet geçmişini aç'}
              aria-expanded={sidebarOpen} aria-controls="history-sidebar" title="Sohbet geçmişi"
              className="-ml-1 rounded-lg p-2 text-slate-600 hover:bg-slate-100 focus-visible:outline-2 focus-visible:outline-indigo-600">
              <MenuIcon />
            </button>
            <div className="flex h-9 w-9 items-center justify-center rounded-xl bg-indigo-600 text-sm font-bold text-white">360</div>
            <div>
              <div className="font-semibold leading-tight text-slate-900">Diyalog 360</div>
              <div className="text-xs text-slate-500">Türkçe sorun, raporunuz gelsin</div>
            </div>
          </div>
          <div className="flex items-center gap-2 text-sm">
            <span className={`flex items-center gap-1.5 rounded-full px-2.5 py-1 text-xs ${health?.llm ? 'bg-emerald-50 text-emerald-700' : 'bg-rose-50 text-rose-700'}`}
              title={health?.model}>
              <span className={`h-1.5 w-1.5 rounded-full ${health?.llm ? 'bg-emerald-500' : 'bg-rose-500'}`} />
              {health?.llm ? `Yerel model hazır` : health ? 'Model kapalı · yalnızca önbellek' : 'API bağlantısı yok'}
            </span>
            <button onClick={() => setShowSchema(s => !s)} className="rounded-lg px-3 py-1.5 text-slate-600 hover:bg-slate-100">Veri kataloğu</button>
            <button onClick={newChat} className="hidden rounded-lg border border-slate-200 px-3 py-1.5 text-slate-700 hover:bg-slate-50 sm:inline-block">Yeni sohbet</button>
            <div className="ml-2 flex items-center gap-2 border-l border-slate-200 pl-4">
              <span className="flex h-8 w-8 items-center justify-center rounded-full bg-slate-100 text-sm font-semibold uppercase text-slate-600" aria-hidden="true">
                {username.slice(0, 1)}
              </span>
              <span className="hidden text-slate-700 sm:inline">{username}</span>
              <button onClick={onLogout} className="rounded-lg px-3 py-1.5 text-slate-600 hover:bg-slate-100">Çıkış yap</button>
            </div>
          </div>
        </header>

        <main className="flex-1 overflow-y-auto">
          <div className="mx-auto max-w-5xl space-y-5 px-6 py-6">
            {items.length === 0 && !pending && (
              <div className="py-16 text-center">
                <h1 className="text-2xl font-semibold text-slate-900">Hangi raporu görmek istersiniz?</h1>
                <p className="mx-auto mt-2 max-w-xl text-slate-500">
                  Sorunuzu Türkçe yazın. Diyalog 360 veritabanı şemasına bakarak SQL'i yazar, güvenle çalıştırır ve sonucu grafik ve özetle sunar.
                  Takip sorularıyla raporu daraltabilirsiniz.
                </p>
                <div className="mx-auto mt-8 grid max-w-3xl gap-2 sm:grid-cols-2">
                  {SUGGESTIONS.map(s => (
                    <button key={s} onClick={() => send(s)}
                      className="rounded-xl border border-slate-200 bg-white px-4 py-3 text-left text-sm text-slate-700 shadow-sm hover:border-indigo-300 hover:bg-indigo-50/40">
                      {s}
                    </button>
                  ))}
                </div>
              </div>
            )}

            {items.map((item, i) => item.role === 'user'
              ? <UserMessage key={i} text={item.text} createdAt={item.createdAt} />
              : <AssistantMessage key={i} response={item.response} isLast={i === lastAssistant && !pending} onSend={send} onRunSql={runSql}
                  onDrill={drill} onDrillBack={drillBack} busy={!!pending} />)}

            {pending && <Thinking started={pending.started} mode={pending.text.startsWith(MANUAL_SQL_PREFIX) ? 'manual' : pending.text.startsWith(DRILL_PREFIX) ? 'drill' : 'chat'} />}
            {error && <div className="rounded-xl border border-rose-200 bg-rose-50 px-4 py-3 text-sm text-rose-700">{error}</div>}
            <div ref={bottomRef} />
          </div>
        </main>

        <footer className="border-t border-slate-200 bg-white px-6 py-4">
          <form onSubmit={e => { e.preventDefault(); send(input) }} className="relative mx-auto flex max-w-5xl items-end gap-2">
            {zb && <ZbDialog key={`${zb.term}-${zb.analysis ? 'hazir' : 'yukleniyor'}`} term={zb.term} analysis={zb.analysis} error={zb.error} onCancel={closeZb} onConfirm={confirmZb} />}
            <ZbEditor ref={editor} value={input} criteria={criteriaTexts} onChange={onInputChange} onEnter={() => send(input)}
              onZbClick={i => { zbDismissed.current = null; openZb(i, input) }} disabled={!!pending}
              placeholder={items.length ? 'Takip sorusu sorun: "Bunu şubelere göre kır", "Sadece Premium müşteriler"…' : 'Örn. Geçen ay sektörlere göre toplam kart harcaması'} />
            <button disabled={!!pending || !input.trim()} className="h-[50px] rounded-xl bg-indigo-600 px-5 font-medium text-white hover:bg-indigo-700 disabled:opacity-40">
              Sor
            </button>
          </form>
          <p className="mx-auto mt-2 max-w-5xl text-xs text-slate-400">
            Kriter seçmek için <span className="rounded bg-amber-100 px-1 font-bold text-amber-900 ring-1 ring-amber-300">ZB</span> yazıp bir ifade ekleyin:
            <span className="text-slate-500"> ZB yaş</span>, <span className="text-slate-500">ZB genç kadın</span>, <span className="text-slate-500">ZB kart limiti</span>.
          </p>
        </footer>
      </div>
      {showSchema && <SchemaPanel onClose={() => setShowSchema(false)} />}
    </div>
  )
}

function Thinking({ started, mode }: { started: number; mode: 'chat' | 'manual' | 'drill' }) {
  const [now, setNow] = useState(() => Date.now())
  useEffect(() => { const t = setInterval(() => setNow(Date.now()), 250); return () => clearInterval(t) }, [])
  const seconds = Math.floor((now - started) / 1000)
  const step = mode === 'manual' ? 'Sorgu doğrulanıp çalıştırılıyor'
    : mode === 'drill' ? (seconds < 3 ? 'Rapor detaylandırılıyor' : 'Sorgu karmaşık; detay dil modeliyle hazırlanıyor')
    : seconds < 2 ? 'Soru ve sohbet bağlamı hazırlanıyor' : 'Yerel model soruyu SQL\'e çeviriyor'
  return (
    <div className="flex items-center gap-3 rounded-2xl border border-slate-200 bg-white px-5 py-4 text-sm text-slate-600 shadow-sm">
      <span className="flex gap-1">
        {[0, 150, 300].map(d => <span key={d} className="h-2 w-2 animate-bounce rounded-full bg-indigo-400" style={{ animationDelay: `${d}ms` }} />)}
      </span>
      {step}… <span className="tabular-nums text-slate-400">{seconds} sn</span>
    </div>
  )
}
