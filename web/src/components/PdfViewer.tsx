import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { Document, Page, pdfjs } from 'react-pdf'
import 'react-pdf/dist/Page/AnnotationLayer.css'
import 'react-pdf/dist/Page/TextLayer.css'
import type { ViewerTarget } from '../types'

pdfjs.GlobalWorkerOptions.workerSrc = new URL('pdfjs-dist/build/pdf.worker.min.mjs', import.meta.url).toString()

const normalize = (s: string) => s.toLowerCase().replace(/[^\p{L}\p{N}]+/gu, ' ').trim()
const escapeHtml = (s: string) =>
  s.replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[c]!)

interface Props {
  token: string
  target: ViewerTarget
  onClose: () => void
}

export function PdfViewer({ token, target, onClose }: Props) {
  const [pages, setPages] = useState(0)
  // The page resets whenever a new citation (target) is opened, without an effect.
  const [pageState, setPageState] = useState({ target, page: target.page })
  const page = pageState.target === target ? pageState.page : target.page
  const setPage = (fn: (p: number) => number) => setPageState({ target, page: fn(page) })
  const [width, setWidth] = useState(520)
  const container = useRef<HTMLDivElement>(null)

  useEffect(() => {
    const el = container.current
    if (!el) return
    const observer = new ResizeObserver(([entry]) => setWidth(Math.max(280, entry.contentRect.width - 32)))
    observer.observe(el)
    return () => observer.disconnect()
  }, [])

  // Stable object so react-pdf does not reload the file on every render.
  const file = useMemo(
    () => ({ url: `/api/documents/${target.documentId}/file`, httpHeaders: { Authorization: `Bearer ${token}` } }),
    [target.documentId, token],
  )

  // The chunk text is matched against the page's text items; items that are part of the cited passage get a <mark>.
  const passage = useMemo(() => (target.highlight ? normalize(target.highlight) : ''), [target.highlight])
  const renderText = useCallback(
    ({ str }: { str: string }) => {
      const n = normalize(str)
      const hit = page === target.page && passage && n.length >= 15 && passage.includes(n)
      return hit ? `<mark class="citation-hit">${escapeHtml(str)}</mark>` : escapeHtml(str)
    },
    [passage, page, target.page],
  )

  return (
    <aside className="flex h-full min-h-0 flex-col border-l border-slate-200 bg-slate-50">
      <header className="flex items-center gap-2 border-b border-slate-200 bg-white px-4 py-2">
        <div className="min-w-0 flex-1">
          <p className="truncate text-sm font-medium">{target.fileName}</p>
          <p className="text-xs text-slate-500">
            Page {page} of {pages || '…'}
            {target.highlight && page === target.page && ' · cited passage highlighted'}
          </p>
        </div>
        <button
          className="rounded px-2 py-1 text-sm hover:bg-slate-100 disabled:opacity-30"
          disabled={page <= 1}
          onClick={() => setPage((p) => p - 1)}
          aria-label="Previous page"
        >
          ‹
        </button>
        <button
          className="rounded px-2 py-1 text-sm hover:bg-slate-100 disabled:opacity-30"
          disabled={pages > 0 && page >= pages}
          onClick={() => setPage((p) => p + 1)}
          aria-label="Next page"
        >
          ›
        </button>
        <button className="rounded px-2 py-1 text-sm text-slate-500 hover:bg-slate-100" onClick={onClose} aria-label="Close viewer">
          ✕
        </button>
      </header>
      <div ref={container} className="min-h-0 flex-1 overflow-auto p-4">
        <Document
          file={file}
          onLoadSuccess={(pdf) => setPages(pdf.numPages)}
          loading={<p className="text-sm text-slate-500">Loading PDF…</p>}
          error={<p className="text-sm text-red-600">Could not load the PDF.</p>}
        >
          <Page
            pageNumber={page}
            width={width}
            customTextRenderer={renderText}
            onRenderTextLayerSuccess={() =>
              container.current?.querySelector('mark.citation-hit')?.scrollIntoView({ block: 'center', behavior: 'smooth' })
            }
            className="mx-auto shadow"
          />
        </Document>
      </div>
    </aside>
  )
}
