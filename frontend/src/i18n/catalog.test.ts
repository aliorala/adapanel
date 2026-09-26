import { expect, it } from 'vitest';
import ts from 'typescript';
import { parse as parseSfc } from '@vue/compiler-sfc';
import { parse as parseTemplate } from '@vue/compiler-dom';
import tr from './tr.json';
import en from './en.json';

const sources = import.meta.glob<string>('../**/*.{ts,vue}', { query: '?raw', import: 'default', eager: true });
const rawCatalogs = import.meta.glob<string>('./{tr,en}.json', { query: '?raw', import: 'default', eager: true });
const catalogs: Record<string, Record<string, string>> = {tr, en};
it('ships matching, nonempty catalogs without duplicate keys or mismatched parameters', () => {
  expect(Object.keys(en).sort()).toEqual(Object.keys(tr).sort());
  const tokens = (value: string) => [...value.matchAll(/\{([^{}]*)\}/g)].map(match => match[1].trim()).filter(token => !/^['"]/.test(token)).sort();
  for (const [locale, messages] of Object.entries(catalogs)) {
    const raw = rawCatalogs[`./${locale}.json`];
    const keys = [...raw.matchAll(/^\s*"([^"\n]+)"\s*:/gm)].map(match => match[1]);
    expect(new Set(keys).size, `${locale}: duplicate key`).toBe(keys.length);
    for (const [key, value] of Object.entries(messages)) {
      expect(value.trim(), key).not.toBe('');
      expect(tokens(value), `${locale}:${key}`).toEqual(tokens(tr[key as keyof typeof tr]));
    }
  }
});

it('translates every static panel message referenced by production views and scripts', () => {
  const missing = new Set<string>();
  const scanScript = (source: string, file: string) => {
    const ast = ts.createSourceFile(file, source, ts.ScriptTarget.Latest, true);
    const visit = (node: ts.Node) => {
      if (ts.isStringLiteral(node) || ts.isNoSubstitutionTemplateLiteral(node)) {
        const key = node.text;
        // A trailing dot/underscore denotes a dynamic namespace, not a full message.
        if (key.startsWith('panel.') && !/[._]$/.test(key) && !(key in en)) missing.add(`${file}: ${key}`);
      }
      ts.forEachChild(node, visit);
    };
    visit(ast);
  };
  const walk = () => {
    for (const [file, source] of Object.entries(sources)) {
      if(!/\.(ts|vue)$/.test(file)||file.includes('.test.')||file.includes('__preview')) continue;
      if(file.endsWith('.ts')) {scanScript(source,file);continue;}
      const {descriptor}=parseSfc(source);
      scanScript((descriptor.script?.content??'')+'\n'+(descriptor.scriptSetup?.content??''),file);
      if(descriptor.template) {
        const template=parseTemplate(descriptor.template.content);
        const visit=(node: {type:number;content?:unknown;children?:unknown[];props?:unknown[];exp?:unknown;arg?:unknown})=>{
          if(node.type===4&&typeof node.content==='string')scanScript(`(${node.content})`,file);
          for(const child of [...(node.children??[]),...(node.props??[]),node.exp,node.arg,typeof node.content==='object'?node.content:null]) if(child&&typeof child==='object')visit(child as typeof node);
        };
        visit(template);
      }
    }
  };
  walk();
  expect([...missing]).toEqual([]);
});
