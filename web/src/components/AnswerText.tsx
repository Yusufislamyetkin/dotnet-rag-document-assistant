import type { ReactNode } from 'react'
import type { Citation } from '../types'

const MARKER = /\[((?:\s*doc\d+\s*:\s*(?:p\.?\s*)?\d+\s*[,;]?)+)\]/gi
const REF = /doc(\d+)\s*:\s*(?:p\.?\s*)?(\d+)/gi

interface Props {
  text: string
  citations: Citation[]
  onCite: (c: Citation) => void
}

/** Renders the answer with [docN:page] markers replaced by clickable chips, plus minimal list/bold formatting. */
export function AnswerText({ text, citations, onCite }: Props) {
  const lines = text.split('\n')
  const blocks: ReactNode[] = []
  let list: ReactNode[] = []

  const flush = () => {
    if (list.length) blocks.push(<ul key={`ul${blocks.length}`} className="my-1 list-disc space-y-1 pl-5">{list}</ul>)
    list = []
  }

  lines.forEach((line, i) => {
    const bullet = /^\s*[-*•]\s+(.*)$/.exec(line)
    if (bullet) {
      list.push(<li key={i}>{inline(bullet[1], citations, onCite)}</li>)
      return
    }
    flush()
    if (line.trim()) blocks.push(<p key={i} className="my-1">{inline(line, citations, onCite)}</p>)
  })
  flush()
  return <div className="leading-relaxed">{blocks}</div>
}

function inline(text: string, citations: Citation[], onCite: (c: Citation) => void): ReactNode[] {
  const out: ReactNode[] = []
  let last = 0
  for (const m of text.matchAll(MARKER)) {
    out.push(...bold(text.slice(last, m.index), out.length))
    for (const ref of m[1].matchAll(REF)) {
      const alias = `doc${ref[1]}`
      const page = Number(ref[2])
      const c = citations.find((x) => x.alias === alias && x.page === page)
      out.push(
        c ? (
          <CitationChip key={`c${out.length}`} citation={c} onClick={() => onCite(c)} />
        ) : (
          <span key={`c${out.length}`} className="mx-0.5 text-xs text-slate-400" title="Citation could not be verified">
            [{alias}:{page}]
          </span>
        ),
      )
    }
    last = m.index + m[0].length
  }
  out.push(...bold(text.slice(last), out.length))
  return out
}

function bold(text: string, keyBase: number): ReactNode[] {
  return text.split(/(\*\*[^*]+\*\*)/g).map((part, i) =>
    part.startsWith('**') && part.endsWith('**') ? <strong key={`b${keyBase}-${i}`}>{part.slice(2, -2)}</strong> : part,
  )
}

export function CitationChip({ citation, onClick }: { citation: Citation; onClick: () => void }) {
  return (
    <button
      onClick={onClick}
      title={`${citation.fileName}, page ${citation.page}`}
      className="mx-0.5 inline-flex items-center gap-1 rounded-md border border-indigo-200 bg-indigo-50 px-1.5 py-0 align-baseline text-xs font-medium text-indigo-700 hover:bg-indigo-100"
    >
      <span className="max-w-[10rem] truncate">{shortName(citation.fileName)}</span>
      <span className="text-indigo-400">p.{citation.page}</span>
    </button>
  )
}

const shortName = (name: string) => name.replace(/\.pdf$/i, '')
