import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { summarizeLocal } from './measure.mjs';
import { parseNdjson } from './vendor/measure.mjs';
const plan = JSON.parse(fs.readFileSync(new URL('./profiles/adapanel.questions.json', import.meta.url), 'utf8'));
const tmp = fs.mkdtempSync(path.join(os.tmpdir(), 'adapanel-measure-'));
const obsFile = path.join(tmp,'observations.ndjson');
fs.writeFileSync(path.join(tmp,'evidence.txt'), 'Synthetic unit fixture only. No real measurement.');
const record = () => ({planVersion:plan.planVersion, conditions:plan.conditions, questionId:'brand', engine:plan.engines[0], model:'unit fixture', locale:plan.locale, country:plan.country, measuredAt:'2026-10-28T01:00:00Z', repetition:1, status:'measured', mentioned:true, correctDescription:true, citedUrls:['https://adapanel.host/adapanel-nedir/'], evidenceRef:'evidence.txt'});
test('empty observations are explicitly unmeasured', () => {
  const empty = parseNdjson(fs.readFileSync(new URL('./profiles/adapanel.observations.ndjson',import.meta.url),'utf8'));
  assert.equal(empty.length,0);
  const s = summarizeLocal(plan,empty,obsFile);
  assert.equal(s.status,'ölçülmedi'); assert.equal(s.plannedObservations,72);
  assert.equal(s.coverage,0); assert.equal(s.mentionedRate,null); assert.equal(s.accuracy,null); assert.equal(s.citationRate,null);
});
test('measured evidence and target boundary', () => {
  assert.equal(summarizeLocal(plan,[record()],obsFile).status,'ölçüldü');
  assert.equal(summarizeLocal(plan,[record()],obsFile).citationRate,1);
  for(const url of ['https://competitor.example/','https://adapanel.host.evil.example/']) {
    assert.equal(summarizeLocal(plan,[{...record(),citedUrls:[url]}],obsFile).citationRate,0);
  }
});
test('errors do not claim success', () => {
  const s=summarizeLocal(plan,[{...record(),status:'error',mentioned:false,correctDescription:null,citedUrls:[]}],obsFile);
  assert.equal(s.error,1); assert.equal(s.coverage,0); assert.equal(s.mentionedRate,null);
  assert.equal(s.status,'ölçülmedi'); assert.equal(s.accuracy,null); assert.equal(s.citationRate,null);
  assert.throws(()=>summarizeLocal(plan,[{...record(),status:'error'}],obsFile));
});
test('duplicates, invalid NDJSON, plan and conditions mismatch fail', () => {
  assert.throws(()=>summarizeLocal(plan,[record(),record()],obsFile),/Yinelenen/);
  assert.throws(()=>parseNdjson('{invalid}'));
  assert.throws(()=>summarizeLocal(plan,[{...record(),planVersion:'old'}],obsFile),/mismatch/);
  assert.throws(()=>summarizeLocal(plan,[{...record(),conditions:{}}],obsFile),/mismatch/);
  assert.throws(()=>summarizeLocal(plan,[{...record(),questionId:'outside'}],obsFile),/plan/);
});
test('missing and remote evidence fail', () => {
  assert.throws(()=>summarizeLocal(plan,[{...record(),evidenceRef:'missing.txt'}],obsFile),/Missing evidence/);
  assert.throws(()=>summarizeLocal(plan,[{...record(),evidenceRef:'https://example.org/evidence'}],obsFile),/local evidenceRef/);
});
test('conditions object order is irrelevant; public evidence is rejected', () => {
  assert.equal(summarizeLocal(plan,[{...record(), conditions:Object.fromEntries(Object.entries(plan.conditions).reverse())}],obsFile).measured,1);
  const evidence = new URL('../../website/index.html', import.meta.url);
  const localObservation = fileURLToPath(new URL('./profiles/adapanel.observations.ndjson', import.meta.url));
  const relative = path.relative(path.dirname(localObservation), fileURLToPath(evidence));
  assert.throws(()=>summarizeLocal(plan,[{...record(),evidenceRef:relative}],localObservation),/outside public/);
});
test.after(()=>fs.rmSync(tmp,{recursive:true,force:true}));
