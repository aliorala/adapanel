import { afterEach, expect, it } from 'vitest';
import { computed, createSSRApp, h } from 'vue';
import { createI18n } from 'vue-i18n';
import { renderToString } from 'vue/server-renderer';
import { safeMessageCompiler } from '../i18n';
import { formatDateTime } from './datetime';
import { getFormattingLocale, setFormattingLocale } from './locale';
import { webmailPage } from './webmailPage';
import { serviceLabel } from './serviceLabel';
import CodeSample from '../components/CodeSample.vue';
import tr from '../i18n/tr.json';
import en from '../i18n/en.json';

afterEach(()=>setFormattingLocale('tr'));
it('reactively formats dates and numbers in the selected panel language',()=>{
  const date='2026-09-26T12:00:00Z';
  const rendered=computed(()=>formatDateTime(date));
  setFormattingLocale('en');
  expect(rendered.value).toBe(new Date(date).toLocaleString('en-US'));
  expect((1234567).toLocaleString(getFormattingLocale())).toBe('1,234,567');
  setFormattingLocale('tr');
  expect(rendered.value).toBe(new Date(date).toLocaleString('tr-TR'));
  expect((1234567).toLocaleString(getFormattingLocale())).toBe('1.234.567');
});
it.each(['tr','en'])('localizes copy buttons and webmail pages in %s',async(language)=>{
  const messages=language==='en'?en:tr;
  const i18n=createI18n({legacy:false,locale:language,messageCompiler:safeMessageCompiler,messages:{tr,en}});
  const app=createSSRApp({render:()=>h(CodeSample,{code:'example'})}).use(i18n);
  const html=await renderToString(app);
  expect(html).toContain(messages['panel.files.copy']);
  const popup=webmailPage(language,messages['panel.mail.webmailPreparingTitle'],messages['panel.mail.webmailPreparingHint']);
  expect(popup).toContain(`lang="${language}"`);
  expect(popup).toContain(messages['panel.mail.webmailPreparingTitle']);
  expect(serviceLabel({name:'WAS',label:'IIS — Süreç Etkinleştirme'},key=>messages[key as keyof typeof messages])).toBe(messages['panel.health.service.iisActivation']);
});
it('preserves custom service names and escapes popup text',()=>{
  expect(serviceLabel({name:'Custom',label:'Customer Worker'},()=>{throw Error('not a system service');})).toBe('Customer Worker');
  const html=webmailPage('en','<script>alert(1)</script>','"test" & <img src=x>',true);
  expect(html).not.toContain('<script>');
  expect(html).not.toContain('<img');
  expect(html).toContain('&lt;script&gt;');
});
