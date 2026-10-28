import { spawn } from 'node:child_process';
import { createInterface } from 'node:readline';
import { EventEmitter } from 'node:events';

// Узкий JSONL-клиент. Никакие запросы на выполнение действий не подтверждаются.
export class CodexRpc extends EventEmitter {
  constructor(args = ['app-server', '--stdio']) {
    super();
    this.pending = new Map();
    this.nextId = 0;
    this.diagnostics = '';
    this.closed = false;
    this.child = spawn('codex', args, { windowsHide: true, stdio: ['pipe', 'pipe', 'pipe'] });
    this.child.stderr.on('data', data => { this.diagnostics = (this.diagnostics + data).slice(-6000); });
    this.lines = createInterface({ input: this.child.stdout });
    this.child.on('error', error => this.fail(error));
    this.child.stdin.on('error', error => this.fail(error));
    this.child.on('exit', (code, signal) => this.fail(new Error(`Codex exited (${code}, ${signal})`)));
    this.lines.on('line', line => {
      let message;
      try { message = JSON.parse(line); } catch { return; }
      if (message.id !== undefined && message.method) {
        this.send({ id: message.id, error: { code: -32601, message: 'Actions disabled by integration probe' } });
      } else if (message.id !== undefined) {
        const entry = this.pending.get(message.id);
        if (!entry) return;
        clearTimeout(entry.timer);
        this.pending.delete(message.id);
        if (message.error) entry.reject(new Error(JSON.stringify(message.error)));
        else entry.resolve(message.result);
      } else this.emit('notification', message);
    });
  }
  fail(error) {
    this.closed = true;
    for (const entry of this.pending.values()) { clearTimeout(entry.timer); entry.reject(error); }
    this.pending.clear();
    this.emit('disconnected', error);
  }
  send(message) { this.child.stdin.write(JSON.stringify(message) + '\n'); }
  request(method, params = {}, timeoutMs = 30_000) {
    if (this.closed) return Promise.reject(new Error('Codex disconnected'));
    return new Promise((resolve, reject) => {
      const id = ++this.nextId;
      const timer = setTimeout(() => { this.pending.delete(id); reject(new Error(`Timeout: ${method}`)); }, timeoutMs);
      this.pending.set(id, { resolve, reject, timer });
      this.send({ id, method, params });
    });
  }
  async initialize() {
    const result = await this.request('initialize', { clientInfo: { name: 'speech_analyzer_probe', version: '0.1.0' } });
    this.send({ method: 'initialized', params: {} });
    return result;
  }
  async turn(threadId, text) {
    // Подписка до turn/start: короткий ответ может завершиться до обработки response.
    const messages = [];
    let deltas = 0;
    let resolveCompletion;
    let rejectCompletion;
    const completion = new Promise((resolve, reject) => { resolveCompletion = resolve; rejectCompletion = reject; });
    // Предотвращает unhandled rejection, пока ожидается turn/start.
    completion.catch(() => {});
    const onNotification = ({ method, params }) => {
      if (params?.threadId !== threadId) return;
      if (method === 'item/agentMessage/delta') deltas++;
      if (method === 'item/completed' && params.item?.type === 'agentMessage') messages.push(params.item.text);
      if (method === 'turn/completed') {
        if (params.turn.status === 'completed') resolveCompletion({ text: messages.join('\n'), deltas, status: params.turn.status });
        else rejectCompletion(new Error(JSON.stringify(params.turn.error ?? params.turn.status)));
      }
    };
    const onDisconnect = error => rejectCompletion(error);
    const timer = setTimeout(() => rejectCompletion(new Error('Timeout waiting for turn/completed')), 120_000);
    this.on('notification', onNotification);
    this.on('disconnected', onDisconnect);
    try {
      await this.request('turn/start', { threadId, input: [{ type: 'text', text }], model: 'gpt-6.1-terra', effort: 'low' });
      return await completion;
    } finally {
      clearTimeout(timer);
      this.off('notification', onNotification);
      this.off('disconnected', onDisconnect);
    }
  }
  close() {
    this.fail(new Error('Client closed'));
    this.lines.close();
    this.child.stdin.destroy();
    this.child.kill();
  }
}
