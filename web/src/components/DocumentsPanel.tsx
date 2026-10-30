import { useRef, useState } from 'react'
import { api } from '../api'
import type { DocumentDto } from '../types'

const MAX_BYTES = 20 * 1024 * 1024

const statusStyle: Record<DocumentDto['status'], string> = {
  Uploaded: 'bg-slate-100 text-slate-600',
  Processing: 'bg-amber-100 text-amber-700',
  Ready: 'bg-emerald-100 text-emerald-700',
  Failed: 'bg-red-100 text-red-700',
}

interface Props {
  token: string
  documents: DocumentDto[]
  selected: Set<string>
  onToggle: (id: string) => void
  onChanged: () => void
  onOpen: (doc: DocumentDto) => void
}

export function DocumentsPanel({ token, documents, selected, onToggle, onChanged, onOpen }: Props) {
  const input = useRef<HTMLInputElement>(null)
  const [messages, setMessages] = useState<string[]>([])
  const [uploading, setUploading] = useState(false)
  const [dragging, setDragging] = useState(false)

  async function upload(files: File[]) {
    const notes: string[] = []
    const valid = files.filter((f) => {
      if (!f.name.toLowerCase().endsWith('.pdf')) notes.push(`${f.name}: only PDF files are supported.`)
      else if (f.size > MAX_BYTES) notes.push(`${f.name}: larger than 20 MB.`)
      else return true
      return false
    })
    if (valid.length > 0) {
      setUploading(true)
      try {
        const results = await api.upload(token, valid)
        for (const r of results) if (r.error) notes.push(`${r.fileName}: ${r.error}`)
      } catch (e) {
        notes.push(e instanceof Error ? e.message : 'Upload failed')
      } finally {
        setUploading(false)
        onChanged()
      }
    }
    setMessages(notes)
  }

  return (
    <section className="flex min-h-0 flex-1 flex-col">
      <div className="flex items-center justify-between px-4 pt-4 pb-2">
        <h2 className="text-xs font-semibold tracking-wide text-slate-500 uppercase">Documents</h2>
        {selected.size > 0 && <span className="text-xs text-indigo-600">{selected.size} selected for chat</span>}
      </div>

      <div
        onDragOver={(e) => {
          e.preventDefault()
          setDragging(true)
        }}
        onDragLeave={() => setDragging(false)}
        onDrop={(e) => {
          e.preventDefault()
          setDragging(false)
          void upload([...e.dataTransfer.files])
        }}
        onClick={() => input.current?.click()}
        className={`mx-4 cursor-pointer rounded-xl border-2 border-dashed px-3 py-4 text-center text-sm transition ${
          dragging ? 'border-indigo-500 bg-indigo-50' : 'border-slate-300 hover:border-indigo-400'
        }`}
      >
        <p className="font-medium text-slate-700">{uploading ? 'Uploading…' : 'Drop PDFs or click to upload'}</p>
        <p className="text-xs text-slate-400">Max 20 MB per file</p>
        <input
          ref={input}
          type="file"
          accept="application/pdf,.pdf"
          multiple
          hidden
          onChange={(e) => {
            void upload([...(e.target.files ?? [])])
            e.target.value = ''
          }}
        />
      </div>
      {messages.length > 0 && (
        <ul className="mx-4 mt-2 space-y-1 text-xs text-amber-700">
          {messages.map((m) => (
            <li key={m}>{m}</li>
          ))}
        </ul>
      )}

      <ul className="mt-3 min-h-0 flex-1 space-y-1 overflow-y-auto px-2 pb-4">
        {documents.length === 0 && <li className="px-2 text-sm text-slate-400">No documents yet.</li>}
        {documents.map((d) => (
          <li key={d.id} className="group rounded-lg px-2 py-2 hover:bg-slate-100">
            <div className="flex items-start gap-2">
              <input
                type="checkbox"
                className="mt-1 accent-indigo-600"
                disabled={d.status !== 'Ready'}
                checked={selected.has(d.id)}
                onChange={() => onToggle(d.id)}
                title="Limit chat to selected documents"
              />
              <div className="min-w-0 flex-1">
                <button
                  onClick={() => onOpen(d)}
                  className="block w-full truncate text-left text-sm font-medium text-slate-800 hover:text-indigo-600"
                  title={d.fileName}
                >
                  {d.fileName}
                </button>
                <div className="mt-0.5 flex items-center gap-2 text-xs text-slate-500">
                  <span className={`rounded px-1.5 py-0.5 font-medium ${statusStyle[d.status]}`}>{d.status}</span>
                  {d.status === 'Ready' && (
                    <span>
                      {d.pageCount} pages · {d.chunkCount} chunks
                    </span>
                  )}
                </div>
                {d.errorMessage && <p className="mt-1 text-xs text-red-600">{d.errorMessage}</p>}
              </div>
              <div className="flex gap-1 opacity-0 group-hover:opacity-100">
                {d.status === 'Failed' && (
                  <button
                    className="text-xs text-slate-500 hover:text-indigo-600"
                    onClick={() => void api.retryDocument(token, d.id).then(onChanged)}
                  >
                    Retry
                  </button>
                )}
                <button
                  className="text-xs text-slate-400 hover:text-red-600"
                  onClick={() => {
                    if (confirm(`Delete ${d.fileName}?`)) void api.deleteDocument(token, d.id).then(onChanged)
                  }}
                >
                  Delete
                </button>
              </div>
            </div>
          </li>
        ))}
      </ul>
    </section>
  )
}
