<script setup lang="ts">
const apiKey = ref('')
const formats = ref<Format[]>([])
const file = ref<File | null>(null)
const target = ref('')
const jobs = ref<Job[]>([])
const error = ref('')
const sending = ref(false)
const watchers = new Map<string, AbortController>()

const source = computed(() => (file.value ? formatFromName(file.value.name) : ''))
const targets = computed(() => formats.value.find(f => f.source === source.value)?.targets ?? [])
const canSend = computed(() => apiKey.value && file.value && target.value && !sending.value)

const statusLabels: Record<JobStatus, string> = {
  Queued: 'En attente',
  Processing: 'Conversion…',
  Succeeded: 'Terminé',
  Failed: 'Échec',
  Canceled: 'Annulé',
  Expired: 'Expiré',
}

onMounted(async () => {
  apiKey.value = loadKey()
  try {
    formats.value = await getFormats()
  } catch (e) {
    error.value = `Impossible de lire les formats : ${(e as Error).message}`
  }
})

onBeforeUnmount(() => watchers.forEach(watcher => watcher.abort()))

watch(apiKey, key => saveKey(key))

// A new file: preselect its first target, or clear the choice if the format is not accepted.
watch(targets, list => {
  target.value = list.includes(target.value) ? target.value : (list[0] ?? '')
})

function onFile(event: Event): void {
  file.value = (event.target as HTMLInputElement).files?.[0] ?? null
}

async function send(): Promise<void> {
  if (!file.value) {
    return
  }
  error.value = ''
  sending.value = true
  try {
    const job = await createJob(apiKey.value, file.value, target.value)
    jobs.value = [job, ...jobs.value]
    follow(job.id)
  } catch (e) {
    error.value = (e as Error).message
  } finally {
    sending.value = false
  }
}

function follow(id: string): void {
  const watcher = new AbortController()
  watchers.set(id, watcher)
  followJob(apiKey.value, id, update, watcher.signal)
    .catch(e => {
      if (!watcher.signal.aborted) {
        error.value = `Suivi interrompu : ${(e as Error).message}`
      }
    })
    .finally(() => watchers.delete(id))
}

function update(job: Job): void {
  jobs.value = jobs.value.map(j => (j.id === job.id ? job : j))
}

async function cancel(job: Job): Promise<void> {
  error.value = ''
  try {
    await cancelJob(apiKey.value, job.id)
    // The job is final now: free its stream's connection rather than wait for the last event.
    watchers.get(job.id)?.abort()
    update({ ...job, status: job.status === 'Succeeded' ? 'Expired' : 'Canceled' })
  } catch (e) {
    error.value = (e as Error).message
  }
}

async function download(job: Job): Promise<void> {
  error.value = ''
  try {
    await downloadResult(apiKey.value, job)
  } catch (e) {
    error.value = (e as Error).message
  }
}
</script>

<template>
  <main>
    <h1>ConvertAPI</h1>
    <p class="intro">Convertir une image, un son ou une vidéo. Les fichiers sont supprimés au bout de 24 h.</p>

    <form @submit.prevent="send">
      <label>
        Clé d'API
        <input v-model.trim="apiKey" type="password" autocomplete="off" placeholder="cvt_…" aria-describedby="key-hint" required>
        <small id="key-hint">Gardée dans cet onglet seulement, jamais envoyée ailleurs qu'à l'API.</small>
      </label>

      <label>
        Fichier
        <input type="file" required @change="onFile">
      </label>

      <label>
        Convertir en
        <select v-model="target" :disabled="!targets.length" required>
          <option v-if="file && !targets.length" value="">Format non pris en charge</option>
          <option v-for="t in targets" :key="t" :value="t">{{ t.toUpperCase() }}</option>
        </select>
      </label>

      <button type="submit" :disabled="!canSend">{{ sending ? 'Envoi…' : 'Convertir' }}</button>
    </form>

    <p v-if="error" class="error" role="alert">{{ error }}</p>

    <section v-if="jobs.length" aria-live="polite">
      <h2>Conversions</h2>
      <ul>
        <li v-for="job in jobs" :key="job.id" :class="`status-${job.status.toLowerCase()}`">
          <span class="formats">{{ job.sourceFormat.toUpperCase() }} → {{ job.targetFormat.toUpperCase() }}</span>
          <span class="status">{{ statusLabels[job.status] }}</span>
          <span v-if="job.failureReason" class="reason">{{ job.failureReason }}</span>
          <span class="actions">
            <button v-if="job.status === 'Succeeded'" type="button" @click="download(job)">Télécharger</button>
            <button v-if="!FINAL_STATUSES.includes(job.status) || job.status === 'Succeeded'" type="button" class="secondary" @click="cancel(job)">
              {{ job.status === 'Succeeded' ? 'Supprimer' : 'Annuler' }}
            </button>
          </span>
        </li>
      </ul>
    </section>
  </main>
</template>

<style>
:root {
  color-scheme: light dark;
  --bg: #f7f7f8;
  --card: #ffffff;
  --text: #1c1c22;
  --muted: #5f6070;
  --accent: #3b5bdb;
  --ok: #2b8a3e;
  --ko: #c92a2a;
  --line: #dcdce3;
  font-family: system-ui, sans-serif;
}

@media (prefers-color-scheme: dark) {
  :root {
    --bg: #141418;
    --card: #1e1e24;
    --text: #ececf1;
    --muted: #a0a0b0;
    --accent: #4263eb;
    --ok: #69db7c;
    --ko: #ff8787;
    --line: #34343e;
  }
}

body {
  margin: 0;
  background: var(--bg);
  color: var(--text);
}

main {
  max-width: 40rem;
  margin: 0 auto;
  padding: 2rem 1rem;
}

.intro,
small {
  color: var(--muted);
}

form,
section {
  display: grid;
  gap: 1rem;
  background: var(--card);
  border: 1px solid var(--line);
  border-radius: 0.75rem;
  padding: 1.25rem;
  margin-top: 1.5rem;
}

label {
  display: grid;
  gap: 0.35rem;
  font-weight: 600;
}

small {
  font-weight: 400;
}

input,
select,
button {
  font: inherit;
  padding: 0.55rem 0.7rem;
  border-radius: 0.5rem;
  border: 1px solid var(--line);
  background: var(--bg);
  color: var(--text);
}

button {
  background: var(--accent);
  border-color: var(--accent);
  color: #fff;
  cursor: pointer;
}

button.secondary {
  background: transparent;
  color: var(--text);
}

button:disabled {
  opacity: 0.5;
  cursor: not-allowed;
}

.error {
  color: var(--ko);
}

ul {
  list-style: none;
  margin: 0;
  padding: 0;
  display: grid;
  gap: 0.5rem;
}

li {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 0.5rem 1rem;
  padding: 0.6rem 0;
  border-top: 1px solid var(--line);
}

.formats {
  font-weight: 600;
}

.status-succeeded .status {
  color: var(--ok);
}

.status-failed .status {
  color: var(--ko);
}

.reason {
  flex-basis: 100%;
  color: var(--muted);
  font-size: 0.9rem;
}

.actions {
  margin-left: auto;
  display: flex;
  gap: 0.5rem;
}
</style>
