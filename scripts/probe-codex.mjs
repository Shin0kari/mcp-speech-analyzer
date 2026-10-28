// Проверка протокола без создания сессий и отправки сообщений модели.
import { spawn } from 'node:child_process';
import { createInterface } from 'node:readline';

const mode = process.argv[2] ?? 'proxy';
if (!['proxy', 'standalone'].includes(mode)) throw new Error('Usage: node scripts/probe-codex.mjs proxy|standalone');
const args = mode === 'proxy' ? ['app-server', 'proxy'] : ['app-server', '--stdio'];
const child = spawn('codex', args, { windowsHide: true, stdio: ['pipe', 'pipe', 'pipe'] });
let nextId = 0;
const pending = new Map();
const stderr = [];
const timeout = setTimeout(() => { fail(new Error('Общий таймаут проверки (40 секунд)')); }, 40_000);
function fail(error) {
  for (const { reject } of pending.values()) reject(error);
  pending.clear();
  child.kill();
}
child.on('error', fail);
child.stdin.on('error', fail);
child.stderr.on('data', data => { if (stderr.length < 20) stderr.push(data.toString().slice(0, 1500)); });
const lines = createInterface({ input: child.stdout });
lines.on('line', line => {
  let message;
  try { message = JSON.parse(line); } catch { return; }
  if (message.id !== undefined && message.method) {
    // Не разрешать действия сервера во время диагностики.
    child.stdin.write(JSON.stringify({ id: message.id, error: { code: -32601, message: 'Read-only probe' } }) + '\n');
    return;
  }
  const request = pending.get(message.id);
  if (!request) return;
  pending.delete(message.id);
  if (message.error) request.reject(new Error(JSON.stringify(message.error)));
  else request.resolve(message.result);
});
function request(method, params = {}) {
  return new Promise((resolve, reject) => {
    const id = ++nextId;
    pending.set(id, { resolve, reject });
    child.stdin.write(JSON.stringify({ id, method, params }) + '\n');
  });
}
child.on('exit', (code, signal) => {
  if (pending.size) fail(new Error(`Codex завершился: code=${code}, signal=${signal}`));
});
try {
  const initialized = await request('initialize', { clientInfo: { name: 'speech_analyzer_probe', title: 'Speech Analyzer Probe', version: '0.1.0' } });
  console.log(JSON.stringify({ mode, initialize: initialized }));
  child.stdin.write(JSON.stringify({ method: 'initialized', params: {} }) + '\n');
  let cursor;
  const models = [];
  do {
    const page = await request('model/list', { limit: 100, includeHidden: true, ...(cursor ? { cursor } : {}) });
    models.push(...page.data.map(m => ({ id: m.id, model: m.model, name: m.displayName, hidden: m.hidden })));
    cursor = page.nextCursor;
  } while (cursor);
  console.log(JSON.stringify({ models }));
  const loaded = await request('thread/loaded/list');
  console.log(JSON.stringify({ loadedThreadCount: loaded.data?.length ?? null, note: 'IDs и содержимое чужих чатов не выводятся' }));
} catch (error) {
  console.error(error.message);
  process.exitCode = 1;
} finally {
  clearTimeout(timeout);
  lines.close();
  child.stdin.destroy();
  child.kill();
  if (stderr.length) console.error(stderr.join('').slice(0, 6000));
}
