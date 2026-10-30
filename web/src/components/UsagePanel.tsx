import { useEffect, useState } from 'react'
import { api } from '../api'
import type { UsageSummary } from '../types'

const usd = (n: number) => `$${n.toFixed(n < 1 ? 4 : 2)}`
const num = (n: number) => n.toLocaleString()

export function UsagePanel({ token, onClose }: { token: string; onClose: () => void }) {
  const [days, setDays] = useState(30)
  const [data, setData] = useState<UsageSummary | null>(null)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    let live = true
    api
      .usage(token, days)
      .then((d) => live && setData(d))
      .catch((e: Error) => live && setError(e.message))
    return () => {
      live = false
    }
  }, [token, days])

  const maxDay = Math.max(0.000001, ...(data?.byDay.map((d) => d.estimatedCostUsd) ?? [0]))

  return (
    <div className="fixed inset-0 z-20 flex items-center justify-center bg-slate-900/40 p-4" onClick={onClose}>
      <div className="max-h-[90vh] w-full max-w-2xl overflow-y-auto rounded-2xl bg-white p-6 shadow-xl" onClick={(e) => e.stopPropagation()}>
        <div className="flex items-center justify-between">
          <h2 className="text-lg font-semibold">Usage &amp; cost</h2>
          <div className="flex items-center gap-2">
            <select value={days} onChange={(e) => setDays(Number(e.target.value))} className="rounded-lg border border-slate-300 px-2 py-1 text-sm">
              <option value={1}>Last 24 hours</option>
              <option value={7}>Last 7 days</option>
              <option value={30}>Last 30 days</option>
            </select>
            <button onClick={onClose} className="rounded px-2 py-1 text-slate-500 hover:bg-slate-100" aria-label="Close">
              ✕
            </button>
          </div>
        </div>
        <p className="mt-1 text-xs text-slate-500">Estimated from token counts and the list prices in configuration.</p>

        {error && <p className="mt-4 text-sm text-red-600">{error}</p>}
        {data && (
          <>
            <div className="mt-5 grid grid-cols-2 gap-3 sm:grid-cols-4">
              <Stat label="Estimated cost" value={usd(data.totals.estimatedCostUsd)} />
              <Stat label="LLM / embedding calls" value={num(data.totals.requests)} />
              <Stat label="Input tokens" value={num(data.totals.inputTokens)} />
              <Stat label="Output tokens" value={num(data.totals.outputTokens)} />
            </div>

            <h3 className="mt-6 text-sm font-semibold text-slate-600">Cost per day</h3>
            <div className="mt-2 flex h-24 items-end gap-1">
              {data.byDay.length === 0 && <p className="text-sm text-slate-400">No usage in this period.</p>}
              {data.byDay.map((d) => (
                <div
                  key={d.day}
                  title={`${d.day}: ${usd(d.estimatedCostUsd)}, ${num(d.inputTokens + d.outputTokens)} tokens`}
                  className="min-w-[6px] flex-1 rounded-t bg-indigo-500"
                  style={{ height: `${Math.max(4, (d.estimatedCostUsd / maxDay) * 100)}%` }}
                />
              ))}
            </div>

            <h3 className="mt-6 text-sm font-semibold text-slate-600">By operation and model</h3>
            <table className="mt-2 w-full text-sm">
              <thead className="text-left text-xs text-slate-500">
                <tr>
                  <th className="py-1">Model</th>
                  <th className="py-1 text-right">Calls</th>
                  <th className="py-1 text-right">Tokens in</th>
                  <th className="py-1 text-right">Tokens out</th>
                  <th className="py-1 text-right">Cost</th>
                </tr>
              </thead>
              <tbody>
                {data.byModel.map((m) => (
                  <tr key={m.provider + m.model} className="border-t border-slate-100">
                    <td className="py-1.5">
                      {m.model} <span className="text-xs text-slate-400">{m.provider}</span>
                    </td>
                    <td className="text-right">{num(m.requests)}</td>
                    <td className="text-right">{num(m.inputTokens)}</td>
                    <td className="text-right">{num(m.outputTokens)}</td>
                    <td className="text-right">{usd(m.estimatedCostUsd)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
            <div className="mt-3 flex flex-wrap gap-2 text-xs">
              {data.byOperation.map((o) => (
                <span key={o.operation} className="rounded-full bg-slate-100 px-2 py-1 text-slate-600">
                  {o.operation}: {num(o.requests)} calls · {usd(o.estimatedCostUsd)}
                </span>
              ))}
            </div>
          </>
        )}
      </div>
    </div>
  )
}

function Stat({ label, value }: { label: string; value: string }) {
  return (
    <div className="rounded-xl border border-slate-200 p-3">
      <p className="text-xs text-slate-500">{label}</p>
      <p className="mt-1 text-lg font-semibold">{value}</p>
    </div>
  )
}
