export type DocumentStatus = 'Uploaded' | 'Processing' | 'Ready' | 'Failed'

export interface DocumentDto {
  id: string
  fileName: string
  sizeBytes: number
  pageCount: number
  status: DocumentStatus
  errorMessage: string | null
  createdAt: string
  processedAt: string | null
  chunkCount: number
}

export interface UploadResult {
  fileName: string
  outcome: 'Created' | 'Duplicate' | 'Invalid'
  document: DocumentDto | null
  error: string | null
}

export interface Citation {
  alias: string
  documentId: string
  fileName: string
  page: number
  chunkId: string
  snippet: string
}

export interface ChatMessage {
  id: string
  role: 'user' | 'assistant'
  content: string
  citations: Citation[]
  rewrittenQuery?: string | null
  pending?: boolean
  error?: string
  stats?: { inputTokens: number; outputTokens: number; estimatedCostUsd: number; answerFound: boolean }
}

export interface ChatSession {
  id: string
  title: string
  createdAt: string
  updatedAt: string
}

export interface UsageSummary {
  from: string
  to: string
  totals: UsageRow
  byOperation: (UsageRow & { operation: string })[]
  byModel: (UsageRow & { provider: string; model: string })[]
  byDay: (UsageRow & { day: string })[]
}

export interface UsageRow {
  requests: number
  inputTokens: number
  outputTokens: number
  estimatedCostUsd: number
}

export interface ViewerTarget {
  documentId: string
  fileName: string
  page: number
  highlight?: string
}
