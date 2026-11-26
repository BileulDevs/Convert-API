// A single-page app: `nuxt generate` writes static files that the API serves from the same origin,
// so the browser calls /v1/... directly, with no CORS and no server of its own.
export default defineNuxtConfig({
  ssr: false,
  compatibilityDate: '2026-11-01',
  devtools: { enabled: false },
  app: {
    head: {
      title: 'ConvertAPI',
      htmlAttrs: { lang: 'fr' },
      meta: [{ name: 'description', content: 'Convertir un fichier avec ConvertAPI' }],
    },
  },
  nitro: {
    // `npm run dev` only: forward the API calls to a local API (Compose or Kestrel).
    devProxy: {
      '/v1': { target: `${(process.env.API_URL ?? 'http://localhost:5080').replace(/\/$/, '')}/v1`, changeOrigin: true },
    },
  },
})
