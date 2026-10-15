import { StrictMode, useCallback, useEffect, useState } from 'react'
import { createRoot } from 'react-dom/client'
import './index.css'
import App from './App.tsx'
import { Login } from './components/Login.tsx'
import { auth, UNAUTHORIZED_EVENT } from './api.ts'

type Session = { state: 'checking' } | { state: 'signedOut' } | { state: 'signedIn'; username: string }

function Root() {
  const [session, setSession] = useState<Session>({ state: 'checking' })

  useEffect(() => {
    auth.me()
      .then(me => setSession(me ? { state: 'signedIn', username: me.username } : { state: 'signedOut' }))
      .catch(() => setSession({ state: 'signedOut' }))
    const onUnauthorized = () => setSession({ state: 'signedOut' })
    window.addEventListener(UNAUTHORIZED_EVENT, onUnauthorized)
    return () => window.removeEventListener(UNAUTHORIZED_EVENT, onUnauthorized)
  }, [])

  const logout = useCallback(async () => {
    await auth.logout().catch(() => undefined)
    setSession({ state: 'signedOut' })
  }, [])

  if (session.state === 'checking') return <div className="h-full bg-slate-50" aria-busy="true" />
  if (session.state === 'signedOut') return <Login onSignedIn={username => setSession({ state: 'signedIn', username })} />
  return <App username={session.username} onLogout={logout} />
}

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <Root />
  </StrictMode>,
)
