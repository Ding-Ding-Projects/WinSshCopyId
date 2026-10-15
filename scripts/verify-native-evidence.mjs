#!/usr/bin/env node
import { createHash } from 'node:crypto';
import { execFileSync } from 'node:child_process';
import { readFileSync, realpathSync } from 'node:fs';
import { dirname, isAbsolute, relative, resolve } from 'node:path';
import { pathToFileURL } from 'node:url';

const requireValue = (condition, message) => { if (!condition) throw new Error(message); };
const digest = bytes => createHash('sha256').update(bytes).digest('hex');
const hashPattern = /^[a-f0-9]{64}$/;
const text = v => typeof v === 'string' && v.trim().length > 0;
const integer = v => Number.isSafeInteger(v) && v > 0;
const json = path => JSON.parse(readFileSync(path, 'utf8').replace(/^\uFEFF/, ''));
const time = (value, label) => { const n = Date.parse(value); requireValue(text(value) && Number.isFinite(n), `${label}: invalid timestamp`); return n; };
function contained(root, path) {
  requireValue(text(path), 'Missing file path');
  const actual = realpathSync(resolve(root, path));
  const rel = relative(root, actual);
  requireValue(rel && !rel.startsWith('..') && !isAbsolute(rel), 'File escapes owned root');
  return actual;
}
function hashed(root, ref) {
  requireValue(ref && hashPattern.test(ref.sha256), 'Missing sidecar hash');
  const path = contained(root, ref.path);
  requireValue(digest(readFileSync(path)) === ref.sha256, 'Sidecar hash mismatch');
  return json(path);
}
function crc32(bytes) {
  let crc = 0xffffffff;
  for (const byte of bytes) { crc ^= byte; for (let i = 0; i < 8; i++) crc = (crc >>> 1) ^ ((crc & 1) ? 0xedb88320 : 0); }
  return (crc ^ 0xffffffff) >>> 0;
}
export function inspectPng(bytes) {
  requireValue(bytes.length >= 45 && bytes.subarray(0, 8).equals(Buffer.from([137,80,78,71,13,10,26,10])), 'Invalid PNG signature');
  let offset = 8, ihdr = 0, idat = 0, iend = 0, width, height;
  while (offset + 12 <= bytes.length) {
    const length = bytes.readUInt32BE(offset), kind = bytes.toString('ascii', offset + 4, offset + 8);
    requireValue(offset + length + 12 <= bytes.length, 'Invalid PNG bounds');
    requireValue(crc32(bytes.subarray(offset + 4, offset + 8 + length)) === bytes.readUInt32BE(offset + 8 + length), 'Invalid PNG CRC');
    requireValue(!['tEXt','zTXt','iTXt'].includes(kind), 'PNG textual metadata prohibited');
    if (kind === 'IHDR') { requireValue(offset === 8 && length === 13, 'Invalid PNG IHDR'); ihdr++; width = bytes.readUInt32BE(offset + 8); height = bytes.readUInt32BE(offset + 12); }
    if (kind === 'IDAT') idat++;
    offset += length + 12;
    if (kind === 'IEND') { requireValue(length === 0, 'Invalid PNG IEND'); iend++; break; }
  }
  requireValue(ihdr === 1 && idat > 0 && iend === 1 && offset === bytes.length && integer(width) && integer(height), 'Incomplete PNG');
  return { width, height };
}

/** Validates recorded native evidence, not the honesty of its producer or visual review. */
export function verifyNativeEvidence({ receiptPath, repoRoot, runRoot, expectedCommit, maxAgeHours = 168, now = Date.now() }) {
  repoRoot = realpathSync(repoRoot); runRoot = realpathSync(runRoot);
  requireValue(/^[a-f0-9]{40,64}$/.test(expectedCommit) && [40,64].includes(expectedCommit.length), 'Invalid expected commit');
  requireValue(Number.isFinite(maxAgeHours) && maxAgeHours > 0 && maxAgeHours <= 8760, 'Invalid freshness bound');
  const r = json(contained(runRoot, receiptPath));
  requireValue(r.version === 1 && r.kind === 'winui-native-window' && r.route === 'direct-cheap-cli-with-uia-helper', 'Unsupported native evidence route');
  requireValue(/^[a-z0-9][a-z0-9._-]{2,80}$/.test(r.id), 'Invalid evidence id');
  requireValue(r.source?.startCommit === expectedCommit && r.source.endCommit === expectedCommit, 'Source changed during capture');
  const git = args => execFileSync('git', ['-C', repoRoot, ...args], { encoding: 'utf8', stdio: ['ignore','pipe','pipe'] }).trim();
  git(['merge-base','--is-ancestor',expectedCommit,'HEAD']);
  const productPaths = ['src','build','build.bat','build-installer.bat','global.json'];
  requireValue(!git(['diff', expectedCommit, '--', ...productPaths]) && !git(['ls-files','--others','--exclude-standard','--',...productPaths]), 'Product source differs from captured commit');
  const sourceStart = time(r.source.startedAt, 'source.startedAt'), sourceEnd = time(r.source.completedAt, 'source.completedAt');
  requireValue(sourceEnd >= sourceStart && r.source.cleanAtStart === true && r.source.cleanAtEnd === true, 'Source interval or clean-state evidence missing');
  const binding = hashed(runRoot, r.source.buildBinding);
  requireValue(binding.kind === 'derived-build-binding' && binding.sourceCommit === expectedCommit && binding.artifactSha256 === r.source.artifactSha256, 'Invalid derived build binding');
  const original = hashed(runRoot, binding.originalReceipt);
  requireValue(original.sourceCommit === expectedCommit && Array.isArray(original.files), 'Original build source mismatch');
  const entries = original.files.filter(f => f.path === binding.artifactEntry?.path);
  requireValue(entries.length === 1 && JSON.stringify(entries[0]) === JSON.stringify(binding.artifactEntry), 'Build file entry mismatch');
  requireValue(entries[0].sha256.toLowerCase() === r.source.artifactSha256 && hashPattern.test(r.source.artifactSha256), 'Build artifact hash mismatch');
  const artifact = readFileSync(resolve(repoRoot, r.source.artifactPath));
  requireValue(digest(artifact) === r.source.artifactSha256 && artifact.length === entries[0].bytes, 'Artifact bytes changed');
  const builtAt = time(original.createdUtc, 'build.createdUtc');
  const start = time(r.capture?.startedAt, 'capture.startedAt'), end = time(r.capture?.capturedAt, 'capture.capturedAt');
  requireValue(builtAt <= sourceStart && sourceStart <= start && start <= end && end <= sourceEnd && end - start <= 600000, 'Invalid source/build/capture interval');
  requireValue(sourceEnd <= now + 300000 && now - end <= maxAgeHours * 3600000, 'Stale or future evidence');
  const raw = readFileSync(contained(runRoot, r.capture.rawPath));
  const promoted = readFileSync(contained(repoRoot, r.capture.promotedPath));
  requireValue(r.capture.mimeType === 'image/png' && hashPattern.test(r.capture.sha256) && r.capture.rawSha256 === r.capture.sha256, 'Invalid capture hash declaration');
  requireValue(raw.equals(promoted) && digest(raw) === r.capture.sha256 && raw.length === r.capture.bytes, 'Capture bytes changed');
  const dimensions = inspectPng(raw);
  requireValue(dimensions.width === r.capture.width && dimensions.height === r.capture.height, 'Capture dimensions mismatch');
  const state = r.state, geometry = r.runtime?.geometry;
  requireValue(state && ['screen','state','theme','language'].every(k => text(state[k])) && state.captureKind === 'window', 'Missing native state tuple');
  requireValue(geometry && integer(geometry.dpi) && geometry.windowWidth === dimensions.width && geometry.windowHeight === dimensions.height, 'Native geometry mismatch');
  requireValue(state.viewport?.scale === geometry.dpi / 96 && Math.round(state.viewport.width * state.viewport.scale) === dimensions.width && Math.round(state.viewport.height * state.viewport.scale) === dimensions.height, 'Viewport and DPI mismatch');
  const runtime = r.runtime;
  requireValue(integer(runtime.launchPid) && /^0x[0-9a-f]+$/i.test(runtime.hwnd) && runtime.hwndResolvedLive === true, 'Missing native identity');
  requireValue(!('consoleErrorCount' in runtime) && !('pageErrorCount' in runtime) && runtime.diagnosticKind === 'winui-uia-and-application-events', 'Browser fields are not native evidence');
  const identity = hashed(runRoot, runtime.identity);
  requireValue(identity.pid === runtime.launchPid && identity.hwnd === runtime.hwnd && identity.sourceCommit === expectedCommit && identity.artifactSha256 === r.source.artifactSha256 && text(identity.creationDate) && text(identity.executablePath) && text(identity.desktop), 'Native identity sidecar mismatch');
  const interaction = hashed(runRoot, runtime.interaction);
  requireValue(interaction.id === runtime.interactionProofId && interaction.pid === runtime.launchPid && interaction.hwnd === runtime.hwnd && interaction.sourceCommit === expectedCommit && interaction.captureSha256 === r.capture.sha256 && interaction.observed === true && Array.isArray(interaction.steps) && interaction.steps.length > 0, 'Invalid interaction evidence');
  for (const step of interaction.steps) requireValue(text(step.action) && text(step.target) && text(step.observedResult) && step.ok === true, 'Incomplete interaction step');
  const diagnostics = hashed(runRoot, runtime.diagnostics);
  requireValue(diagnostics.kind === runtime.diagnosticKind && diagnostics.pid === runtime.launchPid && diagnostics.sourceCommit === expectedCommit && diagnostics.querySucceeded === true && diagnostics.browserObservation === 'not-applicable' && diagnostics.observedCrashCount === 0 && Array.isArray(diagnostics.providers) && diagnostics.providers.includes('Application Error') && diagnostics.providers.includes('.NET Runtime'), 'Invalid native diagnostic observation');
  requireValue(time(diagnostics.startedAt,'diagnostics.startedAt') <= start && time(diagnostics.completedAt,'diagnostics.completedAt') >= end, 'Diagnostics do not cover capture');
  const inspection = hashed(runRoot, r.inspection);
  requireValue(inspection.captureSha256 === r.capture.sha256 && text(inspection.reviewer), 'Inspection binding missing');
  for (const k of ['decoded','pixelsInspected','expectedStateVisible','sensitiveDataReviewed','visibleDesktopUntouched','expectedSurfaceOnly']) requireValue(inspection[k] === true, `Inspection missing ${k}`);
  for (const k of ['unrelatedTargetsObserved','mocked','handEdited']) requireValue(inspection[k] === false, `Inspection invalid ${k}`);
  const cleanup = hashed(runRoot, runtime.cleanup);
  requireValue(cleanup.pid === runtime.launchPid && cleanup.hwnd === runtime.hwnd && cleanup.desktop === identity.desktop && cleanup.ownedOnly === true && cleanup.appAbsent === true && cleanup.helpersAbsent === true && cleanup.desktopAbsent === true && time(cleanup.observedAt,'cleanup.observedAt') >= end, 'Incomplete owned cleanup');
  const inventory = json(contained(repoRoot, r.inventory?.path));
  requireValue(r.inventory.recordId === r.id && Array.isArray(inventory.records), 'Inventory identity missing');
  const rows = inventory.records.filter(x => x.id === r.id && x.active === true);
  requireValue(rows.length === 1, 'Expected one exact active inventory row');
  const expected = { path:r.capture.promotedPath, sourceCommit:expectedCommit, artifactSha256:r.source.artifactSha256, captureSha256:r.capture.sha256, screen:state.screen, state:state.state, theme:state.theme, language:state.language, viewportWidth:state.viewport.width, viewportHeight:state.viewport.height, scale:state.viewport.scale, interactionProofId:runtime.interactionProofId, interactionReceiptSha256:runtime.interaction.sha256, inspectionStatus:'inspected' };
  for (const [key,value] of Object.entries(expected)) requireValue(rows[0][key] === value, `Inventory mismatch: ${key}`);
  requireValue(Array.isArray(r.documentation) && r.documentation.length > 0, 'Documentation links missing');
  for (const entry of r.documentation) {
    const doc = contained(repoRoot, entry.path), body = readFileSync(doc,'utf8').replace(/<!--[\s\S]*?-->/g,'');
    requireValue(text(entry.alt), 'Empty alt text');
    const links = [...body.matchAll(/!\[([^\]]+)\]\(([^)\s]+)(?:\s+"[^"]*")?\)/g)];
    requireValue(links.some(m => m[1] === entry.alt && resolve(dirname(doc),m[2]) === contained(repoRoot,r.capture.promotedPath)), 'Exact documentation image link missing');
  }
  return { valid:true, id:r.id, sourceCommit:expectedCommit, captureSha256:r.capture.sha256, ...dimensions, scope:'native-window-pixels-uia-and-bounded-application-events', exclusions:['browser-observation','complete-native-diagnostics','unexecuted-scale-tuples'] };
}
if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) {
  try {
    const args = {}; for (let i=2;i<process.argv.length;i+=2) { requireValue(process.argv[i].startsWith('--') && process.argv[i+1], 'Expected argument/value pairs'); args[process.argv[i].slice(2)] = process.argv[i+1]; }
    console.log(JSON.stringify(verifyNativeEvidence({receiptPath:args.receipt,repoRoot:args['repo-root'],runRoot:args['run-root'],expectedCommit:args['expected-commit'],maxAgeHours:args['max-age-hours']===undefined?168:Number(args['max-age-hours'])}),null,2));
  } catch (error) { console.error(`Native evidence rejected: ${error.message}`); process.exitCode=1; }
}
