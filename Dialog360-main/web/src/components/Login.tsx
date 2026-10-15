import { useState, type FormEvent } from 'react'
import { auth } from '../api'

/** Sol paneldeki örnek: ürünün yaptığı işi (Türkçe soru → SQL → grafik) gerçek bir demo sorusuyla gösterir. */
const SAMPLE_QUESTION = 'Geçen ay sektörlere göre toplam kart harcaması'
const SAMPLE_SQL = [
  ['SELECT', ' s.Ad, SUM(i.Tutar)'],
  ['FROM', ' Islem i JOIN Sektor s'],
  ['WHERE', " i.IslemTipi = 'Harcama'"],
  ['GROUP BY', ' s.Ad'],
]
const SAMPLE_BARS: [string, number][] = [['E-ticaret', 100], ['Eğitim', 95], ['Elektronik', 87], ['Akaryakıt', 73], ['Seyahat', 72]]

export function Login({ onSignedIn }: { onSignedIn: (username: string) => void }) {
  const [username, setUsername] = useState('')
  const [password, setPassword] = useState('')
  const [showPassword, setShowPassword] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  const submit = async (e: FormEvent) => {
    e.preventDefault()
    if (busy) return
    setError(null)
    setBusy(true)
    try {
      const me = await auth.login(username.trim(), password)
      onSignedIn(me.username)
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Giriş yapılamadı. API çalışıyor mu kontrol edin.')
      setPassword('')
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="flex min-h-full">
      <aside className="relative hidden w-[46%] max-w-2xl flex-col justify-between overflow-hidden bg-[#1E1B4B] px-12 py-10 text-indigo-100 lg:flex">
        <div className="flex items-center gap-3">
          <div className="flex h-10 w-10 items-center justify-center rounded-xl bg-indigo-500 text-sm font-bold text-white">360</div>
          <span className="text-lg font-semibold text-white">Diyalog 360</span>
        </div>

        <figure className="my-10 max-w-md" aria-label="Örnek: Türkçe bir sorunun rapora dönüşmesi">
          <p className="ml-auto w-fit max-w-[22rem] rounded-2xl rounded-br-md bg-indigo-500 px-4 py-3 text-[15px] leading-snug text-white">
            {SAMPLE_QUESTION}
          </p>

          <pre className="mt-6 overflow-hidden rounded-xl border border-white/10 bg-black/20 px-4 py-3 font-mono text-[13px] leading-6 text-indigo-200">
            {SAMPLE_SQL.map(([kw, rest]) => (
              <div key={kw}><span className="text-indigo-400">{kw}</span>{rest}</div>
            ))}
          </pre>

          <div className="mt-6 space-y-2">
            {SAMPLE_BARS.map(([label, pct]) => (
              <div key={label} className="flex items-center gap-3 text-[13px]">
                <span className="w-20 shrink-0 text-indigo-300">{label}</span>
                <span className="h-2.5 rounded-full bg-indigo-300/80" style={{ width: `${pct * 0.7}%` }} />
              </div>
            ))}
          </div>
        </figure>

        <div className="max-w-md">
          <p className="text-2xl font-semibold leading-snug text-white">Sorunuzu Türkçe yazın, raporunuz grafiğiyle gelsin.</p>
          <p className="mt-3 text-sm leading-relaxed text-indigo-300">
            Diyalog 360 sorunuzu SQL'e çevirir, rapor veritabanına yalnızca okuma yetkisiyle bağlanır ve sonucu özetler.
          </p>
        </div>
      </aside>

      <main className="flex flex-1 items-center justify-center bg-white px-6 py-12">
        <div className="w-full max-w-sm">
          <div className="mb-10 flex items-center gap-3 lg:hidden">
            <div className="flex h-10 w-10 items-center justify-center rounded-xl bg-indigo-600 text-sm font-bold text-white">360</div>
            <span className="text-lg font-semibold text-slate-900">Diyalog 360</span>
          </div>

          <h1 className="text-[28px] font-semibold leading-tight tracking-tight text-slate-900">Oturum açın</h1>
          <p className="mt-2 text-[15px] leading-relaxed text-slate-500">Raporlara erişmek için kullanıcı adınızı ve şifrenizi girin.</p>

          <form onSubmit={submit} className="mt-8 space-y-5" noValidate>
            <div>
              <label htmlFor="username" className="block text-sm font-medium text-slate-700">Kullanıcı adı</label>
              <input id="username" name="username" autoComplete="username" autoFocus required
                value={username} onChange={e => setUsername(e.target.value)} disabled={busy}
                aria-invalid={!!error} aria-describedby={error ? 'login-error' : undefined}
                className="mt-1.5 block w-full rounded-lg border border-slate-300 px-3.5 py-2.5 text-[15px] text-slate-900 outline-none transition-colors focus:border-indigo-600 focus:ring-4 focus:ring-indigo-100 disabled:bg-slate-50" />
            </div>

            <div>
              <label htmlFor="password" className="block text-sm font-medium text-slate-700">Şifre</label>
              <div className="relative mt-1.5">
                <input id="password" name="password" type={showPassword ? 'text' : 'password'} autoComplete="current-password" required
                  value={password} onChange={e => setPassword(e.target.value)} disabled={busy}
                  aria-invalid={!!error} aria-describedby={error ? 'login-error' : undefined}
                  className="block w-full rounded-lg border border-slate-300 py-2.5 pl-3.5 pr-20 text-[15px] text-slate-900 outline-none transition-colors focus:border-indigo-600 focus:ring-4 focus:ring-indigo-100 disabled:bg-slate-50" />
                <button type="button" onClick={() => setShowPassword(s => !s)} aria-pressed={showPassword}
                  className="absolute inset-y-1.5 right-1.5 rounded-md px-2.5 text-sm text-slate-500 hover:bg-slate-100 hover:text-slate-700 focus-visible:outline-2 focus-visible:outline-indigo-600">
                  {showPassword ? 'Gizle' : 'Göster'}
                </button>
              </div>
            </div>

            {error && (
              <p id="login-error" role="alert" className="rounded-lg bg-rose-50 px-3.5 py-2.5 text-sm text-rose-700">{error}</p>
            )}

            <button type="submit" disabled={busy || !username.trim() || !password}
              className="w-full rounded-lg bg-indigo-600 px-4 py-2.5 text-[15px] font-medium text-white transition-colors hover:bg-indigo-700 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-indigo-600 disabled:cursor-not-allowed disabled:opacity-40">
              {busy ? 'Giriş yapılıyor…' : 'Giriş yap'}
            </button>
          </form>
        </div>
      </main>
    </div>
  )
}
