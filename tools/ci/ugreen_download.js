// Download the file behind a UGREEN NAS share link (ug.link/<device>/filemgr/share-download/?id=...).
// The share page is a JS app that requests a short-lived download task, so a
// headless browser is the most robust way to fetch it.
//   node ugreen_download.js <share url> <output file>
const { chromium } = require('playwright');

(async () => {
  const [url, out] = process.argv.slice(2);
  const browser = await chromium.launch();
  const ctx = await browser.newContext({ acceptDownloads: true });
  const page = await ctx.newPage();
  let saving = null;
  page.on('download', d => {
    console.log('download started: ' + d.suggestedFilename());
    saving = d.saveAs(out);
  });
  await page.goto(url, { waitUntil: 'networkidle', timeout: 120000 });
  await page.waitForTimeout(3000);
  const button = page.locator('button:visible', { hasText: /Download|下载|下載/ }).first();
  await button.click({ timeout: 60000 });
  await page.waitForTimeout(3000);
  const confirm = page.locator('.confirmOk:visible');
  if (await confirm.count()) await confirm.first().click();
  for (let i = 0; i < 120 && !saving; i++) await page.waitForTimeout(1000);
  if (!saving) throw new Error('download did not start (share expired?)');
  await saving;
  console.log('saved ' + out);
  await browser.close();
})().catch(e => { console.error(e); process.exit(1); });
