import type { ChatSession, Citation, DocumentDto, UploadResult, UsageSummary } from './types'

const TOKEN_KEY = 'rag.auth'

export interface AuthState {
  accessToken: string
  email: string
  expiresAt: string
}

export function loadAuth(): AuthState | null {
  try {
    const raw = localStorage.getItem(TOKEN_KEY)
    if (!raw) return null
    const auth = JSON.parse(raw) as AuthState
    return new Date(auth.expiresAt) > new Date() ? auth : null
  } catch {
    return null
  }
}

export function saveAuth(auth: AuthState | null) {
  try {
    if (auth) localStorage.setItem(TOKEN_KEY, JSON.stringify(auth))
    else localStorage.removeItem(TOKEN_KEY)
  } catch {
    /* storage unavailable: session-only login */
  }
}

export class ApiError extends Error {
  status: number
  constructor(status: number, message: string) {
    super(message)
    this.status = status
  }
}

let onUnauthorized: () => void = () => {}
export function setUnauthorizedHandler(fn: () => void) {
  onUnauthorized = fn
}

async function request<T>(path: string, token: string | null, init: RequestInit = {}): Promise<T> {
  const headers = new Headers(init.headers)
  if (token) headers.set('Authorization', `Bearer ${token}`)
  if (init.body && !(init.body instanceof FormData)) headers.set('Content-Type', 'application/json')
  const res = await fetch(path, { ...init, headers })
  if (res.status === 401 && token) onUnauthorized()
  const text = await res.text()
  const body = text ? JSON.parse(text) : null
  if (!res.ok && res.status !== 409) throw new ApiError(res.status, problemMessage(body) ?? res.statusText)
  return body as T
}

function problemMessage(body: unknown): string | undefined {
  if (!body || typeof body !== 'object') return undefined
  const p = body as { detail?: string; title?: string; errors?: Record<string, string[]> }
  if (p.errors) return Object.values(p.errors).flat().join(' ')
  return p.detail ?? p.title
}

export const api = {
  login: (email: string, password: string) =>
    request<AuthState>('/api/auth/login', null, { method: 'POST', body: JSON.stringify({ email, password }) }),
  register: (email: string, password: string) =>
    request<AuthState>('/api/auth/register', null, { method: 'POST', body: JSON.stringify({ email, password }) }),

  documents: (token: string) => request<DocumentDto[]>('/api/documents', token),
  upload: (token: string, files: File[]) => {
    const form = new FormData()
    files.forEach((f) => form.append('files', f))
    return request<UploadResult[]>('/api/documents', token, { method: 'POST', body: form })
  },
  deleteDocument: (token: string, id: string) => request<void>(`/api/documents/${id}`, token, { method: 'DELETE' }),
  retryDocument: (token: string, id: string) => request<void>(`/api/documents/${id}/retry`, token, { method: 'POST' }),

  sessions: (token: string) => request<ChatSession[]>('/api/chat/sessions', token),
  messages: (token: string, sessionId: string) =>
    request<{ id: string; role: 'user' | 'assistant'; content: string; citations: Citation[]; rewrittenQuery: string | null }[]>(
      `/api/chat/sessions/${sessionId}/messages`,
      token,
    ),
  deleteSession: (token: string, id: string) => request<void>(`/api/chat/sessions/${id}`, token, { method: 'DELETE' }),

  usage: (token: string, days: number) => request<UsageSummary>(`/api/usage?days=${days}`, token),
}

export type StreamEvent =
  | { type: 'meta'; sessionId: string; userMessageId: string; searchQuery: string }
  | { type: 'token'; text: string }
  | { type: 'citations'; citations: Citation[] }
  | {
      type: 'done'
      messageId: string
      answerFound: boolean
      inputTokens: number
      outputTokens: number
      estimatedCostUsd: number
      invalidCitations: number
    }
  | { type: 'error'; message: string }

/**
 * POST + Server-Sent Events. EventSource only supports GET without headers, so the stream is read with fetch
 * and parsed here (events are separated by a blank line, each with `event:` and `data:` fields).
 */
export async function streamChat(
  token: string,
  body: { sessionId: string | null; question: string; documentIds: string[] | null },
  onEvent: (e: StreamEvent) => void,
  signal: AbortSignal,
) {
  const res = await fetch('/api/chat/stream', {
    method: 'POST',
    headers: { Authorization: `Bearer ${token}`, 'Content-Type': 'application/json', Accept: 'text/event-stream' },
    body: JSON.stringify(body),
    signal,
  })
  if (res.status === 401) onUnauthorized()
  if (!res.ok || !res.body) {
    const text = await res.text()
    throw new ApiError(res.status, problemMessage(text ? JSON.parse(text) : null) ?? `Request failed (${res.status})`)
  }

  const reader = res.body.pipeThrough(new TextDecoderStream()).getReader()
  let buffer = ''
  for (;;) {
    const { value, done } = await reader.read()
    if (done) break
    buffer += value.replace(/\r\n/g, '\n')
    let boundary: number
    while ((boundary = buffer.indexOf('\n\n')) >= 0) {
      const raw = buffer.slice(0, boundary)
      buffer = buffer.slice(boundary + 2)
      const data = raw
        .split('\n')
        .filter((l) => l.startsWith('data:'))
        .map((l) => l.slice(5).trimStart())
        .join('\n')
      if (data) onEvent(JSON.parse(data) as StreamEvent)
    }
  }
}
