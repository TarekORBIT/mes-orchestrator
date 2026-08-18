#!/usr/bin/env node
'use strict';

const fs = require('fs');
const path = require('path');
const http = require('http');
const { spawn } = require('child_process');
const os = require('os');

const DEFAULT_CONFIG = {
  stationName: 'STATION_NAME_HERE',
  bridgeExePath: 'C:\\MESApps\\ClientGateway\\bridge\\MesHaiBridge.exe',
  haiDllPath: 'C:\\MESApps\\ClientGateway\\bridge\\MES_HAI.dll',
  haiXmlPath: 'C:\\ProgramData\\MESApps\\CIM\\MES_HAI.xml',
  haiInstanceName: 'MES_HAI',
  host: '127.0.0.1',
  port: 7070,
  bridgeTimeoutMs: 20000,
  defaultLayer: 0,
  defaultActivateWorkOrder: true,
  defaultCheckMultiBoard: false,
  logDir: 'C:\\MESApps\\ClientGateway\\logs',
  logFileName: 'mes-orchestrator.log',
  // testMode: 'mock' = simulation locale, 'real' = réseau Visteon réel
  testMode: 'mock',
};

// ── Mock Bridge (simulation locale sans DLL) ─────────────────────────────────
function createMockBridgeRunner(config, logger) {
  const mockSerials = {
    'SN001': { PartNumber: 'PN-VISTEON-001', WorkOrder: 'WO-2026-001', Status: 'Active' },
    'SN002': { PartNumber: 'PN-VISTEON-002', WorkOrder: 'WO-2026-002', Status: 'Active' },
  };

  async function runBridge(action, payload) {
    const station = payload.station || config.stationName;
    const serial = payload.serialNumber || '';
    const ts = new Date().toISOString();

    logger.info('mock_bridge_call', { action, station, serial, mode: 'mock' });

    // Simulate small network delay
    await new Promise(r => setTimeout(r, 120));

    if (action === 'login') {
      const ok = !!station && station !== 'STATION_NAME_HERE';
      return {
        ok,
        action: 'login',
        timestampUtc: ts,
        result: ok ? { name: 'Connected', value: 1 } : null,
        errorDetail: ok ? { ErrorCode: 0, ErrorDescription: 'OK' } : { ErrorCode: 3, ErrorDescription: 'StationNotRegistered: station name is empty or invalid' },
        diagnostics: { assembly: 'MES_HAI [MOCK]', assemblyVersion: '5.2.0.101', dllPath: config.haiDllPath },
      };
    }

    if (action === 'get-info') {
      const info = mockSerials[serial] || null;
      if (!info) {
        return {
          ok: false, action: 'get-info', timestampUtc: ts, result: null,
          errorDetail: { ErrorCode: 102, ErrorDescription: `SerialNotFound: serial '${serial}' not found in MES` },
          diagnostics: { assembly: 'MES_HAI [MOCK]' },
        };
      }
      return {
        ok: true, action: 'get-info', timestampUtc: ts,
        result: { SerialInformation: { SerialNumber: serial, ...info } },
        errorDetail: { ErrorCode: 0, ErrorDescription: 'OK' },
        diagnostics: { assembly: 'MES_HAI [MOCK]' },
      };
    }

    if (action === 'move-in') {
      const info = mockSerials[serial];
      if (!info) {
        return {
          ok: false, action: 'move-in', timestampUtc: ts, result: null,
          errorDetail: { ErrorCode: 102, ErrorDescription: `SerialNotFound: serial '${serial}' not found in MES` },
          diagnostics: { assembly: 'MES_HAI [MOCK]' },
        };
      }
      return {
        ok: true, action: 'move-in', timestampUtc: ts,
        result: { Result: { name: 'Pass', value: 1 }, ResultMessage: 'MoveIn accepted', UnitId: serial, MoveInTime: ts, WorkOrderName: info.WorkOrder },
        errorDetail: { ErrorCode: 0, ErrorDescription: 'OK' },
        diagnostics: { assembly: 'MES_HAI [MOCK]' },
      };
    }

    if (action === 'move-out' || action === 'move-out-and-test') {
      const result = payload.result || 'Pass';
      const isPass = result === 'Pass';
      return {
        ok: true, action, timestampUtc: ts,
        result: null,
        errorDetail: { ErrorCode: 0, ErrorDescription: 'OK' },
        diagnostics: { assembly: 'MES_HAI [MOCK]' },
      };
    }

    return {
      ok: false, action, timestampUtc: ts, result: null,
      errorDetail: { ErrorCode: 999, ErrorDescription: `UnknownAction: '${action}' not handled in mock` },
      diagnostics: { assembly: 'MES_HAI [MOCK]' },
    };
  }

  return { runBridge };
}

// Module-level state so config routes can read/write it
let activeCfgPath = null;
let activeConfig = null;

function parseArgs(argv) {
  const out = { configPath: null };
  for (let i = 2; i < argv.length; i += 1) {
    if (argv[i] === '--config' && argv[i + 1]) {
      out.configPath = argv[i + 1];
      i += 1;
    }
  }
  return out;
}

function readJsonFile(p) {
  const raw = fs.readFileSync(p, 'utf8').replace(/^\uFEFF/, '');
  return JSON.parse(raw);
}

function ensureDir(p) {
  fs.mkdirSync(p, { recursive: true });
}

function safeNow() {
  return new Date().toISOString();
}

function createLogger(logDir, logFileName) {
  ensureDir(logDir);
  const logPath = path.join(logDir, logFileName);
  const sseClients = new Set();

  function write(level, message, details) {
    const entry = { ts: safeNow(), level, message, details: details || null };
    const line = JSON.stringify(entry);
    fs.appendFileSync(logPath, `${line}\n`, 'utf8');
    const sseData = `data: ${line}\n\n`;
    for (const client of [...sseClients]) {
      try {
        client.write(sseData);
      } catch (_) {
        sseClients.delete(client);
      }
    }
  }

  return {
    info: (msg, d) => write('info', msg, d),
    warn: (msg, d) => write('warn', msg, d),
    error: (msg, d) => write('error', msg, d),
    path: logPath,
    addSseClient(res) { sseClients.add(res); },
    removeSseClient(res) { sseClients.delete(res); },
  };
}

function loadConfig(configPath) {
  const cfg = { ...DEFAULT_CONFIG };
  if (configPath && fs.existsSync(configPath)) {
    const parsed = readJsonFile(configPath);
    Object.assign(cfg, parsed);
  }
  return cfg;
}

function parseMesServers(xmlPath) {
  if (!xmlPath || !fs.existsSync(xmlPath)) return [];
  try {
    const content = fs.readFileSync(xmlPath, 'utf8');
    const servers = [];
    const re = /<Server\s+([^/]*?)\/?>/gi;
    let m;
    while ((m = re.exec(content)) !== null) {
      const attrs = m[1];
      const ip = (/IpAddress="([^"]+)"/i.exec(attrs) || [])[1];
      const port = (/Port="([^"]+)"/i.exec(attrs) || [])[1];
      const desc = (/Description="([^"]+)"/i.exec(attrs) || [])[1];
      if (ip) servers.push({ ip, port: port || '8634', description: desc || '' });
    }
    return servers;
  } catch {
    return [];
  }
}

function readLastLines(filePath, n) {
  if (!fs.existsSync(filePath)) return [];
  const content = fs.readFileSync(filePath, 'utf8');
  const lines = content.trim().split('\n').filter(Boolean);
  return lines.slice(-n).map((l) => {
    try { return JSON.parse(l); } catch { return { ts: '', level: 'raw', message: l, details: null }; }
  });
}

function serveFile(res, filePath, contentType) {
  if (!fs.existsSync(filePath)) {
    const body = JSON.stringify({ ok: false, error: 'not_found', path: filePath });
    res.writeHead(404, { 'Content-Type': 'application/json' });
    res.end(body);
    return;
  }
  const content = fs.readFileSync(filePath);
  res.writeHead(200, { 'Content-Type': contentType, 'Content-Length': content.length });
  res.end(content);
}

function sendJson(res, statusCode, payload) {
  const body = JSON.stringify(payload);
  res.writeHead(statusCode, {
    'Content-Type': 'application/json; charset=utf-8',
    'Content-Length': Buffer.byteLength(body),
  });
  res.end(body);
}

function readBody(req) {
  return new Promise((resolve, reject) => {
    const chunks = [];
    req.on('data', (chunk) => chunks.push(chunk));
    req.on('end', () => {
      const raw = Buffer.concat(chunks).toString('utf8').trim();
      if (!raw) { resolve({}); return; }
      try { resolve(JSON.parse(raw)); } catch (e) { reject(new Error(`Invalid JSON body: ${e.message}`)); }
    });
    req.on('error', reject);
  });
}

function classifyErrorDetail(errorDetail) {
  const code = Number(errorDetail?.ErrorCode);
  const description = String(errorDetail?.ErrorDescription || '').trim();
  const descLower = description.toLowerCase();

  if (!Number.isFinite(code)) {
    return { action: 'BLOCK_AND_ESCALATE', reason: 'ErrorCode invalide', severity: 'high' };
  }
  if (code === 0) {
    return { action: 'CONTINUE_FLOW', reason: 'ErrorCode=0', severity: 'none' };
  }
  if (descLower.includes('notlogged') || descLower.includes('not logged')) {
    return { action: 'RELOGIN_AND_RETRY_ONCE', reason: 'Session non connectee', severity: 'medium' };
  }
  if (descLower.includes('notregistered') || descLower.includes('station')) {
    return { action: 'BLOCK_AND_CHECK_STATION_CONFIG', reason: 'Station non valide', severity: 'high' };
  }
  if (descLower.includes('timeout') || descLower.includes('connection') || descLower.includes('network')) {
    return { action: 'SWITCH_SERVER_AND_RETRY_ONCE', reason: 'Defaut reseau', severity: 'high' };
  }
  return { action: 'BLOCK_PART_AND_CREATE_INCIDENT', reason: 'Erreur non classifiee', severity: 'high' };
}

function createBridgeRunner(config, logger) {
  async function runBridge(action, payload) {
    const exe = config.bridgeExePath;
    if (!fs.existsSync(exe)) {
      throw new Error(`Bridge executable introuvable: ${exe}`);
    }

    const request = {
      action,
      haiDllPath: config.haiDllPath,
      haiInstanceName: config.haiInstanceName,
      station: payload.station || config.stationName,
      user: payload.user,
      password: payload.password,
      serialNumber: payload.serialNumber,
      activateWorkOrder: payload.activateWorkOrder,
      layer: payload.layer,
      result: payload.result,
      resultCode: payload.resultCode,
      groupId: payload.groupId,
      groupVersion: payload.groupVersion,
      checkMultiBoard: payload.checkMultiBoard,
      measures: payload.measures,
    };

    return new Promise((resolve, reject) => {
      const child = spawn(exe, [], { windowsHide: true, stdio: ['pipe', 'pipe', 'pipe'] });
      let stdout = '';
      let stderr = '';
      let completed = false;

      const timeout = setTimeout(() => {
        if (completed) return;
        completed = true;
        child.kill('SIGKILL');
        reject(new Error(`Bridge timeout (${config.bridgeTimeoutMs}ms)`));
      }, config.bridgeTimeoutMs);

      child.stdout.on('data', (d) => { stdout += d.toString('utf8'); });
      child.stderr.on('data', (d) => { stderr += d.toString('utf8'); });

      child.on('error', (err) => {
        if (completed) return;
        completed = true;
        clearTimeout(timeout);
        reject(err);
      });

      child.on('close', (code) => {
        if (completed) return;
        completed = true;
        clearTimeout(timeout);

        if (!stdout.trim()) {
          reject(new Error(`Bridge returned no output. exit=${code}, stderr=${stderr.trim()}`));
          return;
        }

        let parsed;
        try {
          parsed = JSON.parse(stdout);
        } catch (e) {
          reject(new Error(`Bridge JSON parse failed: ${e.message}. Raw=${stdout.slice(0, 600)}`));
          return;
        }

        if (stderr.trim()) {
          logger.warn('bridge_stderr', { action, stderr: stderr.trim() });
        }

        resolve(parsed);
      });

      child.stdin.write(JSON.stringify(request));
      child.stdin.end();
    });
  }

  return { runBridge };
}

function normalizePartNumberFromGetInfo(result) {
  const info = result?.result?.SerialInformation || result?.SerialInformation || null;
  if (!info) return null;
  return info.PartNumber || null;
}

async function start() {
  const args = parseArgs(process.argv);
  activeCfgPath = args.configPath
    ? path.resolve(args.configPath)
    : path.resolve(process.cwd(), '..', 'config', 'client-config.json');

  activeConfig = loadConfig(activeCfgPath);
  const logger = createLogger(activeConfig.logDir, activeConfig.logFileName);
  const isMock = (activeConfig.testMode || 'mock') === 'mock';
  const bridge = isMock ? createMockBridgeRunner(activeConfig, logger) : createBridgeRunner(activeConfig, logger);
  logger.info('bridge_mode', { mode: isMock ? 'MOCK_LOCAL' : 'REAL_VISTEON' });

  const state = {
    startedAt: Date.now(),
    lastLogin: null,
    lastError: null,
  };

  logger.info('orchestrator_start', {
    host: os.hostname(),
    node: process.version,
    configPath: activeCfgPath,
    bind: `${activeConfig.host}:${activeConfig.port}`,
  });

  const server = http.createServer(async (req, res) => {
    const rawUrl = req.url || '/';
    // Strip query string for route matching, keep full URL for query parsing
    const url = rawUrl.split('?')[0];
    const method = req.method || 'GET';

    // CORS for local dashboard
    res.setHeader('Access-Control-Allow-Origin', '*');
    res.setHeader('Access-Control-Allow-Methods', 'GET,POST,OPTIONS');
    res.setHeader('Access-Control-Allow-Headers', 'Content-Type');
    if (method === 'OPTIONS') { res.writeHead(204); res.end(); return; }

    try {
      // ── Favicon (évite 404 dans les logs navigateur) ─────────────────
      if (method === 'GET' && url === '/favicon.ico') {
        res.writeHead(204); res.end(); return;
      }

      // ── Dashboard UI ────────────────────────────────────────────────
      if (method === 'GET' && (url === '/' || url === '/index.html')) {
        serveFile(res, path.join(__dirname, 'public', 'index.html'), 'text/html; charset=utf-8');
        return;
      }

      // ── Config API ───────────────────────────────────────────────────
      if (method === 'GET' && url === '/api/config') {
        sendJson(res, 200, { ok: true, config: activeConfig, configPath: activeCfgPath });
        return;
      }

      if (method === 'POST' && url === '/api/config') {
        const body = await readBody(req);
        const newConfig = { ...activeConfig, ...body };
        ensureDir(path.dirname(activeCfgPath));
        fs.writeFileSync(activeCfgPath, JSON.stringify(newConfig, null, 2), 'utf8');
        Object.assign(activeConfig, newConfig);
        logger.info('config_saved', { configPath: activeCfgPath });
        sendJson(res, 200, { ok: true, config: activeConfig });
        return;
      }

      // ── Logs SSE stream ──────────────────────────────────────────────
      if (method === 'GET' && url.startsWith('/api/logs/stream')) {
        res.writeHead(200, {
          'Content-Type': 'text/event-stream',
          'Cache-Control': 'no-cache',
          'Connection': 'keep-alive',
        });
        res.write(`data: ${JSON.stringify({ ts: safeNow(), level: 'info', message: 'stream_connected', details: null })}\n\n`);
        logger.addSseClient(res);
        req.on('close', () => logger.removeSseClient(res));
        return;
      }

      // ── Logs REST ────────────────────────────────────────────────────
      if (method === 'GET' && url.startsWith('/api/logs')) {
        const qs = rawUrl.includes('?') ? rawUrl.slice(rawUrl.indexOf('?') + 1) : '';
        const params = Object.fromEntries(new URLSearchParams(qs));
        const limit = Math.min(parseInt(params.limit || '200', 10), 2000);
        const level = params.level || null;
        let lines = readLastLines(logger.path, limit);
        if (level && level !== 'all') lines = lines.filter((l) => l.level === level);
        sendJson(res, 200, { ok: true, lines, count: lines.length, logPath: logger.path });
        return;
      }

      // ── Health ───────────────────────────────────────────────────────
      if (method === 'GET' && url === '/health') {
        const mockMode = isMock;
        sendJson(res, 200, {
          ok: true,
          service: 'mes-orchestrator',
          uptimeSec: Math.floor((Date.now() - state.startedAt) / 1000),
          stationName: activeConfig.stationName,
          testMode: activeConfig.testMode || 'mock',
          bridgeExeExists: mockMode ? null : fs.existsSync(activeConfig.bridgeExePath),
          dllExists: mockMode ? null : fs.existsSync(activeConfig.haiDllPath),
          xmlExists: fs.existsSync(activeConfig.haiXmlPath || ''),
          lastLogin: state.lastLogin,
          lastError: state.lastError,
          ts: safeNow(),
        });
        return;
      }

      // ── MES Servers (parsed from XML) ────────────────────────────────
      if (method === 'GET' && url === '/api/mes-servers') {
        const xmlPath = activeConfig.haiXmlPath || '';
        const exists = fs.existsSync(xmlPath);
        const servers = parseMesServers(xmlPath);
        sendJson(res, 200, { ok: true, xmlPath, xmlExists: exists, servers });
        return;
      }

      // ── Switch mode (mock <-> real) — restart required ───────────────
      if (method === 'POST' && url === '/api/mode') {
        const body = await readBody(req);
        const mode = body.mode === 'real' ? 'real' : 'mock';
        activeConfig.testMode = mode;
        if (fs.existsSync(activeCfgPath)) {
          const saved = JSON.parse(fs.readFileSync(activeCfgPath, 'utf8'));
          saved.testMode = mode;
          fs.writeFileSync(activeCfgPath, JSON.stringify(saved, null, 2), 'utf8');
        }
        logger.info('mode_switched', { mode, note: 'restart required to apply' });
        sendJson(res, 200, { ok: true, testMode: mode, note: 'Redemarrer le serveur pour appliquer' });
        return;
      }

      // ── Login ────────────────────────────────────────────────────────
      if (method === 'POST' && url === '/v1/login') {
        const body = await readBody(req);
        const response = await bridge.runBridge('login', {
          station: body.station || activeConfig.stationName,
          user: body.user,
          password: body.password,
        });
        state.lastLogin = { ts: safeNow(), ok: !!response.ok, station: body.station || activeConfig.stationName };
        if (!response.ok) state.lastError = response.error || null;
        logger.info('login', { station: body.station || activeConfig.stationName, ok: response.ok, errorDetail: response.errorDetail });
        sendJson(res, response.ok ? 200 : 502, response);
        return;
      }

      // ── Get Info ─────────────────────────────────────────────────────
      if (method === 'POST' && url === '/v1/get-info') {
        const body = await readBody(req);
        if (!body.serialNumber) { sendJson(res, 400, { ok: false, error: 'serialNumber is required' }); return; }
        const response = await bridge.runBridge('get-info', {
          station: body.station || activeConfig.stationName,
          serialNumber: body.serialNumber,
        });
        logger.info('get_info', { serial: body.serialNumber, ok: response.ok, errorDetail: response.errorDetail });
        sendJson(res, response.ok ? 200 : 502, response);
        return;
      }

      // ── Move In (full flow) ──────────────────────────────────────────
      if (method === 'POST' && url === '/v1/move-in') {
        const body = await readBody(req);
        if (!body.serialNumber) { sendJson(res, 400, { ok: false, error: 'serialNumber is required' }); return; }

        const station = body.station || activeConfig.stationName;
        const layer = Number.isInteger(body.layer) ? body.layer : activeConfig.defaultLayer;
        const activateWorkOrder = typeof body.activateWorkOrder === 'boolean'
          ? body.activateWorkOrder : activeConfig.defaultActivateWorkOrder;

        const steps = [];

        const login = await bridge.runBridge('login', { station, user: body.user, password: body.password });
        steps.push({ step: 'login', ok: !!login.ok, response: login });
        if (!login.ok) {
          logger.warn('move_in_login_failed', { station, serial: body.serialNumber });
          sendJson(res, 502, { ok: false, flow: 'move-in', steps }); return;
        }

        const info = await bridge.runBridge('get-info', { station, serialNumber: body.serialNumber });
        steps.push({ step: 'get-info', ok: !!info.ok, response: info });
        if (!info.ok) {
          logger.warn('move_in_getinfo_failed', { station, serial: body.serialNumber });
          sendJson(res, 502, { ok: false, flow: 'move-in', steps }); return;
        }

        const mesPart = normalizePartNumberFromGetInfo(info);
        if (body.expectedPartNumber && mesPart && String(body.expectedPartNumber) !== String(mesPart)) {
          sendJson(res, 409, { ok: false, flow: 'move-in', error: 'part_number_mismatch',
            expectedPartNumber: body.expectedPartNumber, mesPartNumber: mesPart, steps });
          return;
        }

        const moveIn = await bridge.runBridge('move-in', { station, serialNumber: body.serialNumber, layer, activateWorkOrder });
        steps.push({ step: 'move-in', ok: !!moveIn.ok, response: moveIn });
        logger.info('move_in', { station, serial: body.serialNumber, ok: moveIn.ok, errorDetail: moveIn.errorDetail });
        sendJson(res, moveIn.ok ? 200 : 502, { ok: !!moveIn.ok, flow: 'move-in', mesPartNumber: mesPart, steps });
        return;
      }

      // ── Move Out + Test Results ───────────────────────────────────────
      if (method === 'POST' && url === '/v1/move-out-and-test') {
        const body = await readBody(req);
        if (!body.serialNumber) { sendJson(res, 400, { ok: false, error: 'serialNumber is required' }); return; }

        const station = body.station || activeConfig.stationName;
        const payload = {
          station,
          serialNumber: body.serialNumber,
          result: body.result || 'Pass',
          resultCode: body.resultCode,
          groupId: body.groupId || '',
          groupVersion: body.groupVersion || '',
          measures: Array.isArray(body.measures) ? body.measures : [],
          layer: Number.isInteger(body.layer) ? body.layer : activeConfig.defaultLayer,
          checkMultiBoard: typeof body.checkMultiBoard === 'boolean' ? body.checkMultiBoard : activeConfig.defaultCheckMultiBoard,
        };

        const login = await bridge.runBridge('login', { station, user: body.user, password: body.password });
        if (!login.ok) {
          sendJson(res, 502, { ok: false, flow: 'move-out-and-test', step: 'login', response: login }); return;
        }

        const response = await bridge.runBridge('move-out-and-test', payload);
        const decision = classifyErrorDetail(response.errorDetail);
        logger.info('move_out_and_test', { station, serial: body.serialNumber, ok: response.ok, decision, errorDetail: response.errorDetail });
        sendJson(res, response.ok ? 200 : 502, { ok: !!response.ok, flow: 'move-out-and-test', response, decision });
        return;
      }

      // ── Raw bridge passthrough ────────────────────────────────────────
      if (method === 'POST' && url === '/v1/bridge') {
        const body = await readBody(req);
        if (!body.action) { sendJson(res, 400, { ok: false, error: 'action is required' }); return; }
        const response = await bridge.runBridge(body.action, body);
        logger.info('bridge_raw', { action: body.action, ok: response.ok, errorDetail: response.errorDetail });
        sendJson(res, response.ok ? 200 : 502, response);
        return;
      }

      sendJson(res, 404, { ok: false, error: 'route_not_found', method, url });
    } catch (err) {
      const message = err && err.message ? err.message : String(err);
      state.lastError = { ts: safeNow(), message };
      logger.error('request_failed', { method, url, message });
      sendJson(res, 500, { ok: false, error: message });
    }
  });

  server.listen(activeConfig.port, activeConfig.host, () => {
    logger.info('http_listening', { host: activeConfig.host, port: activeConfig.port });
    console.log(JSON.stringify({
      ok: true,
      service: 'mes-orchestrator',
      dashboard: `http://${activeConfig.host}:${activeConfig.port}/`,
      host: activeConfig.host,
      port: activeConfig.port,
      station: activeConfig.stationName,
      configPath: activeCfgPath,
      logFile: logger.path,
    }));
  });

  process.on('uncaughtException', (err) => {
    logger.error('uncaught_exception', { message: err.message, stack: err.stack });
  });
  process.on('unhandledRejection', (reason) => {
    logger.error('unhandled_rejection', { reason: String(reason) });
  });
}

start().catch((err) => {
  console.error(err && err.stack ? err.stack : String(err));
  process.exit(1);
});
