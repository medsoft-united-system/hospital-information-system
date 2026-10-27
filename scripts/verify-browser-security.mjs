import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { existsSync, mkdtempSync, mkdirSync, readFileSync, rmSync, statSync, writeFileSync } from 'node:fs';
import https from 'node:https';
import net from 'node:net';
import os from 'node:os';
import path from 'node:path';
import { chromium } from 'playwright';

function required(name) {
  const value = process.env[name];
  assert(value, `Configure ${name}.`);
  return value;
}

const caFile = required('BROWSER_CA_FILE');
const pfxFile = required('BROWSER_SERVER_PFX_FILE');
const pfxPasswordFile = required('BROWSER_SERVER_PFX_PASSWORD_FILE');
const webRoot = path.resolve(required('BROWSER_WEB_ROOT'));
const credentialsFile = required('BROWSER_CREDENTIALS_FILE');
const apiHost = required('BROWSER_API_HOST');
const apiPort = Number(process.env.BROWSER_API_PORT || 5201);
const outputFile = process.env.BROWSER_RESULT_FILE;
const credentials = JSON.parse(readFileSync(credentialsFile, 'utf8'));
assert(typeof credentials.userName === 'string' && typeof credentials.password === 'string',
  'The credentials file must contain userName and password.');
assert(existsSync(path.join(webRoot, 'index.html')), 'Publish the Web application before running the browser check.');
const temporary = mkdtempSync(path.join(os.tmpdir(), 'medsoft-browser-security-'));
const browsers = [];
const evidence = { certificateRejectedWithoutCa: false, trustedHttps: false, setupPreflight: false,
  bearerPreflight: false, unconfiguredHeaderRejected: false, login: false, reloadRefresh: false, logout: false,
  stage: 'initialization', preflightRequests: [], apiResponses: [] };
const types = { '.html': 'text/html; charset=utf-8', '.js': 'text/javascript', '.json': 'application/json',
  '.wasm': 'application/wasm', '.css': 'text/css', '.svg': 'image/svg+xml', '.png': 'image/png',
  '.woff': 'font/woff', '.woff2': 'font/woff2', '.dat': 'application/octet-stream', '.webcil': 'application/octet-stream' };
const web = https.createServer({ pfx: readFileSync(pfxFile),
  passphrase: readFileSync(pfxPasswordFile, 'utf8').trim(), minVersion: 'TLSv1.2' }, (request, response) => {
  let pathname;
  try { pathname = decodeURIComponent(new URL(request.url, 'https://localhost:5101').pathname); }
  catch { response.writeHead(400).end(); return; }
  const requested = path.resolve(webRoot, '.' + pathname);
  if (requested !== webRoot && !requested.startsWith(webRoot + path.sep)) {
    response.writeHead(400).end(); return;
  }
  const file = existsSync(requested) && statSync(requested).isFile() ? requested : path.join(webRoot, 'index.html');
  response.writeHead(200, { 'Content-Type': types[path.extname(file)] || 'application/octet-stream',
    'Cache-Control': 'no-store' });
  response.end(readFileSync(file));
});
const proxy = net.createServer(client => {
  const upstream = net.connect({ host: apiHost, port: apiPort });
  client.on('error', () => upstream.destroy());
  upstream.on('error', () => client.destroy());
  client.pipe(upstream).pipe(client);
});
function listen(server, port) {
  return new Promise((resolve, reject) => { server.once('error', reject); server.listen(port, '127.0.0.1', resolve); });
}
function browserHome(name, trust) {
  const home = path.join(temporary, name);
  const database = path.join(home, '.pki', 'nssdb');
  mkdirSync(database, { recursive: true, mode: 0o700 });
  execFileSync('certutil', ['-N', '--empty-password', '-d', 'sql:' + database], { stdio: 'pipe' });
  if (trust) execFileSync('certutil', ['-A', '-d', 'sql:' + database, '-n', 'MedSoft temporary verification CA',
    '-t', 'C,,', '-i', caFile], { stdio: 'pipe' });
  return home;
}
async function openBrowser(home) {
  const browser = await chromium.launch({ headless: true, env: { ...process.env, HOME: home } });
  browsers.push(browser);
  return browser;
}
try {
  await Promise.all([listen(web, 5101), listen(proxy, 5201)]);
  const untrusted = await openBrowser(browserHome('untrusted', false));
  const rejectedPage = await untrusted.newPage();
  try { await rejectedPage.goto('https://localhost:5101/login'); }
  catch (error) { evidence.certificateRejectedWithoutCa = /CERT_AUTHORITY_INVALID/.test(error.message); }
  assert(evidence.certificateRejectedWithoutCa, 'The browser must reject the certificate without its CA.');
  await untrusted.close();

  const browser = await openBrowser(browserHome('trusted', true));
  const page = await browser.newPage();
  const cdp = await page.context().newCDPSession(page);
  await cdp.send('Network.enable');
  cdp.on('Network.requestWillBeSent', ({ request }) => {
    if (request.method !== 'OPTIONS') return;
    const headers = request.headers;
    const requestedHeaders = Object.entries(headers).find(([key]) => key.toLowerCase() === 'access-control-request-headers')?.[1] || '';
    evidence.preflightRequests.push({ path: new URL(request.url).pathname,
      headers: requestedHeaders.toLowerCase().split(',').map(value => value.trim()).filter(Boolean) });
  });
  let successfulRefreshes = 0;
  let successfulLogout = false;
  page.on('response', response => {
    const pathname = new URL(response.url()).pathname;
    if (pathname.startsWith('/api/')) evidence.apiResponses.push({ path: pathname, status: response.status() });
    if (pathname === '/api/auth/refresh' && response.status() === 200) successfulRefreshes++;
    if (pathname === '/api/auth/logout' && response.status() === 204) successfulLogout = true;
  });
  evidence.stage = 'open-login';
  await page.goto('https://localhost:5101/login');
  await page.getByLabel('Логин', { exact: true }).waitFor();
  evidence.trustedHttps = true;

  const setupStatus = await page.evaluate(async () => {
    const csrf = await fetch('https://localhost:5201/api/auth/csrf', { credentials: 'include' }).then(response => response.json());
    const response = await fetch('https://localhost:5201/api/auth/setup/begin', { method: 'POST', credentials: 'include',
      headers: { 'Content-Type': 'application/json', 'X-CSRF-TOKEN': csrf.requestToken,
        'X-Setup-Token': 'synthetic-invalid-owner-for-cors-verification' },
      body: JSON.stringify({ password: 'Synthetic.Cors-Only.425!', confirmPassword: 'Synthetic.Cors-Only.425!' }) });
    return response.status;
  });
  assert.equal(setupStatus, 403, 'CORS must allow the setup headers while the API rejects an invalid owner token.');
  evidence.setupPreflight = true;

  evidence.unconfiguredHeaderRejected = await page.evaluate(async () => {
    try {
      await fetch('https://localhost:5201/api/patients', { credentials: 'include',
        headers: { 'X-Unconfigured-Header': 'synthetic-cors-negative-check' } });
      return false;
    } catch { return true; }
  });
  assert(evidence.unconfiguredHeaderRejected, 'The browser must reject an unconfigured cross-origin header.');

  evidence.stage = 'login';
  await page.getByLabel('Логин', { exact: true }).fill(credentials.userName);
  await page.getByLabel('Пароль', { exact: true }).fill(credentials.password);
  await Promise.all([page.waitForURL('**/medical/patients'), page.getByRole('button', { name: 'Войти', exact: true }).click()]);
  await page.getByRole('heading', { name: 'Пациенты', exact: true }).waitFor();
  evidence.login = true;
  const csrfAfterLogin = await page.evaluate(async () => {
    const response = await fetch('https://localhost:5201/api/patients', {
      credentials: 'include', headers: { Authorization: 'Bearer synthetic-invalid-token-for-cors-verification' } });
    return response.status;
  });
  assert.equal(csrfAfterLogin, 401, 'CORS must permit Authorization while the API rejects an invalid bearer.');
  evidence.bearerPreflight = true;

  const refreshesBeforeReload = successfulRefreshes;
  evidence.stage = 'reload';
  await page.reload();
  await page.getByRole('heading', { name: 'Пациенты', exact: true }).waitFor();
  assert.match(page.url(), /\/medical\/patients$/);
  assert.equal(successfulRefreshes, refreshesBeforeReload + 1, 'Reload must restore the session through one successful refresh.');
  evidence.reloadRefresh = true;

  evidence.stage = 'logout';
  await Promise.all([page.waitForURL(url => url.pathname === '/login'), page.getByRole('button', { name: 'Выйти', exact: true }).click()]);
  await page.getByLabel('Логин', { exact: true }).waitFor();
  assert(successfulLogout, 'The UI must confirm server-side logout.');
  const refreshAfterLogout = await page.evaluate(async () => {
    const csrf = await fetch('https://localhost:5201/api/auth/csrf', { credentials: 'include' }).then(response => response.json());
    return (await fetch('https://localhost:5201/api/auth/refresh', { method: 'POST', credentials: 'include',
      headers: { 'X-CSRF-TOKEN': csrf.requestToken } })).status;
  });
  assert.equal(refreshAfterLogout, 401, 'Logout must prevent refresh-cookie restoration.');
  evidence.logout = true;
  evidence.stage = 'complete';
  if (outputFile) writeFileSync(outputFile, JSON.stringify(evidence, null, 2) + '\n', { mode: 0o600 });
  console.log(JSON.stringify(evidence));
} catch (error) {
  console.error(JSON.stringify(evidence));
  console.error('Browser security verification failed: ' + error.message.split('\n')[0]);
  process.exitCode = 1;
} finally {
  await Promise.all(browsers.map(browser => browser.close().catch(() => {})));
  await Promise.all([new Promise(resolve => web.close(resolve)), new Promise(resolve => proxy.close(resolve))]);
  rmSync(temporary, { recursive: true, force: true });
}
