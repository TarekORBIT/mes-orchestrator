#!/usr/bin/env node
'use strict';

const fs = require('fs');
const path = require('path');
const crypto = require('crypto');
const net = require('net');
const os = require('os');

const DEFAULTS = {
  xmlFile: 'MES_HAI.xml',
  dllFile: 'MES_HAI.dll',
  outDirName: '.mes_precheck',
  timeoutMs: 1200,
};

function parseArgs(argv) {
  const args = {
    dir: process.cwd(),
    xmlFile: DEFAULTS.xmlFile,
    dllFile: DEFAULTS.dllFile,
    iniFile: null,
    station: null,
    errorFile: null,
    timeoutMs: DEFAULTS.timeoutMs,
    checkNetwork: false,
    selfTest: false,
    help: false,
  };

  for (let i = 2; i < argv.length; i += 1) {
    const a = argv[i];
    const next = argv[i + 1];
    if (a === '--dir' && next) {
      args.dir = next;
      i += 1;
    } else if (a === '--xml' && next) {
      args.xmlFile = next;
      i += 1;
    } else if (a === '--dll' && next) {
      args.dllFile = next;
      i += 1;
    } else if (a === '--ini' && next) {
      args.iniFile = next;
      i += 1;
    } else if (a === '--station' && next) {
      args.station = next;
      i += 1;
    } else if (a === '--error-file' && next) {
      args.errorFile = next;
      i += 1;
    } else if (a === '--timeout-ms' && next) {
      const parsed = Number(next);
      if (Number.isFinite(parsed) && parsed > 0) {
        args.timeoutMs = parsed;
      }
      i += 1;
    } else if (a === '--check-network') {
      args.checkNetwork = true;
    } else if (a === '--self-test') {
      args.selfTest = true;
    } else if (a === '--help' || a === '-h') {
      args.help = true;
    }
  }

  return args;
}

function printHelp() {
  const lines = [
    'Usage: node mes_precheck.js [options]',
    '',
    'Options:',
    '  --dir <path>           Dossier de travail (defaut: dossier courant)',
    '  --xml <name/path>      Fichier XML (defaut: MES_HAI.xml)',
    '  --dll <name/path>      Fichier DLL (defaut: MES_HAI.dll)',
    '  --ini <name/path>      Fichier INI pour StationName',
    '  --station <name>       StationName direct',
    '  --error-file <path>    Fichier JSON d erreurs a simuler (ErrorCode/ErrorDescription)',
    '  --check-network        Test TCP des serveurs XML',
    '  --timeout-ms <n>       Timeout reseau en ms (defaut: 1200)',
    '  --self-test            Execute les tests internes de fiabilite',
    '  -h, --help             Affiche cette aide',
    '',
    'Sortie:',
    '  Ecrit un dossier sandbox .mes_precheck avec rapport JSON et fichiers testes.',
  ];
  console.log(lines.join('\n'));
}

function absPath(baseDir, p) {
  if (!p) return null;
  return path.isAbsolute(p) ? p : path.join(baseDir, p);
}

function fileInfo(p) {
  if (!p || !fs.existsSync(p)) {
    return { exists: false };
  }
  const stat = fs.statSync(p);
  return {
    exists: true,
    path: p,
    size: stat.size,
    mtime: stat.mtime.toISOString(),
  };
}

function sha256File(p) {
  const hash = crypto.createHash('sha256');
  hash.update(fs.readFileSync(p));
  return hash.digest('hex');
}

function parseXmlServers(xmlText) {
  const issues = [];
  const servers = [];
  const hasConfigurationTag = /<configuration[\s>]/i.test(xmlText) && /<\/configuration>/i.test(xmlText);
  const hasServersTag = /<Servers[\s>]/i.test(xmlText) && /<\/Servers>/i.test(xmlText);

  if (!hasConfigurationTag) {
    issues.push('Tag <configuration> manquant ou invalide');
  }
  if (!hasServersTag) {
    issues.push('Tag <Servers> manquant ou invalide');
  }

  const serverRegex = /<Server\b([^>]*)\/?>/gi;
  let match;
  while ((match = serverRegex.exec(xmlText)) !== null) {
    const attrText = match[1] || '';
    const attrs = {};
    const attrRegex = /([A-Za-z0-9_]+)\s*=\s*"([^"]*)"/g;
    let attrMatch;
    while ((attrMatch = attrRegex.exec(attrText)) !== null) {
      attrs[attrMatch[1]] = attrMatch[2];
    }
    servers.push({
      IpAddress: attrs.IpAddress || '',
      Port: attrs.Port || '',
      Description: attrs.Description || '',
    });
  }

  return { servers, issues };
}

function isValidIpv4(ip) {
  const parts = ip.split('.');
  if (parts.length !== 4) return false;
  return parts.every((p) => {
    const n = Number(p);
    return String(n) === p && Number.isInteger(n) && n >= 0 && n <= 255;
  });
}

function validateServers(servers) {
  const warnings = [];
  const critical = [];
  const unique = new Set();

  if (servers.length === 0) {
    critical.push('Aucun serveur defini dans XML');
  }

  servers.forEach((s, idx) => {
    const line = `Server#${idx + 1}`;
    if (!s.IpAddress) {
      critical.push(`${line}: IpAddress vide`);
    } else if (!isValidIpv4(s.IpAddress)) {
      warnings.push(`${line}: IpAddress non IPv4 stricte (${s.IpAddress})`);
    }

    const p = Number(s.Port);
    if (!Number.isInteger(p) || p < 1 || p > 65535) {
      critical.push(`${line}: Port invalide (${s.Port})`);
    }

    const key = `${s.IpAddress}:${s.Port}`;
    if (unique.has(key)) {
      warnings.push(`${line}: doublon detecte (${key})`);
    } else {
      unique.add(key);
    }
  });

  return { warnings, critical };
}

function normalizeXml(servers) {
  const lines = ['<configuration>', '\t<Servers>'];
  for (const s of servers) {
    lines.push(`\t\t<Server IpAddress="${s.IpAddress}" Port="${s.Port}" Description="${s.Description}"/>`);
  }
  lines.push('\t</Servers>', '</configuration>');
  return `${lines.join('\n')}\n`;
}

function parseIni(text) {
  const result = {};
  let currentSection = '';

  const lines = text.split(/\r?\n/);
  for (const raw of lines) {
    const line = raw.trim();
    if (!line || line.startsWith(';') || line.startsWith('#')) continue;
    if (line.startsWith('[') && line.endsWith(']')) {
      currentSection = line.slice(1, -1).trim();
      if (!result[currentSection]) result[currentSection] = {};
      continue;
    }
    const eq = line.indexOf('=');
    if (eq < 0) continue;
    const k = line.slice(0, eq).trim();
    const v = line.slice(eq + 1).trim();
    if (currentSection) {
      if (!result[currentSection]) result[currentSection] = {};
      result[currentSection][k] = v;
    } else {
      result[k] = v;
    }
  }
  return result;
}

function pickStationName(parsedIni, explicitStation) {
  if (explicitStation && explicitStation.trim()) return explicitStation.trim();
  if (!parsedIni) return null;

  const keys = ['StationName', 'stationName', 'station_name', 'station'];
  for (const k of keys) {
    if (typeof parsedIni[k] === 'string' && parsedIni[k].trim()) return parsedIni[k].trim();
  }

  for (const section of Object.keys(parsedIni)) {
    const obj = parsedIni[section];
    if (obj && typeof obj === 'object') {
      for (const k of keys) {
        if (typeof obj[k] === 'string' && obj[k].trim()) return obj[k].trim();
      }
    }
  }
  return null;
}

function analyzeDll(dllPath) {
  const info = fileInfo(dllPath);
  const report = {
    file: info,
    critical: [],
    warnings: [],
    tokens: {},
    hashSha256: null,
    isMzBinary: false,
  };

  if (!info.exists) {
    report.critical.push('DLL introuvable');
    return report;
  }
  if (info.size < 1024) {
    report.critical.push('DLL trop petite (fichier probablement invalide)');
  }

  const buffer = fs.readFileSync(dllPath);
  report.isMzBinary = buffer.length >= 2 && buffer[0] === 0x4d && buffer[1] === 0x5a;
  if (!report.isMzBinary) {
    report.critical.push('Signature MZ absente (fichier non PE/.NET)');
  }
  report.hashSha256 = sha256File(dllPath);

  const tokenRules = [
    { name: 'MES_HAI.Traceability', anyOf: ['MES_HAI.Traceability'] },
    { name: 'ErrorDetail', anyOf: ['MES_HAI.Entity.ErrorDetail', 'MES_HAI.TraceabilityReference.ErrorDetail', 'ErrorDetail'] },
    { name: 'ErrorCode', anyOf: ['ErrorCode'] },
    { name: 'ErrorDescription', anyOf: ['ErrorDescription'] },
    { name: 'Serial_GetInformation', anyOf: ['Serial_GetInformation'] },
    { name: 'Serial_MoveIn', anyOf: ['Serial_MoveIn'] },
    { name: 'Serial_MoveOutAndTestResults', anyOf: ['Serial_MoveOutAndTestResults'] },
    { name: 'User_Login', anyOf: ['User_Login'] },
    { name: 'MESApps_CIM_PathHint', anyOf: ['\\MESApps\\CIM\\MES_HAI.xml', '\\MESApps\\CIM\\', '/MESApps/CIM/'] },
  ];

  const containsToken = (candidate) => {
    const utf8 = Buffer.from(candidate, 'utf8');
    const utf16 = Buffer.from(candidate, 'utf16le');
    return buffer.includes(utf8) || buffer.includes(utf16);
  };

  for (const rule of tokenRules) {
    const present = rule.anyOf.some((candidate) => containsToken(candidate));
    report.tokens[rule.name] = present;
    if (!present) {
      report.warnings.push(`Token non detecte dans DLL: ${rule.name}`);
    }
  }

  return report;
}

function parseErrorInputFile(errorFilePath) {
  if (!errorFilePath || !fs.existsSync(errorFilePath)) return [];
  const raw = fs.readFileSync(errorFilePath, 'utf8').trim();
  if (!raw) return [];

  try {
    const parsed = JSON.parse(raw);
    if (Array.isArray(parsed)) return parsed;
    if (parsed && typeof parsed === 'object') return [parsed];
  } catch (_) {
    // fallback no-op
  }

  return [];
}

function classifyErrorDetail(detail) {
  const code = Number(detail?.ErrorCode);
  const description = String(detail?.ErrorDescription || '').trim();
  const descLower = description.toLowerCase();

  if (!Number.isFinite(code)) {
    return {
      action: 'BLOCK_AND_ESCALATE',
      severity: 'high',
      reason: 'ErrorCode invalide/non numerique',
    };
  }

  if (code === 0) {
    return {
      action: 'CONTINUE_FLOW',
      severity: 'none',
      reason: 'Retour MES nominal',
    };
  }

  if (descLower.includes('notlogged') || descLower.includes('not logged') || descLower.includes('session')) {
    return {
      action: 'RELOGIN_AND_RETRY_ONCE',
      severity: 'medium',
      reason: 'Session MES non connectee',
    };
  }

  if (descLower.includes('notregistered') || descLower.includes('station')) {
    return {
      action: 'BLOCK_AND_CHECK_STATION_CONFIG',
      severity: 'high',
      reason: 'Probleme station/configuration',
    };
  }

  if (descLower.includes('timeout') || descLower.includes('connection') || descLower.includes('network')) {
    return {
      action: 'SWITCH_SERVER_AND_RETRY_ONCE',
      severity: 'high',
      reason: 'Probleme reseau/serveur',
    };
  }

  return {
    action: 'BLOCK_PART_AND_CREATE_INCIDENT',
    severity: 'high',
    reason: 'Erreur metier non classifiee',
  };
}

function buildErrorSimulationInputs(fromFile) {
  if (fromFile.length > 0) return fromFile;
  return [
    { ErrorCode: 0, ErrorDescription: 'OK' },
    { ErrorCode: 3, ErrorDescription: 'StationNotLogged' },
    { ErrorCode: 101, ErrorDescription: 'Connection timeout' },
    { ErrorCode: 210, ErrorDescription: 'NotRegistered station' },
    { ErrorCode: 999, ErrorDescription: 'Unexpected CIM error' },
  ];
}

function checkTcp(ip, port, timeoutMs) {
  return new Promise((resolve) => {
    const socket = new net.Socket();
    let finished = false;
    const done = (status, message = '') => {
      if (finished) return;
      finished = true;
      socket.destroy();
      resolve({ ip, port, status, message });
    };

    socket.setTimeout(timeoutMs);
    socket.once('connect', () => done('open'));
    socket.once('timeout', () => done('timeout', `timeout ${timeoutMs}ms`));
    socket.once('error', (err) => done('error', err.message));

    try {
      socket.connect(port, ip);
    } catch (e) {
      done('error', e.message);
    }
  });
}

async function checkServersConnectivity(servers, timeoutMs) {
  const results = [];
  for (const s of servers) {
    const port = Number(s.Port);
    if (!Number.isInteger(port)) {
      results.push({
        ip: s.IpAddress,
        port: s.Port,
        status: 'skipped',
        message: 'invalid_port',
      });
      continue;
    }
    // eslint-disable-next-line no-await-in-loop
    const res = await checkTcp(s.IpAddress, port, timeoutMs);
    results.push(res);
  }
  return results;
}

function computeScore(parts) {
  let score = 100;
  score -= parts.criticalCount * 20;
  score -= parts.warningCount * 5;
  if (score < 0) score = 0;
  return score;
}

function runSelfTests() {
  const scenarios = [
    [{ ErrorCode: 0, ErrorDescription: 'OK' }, 'CONTINUE_FLOW'],
    [{ ErrorCode: 5, ErrorDescription: 'StationNotLogged' }, 'RELOGIN_AND_RETRY_ONCE'],
    [{ ErrorCode: 9, ErrorDescription: 'NotRegistered station' }, 'BLOCK_AND_CHECK_STATION_CONFIG'],
    [{ ErrorCode: 11, ErrorDescription: 'connection timeout' }, 'SWITCH_SERVER_AND_RETRY_ONCE'],
    [{ ErrorCode: 99, ErrorDescription: 'Unknown problem' }, 'BLOCK_PART_AND_CREATE_INCIDENT'],
  ];

  const failures = [];
  for (const [input, expected] of scenarios) {
    const got = classifyErrorDetail(input).action;
    if (got !== expected) {
      failures.push({ input, expected, got });
    }
  }
  return {
    passed: failures.length === 0,
    total: scenarios.length,
    failures,
  };
}

function buildStationTemplate(stationName) {
  const sn = stationName || 'STATION_NAME_HERE';
  return [
    '[MES]',
    `StationName=${sn}`,
    'TimeoutMs=1200',
    'RetryCount=1',
    '',
  ].join('\n');
}

async function main() {
  const args = parseArgs(process.argv);
  if (args.help) {
    printHelp();
    return;
  }

  const baseDir = path.resolve(args.dir);
  const xmlPath = absPath(baseDir, args.xmlFile);
  const dllPath = absPath(baseDir, args.dllFile);
  const iniPath = args.iniFile ? absPath(baseDir, args.iniFile) : null;
  const errorFilePath = args.errorFile ? absPath(baseDir, args.errorFile) : null;
  const outDir = path.join(baseDir, DEFAULTS.outDirName);

  const critical = [];
  const warnings = [];
  const recommendations = [];

  const xmlInfo = fileInfo(xmlPath);
  const dllReport = analyzeDll(dllPath);

  let xmlRaw = '';
  let xmlParsed = { servers: [], issues: [] };
  let xmlValidated = { warnings: [], critical: [] };

  if (!xmlInfo.exists) {
    critical.push('MES_HAI.xml introuvable');
  } else {
    xmlRaw = fs.readFileSync(xmlPath, 'utf8');
    xmlParsed = parseXmlServers(xmlRaw);
    xmlValidated = validateServers(xmlParsed.servers);
    if (xmlParsed.issues.length > 0) warnings.push(...xmlParsed.issues);
    warnings.push(...xmlValidated.warnings);
    critical.push(...xmlValidated.critical);
  }

  critical.push(...dllReport.critical);
  warnings.push(...dllReport.warnings);

  let iniParsed = null;
  if (iniPath && fs.existsSync(iniPath)) {
    iniParsed = parseIni(fs.readFileSync(iniPath, 'utf8'));
  }
  const stationName = pickStationName(iniParsed, args.station);
  if (!stationName) {
    warnings.push('StationName absent (fournir --station ou --ini)');
    recommendations.push('Renseigner StationName dans un .ini pour reproduire le flux machine');
  }

  let networkResults = [];
  if (args.checkNetwork && xmlParsed.servers.length > 0) {
    networkResults = await checkServersConnectivity(xmlParsed.servers, args.timeoutMs);
    const hasOpen = networkResults.some((r) => r.status === 'open');
    if (!hasOpen) {
      warnings.push('Aucun endpoint MES joignable pendant le test reseau');
      recommendations.push('Verifier VPN/routage/firewall avant de tester sur machine');
    }
  }

  const fileErrors = parseErrorInputFile(errorFilePath);
  const errorSamples = buildErrorSimulationInputs(fileErrors);
  const errorActionResults = errorSamples.map((sample, idx) => ({
    sampleId: idx + 1,
    input: {
      ErrorCode: sample?.ErrorCode,
      ErrorDescription: sample?.ErrorDescription,
    },
    decision: classifyErrorDetail(sample),
  }));

  const selfTest = args.selfTest ? runSelfTests() : null;
  if (selfTest && !selfTest.passed) {
    critical.push('Echec des auto-tests de politique ErrorDetail');
  }

  if (xmlParsed.servers.length >= 2) {
    recommendations.push('Conserver les 2 serveurs (primary/secondary) pour le fallback');
  }
  if (dllReport.tokens['MES_HAI.Entity.ErrorDetail'] === false) {
    recommendations.push('Verifier que la DLL est bien la version attendue (ErrorDetail non detecte)');
  }
  if (dllReport.tokens['\\MESApps\\CIM\\MES_HAI.xml'] === false) {
    recommendations.push('Verifier le chemin de config MES_HAI.xml dans le runtime client');
  }

  const summary = {
    criticalCount: critical.length,
    warningCount: warnings.length,
    score: computeScore({
      criticalCount: critical.length,
      warningCount: warnings.length,
    }),
    status: critical.length === 0 ? 'READY_FOR_STAGING_TEST' : 'NOT_READY',
  };

  const report = {
    generatedAt: new Date().toISOString(),
    host: os.hostname(),
    nodeVersion: process.version,
    workingDir: baseDir,
    inputs: {
      xml: xmlPath,
      dll: dllPath,
      ini: iniPath,
      station: stationName,
      errorFile: errorFilePath,
      checkNetwork: args.checkNetwork,
      timeoutMs: args.timeoutMs,
    },
    summary,
    checks: {
      xml: {
        file: xmlInfo,
        serverCount: xmlParsed.servers.length,
        servers: xmlParsed.servers,
        issues: xmlParsed.issues,
        validation: xmlValidated,
      },
      dll: dllReport,
      ini: {
        file: iniPath ? fileInfo(iniPath) : { exists: false },
        stationName,
      },
      network: networkResults,
    },
    errorActionResults,
    critical,
    warnings,
    recommendations,
    selfTest,
  };

  fs.mkdirSync(outDir, { recursive: true });

  if (xmlInfo.exists) {
    fs.copyFileSync(xmlPath, path.join(outDir, path.basename(xmlPath)));
    const normalizedXml = normalizeXml(xmlParsed.servers);
    fs.writeFileSync(path.join(outDir, 'MES_HAI.normalized.xml'), normalizedXml, 'utf8');
  }
  if (dllReport.file.exists) {
    fs.copyFileSync(dllPath, path.join(outDir, path.basename(dllPath)));
  }
  if (iniPath && fs.existsSync(iniPath)) {
    fs.copyFileSync(iniPath, path.join(outDir, path.basename(iniPath)));
  } else {
    fs.writeFileSync(path.join(outDir, 'station.template.ini'), buildStationTemplate(stationName), 'utf8');
  }

  fs.writeFileSync(path.join(outDir, 'error_actions.json'), JSON.stringify(errorActionResults, null, 2), 'utf8');
  fs.writeFileSync(path.join(outDir, 'reliability_report.json'), JSON.stringify(report, null, 2), 'utf8');

  const consoleSummary = {
    status: summary.status,
    score: summary.score,
    criticalCount: summary.criticalCount,
    warningCount: summary.warningCount,
    report: path.join(outDir, 'reliability_report.json'),
  };
  console.log(JSON.stringify(consoleSummary, null, 2));
}

main().catch((err) => {
  console.error('Fatal:', err && err.stack ? err.stack : String(err));
  process.exitCode = 1;
});
