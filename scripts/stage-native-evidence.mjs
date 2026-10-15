#!/usr/bin/env node

import { createHash, randomUUID } from 'node:crypto';
import { closeSync, constants, copyFileSync, existsSync, fsyncSync, lstatSync, openSync, readFileSync, realpathSync, renameSync, unlinkSync, writeFileSync } from 'node:fs';
import { dirname, isAbsolute, relative, resolve } from 'node:path';

function fail(message) {
  console.error(`Evidence transaction rejected: ${message}`);
  process.exit(1);
}

function parseArgs(argv) {
  const result = {};
  for (let index = 0; index < argv.length; index += 1) {
    const arg = argv[index];
    if (arg.startsWith('--')) result[arg.slice(2)] = argv[++index];
    else fail(`unknown argument ${arg}`);
  }
  return result;
}

const options = parseArgs(process.argv.slice(2));
if (!options.mode || !['stage', 'rollback'].includes(options.mode)) fail('--mode must be stage or rollback');
if (!options['repo-root'] || !options['run-root']) fail('--repo-root and --run-root are required');
const repoRoot = realpathSync(resolve(options['repo-root']));
const runRoot = realpathSync(resolve(options['run-root']));
const hash = (path) => createHash('sha256').update(readFileSync(path)).digest('hex');
const bounded = (root, path, label, allowMissing = false) => {
  const lexical = resolve(root, path);
  const parent = realpathSync(dirname(lexical));
  const parentRelative = relative(root, parent);
  if (parentRelative.startsWith('..') || isAbsolute(parentRelative)) fail(`${label} parent escapes its owned root`);
  if (!allowMissing && !existsSync(lexical)) fail(`${label} does not exist`);
  if (existsSync(lexical)) {
    if (lstatSync(lexical).isSymbolicLink()) fail(`${label} is a symlink or junction`);
    const actual = realpathSync(lexical);
    const rel = relative(root, actual);
    if (rel === '' || rel.startsWith('..') || isAbsolute(rel)) fail(`${label} escapes its owned root`);
    return actual;
  }
  return lexical;
};
const atomicReplace = (source, target) => {
  const temporary = resolve(dirname(target), `.evidence-${process.pid}-${randomUUID()}.tmp`);
  copyFileSync(source, temporary, constants.COPYFILE_EXCL);
  const handle = openSync(temporary, 'r+');
  try { fsyncSync(handle); } finally { closeSync(handle); }
  let lastError;
  for (let attempt = 0; attempt < 6; attempt += 1) {
    try { renameSync(temporary, target); return; } catch (error) {
      lastError = error;
      if (!['EPERM', 'EACCES', 'EBUSY'].includes(error?.code)) break;
      Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0, 50 * (attempt + 1));
    }
  }
  if (existsSync(temporary)) unlinkSync(temporary);
  throw lastError;
};

if (options.mode === 'stage') {
  for (const name of ['raw', 'target', 'expected-raw-sha256', 'transaction']) if (!options[name]) fail(`--${name} is required for stage`);
  if (!/^[0-9a-f]{64}$/i.test(options['expected-raw-sha256'])) fail('--expected-raw-sha256 is invalid');
  const raw = bounded(runRoot, options.raw, 'raw capture');
  const target = bounded(repoRoot, options.target, 'promoted target', true);
  const transaction = bounded(runRoot, options.transaction, 'transaction output', true);
  if (existsSync(transaction)) fail('transaction output already exists');
  const rawHash = hash(raw);
  if (rawHash !== options['expected-raw-sha256'].toLowerCase()) fail('raw capture SHA-256 does not match');
  const previousExisted = existsSync(target);
  let previousSha256 = null;
  let backupPath = null;
  if (previousExisted) {
    if (!options['expected-existing-sha256'] || !/^[0-9a-f]{64}$/i.test(options['expected-existing-sha256'])) fail('replacement requires --expected-existing-sha256');
    previousSha256 = hash(target);
    if (previousSha256 !== options['expected-existing-sha256'].toLowerCase()) fail('existing target changed before promotion');
    backupPath = resolve(runRoot, `.evidence-backup-${randomUUID()}.bin`);
    copyFileSync(target, backupPath);
  } else if (options['expected-existing-sha256']) fail('target is absent but an existing hash was supplied');

  try {
    atomicReplace(raw, target);
    if (hash(target) !== rawHash || !readFileSync(target).equals(readFileSync(raw))) fail('promoted target read-back differs from raw capture');
  } catch (error) {
    if (backupPath && existsSync(backupPath)) atomicReplace(backupPath, target);
    else if (!previousExisted && existsSync(target)) unlinkSync(target);
    throw error;
  }

  const record = {
    version: 1,
    stagedAt: new Date().toISOString(),
    repoRoot,
    runRoot,
    rawPath: raw,
    targetPath: target,
    promotedSha256: rawHash,
    previousExisted,
    previousSha256,
    backupPath,
    rollbackPending: true
  };
  writeFileSync(transaction, `${JSON.stringify(record, null, 2)}\n`, { encoding: 'utf8', flag: 'wx' });
  console.log(JSON.stringify({ staged: true, target, sha256: rawHash, transaction }, null, 2));
} else {
  if (!options.transaction) fail('--transaction is required for rollback');
  const transaction = bounded(runRoot, options.transaction, 'transaction');
  let record;
  try { record = JSON.parse(readFileSync(transaction, 'utf8')); } catch { fail('transaction is not valid JSON'); }
  if (record.version !== 1 || record.repoRoot !== repoRoot || record.runRoot !== runRoot || record.rollbackPending !== true) fail('transaction ownership or state is invalid');
  const target = bounded(repoRoot, record.targetPath, 'promoted target');
  if (hash(target) !== record.promotedSha256) fail('promoted target changed; rollback refused');
  if (record.previousExisted) {
    const backup = bounded(runRoot, record.backupPath, 'transaction backup');
    if (hash(backup) !== record.previousSha256) fail('transaction backup changed; rollback refused');
    atomicReplace(backup, target);
  } else {
    unlinkSync(target);
  }
  console.log(JSON.stringify({ rolledBack: true, target, restoredSha256: record.previousSha256 }, null, 2));
}

