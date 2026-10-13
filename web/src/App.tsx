import { useCallback, useEffect, useRef, useState } from 'react'
import { api, auth, UNAUTHORIZED_EVENT, type Health, type HistoryItem } from './api'
import { HistoryDrawer } from './components/HistoryDrawer'
import { LogoutIcon, MenuIcon } from './components/icons'
import { MicButton } from './components/MicButton'
import { Login } from './components/Login'
import { AssistantMessage, MANUAL_SQL_PREFIX, UserMessage } from './components/MessageView'
import { SchemaPanel } from './components/SchemaPanel'
import { getActiveId, loadConversations, removeConversation, saveConversation, setActiveId, type Conversation } from './history'

const SUGGESTIONS = [
  'Geçen ay sektörlere göre toplam kart harcaması',
  'En iyi müşterilerimiz kimler?',
  'Son 90 günde hiç işlem yapmayan Platinum kart sahibi sayısı',
  'Alsancak şubesinin son 6 aylık harcama trendi',
  'Yıllara göre e-ticaret harcaması',
  'Bölgelere göre aktif TL mevduat toplamı',
]

type AuthState = { status: 'loading' } | { status: 'anonymous'; configured: boolean } | { status: 'signed-in'; username: string }

/** Giriş kapısı: oturum yoksa giriş ekranı, varsa uygulama. Herhangi bir API çağrısı 401 alırsa giriş ekranına dönülür. */
export default function App() {
  const [state, setState] = useState<AuthState>({ status: 'loading' })

  useEffect(() => {
    auth.me()
      .then(r => setState('username' in r ? { status: 'signed-in', username: r.username } : { status: 'anonymous', configured: r.configured }))
      .catch(() => setState({ status: 'anonymous', configured: true }))
    const onUnauthorized = () => setState({ status: 'anonymous', configured: true })
    window.addEventListener(UNAUTHORIZED_EVENT, onUnauthorized)
    return () => window.removeEventListener(UNAUTHORIZED_EVENT, onUnauthorized)
  }, [])

  const logout = async () => {
    await auth.logout().catch(() => undefined)
    setState({ status: 'anonymous', configured: true })
  }

  if (state.status === 'loading') return <div className="flex h-full items-center justify-center text-sm text-slate-400">Yükleniyor…</div>
  if (state.status === 'anonymous') return <Login configured={state.configured} onLogin={username => setState({ status: 'signed-in', username })} />
  return <ChatApp username={state.username} onLogout={logout} />
}

function ChatApp({ username, onLogout }: { username: string; onLogout: () => void }) {
  const [chatId, setChatId] = useState<string | null>(getActiveId)
  const [conversations, setConversations] = useState<Conversation[]>(loadConversations)
  const [items, setItems] = useState<HistoryItem[]>(() => conversations.find(c => c.id === chatId)?.items ?? [])
  const [drawerOpen, setDrawerOpen] = useState(false)
  const needsSave = useRef(false)
  const [input, setInput] = useState('')
  const [pending, setPending] = useState<{ text: string; started: number } | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [health, setHealth] = useState<Health | null>(null)
  const inputRef = useRef<HTMLInputElement>(null)
  const [showSchema, setShowSchema] = useState(false)
  const bottomRef = useRef<HTMLDivElement>(null)

  /** Yerel kopya anında gösterilir; tam sonuçlar (satır sınırı olmadan) sunucudan tazelenir. */
  const refreshFromServer = useCallback((id: string) => {
    api.history(id).then(server => { if (server.length > 0) setItems(server) }).catch(() => undefined)
  }, [])

  useEffect(() => {
    if (chatId) refreshFromServer(chatId)
  }, []) // eslint-disable-line react-hooks/exhaustive-deps -- yalnızca açılışta

  // Yeni mesajlar geldikçe konuşmayı "Geçmiş Sorgularım"a kaydet (yalnızca açmak sıralamayı değiştirmesin)
  useEffect(() => {
    if (needsSave.current && chatId && items.length > 0) {
      needsSave.current = false
      setConversations(saveConversation(chatId, items))
    }
  }, [chatId, items])

  useEffect(() => {
    const check = () => api.health().then(setHealth).catch(() => setHealth(null))
    check()
    const t = setInterval(check, 15000)
    return () => clearInterval(t)
  }, [])

  useEffect(() => { bottomRef.current?.scrollIntoView({ behavior: 'smooth' }) }, [items, pending])

  const submit = useCallback(async (userText: string, call: () => ReturnType<typeof api.chat>) => {
    setError(null)
    setItems(prev => [...prev, { role: 'user', text: userText }])
    setPending({ text: userText, started: Date.now() })
    try {
      const response = await call()
      needsSave.current = true
      setChatId(response.chatId)
      setActiveId(response.chatId)
      setItems(prev => [...prev, { role: 'assistant', response }])
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Beklenmeyen hata')
    } finally {
      setPending(null)
    }
  }, [])

  const send = (text: string) => {
    const message = text.trim()
    if (!message || pending) return
    setInput('')
    submit(message, () => api.chat(chatId, message))
  }

  const runSql = (sql: string) => {
    if (pending) return
    submit(MANUAL_SQL_PREFIX + sql, () => api.runSql(chatId, sql))
  }

  const newChat = () => {
    if (pending) return
    setActiveId(null)
    setChatId(null)
    setItems([])
    setError(null)
    setDrawerOpen(false)
  }

  const openConversation = (c: Conversation) => {
    if (pending) return
    setActiveId(c.id)
    setChatId(c.id)
    setItems(c.items)
    setError(null)
    setDrawerOpen(false)
    refreshFromServer(c.id)
  }

  const deleteConversation = async (c: Conversation) => {
    try {
      await api.deleteChat(c.id)
    } catch {
      setError('Konuşma bu tarayıcıdan silindi ancak sunucudaki kaydı silinemedi.')
    }
    setConversations(removeConversation(c.id))
    if (c.id === chatId) {
      setChatId(null)
      setItems([])
    }
  }

  const lastAssistant = items.findLastIndex(i => i.role === 'assistant')

  return (
    <div className="flex h-full">
      <div className="flex min-w-0 flex-1 flex-col">
        <header className="flex items-center justify-between border-b border-slate-200 bg-white px-4 py-3">
          <div className="flex items-center gap-3">
            <button onClick={() => setDrawerOpen(true)} aria-label="Geçmiş Sorgularım menüsünü aç" title="Geçmiş Sorgularım"
              className="rounded-lg p-2 text-slate-600 hover:bg-slate-100"><MenuIcon /></button>
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
              {health?.llm ? 'Model hazır' : health ? 'Model kapalı · yalnızca önbellek' : 'API bağlantısı yok'}
            </span>
            <button onClick={() => setShowSchema(s => !s)} className="rounded-lg px-3 py-1.5 text-slate-600 hover:bg-slate-100">Veri kataloğu</button>
            <button onClick={newChat} className="rounded-lg border border-slate-200 px-3 py-1.5 text-slate-700 hover:bg-slate-50">Yeni sohbet</button>
            <div className="ml-2 flex items-center gap-2 border-l border-slate-200 pl-3">
              <span className="text-slate-500" title="Oturum açık">{username}</span>
              <button onClick={onLogout} className="flex items-center gap-1.5 rounded-lg px-2.5 py-1.5 text-slate-600 hover:bg-rose-50 hover:text-rose-700">
                <LogoutIcon /> Çıkış
              </button>
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
              ? <UserMessage key={i} text={item.text} />
              : <AssistantMessage key={i} response={item.response} isLast={i === lastAssistant && !pending} onSend={send} onRunSql={runSql} />)}

            {pending && <Thinking started={pending.started} manual={pending.text.startsWith(MANUAL_SQL_PREFIX)} />}
            {error && <div className="rounded-xl border border-rose-200 bg-rose-50 px-4 py-3 text-sm text-rose-700">{error}</div>}
            <div ref={bottomRef} />
          </div>
        </main>

        <footer className="border-t border-slate-200 bg-white px-6 py-4">
          <form onSubmit={e => { e.preventDefault(); send(input) }} className="mx-auto flex max-w-5xl gap-2">
            <input ref={inputRef} value={input} onChange={e => setInput(e.target.value)} disabled={!!pending} autoFocus
              placeholder={items.length ? 'Takip sorusu sorun: "Bunu şubelere göre kır", "Sadece Premium müşteriler"…' : 'Örn. Geçen ay sektörlere göre toplam kart harcaması'}
              className="flex-1 rounded-xl border border-slate-300 px-4 py-3 outline-none focus:border-indigo-500 focus:ring-2 focus:ring-indigo-100 disabled:bg-slate-50" />
            <MicButton available={!!health?.speech} disabled={!!pending}
              onText={text => { setError(null); setInput(prev => (prev.trim() ? `${prev.trim()} ${text}` : text)); inputRef.current?.focus() }}
              onError={setError} />
            <button disabled={!!pending || !input.trim()} className="rounded-xl bg-indigo-600 px-5 font-medium text-white hover:bg-indigo-700 disabled:opacity-40">
              Sor
            </button>
          </form>
        </footer>
      </div>
      {showSchema && <SchemaPanel onClose={() => setShowSchema(false)} />}
      <HistoryDrawer open={drawerOpen} conversations={conversations} activeId={chatId}
        onSelect={openConversation} onDelete={deleteConversation} onNew={newChat} onClose={() => setDrawerOpen(false)} />
    </div>
  )
}

function Thinking({ started, manual }: { started: number; manual: boolean }) {
  const [now, setNow] = useState(() => Date.now())
  useEffect(() => { const t = setInterval(() => setNow(Date.now()), 250); return () => clearInterval(t) }, [])
  const seconds = Math.floor((now - started) / 1000)
  const step = manual ? 'Sorgu doğrulanıp çalıştırılıyor' : seconds < 2 ? 'Soru ve sohbet bağlamı hazırlanıyor' : 'Model soruyu SQL\'e çeviriyor'
  return (
    <div className="flex items-center gap-3 rounded-2xl border border-slate-200 bg-white px-5 py-4 text-sm text-slate-600 shadow-sm">
      <span className="flex gap-1">
        {[0, 150, 300].map(d => <span key={d} className="h-2 w-2 animate-bounce rounded-full bg-indigo-400" style={{ animationDelay: `${d}ms` }} />)}
      </span>
      {step}… <span className="tabular-nums text-slate-400">{seconds} sn</span>
    </div>
  )
}
