// The test environment. Secrets come from the deployment's environment variables (GitHub
// Actions secrets in CI), never from this file:
//   POSTGRES_PASSWORD     the database administrator password
//   API_KEYS              JSON array of { id, keyId, name, secretHash } (hashes, not keys)
//   WEBHOOK_SECRETS       JSON object { "<keyId>": "<secret of 32+ characters>" }
//   API_IMAGE, WORKER_IMAGE  the images to run, pinned to a commit SHA
using 'main.bicep'

param environmentName = 'test'
param apiImage = readEnvironmentVariable('API_IMAGE')
param workerImage = readEnvironmentVariable('WORKER_IMAGE')
param postgresPassword = readEnvironmentVariable('POSTGRES_PASSWORD')
param apiKeys = json(readEnvironmentVariable('API_KEYS', '[]'))
param webhookSecrets = json(readEnvironmentVariable('WEBHOOK_SECRETS', '{}'))
