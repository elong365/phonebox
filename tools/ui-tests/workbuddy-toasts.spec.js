// The agent sends WorkBuddy notifications as "wbEvents.<state>|<project>": the phone
// fills the key in through its dictionary, so the toast speaks the panel language
// instead of showing the raw key. Against the web demo, in German.
const { test, expect } = require('@playwright/test');

async function load(page, display = { language: 'de' }) {
  await page.goto('/panel/style.css');
  await page.evaluate((d) => {
    localStorage.clear();
    localStorage.setItem('pb-demo-config', JSON.stringify({
      night: { enabled: false }, rules: [], shortcuts: [],
      display: d,
      pages: [{ id: 'p', title: 'P', widgets: [] }],
    }));
  }, display);
  await page.goto('/panel/');
}

// feeds one notify through the same path the agent's event stream takes
async function notify(page, text) {
  return page.evaluate(async (t) => {
    const { handleNotify } = await import('/panel/main.js');
    document.getElementById('toast').className = 'toast';
    handleNotify({ title: 'WorkBuddy', text: t, level: 'error', vibrate: true });
    const el = document.getElementById('toast');
    return { on: el.classList.contains('on'), text: el.textContent };
  }, text);
}

test('a WorkBuddy notification is translated, not shown as its raw key', async ({ page }) => {
  await load(page);
  const r = await notify(page, 'wbEvents.failed|Quarterly report');
  expect(r.on).toBe(true);
  expect(r.text).toContain('ist fehlgeschlagen'); // "Quarterly report ist fehlgeschlagen"
  expect(r.text).not.toContain('wbEvents.failed');

  const c = await notify(page, 'wbEvents.completed|Quarterly report');
  expect(c.text).toContain('ist fertig');
  expect(c.text).not.toContain('wbEvents.completed');
});

test('the project name and the saved note carry the translated text', async ({ page }) => {
  await load(page, { language: 'zh' });
  const r = await notify(page, 'wbEvents.failed|Quarterly report');
  expect(r.text).toContain('已失败');
  expect(r.text).toContain('Quarterly report');
  // the notes inbox stores what the toast said, not the raw key
  const stored = await page.evaluate(async () => {
    const { D } = await import('/panel/main.js');
    return D.notes[0];
  });
  expect(stored.text).toContain('已失败');
  expect(stored.text).not.toContain('wbEvents.failed');
});
