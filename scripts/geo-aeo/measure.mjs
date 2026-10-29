import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { summarize, parseNdjson } from './vendor/measure.mjs';

// Evidence references are relative to the observation file, never public URLs.
export function summarizeLocal(plan, observations, observationFile) {
  if (!plan.planVersion || !plan.conditions || typeof plan.conditions !== 'object') throw new Error('Plan planVersion/conditions required');
  const publicRoots = ['website', 'artifacts/website-public'].map(p => path.resolve(fileURLToPath(new URL('../../', import.meta.url)), p));
  for (const record of observations) {
    const expected = Object.entries(plan.conditions).sort(([a],[b])=>a.localeCompare(b));
    const actualConditions = record.conditions && typeof record.conditions === 'object' ? Object.entries(record.conditions).sort(([a],[b])=>a.localeCompare(b)) : null;
    if (record.planVersion !== plan.planVersion || JSON.stringify(actualConditions) !== JSON.stringify(expected)) throw new Error('Observation planVersion/conditions mismatch');
    if (typeof record.evidenceRef !== 'string' || !record.evidenceRef.trim() || path.isAbsolute(record.evidenceRef) || /^[a-z]+:/i.test(record.evidenceRef)) throw new Error('Relative local evidenceRef required');
    const evidence = path.resolve(path.dirname(observationFile), record.evidenceRef);
    if (!fs.existsSync(evidence) || !fs.statSync(evidence).isFile()) throw new Error('Missing evidence: ' + record.evidenceRef);
    const actual = fs.realpathSync(evidence);
    if (publicRoots.some(root => actual === root || actual.startsWith(root + path.sep))) throw new Error('Evidence must be outside public output');
  }
  const result = summarize(plan, observations);
  return {planVersion:plan.planVersion, conditions:plan.conditions, status:result.measured > 0 ? 'ölçüldü' : 'ölçülmedi', ...result};
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  try {
    const [planFile, observationFile] = process.argv.slice(2);
    if (!planFile || !observationFile) throw new Error('Usage: node scripts/geo-aeo/measure.mjs PLAN.json OBSERVATIONS.ndjson');
    const plan = JSON.parse(fs.readFileSync(planFile, 'utf8'));
    console.log(JSON.stringify(summarizeLocal(plan, parseNdjson(fs.readFileSync(observationFile, 'utf8')), path.resolve(observationFile)), null, 2));
  } catch (error) { console.error(error.message); process.exitCode = 1; }
}
