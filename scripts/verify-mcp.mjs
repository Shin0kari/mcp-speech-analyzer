// По умолчанию только handshake/list. Переходы разрешены отдельным флагом только в остановленной симуляции.
import { spawn } from 'node:child_process';
import { createInterface } from 'node:readline';
import { fileURLToPath } from 'node:url';
import { resolve, dirname } from 'node:path';
const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const child = spawn(resolve(root, 'mcp/target/debug/speech-analyzer-mcp.exe'), [], { windowsHide: true, stdio: ['pipe','pipe','pipe'] });
const lines = createInterface({ input: child.stdout });
const pending = new Map(); let id = 0; let stderr = '';
child.stderr.on('data', data => { stderr = (stderr + data).slice(-1000); });
function fail(error) { for (const item of pending.values()) { clearTimeout(item.timer); item.reject(error); } pending.clear(); }
child.on('error', fail); child.on('exit', code => fail(new Error('MCP exited: ' + code)));
child.stdin.on('error', fail);
lines.on('line', line => {
  try {
    const message = JSON.parse(line), item = pending.get(message.id);
    if (!item) return;
    clearTimeout(item.timer); pending.delete(message.id);
    if (message.error) item.reject(new Error(JSON.stringify(message.error))); else item.resolve(message.result);
  } catch (error) { fail(error); }
});
function send(value) { child.stdin.write(JSON.stringify(value) + '\n'); }
function request(method, params = {}) {
  return new Promise((resolve, reject) => {
    const number = ++id;
    const timer = setTimeout(() => { pending.delete(number); reject(new Error('Timeout: ' + method)); }, 15000);
    pending.set(number, { resolve, reject, timer }); send({ jsonrpc:'2.0', id:number, method, params });
  });
}
try {
  const initialized = await request('initialize', { protocolVersion:'2025-11-25', capabilities:{}, clientInfo:{ name:'speech-analyzer-check', version:'1.0' } });
  send({ jsonrpc:'2.0', method:'notifications/initialized' });
  const { tools } = await request('tools/list');
  const expected = ['get_status','list_audio_sources','set_audio_sources','start_capture','pause_capture','resume_capture','stop_capture','get_transcript','search_transcript','get_open_questions','get_analysis_state','set_context'];
  if (tools.length !== expected.length || expected.some(name => !tools.some(tool => tool.name === name))) throw new Error('Unexpected tools list');
  const report = { initialize:true, protocolVersion:initialized.protocolVersion, toolCount:tools.length, statusChecked:false, captureCommandsSent:0 };
  if (process.argv.includes('--status') || process.argv.includes('--sources') || process.argv.includes('--capture-brave') || process.argv.includes('--simulation-lifecycle') || process.argv.includes('--simulation-transcript') || process.argv.includes('--read-matrix')) {
    const status = await request('tools/call', { name:'get_status', arguments:{} });
    if (status.isError) throw new Error('get_status failed: ' + JSON.stringify(status.content));
    const body = JSON.parse(status.content.find(item => item.type === 'text').text);
    if (!body.ok || body.result.capture !== 'Stopped') throw new Error('Expected capture=Stopped: ' + JSON.stringify(body));
    report.statusChecked = true; report.capture = body.result.capture; report.isSimulation = body.result.isSimulation;
    if (!Number.isSafeInteger(body.result.activeCaptureDurationMs) || body.result.activeCaptureDurationMs < 0 ||
        typeof body.result.activeCaptureDurationUncertain !== 'boolean' ||
        body.result.activeCaptureDurationMs !== body.result.captureDuration?.ActiveCaptureDurationMs)
      throw new Error('Invalid active capture duration contract');
    report.activeCaptureDurationMs = body.result.activeCaptureDurationMs;
    report.activeCaptureDurationUncertain = body.result.activeCaptureDurationUncertain;
    if (process.argv.includes('--read-matrix')) {
      // Только чтение и заведомо неверные параметры. Контекст/источники не изменяются, захват не запускается.
      let validCalls = 0, rejectedCalls = 0;
      const call = async (name, args = {}) => {
        const result = await request('tools/call', { name, arguments: args });
        const parsed = JSON.parse(result.content.find(item => item.type === 'text').text);
        if (result.isError || !parsed.ok) throw new Error(`Неуспешный вызов ${name}`);
        validCalls++; return parsed.result;
      };
      const reject = async (name, args = {}, expectedCode) => {
        let result;
        try { result = await request('tools/call', { name, arguments: args }); }
        catch (error) {
          if (expectedCode || !/"code":-32(?:602|601)/.test(error.message)) throw error;
          rejectedCalls++; return;
        }
        if (!result.isError) throw new Error(`Неверные параметры приняты: ${name}`);
        if (expectedCode) {
          const parsed = JSON.parse(result.content.find(item => item.type === 'text').text);
          if (parsed.ok !== false || parsed.error?.code !== expectedCode) throw new Error(`Неверный код отказа: ${name}`);
        }
        rejectedCalls++;
      };
      const state = await call('get_analysis_state');
      const questions = await call('get_open_questions');
      if (!Array.isArray(questions) || !Number.isSafeInteger(state.ProcessedThroughMs)) throw new Error('Неверное состояние анализа');
      let page = await call('get_transcript', { limit: 1 });
      const total = page.total, segments = [...page.segments];
      let firstCursor = page.nextCursor;
      while (page.hasMore) {
        page = await call('get_transcript', { limit: 1, cursor: page.nextCursor });
        segments.push(...page.segments);
        if (segments.length > total) throw new Error('Пагинация вышла за размер снимка');
      }
      if (segments.length !== total || new Set(segments.map(s => s.Id)).size !== total) throw new Error('Пропуски или дубли страниц');
      for (const segment of segments.slice(0, 3)) {
        const found = await call('search_transcript', { segment_id: segment.Id });
        if (found.total !== 1 || JSON.stringify(found.segments[0]) !== JSON.stringify(segment)) throw new Error('Поиск по ID изменил оригинал');
        const byText = await call('search_transcript', { text: segment.Text.slice(0, 20) });
        if (!byText.segments.some(s => s.Id === segment.Id)) throw new Error('Поиск текста не нашёл исходный сегмент');
      }
      if (firstCursor) await reject('get_transcript', { cursor: firstCursor, text: 'другой фильтр' }, 'stale_cursor');
      await reject('get_transcript', { cursor: 'invalid-cursor' }, 'stale_cursor');
      await reject('get_transcript', { limit: 0 });
      await reject('search_transcript', { limit: 501 });
      await reject('search_transcript', { channel: 'missing-channel' });
      await reject('search_transcript', { segment_id: 'not-a-uuid' });
      await reject('get_transcript', { from_ms: 10, to_ms: 1 });
      await reject('set_context', { text: 'x'.repeat(30001) });
      await reject('set_context', {});
      const catalog = await call('list_audio_sources');
      await call('list_audio_sources');
      await reject('set_audio_sources', { catalog_version: catalog.catalogVersion, policy: 'application' }, 'stale_catalog');
      await reject('set_audio_sources', { catalog_version: -1, policy: 'application' });
      await reject('set_audio_sources', { catalog_version: catalog.catalogVersion, policy: 'invalid' });
      const after = await call('get_status');
      if (after.capture !== 'Stopped' || after.isSimulation !== body.result.isSimulation || after.sessionError !== body.result.sessionError
          || after.activeCaptureDurationMs !== body.result.activeCaptureDurationMs) throw new Error('Проверка чтения изменила состояние сессии');
      report.readMatrix = { validCalls, rejectedCalls, transcriptSegments: total, exactOriginalsChecked: Math.min(3, total), captureStarted: false };
    }
    // Только после отдельного разрешения и сигнала готовности тестового звука пользователем.
    if (process.argv.includes('--capture-brave')) {
      const call = async (name, args = {}) => {
        const result = await request('tools/call', { name, arguments: args });
        const parsed = JSON.parse(result.content.find(item => item.type === 'text').text);
        if (result.isError || !parsed.ok) throw new Error(`${name}: ${parsed.error ?? 'failed'}`);
        return parsed.result;
      };
      const catalog = await call('list_audio_sources');
      const brave = catalog.sources.filter(s => s.Kind === 'application_window' && /^Brave Browser —/.test(s.Label));
      if (brave.length !== 1 || !catalog.selection.MicrophoneSourceId) throw new Error('Requires exactly one Brave window and a previously selected microphone');
      await call('set_audio_sources', { microphone_source_id: catalog.selection.MicrophoneSourceId,
        window_source_id: brave[0].SourceId, policy: 'application' });
      let latest; let youPeak = 0, otherPeak = 0; let attempted = false;
      try {
        attempted = true;
        latest = await call('start_capture'); report.captureCommandsSent++;
        if (latest.phase !== 'Capturing' || latest.isSimulation) throw new Error('Expected real capture');
        for (let second = 0; second < 10; second++) {
          await new Promise(resolve => setTimeout(resolve, 1000));
          latest = await call('get_status');
          youPeak = Math.max(youPeak, latest.audioLevels?.you ?? 0); otherPeak = Math.max(otherPeak, latest.audioLevels?.other ?? 0);
          if (latest.phase !== 'Capturing' || latest.sessionError) throw new Error('Capture failed: ' + latest.sessionError);
        }
      } finally {
        if (attempted) { latest = await call('stop_capture'); report.captureCommandsSent++; }
      }
      if (latest.phase !== 'Stopped' || !(latest.audioTimeline?.YouSamples > 0) || !(latest.audioTimeline?.OtherSamples > 0)) throw new Error('Both channels must contain samples and stop cleanly');
      report.hardware = { seconds: 10, phase: latest.phase, youSamples: latest.audioTimeline.YouSamples,
        otherSamples: latest.audioTimeline.OtherSamples, youPeak, otherPeak, receivedThroughMs: latest.receivedThroughMs,
        finalizedThroughMs: latest.FinalizedThroughMs, retainedAudio: latest.audioRetained, modelInference: false };
      report.capture = latest.capture; report.isSimulation = latest.isSimulation;
    }
    if (process.argv.includes('--sources')) {
      const call = async (name, args = {}) => {
        const result = await request('tools/call', { name, arguments: args });
        const parsed = JSON.parse(result.content.find(item => item.type === 'text').text);
        return { result, parsed };
      };
      const first = await call('list_audio_sources');
      if (first.result.isError || !first.parsed.ok) throw new Error('Source catalog failed');
      const selection = first.parsed.result.selection;
      if (!selection.MicrophoneSourceId || !selection.WindowSourceId) throw new Error('Select microphone and window in UI before --sources');
      const args = { microphone_source_id: selection.MicrophoneSourceId, window_source_id: selection.WindowSourceId,
        catalog_version: first.parsed.result.catalogVersion,
        policy: selection.Selected.Window.Policy === 1 ? 'application' : 'specific_window' };
      for (let repeat = 0; repeat < 2; repeat++) {
        const set = await call('set_audio_sources', args);
        if (set.result.isError || !set.parsed.ok || set.parsed.result.captureStarted !== false) throw new Error('Source selection failed');
      }
      await call('list_audio_sources');
      const stale = await call('set_audio_sources', args);
      if (!stale.result.isError || stale.parsed.ok !== false) throw new Error('Stale catalog IDs accepted');
      const after = await call('get_status');
      if (after.parsed.result.phase !== 'Stopped' || after.parsed.result.audioSources.Availability !== 'available' || after.parsed.result.sessionError) throw new Error('Selection started capture, lost target or damaged session status');
      report.sourceSelectionChecked = true; report.staleIdsRejected = true; report.repeatedSelectionChecked = true;
    }
    if (process.argv.includes('--simulation-lifecycle') || process.argv.includes('--simulation-transcript')) {
      if (body.result.isSimulation !== true || body.result.phase !== 'Stopped') throw new Error('Lifecycle check requires an explicitly selected stopped simulation');
      const simulationCall = async (name, args = {}) => {
        const response = await request('tools/call', { name, arguments: args });
        const parsed = JSON.parse(response.content.find(item => item.type === 'text').text);
        if (response.isError || !parsed.ok) throw new Error('Simulation call failed: ' + name + '; ' +
          (typeof parsed.error === 'string' ? parsed.error : parsed.error?.code ?? 'unknown_error'));
        return parsed.result;
      };
      let started = false;
      try {
        for (const [name, expected] of [['start_capture','Capturing'], ['pause_capture','Paused'], ['resume_capture','Capturing'], ['stop_capture','Stopped']]) {
          if (name === 'start_capture') started = true;
          for (let repeat = 0; repeat < 2; repeat++) {
            const result = await simulationCall(name); report.captureCommandsSent++;
            if (result.phase !== expected || result.isSimulation !== true) throw new Error('Unexpected lifecycle result for ' + name);
          }
          if (name === 'start_capture' && process.argv.includes('--simulation-transcript')) {
            const deadline = Date.now() + 35000;
            const waitForSegments = async count => {
              while (Date.now() < deadline) {
                const page = await simulationCall('get_transcript', { limit: 500 });
                if (page.total >= count) return page;
                await new Promise(resolve => setTimeout(resolve, 500));
              }
              throw new Error('Имитатор не создал достаточное число окончательных сегментов');
            };
            await waitForSegments(2);
            let page = await simulationCall('get_transcript', { limit: 1 });
            const snapshotTotal = page.total, ids = page.segments.map(s => s.Id);
            // Не создаём десятки новых снимков при ожидании: их лимит сам вытеснил бы проверяемый cursor.
            const beforeGrowth = await simulationCall('get_status');
            while (Date.now() < deadline) {
              const status = await simulationCall('get_status');
              if (status.CaptureBoundaryMs >= beforeGrowth.CaptureBoundaryMs + 9000) break;
              await new Promise(resolve => setTimeout(resolve, 500));
            }
            const grown = await simulationCall('get_transcript', { limit: 500 });
            if (grown.total <= snapshotTotal) throw new Error('Не подтверждён рост расшифровки');
            while (page.hasMore) {
              page = await simulationCall('get_transcript', { limit: 1, cursor: page.nextCursor });
              ids.push(...page.segments.map(s => s.Id));
              if (ids.length > snapshotTotal) throw new Error('Рост журнала изменил снимок страниц');
            }
            if (ids.length !== snapshotTotal || new Set(ids).size !== snapshotTotal) throw new Error('Потеря или дубли страниц при росте');
            report.paginationDuringGrowth = { checked: true, snapshotTotal };
          }
          if (name === 'stop_capture') started = false;
        }
      } finally {
        if (started) { await simulationCall('stop_capture'); report.captureCommandsSent++; }
      }
      report.simulationLifecycleChecked = true;
    }
  }
  console.log(JSON.stringify(report));
} catch (error) { console.error(error.message); if (stderr) console.error(stderr); process.exitCode = 1; }
finally { fail(new Error('Check finished')); lines.close(); child.stdin.destroy(); child.kill(); }
