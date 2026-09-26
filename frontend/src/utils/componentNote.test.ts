import { expect, it } from 'vitest';
import manifest from '../../../components.json';
import { componentNote } from './componentNote';
import en from '../i18n/en.json';
import tr from '../i18n/tr.json';

it('has translations for every shipped component description', () => {
  for (const item of manifest.components.map(item => ({id: item.id, note: item.note ?? ''})).filter(item => item.note)) {
    const key = componentNote(item, key => key);
    expect(key).toBe(`panel.software.componentNote.${item.id}`);
    expect(en[key as keyof typeof en]).toBeTruthy();
    expect(tr[key as keyof typeof tr]).toBeTruthy();
    expect(en[key as keyof typeof en]).not.toBe(tr[key as keyof typeof tr]);
  }
});

it('preserves unknown component descriptions and empty notes', () => {
  expect(componentNote({id:'custom', note:'Custom runtime note'}, key => key)).toBe('Custom runtime note');
  expect(componentNote({id:'clamav', note:''}, key => key)).toBe('');
});
