// Progressive enhancement: every guide and link is present without JavaScript.
(() => {
  const root = document.getElementById('guides');
  const controls = root?.querySelector('.guide-filter');
  if (!controls) return;
  const search = document.getElementById('guide-search');
  const category = document.getElementById('guide-category');
  const count = document.getElementById('guide-count');
  const groups = [...root.querySelectorAll('.guide-group')];
  const tr = document.documentElement.lang === 'tr';
  const normalize = value => value.normalize('NFD').replace(/[\u0300-\u036f]/g, '').toLowerCase().replace(/ı/g, 'i');
  const entries = groups.flatMap(group => [...group.querySelectorAll('.resource-card')].map(card => ({card, group, text: normalize(card.textContent)})));
  function filter() {
    const words = normalize(search.value).trim().split(/\s+/).filter(Boolean);
    let visible = 0;
    for (const entry of entries) {
      const match = (!category.value || entry.group.dataset.category === category.value) && words.every(word => entry.text.includes(word));
      entry.card.hidden = !match;
      if (match) visible++;
    }
    for (const group of groups) group.hidden = !entries.some(entry => entry.group === group && !entry.card.hidden);
    count.textContent = visible ? (tr ? `${entries.length} rehberden ${visible} tanesi gösteriliyor.` : `Showing ${visible} of ${entries.length} guides.`) : (tr ? 'Eşleşen rehber bulunamadı. Daha kısa bir sözcük deneyin veya filtreleri temizleyin.' : 'No matching guides. Try a shorter term or clear the filters.');
  }
  search.addEventListener('input', filter);
  category.addEventListener('change', filter);
  document.getElementById('guide-reset').addEventListener('click', () => {
    search.value = ''; category.value = ''; filter(); search.focus();
  });
  // Category links keep their native anchors; clear filters before navigating.
  root.querySelectorAll('.guide-categories a').forEach(link => link.addEventListener('click', () => {
    search.value = ''; category.value = ''; filter();
  }));
  controls.hidden = false;
  filter();
})();
