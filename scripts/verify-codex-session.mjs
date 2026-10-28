// Создаёт только отдельную тестовую сессию; делает два коротких вызова Terra.
// Не отправляет сообщения в существующие чаты пользователя.
import { randomBytes } from 'node:crypto';
import { mkdir, writeFile } from 'node:fs/promises';
import { CodexRpc } from './codex-rpc.mjs';

const rpc = new CodexRpc();
let resumed;
const report = { timestamp: new Date().toISOString(), model: 'gpt-6.1-terra', transport: 'dedicated-stdio', desktopSync: 'not-tested' };
try {
  report.initialize = await rpc.initialize();
  const models = await rpc.request('model/list', { limit: 100, includeHidden: true });
  if (!models.data.some(m => m.model === report.model)) throw new Error('Requested Terra model not present in catalog');
  const started = await rpc.request('thread/start', {
    model: report.model, cwd: process.cwd(), approvalPolicy: 'never', sandbox: 'read-only',
    baseInstructions: 'This is an isolated conversation-memory connectivity test. Reply briefly in Russian. Do not use tools, read files, change files, or perform external actions.',
    config: { mcp_servers: {} }
  });
  if (started.model !== report.model) throw new Error(`Unexpected model: ${started.model}`);
  const threadId = started.thread.id;
  report.threadId = threadId;
  report.selectedModel = started.model;
  const marker = randomBytes(12).toString('hex');
  const first = await rpc.turn(threadId, `Запомни проверочный код для следующего сообщения: ${marker}. Ответь только «Код запомнен».`);
  report.firstTurn = { status: first.status, streamingDeltaCount: first.deltas };
  rpc.close();

  // Новый процесс проверяет именно восстановление сохранённого контекста.
  resumed = new CodexRpc();
  await resumed.initialize();
  const result = await resumed.request('thread/resume', { threadId, excludeTurns: true, model: report.model, approvalPolicy: 'never', sandbox: 'read-only' });
  if (result.model !== report.model) throw new Error(`Unexpected resumed model: ${result.model}`);
  const second = await resumed.turn(threadId, 'Назови только проверочный код, который я сообщил ранее. Не читай файлы и не вызывай инструменты.');
  report.secondTurn = { status: second.status, streamingDeltaCount: second.deltas };
  report.contextPreserved = second.text.trim() === marker;
  if (!report.contextPreserved) throw new Error('Response did not reproduce the original marker exactly');
  report.success = true;
} catch (error) {
  report.success = false;
  report.error = error.message;
  process.exitCode = 1;
} finally {
  const diagnostics = [rpc.diagnostics, resumed?.diagnostics].filter(Boolean).join('\n');
  rpc.close();
  resumed?.close();
  await mkdir('artifacts', { recursive: true });
  await writeFile('artifacts/codex-session-check.json', JSON.stringify(report, null, 2) + '\n');
  console.log(JSON.stringify(report, null, 2));
  if (!report.success) console.error(diagnostics.slice(-4000));
}
