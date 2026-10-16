// Execute the real inline script after the real bundle's synchronous visual flip.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const root = path.resolve(__dirname, '../..');
async function click(htmlPath, visual) {
  const html = fs.readFileSync(htmlPath, 'utf8');
  const code = html.split('<script>')[1].split('</script>')[0];
  let theme = 'dark', handler, sent;
  const trailers = Buffer.from('grpc-status: 0\r\n');
  const response = Buffer.concat([Buffer.from([0,0,0,0,0,128,0,0,0,trailers.length]), trailers]);
  vm.runInNewContext(code, {
    window: { location: { origin: 'http://localhost' } }, Uint8Array, TextDecoder,
    document: {
      documentElement: { getAttribute: () => theme, setAttribute: (_, v) => theme = v },
      querySelector: () => ({ setAttribute: () => {} }),
      getElementById: () => ({ addEventListener: (_, fn) => handler = fn })
    },
    fetch: async (url, options) => {
      if (url.endsWith('SetUiTheme')) sent = options.body[6] === 1;
      return { arrayBuffer: async () => response.buffer.slice(response.byteOffset, response.byteOffset + response.byteLength) };
    }
  });
  await new Promise(setImmediate); theme = visual; handler(); await new Promise(setImmediate);
  return sent;
}
(async () => {
  const original = path.join(root, '../iCemDataMonitor.Host/wwwroot/index.html');
  const migrated = path.join(root, 'web/dashboard/index.html');
  assert.equal(await click(original, 'light'), false, 'D16 baseline counterexample changed');
  assert.equal(await click(migrated, 'light'), true);
  assert.equal(await click(migrated, 'dark'), false);
  console.log('PASS D16: original inverted bool reproduced, migrated light/dark follows actual visual state');
})().catch(error => { console.error(error); process.exitCode = 1; });
