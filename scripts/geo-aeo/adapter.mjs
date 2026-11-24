import { validateConfig } from './vendor/kit.mjs';
if (Number(process.versions.node.split('.')[0]) < 20) throw new Error('Node.js 20+ required');
let body = '';
for await (const chunk of process.stdin) body += chunk;
const configs = JSON.parse(body);
if (!Array.isArray(configs) || configs.length !== 2) throw new Error('TR/EN configs required');
configs.forEach(validateConfig);
process.stdout.write(JSON.stringify({project:'adapanel', configs}, null, 2) + '\n');
