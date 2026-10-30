import { useEffect, useRef, useState, type FormEvent } from 'react'
import { streamChat } from '../api'
import type { ChatMessage, Citation } from '../types'
import { AnswerText, CitationChip } from './AnswerText'

const MAX_CHARS = 2000

interface Props {
  token: string
  sessionId: string | null
  messages: ChatMessage[]
  setMessages: (update: (prev: ChatMessage[]) => ChatMessage[]) => void
  onSession: (id: string) => void
  selectedDocumentIds: string[]
  readyCount: number
  onCite: (c: Citation) => void
}

const suggestions = [
  'What are the characteristics of trustworthy AI?',
  'What are the six functions of the CSF 2.0 Core?',
  'How should an organization govern AI risk?',
]

export function ChatPanel({ token, sessionId, messages, setMessages, onSession, selectedDocumentIds, readyCount, onCite }: Props) {
  const [input, setInput] = useState('')
  const [busy, setBusy] = useState(false)
  const abort = useRef<AbortController | null>(null)
  const bottom = useRef<HTMLDivElement>(null)

  useEffect(() => {
    bottom.current?.scrollIntoView({ behavior: 'smooth' })
  }, [messages])
  useEffect(() => () => abort.current?.abort(), [])

  async function ask(question: string) {
    const q = question.trim()
    if (!q || busy) return
    setInput('')
    setBusy(true)
    const assistantId = crypto.randomUUID()
    setMessages((prev) => [
      ...prev,
      { id: crypto.randomUUID(), role: 'user', content: q, citations: [] },
      { id: assistantId, role: 'assistant', content: '', citations: [], pending: true },
    ])
    const update = (fn: (m: ChatMessage) => ChatMessage) =>
      setMessages((prev) => prev.map((m) => (m.id === assistantId ? fn(m) : m)))

    abort.current = new AbortController()
    try {
      await streamChat(
        token,
        { sessionId, question: q, documentIds: selectedDocumentIds.length ? selectedDocumentIds : null },
        (e) => {
          switch (e.type) {
            case 'meta':
              onSession(e.sessionId)
              if (e.searchQuery !== q) update((m) => ({ ...m, rewrittenQuery: e.searchQuery }))
              break
            case 'token':
              update((m) => ({ ...m, content: m.content + e.text }))
              break
            case 'citations':
              update((m) => ({ ...m, citations: e.citations }))
              break
            case 'done':
              update((m) => ({
                ...m,
                pending: false,
                stats: {
                  inputTokens: e.inputTokens,
                  outputTokens: e.outputTokens,
                  estimatedCostUsd: e.estimatedCostUsd,
                  answerFound: e.answerFound,
                },
              }))
              break
            case 'error':
              update((m) => ({ ...m, pending: false, error: e.message }))
              break
          }
        },
        abort.current.signal,
      )
    } catch (err) {
      if (!(err instanceof DOMException && err.name === 'AbortError'))
        update((m) => ({ ...m, error: err instanceof Error ? err.message : 'Request failed' }))
    } finally {
      update((m) => ({ ...m, pending: false }))
      setBusy(false)
    }
  }

  function submit(e: FormEvent) {
    e.preventDefault()
    void ask(input)
  }

  return (
    <section className="flex min-h-0 flex-1 flex-col">
      <div className="min-h-0 flex-1 overflow-y-auto">
        <div className="mx-auto max-w-3xl space-y-6 px-4 py-6">
          {messages.length === 0 && (
            <div className="py-16 text-center">
              <h2 className="text-xl font-semibold">Ask a question about your documents</h2>
              <p className="mt-1 text-sm text-slate-500">
                {readyCount > 0
                  ? `${readyCount} document${readyCount > 1 ? 's' : ''} ready. Answers cite document and page.`
                  : 'Upload a PDF to get started.'}
              </p>
              {readyCount > 0 && (
                <div className="mt-6 flex flex-wrap justify-center gap-2">
                  {suggestions.map((s) => (
                    <button
                      key={s}
                      onClick={() => void ask(s)}
                      className="rounded-full border border-slate-200 bg-white px-3 py-1.5 text-sm text-slate-600 hover:border-indigo-300 hover:text-indigo-700"
                    >
                      {s}
                    </button>
                  ))}
                </div>
              )}
            </div>
          )}

          {messages.map((m) =>
            m.role === 'user' ? (
              <div key={m.id} className="flex justify-end">
                <div className="max-w-[85%] rounded-2xl rounded-br-sm bg-indigo-600 px-4 py-2 whitespace-pre-wrap text-white">
                  {m.content}
                </div>
              </div>
            ) : (
              <div key={m.id} className="flex gap-3">
                <div className="flex h-7 w-7 shrink-0 items-center justify-center rounded-lg bg-slate-900 text-xs font-bold text-white">
                  AI
                </div>
                <div className="min-w-0 flex-1 text-[15px]">
                  {m.rewrittenQuery && <p className="mb-1 text-xs text-slate-400">Searched for: “{m.rewrittenQuery}”</p>}
                  {m.content ? (
                    <AnswerText text={m.content} citations={m.citations} onCite={onCite} />
                  ) : (
                    m.pending && <p className="animate-pulse text-slate-400">Searching your documents…</p>
                  )}
                  {m.pending && m.content && <span className="ml-0.5 inline-block h-4 w-1.5 animate-pulse bg-slate-400 align-middle" />}
                  {m.error && <p className="mt-2 rounded-lg bg-red-50 px-3 py-2 text-sm text-red-700">{m.error}</p>}
                  {!m.pending && m.citations.length > 0 && (
                    <div className="mt-3 flex flex-wrap items-center gap-1">
                      <span className="text-xs text-slate-400">Sources:</span>
                      {m.citations.map((c) => (
                        <CitationChip key={`${c.alias}:${c.page}`} citation={c} onClick={() => onCite(c)} />
                      ))}
                    </div>
                  )}
                  {m.stats && m.stats.inputTokens + m.stats.outputTokens > 0 && (
                    <p className="mt-2 text-xs text-slate-400">
                      {m.stats.inputTokens.toLocaleString()} in / {m.stats.outputTokens.toLocaleString()} out tokens · $
                      {m.stats.estimatedCostUsd.toFixed(4)}
                    </p>
                  )}
                </div>
              </div>
            ),
          )}
          <div ref={bottom} />
        </div>
      </div>

      <form onSubmit={submit} className="border-t border-slate-200 bg-white px-4 py-3">
        <div className="mx-auto flex max-w-3xl items-end gap-2">
          <div className="flex-1">
            <textarea
              value={input}
              onChange={(e) => setInput(e.target.value.slice(0, MAX_CHARS))}
              onKeyDown={(e) => {
                if (e.key === 'Enter' && !e.shiftKey) {
                  e.preventDefault()
                  void ask(input)
                }
              }}
              rows={1}
              placeholder={selectedDocumentIds.length ? `Ask about ${selectedDocumentIds.length} selected document(s)…` : 'Ask about all your documents…'}
              className="max-h-40 min-h-[44px] w-full resize-y rounded-xl border border-slate-300 px-3 py-2.5 outline-none focus:border-indigo-500"
            />
            {input.length > MAX_CHARS * 0.8 && (
              <p className="text-right text-xs text-slate-400">
                {input.length}/{MAX_CHARS}
              </p>
            )}
          </div>
          {busy ? (
            <button type="button" onClick={() => abort.current?.abort()} className="h-11 rounded-xl bg-slate-200 px-4 font-medium">
              Stop
            </button>
          ) : (
            <button disabled={!input.trim()} className="h-11 rounded-xl bg-indigo-600 px-4 font-medium text-white disabled:opacity-40">
              Send
            </button>
          )}
        </div>
      </form>
    </section>
  )
}
