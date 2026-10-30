import { useCallback, useEffect, useState } from 'react'
import { api, loadAuth, saveAuth, setUnauthorizedHandler, type AuthState } from './api'
import { ChatPanel } from './components/ChatPanel'
import { DocumentsPanel } from './components/DocumentsPanel'
import { LoginPage } from './components/LoginPage'
import { PdfViewer } from './components/PdfViewer'
import { UsagePanel } from './components/UsagePanel'
import type { ChatMessage, ChatSession, Citation, DocumentDto, ViewerTarget } from './types'

export default function App() {
  const [auth, setAuth] = useState<AuthState | null>(loadAuth)

  const logout = useCallback(() => {
    saveAuth(null)
    setAuth(null)
  }, [])
  useEffect(() => {
    setUnauthorizedHandler(logout)
  }, [logout])

  if (!auth)
    return (
      <LoginPage
        onLogin={(a) => {
          saveAuth(a)
          setAuth(a)
        }}
      />
    )
  return <Workspace key={auth.email} auth={auth} onLogout={logout} />
}

function Workspace({ auth, onLogout }: { auth: AuthState; onLogout: () => void }) {
  const token = auth.accessToken
  const [documents, setDocuments] = useState<DocumentDto[]>([])
  const [selected, setSelected] = useState<Set<string>>(new Set())
  const [sessions, setSessions] = useState<ChatSession[]>([])
  const [sessionId, setSessionId] = useState<string | null>(null)
  const [messages, setMessages] = useState<ChatMessage[]>([])
  const [viewer, setViewer] = useState<ViewerTarget | null>(null)
  const [showUsage, setShowUsage] = useState(false)
  const [sidebarOpen, setSidebarOpen] = useState(false)

  const refreshDocuments = useCallback(() => {
    api.documents(token).then(setDocuments).catch(() => {})
  }, [token])
  const refreshSessions = useCallback(() => {
    api.sessions(token).then(setSessions).catch(() => {})
  }, [token])

  useEffect(() => {
    refreshDocuments()
    refreshSessions()
  }, [refreshDocuments, refreshSessions])

  // Poll while any document is still being ingested.
  const pending = documents.some((d) => d.status === 'Uploaded' || d.status === 'Processing')
  useEffect(() => {
    if (!pending) return
    const id = setInterval(refreshDocuments, 2000)
    return () => clearInterval(id)
  }, [pending, refreshDocuments])

  async function openSession(id: string) {
    setSessionId(id)
    setSidebarOpen(false)
    const history = await api.messages(token, id)
    setMessages(history.map((m) => ({ ...m, citations: m.citations ?? [] })))
  }

  function newChat() {
    setSessionId(null)
    setMessages([])
    setSidebarOpen(false)
  }

  const onCite = (c: Citation) => setViewer({ documentId: c.documentId, fileName: c.fileName, page: c.page, highlight: c.snippet })
  const readyCount = documents.filter((d) => d.status === 'Ready').length

  return (
    <div className="flex h-screen flex-col">
      <header className="flex items-center gap-3 border-b border-slate-200 bg-white px-4 py-2">
        <button className="rounded px-2 py-1 text-lg md:hidden" onClick={() => setSidebarOpen((o) => !o)} aria-label="Toggle sidebar">
          ☰
        </button>
        <div className="flex h-8 w-8 items-center justify-center rounded-lg bg-indigo-600 font-bold text-white">R</div>
        <h1 className="font-semibold">Document Assistant</h1>
        <div className="flex-1" />
        <button onClick={() => setShowUsage(true)} className="rounded-lg px-3 py-1.5 text-sm text-slate-600 hover:bg-slate-100">
          Usage
        </button>
        <span className="hidden text-sm text-slate-500 sm:inline">{auth.email}</span>
        <button onClick={onLogout} className="rounded-lg px-3 py-1.5 text-sm text-slate-600 hover:bg-slate-100">
          Sign out
        </button>
      </header>

      <div className="relative flex min-h-0 flex-1">
        <aside
          className={`${sidebarOpen ? 'flex' : 'hidden'} absolute inset-y-0 left-0 z-10 w-80 flex-col border-r border-slate-200 bg-white md:static md:flex`}
        >
          <DocumentsPanel
            token={token}
            documents={documents}
            selected={selected}
            onToggle={(id) =>
              setSelected((s) => {
                const next = new Set(s)
                if (next.has(id)) next.delete(id)
                else next.add(id)
                return next
              })
            }
            onChanged={refreshDocuments}
            onOpen={(d) => d.status === 'Ready' && setViewer({ documentId: d.id, fileName: d.fileName, page: 1 })}
          />
          <section className="max-h-[40%] border-t border-slate-200">
            <div className="flex items-center justify-between px-4 pt-3 pb-1">
              <h2 className="text-xs font-semibold tracking-wide text-slate-500 uppercase">Chats</h2>
              <button onClick={newChat} className="text-xs font-medium text-indigo-600 hover:underline">
                + New chat
              </button>
            </div>
            <ul className="max-h-48 overflow-y-auto px-2 pb-3">
              {sessions.map((s) => (
                <li key={s.id}>
                  <button
                    onClick={() => void openSession(s.id)}
                    className={`w-full truncate rounded-lg px-2 py-1.5 text-left text-sm hover:bg-slate-100 ${
                      s.id === sessionId ? 'bg-slate-100 font-medium' : 'text-slate-600'
                    }`}
                  >
                    {s.title}
                  </button>
                </li>
              ))}
            </ul>
          </section>
        </aside>

        <main className="flex min-w-0 flex-1 flex-col">
          <ChatPanel
            token={token}
            sessionId={sessionId}
            messages={messages}
            setMessages={setMessages}
            onSession={(id) => {
              if (id !== sessionId) {
                setSessionId(id)
                refreshSessions()
              }
            }}
            selectedDocumentIds={[...selected]}
            readyCount={readyCount}
            onCite={onCite}
          />
        </main>

        {viewer && (
          <div className="absolute inset-0 z-10 lg:static lg:w-[44%] lg:min-w-[380px]">
            <PdfViewer token={token} target={viewer} onClose={() => setViewer(null)} />
          </div>
        )}
      </div>

      {showUsage && <UsagePanel token={token} onClose={() => setShowUsage(false)} />}
    </div>
  )
}
