const { chromium } = require(process.env.PLAYWRIGHT_MODULE || 'playwright');
const assert = require('node:assert/strict');
const base = process.argv[2] || 'http://127.0.0.1:4175';

async function scenario(browser, mode) {
  const page = await browser.newPage({ viewport: { width: 390, height: 844 }, reducedMotion: 'reduce' });
  await page.clock.install();
  // Every API request is simulated; these checks never create real leads or Telegram messages.
  await page.route('**/api/promo/leads', route => route.abort());
  await page.addInitScript(mode => {
    window.couponRequests = [];
    const originalFetch = window.fetch;
    window.fetch = async (url, options) => {
      if (!String(url).includes('/api/promo/leads')) return originalFetch(url, options);
      window.couponRequests.push(JSON.parse(options.body));
      const number = window.couponRequests.length;
      if (mode === 'lost-response' && number === 1) {
        return new Promise((resolve, reject) => options.signal.addEventListener('abort', () => reject(new DOMException('Aborted', 'AbortError')), { once: true }));
      }
      if (mode === 'offline') throw new TypeError('Network unavailable');
      if (mode === 'conflict') return new Response(JSON.stringify({ok:false,error:'phone_already_registered'}), {status:409});
      if (mode === 'server-error' && number === 1) return new Response('upstream unavailable', {status:503});
      if (mode === 'bad-json' && number === 1) return new Response('{', {status:200});
      return new Response(JSON.stringify({ok:true,promo_code:'Shahanov10',expires_at:'2030-01-01T00:00:00Z'}), {status:200});
    };
  }, mode);
  try {
    await page.goto(base, { waitUntil: 'domcontentloaded' });
    await page.locator('[name=name]').fill('Тест восстановления');
    await page.locator('[name=phone]').fill('+70000000010');
    await page.locator('[name=email]').fill('test@example.com');
    await page.locator('[name=consent]').check();
    await page.locator('#lead-form button[type=submit]').click();
    await page.waitForFunction(() => window.couponRequests.length > 0);
    for (let i = 0; i < 5; i++) {
      await page.clock.runFor(16000);
      await page.waitForTimeout(20);
    }
    const requests = await page.evaluate(() => window.couponRequests);
    assert.equal(new Set(requests.map(r => r.request_id)).size, 1, 'Retries must reuse the original request ID');
    if (mode === 'offline' || mode === 'conflict') {
      assert(await page.locator('#success').isHidden(), 'Never display an unconfirmed code');
      assert(await page.locator('#lead-form button[type=submit]').isEnabled(), 'Allow a manual retry');
      assert.equal(requests.length, mode === 'conflict' ? 1 : 3);
      if (mode === 'offline') {
        await page.locator('#lead-form button[type=submit]').click();
        for (let i=0;i<5;i++) { await page.clock.runFor(16000); await page.waitForTimeout(20); }
        assert.equal(new Set((await page.evaluate(() => window.couponRequests)).map(r => r.request_id)).size,1);
      }
    } else {
      assert(await page.locator('#success').isVisible(), 'A saved lead must recover its coupon automatically');
      assert.equal(await page.locator('#promo-code').textContent(), 'Shahanov10');
      assert.equal(requests.length, mode === 'success' ? 1 : 2);
      const receipt = await page.evaluate(() => JSON.parse(localStorage.getItem('rika-coupon:rika-exhibition-10')));
      assert.deepEqual(Object.keys(receipt).sort(), ['expires_at','promo_code','version']);
      assert.equal(receipt.version,3);
      await page.reload({waitUntil:'domcontentloaded'});
      assert(await page.locator('#success').isVisible(), 'A confirmed coupon survives a reload');
    }
    console.log(`PASS ${mode}`);
  } finally { await page.close(); }
}

async function legacyReceipt(browser, version, daysSinceIssue, previousDays) {
  const page=await browser.newPage({reducedMotion:'reduce'});
  const day=86400000, issued=Date.now()-daysSinceIssue*day;
  const receipt={promo_code:'Shahanov10',expires_at:new Date(issued+previousDays*day).toISOString()};
  if(version!==undefined)receipt.version=version;
  await page.addInitScript(receipt=>{
    const key='rika-coupon:rika-exhibition-10';
    if(!localStorage.getItem(key))localStorage.setItem(key,JSON.stringify(receipt));
  },receipt);
  try {
    await page.goto(base,{waitUntil:'domcontentloaded'});
    assert.equal(await page.evaluate(()=>localStorage.getItem('rika-coupon:rika-exhibition-10')),null);
    assert(await page.locator('#success').isHidden(), 'Legacy deadlines must be confirmed, never guessed');
    assert(await page.locator('#lead-form').isVisible());
    assert.match(await page.locator('#form-status').textContent(),/30 дней/);
    console.log(`PASS legacy receipt ${previousDays} days / version ${version}: restore from server`);
  }finally{await page.close();}
}

(async () => {
  const browser = await chromium.launch(process.env.BROWSER_EXECUTABLE ? { executablePath: process.env.BROWSER_EXECUTABLE } : {});
  try {
    for (const mode of ['lost-response','success','server-error','bad-json','offline','conflict']) await scenario(browser, mode);
    await legacyReceipt(browser,2,10,365);await legacyReceipt(browser,undefined,10,7);
    await legacyReceipt(browser,2,31,365);await legacyReceipt(browser,2,0,30);
  }
  finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
