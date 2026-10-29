import { readFile } from 'node:fs/promises';
import { build } from './kit.mjs';
import { summarize, parseNdjson } from './measure.mjs';

try {
  const [command, input, output] = process.argv.slice(2);
  if (!input || !output || process.argv.length !== 5) throw new Error('Kullanım: node src/cli.mjs build config.json YENI_DIZIN | summarize questions.json observations.ndjson');
  const config = JSON.parse(await readFile(input, 'utf8'));
  if (command === 'build') console.log(JSON.stringify({files:await build(config, output)}, null, 2));
  else if (command === 'summarize') console.log(JSON.stringify(summarize(config, parseNdjson(await readFile(output, 'utf8'))), null, 2));
  else throw new Error('Bilinmeyen komut');
} catch (error) { console.error(error.message); process.exitCode = 1; }
