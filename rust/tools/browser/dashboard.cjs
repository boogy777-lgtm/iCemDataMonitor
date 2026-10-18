// Real preserved SPA against the actual Rust EXE; no mocked REST or RPC.
const { chromium } = require('playwright-core');
const { spawn } = require('node:child_process');
const fs = require('node:fs/promises');
const path = require('node:path');
const os = require('node:os');
const assert = require('node:assert/strict');
const { createInterface } = require('node:readline');
(async () => {
  const root = path.resolve(__dirname, '../..');
  const dir = await fs.mkdtemp(path.join(os.tmpdir(), 'icem-web-browser-'));
  const fixture = await fs.readFile(path.join(root, 'tests/fixtures/bus-v5-minimal.bin'));
  fixture.fill(0, 64 + 20, 64 + 24); // remove intentional codec NaN for JSON UI scenario
  await fs.writeFile(path.join(dir, 'bus.bin'), fixture);
  await fs.writeFile(path.join(dir, 'ui-theme'), 'dark');
  const exe = process.env.ICEM_HOST_EXE || path.join(root, 'target/debug/icem-host-dev' + (process.platform === 'win32' ? '.exe' : ''));
  const host = spawn(exe, ['--modbus-fixture', path.join(dir, 'bus.bin'), '--bind', '127.0.0.1:0', '--web-bind', '127.0.0.1:0', '--theme-file', path.join(dir, 'ui-theme'), '--registry', path.join(root, '../templates/variable_registry.json'), '--duration-ms', '60000'], { cwd: root });
  let logs = '', stdout = '', browser;
  host.stdout.on('data', b => stdout += b);
  host.stderr.on('data', b => logs += b);
  try {
    const origin = await new Promise((resolve, reject) => {
      const timer = setTimeout(() => reject(new Error('host readiness timeout: ' + logs)), 10000);
      const lines = createInterface({ input: host.stderr });
      lines.on('line', line => { if (line.startsWith('Web http://')) { clearTimeout(timer); resolve(line.slice(4)); } });
      host.on('error', reject);
      host.on('exit', code => { clearTimeout(timer); reject(new Error('host exited ' + code + ': ' + logs)); });
    });
    browser = await chromium.launch({ channel: 'chrome', headless: true });
    const page = await browser.newPage({ viewport: { width: 1440, height: 1000 } });
    const errors = [];
    page.on('pageerror', error => errors.push(error.message));
    for (const file of ['index.html', 'main.css', 'main.js', 'favicon.ico', 'favicon.png']) {
      const response = await page.request.get(origin + '/' + (file === 'index.html' ? '' : file));
      assert.equal(response.status(), 200);
      assert.deepEqual(await response.body(), await fs.readFile(path.join(root, 'web/dashboard', file)));
    }
    await page.goto(origin, { waitUntil: 'domcontentloaded' });
    await page.waitForFunction(() => document.querySelectorAll('#zones input[data-addr]').length === 298);
    await page.waitForFunction(() => document.querySelector('#metricsTable tbody tr td')?.textContent !== 'ожидание первого сэмпла…');
    assert.equal(await page.locator('html').getAttribute('data-theme'), 'dark');
    await page.locator('#zones input[data-addr="6"]').check();
    await page.waitForFunction(() => document.querySelector('#chartLegend')?.textContent.trim().length > 0);
    await page.waitForFunction(() => document.querySelector('#selTable tbody')?.textContent.includes('1'));
    const shots = process.env.ICEM_UI_ARTIFACTS || path.join(dir, 'screenshots');
    await fs.mkdir(shots, { recursive: true });
    await page.screenshot({ path: path.join(shots, 'dashboard-dark.png'), fullPage: true });
    await page.locator('#btnStatus').click();
    await page.locator('#viewStatus').waitFor({ state: 'visible' });
    await page.waitForFunction(() => document.querySelectorAll('#metricsTable tbody tr').length === 302);
    assert.equal((await page.locator('#cardHeartbeat').innerText()).trim(), '32000');
    await page.locator('#themeToggle').click();
    await page.waitForFunction(() => document.documentElement.dataset.theme === 'light');
    // File truth and a second page prove the station-wide theme RPC, not localStorage only.
    for (let i = 0; ; i++) {
      if ((await fs.readFile(path.join(dir, 'ui-theme'), 'utf8')).trim() === 'light') break;
      if (i === 49) throw new Error('SetUiTheme did not persist light');
      await new Promise(r => setTimeout(r, 100));
    }
    await page.screenshot({ path: path.join(shots, 'status-light.png'), fullPage: true });
    const second = await browser.newPage(); await second.goto(origin, { waitUntil: 'domcontentloaded' });
    await second.waitForFunction(() => document.documentElement.dataset.theme === 'light');
    const response = await page.request.get(origin + '/api/regs/history?addr=6');
    const history = await response.json(); assert.ok(history['6'].length >= 1); assert.equal(history['6'][0].v, 1);
    assert.deepEqual(errors, []); assert.equal(stdout, '');
    console.log('PASS: preserved assets, 298 pairs, selection/chart/history, 302 gRPC metrics, status tab, dark/light theme persistence, second-page theme');
  } finally {
    if (browser) await browser.close();
    host.kill();
    await new Promise(resolve => host.exitCode !== null ? resolve() : host.once('exit', resolve));
    await fs.rm(dir, { recursive: true, force: true });
  }
})().catch(error => { console.error(error); process.exitCode = 1; });
