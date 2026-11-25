// Calls to ConvertAPI from the browser. The page is served by the API itself, so every path is
// relative: same origin, no CORS. The key travels in the X-Api-Key header and only lives in this
// tab's sessionStorage.

export type JobStatus = 'Queued' | 'Processing' | 'Succeeded' | 'Failed' | 'Canceled' | 'Expired'

export interface Job {
  id: string
  status: JobStatus
  sourceFormat: string
  targetFormat: string
  createdAt: string
  completedAt: string | null
  expiresAt: string | null
  failureReason: string | null
}

export interface Format {
  source: string
  family: string
  targets: string[]
}

/** An API refusal, carrying the problem details' title and detail. */
export class ApiError extends Error {
  constructor(readonly status: number, message: string) {
    super(message)
  }
}

export const FINAL_STATUSES: readonly JobStatus[] = ['Succeeded', 'Failed', 'Canceled', 'Expired']

const KEY_STORAGE = 'convertapi.key'

export function loadKey(): string {
  return sessionStorage.getItem(KEY_STORAGE) ?? ''
}

export function saveKey(key: string): void {
  if (key) {
    sessionStorage.setItem(KEY_STORAGE, key)
  } else {
    sessionStorage.removeItem(KEY_STORAGE)
  }
}

async function request(path: string, key: string, init: RequestInit = {}): Promise<Response> {
  const response = await fetch(path, { ...init, headers: { ...init.headers, 'X-Api-Key': key } })
  if (!response.ok) {
    throw await toError(response)
  }
  return response
}

async function toError(response: Response): Promise<ApiError> {
  try {
    const problem = (await response.json()) as { title?: string; detail?: string }
    return new ApiError(
      response.status,
      [problem.title, problem.detail].filter(Boolean).join(' : ') || `Erreur HTTP ${response.status}`,
    )
  } catch {
    return new ApiError(response.status, `Erreur HTTP ${response.status}`)
  }
}

export async function getFormats(): Promise<Format[]> {
  const response = await fetch('/v1/formats')
  if (!response.ok) {
    throw await toError(response)
  }
  return ((await response.json()) as { formats: Format[] }).formats
}

export async function createJob(key: string, file: File, targetFormat: string): Promise<Job> {
  const form = new FormData()
  form.append('targetFormat', targetFormat)
  form.append('file', file)
  return (await (await request('/v1/jobs', key, { method: 'POST', body: form })).json()) as Job
}

export async function cancelJob(key: string, id: string): Promise<void> {
  await request(`/v1/jobs/${encodeURIComponent(id)}`, key, { method: 'DELETE' })
}

/**
 * Reads one Server-Sent Events stream of a job's status, calling onUpdate on each event, until the
 * server closes it. Read through fetch: EventSource cannot send the X-Api-Key header.
 * @returns The last job received, or null if none came.
 */
async function readEvents(key: string, id: string, onUpdate: (job: Job) => void, signal: AbortSignal): Promise<Job | null> {
  const response = await request(`/v1/jobs/${encodeURIComponent(id)}/events`, key, {
    signal,
    headers: { Accept: 'text/event-stream' },
  })
  const reader = response.body!.pipeThrough(new TextDecoderStream()).getReader()
  let buffer = ''
  let last: Job | null = null
  for (;;) {
    const { value, done } = await reader.read()
    // Events end with a blank line; the spec allows \r\n and \r as line breaks too.
    buffer += done ? '\n\n' : value.replace(/\r\n?/g, '\n')
    let end: number
    while ((end = buffer.indexOf('\n\n')) >= 0) {
      const job = parseEvent(buffer.slice(0, end))
      buffer = buffer.slice(end + 2)
      if (job) {
        last = job
        onUpdate(job)
      }
    }
    if (done) {
      return last
    }
  }
}

/** The job carried by one event's "data:" lines, or null for an event without data. */
function parseEvent(event: string): Job | null {
  const data = event.split('\n').filter(line => line.startsWith('data:')).map(line => line.slice(5).trim()).join('\n')
  return data ? (JSON.parse(data) as Job) : null
}

const MAX_RECONNECTS = 10

/**
 * Follows a job until it reaches a final status. The API closes a stream after a while (and a proxy
 * may cut it sooner), so a stream that ends early is opened again.
 */
export async function followJob(key: string, id: string, onUpdate: (job: Job) => void, signal: AbortSignal): Promise<void> {
  for (let attempt = 0; attempt <= MAX_RECONNECTS; attempt++) {
    const last = await readEvents(key, id, onUpdate, signal)
    if ((last && FINAL_STATUSES.includes(last.status)) || signal.aborted) {
      return
    }
    await new Promise(resolve => setTimeout(resolve, 1000))
  }
  throw new Error('le suivi a été interrompu trop souvent, rechargez la page pour voir le statut')
}

/** Downloads the result through fetch (it needs the key header), then hands it to the browser. */
export async function downloadResult(key: string, job: Job): Promise<void> {
  const blob = await (await request(`/v1/jobs/${encodeURIComponent(job.id)}/result`, key)).blob()
  const url = URL.createObjectURL(blob)
  const link = document.createElement('a')
  link.href = url
  link.download = `${job.id}.${job.targetFormat}`
  document.body.append(link)
  link.click()
  link.remove()
  // Revoked later: some browsers cancel a download whose URL disappears at once.
  setTimeout(() => URL.revokeObjectURL(url), 60_000)
}

/** The format a file name suggests; the API decides from the content anyway. */
export function formatFromName(name: string): string {
  const extension = name.split('.').pop()?.toLowerCase() ?? ''
  const aliases: Record<string, string> = { jpeg: 'jpg', tif: 'tiff', oga: 'ogg' }
  return aliases[extension] ?? extension
}
