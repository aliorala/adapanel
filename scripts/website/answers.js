// Answers remain readable in the HTML without JavaScript. Deep links also open
// a collapsed answer when the page is reached from a shared fragment URL.
function revealLinkedAnswer() {
  let id;
  try { id = decodeURIComponent(window.location.hash.slice(1)); }
  catch { return; }
  if (!/^(question|answer)-\d+$/.test(id)) return;
  const target = document.getElementById(id);
  const details = target?.closest('details');
  if (!details) return;
  details.open = true;
  requestAnimationFrame(() => target.scrollIntoView({ block: 'start' }));
}
window.addEventListener('hashchange', revealLinkedAnswer);
revealLinkedAnswer();
