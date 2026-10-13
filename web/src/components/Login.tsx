import { useState } from 'react'
import { auth } from '../api'

export function Login({ configured, onLogin }: { configured: boolean; onLogin: (username: string) => void }) {
  const [username, setUsername] = useState('')
  const [password, setPassword] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  const submit = async (e: React.FormEvent) => {
    e.preventDefault()
    if (!username.trim() || !password) { setError('Kullanıcı adı ve şifre gerekli.'); return }
    setBusy(true)
    setError(null)
    try {
      const res = await auth.login(username, password)
      setPassword('')
      onLogin(res.username)
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Giriş başarısız.')
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="flex min-h-full items-center justify-center px-4">
      <form onSubmit={submit} className="w-full max-w-sm rounded-2xl border border-slate-200 bg-white p-8 shadow-sm">
        <div className="mb-6 flex items-center gap-3">
          <div className="flex h-10 w-10 items-center justify-center rounded-xl bg-indigo-600 text-sm font-bold text-white">360</div>
          <div>
            <h1 className="text-lg font-semibold text-slate-900">Diyalog 360</h1>
            <p className="text-xs text-slate-500">Devam etmek için giriş yapın</p>
          </div>
        </div>

        {!configured && (
          <div className="mb-4 rounded-lg border border-amber-200 bg-amber-50 px-3 py-2 text-xs text-amber-800">
            Henüz kullanıcı tanımlı değil. Sunucuda <code className="font-mono">cd api &amp;&amp; dotnet run -- set-password</code> komutunu çalıştırın.
          </div>
        )}

        <label className="mb-1 block text-sm font-medium text-slate-700" htmlFor="username">Kullanıcı adı</label>
        <input id="username" autoComplete="username" autoFocus value={username} onChange={e => setUsername(e.target.value)} maxLength={100}
          className="mb-4 w-full rounded-lg border border-slate-300 px-3 py-2 outline-none focus:border-indigo-500 focus:ring-2 focus:ring-indigo-100" />

        <label className="mb-1 block text-sm font-medium text-slate-700" htmlFor="password">Şifre</label>
        <input id="password" type="password" autoComplete="current-password" value={password} onChange={e => setPassword(e.target.value)} maxLength={200}
          className="w-full rounded-lg border border-slate-300 px-3 py-2 outline-none focus:border-indigo-500 focus:ring-2 focus:ring-indigo-100" />

        {error && <div role="alert" className="mt-3 text-sm text-rose-600">{error}</div>}

        <button disabled={busy} className="mt-6 w-full rounded-lg bg-indigo-600 py-2.5 font-medium text-white hover:bg-indigo-700 disabled:opacity-50">
          {busy ? 'Kontrol ediliyor…' : 'Giriş yap'}
        </button>
      </form>
    </div>
  )
}
