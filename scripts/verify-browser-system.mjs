import assert from 'node:assert/strict';
import { createHmac } from 'node:crypto';

const webOrigin = 'https://localhost:5101';
const apiOrigin = 'https://localhost:5201';
const tabIds = new WeakMap();

export async function bufferBrowserApiResponses(context) {
  await context.addInitScript(({ origin }) => {
    const fetch = window.fetch.bind(window);
    window.fetch = async (...arguments_) => {
      const response = await fetch(...arguments_);
      const url = new URL(response.url);
      if (url.origin === origin && url.pathname.startsWith('/api/') && response.body !== null)
        await response.clone().arrayBuffer();
      return response;
    };
  }, { origin: apiOrigin });
}

function totp(secret, offsetSeconds = 0) {
  const alphabet = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ234567';
  let value = 0;
  let bits = 0;
  const bytes = [];
  for (const character of secret.toUpperCase().replace(/=+$/, '')) {
    const digit = alphabet.indexOf(character);
    assert(digit >= 0, 'The setup secret must be valid Base32.');
    value = (value << 5) | digit;
    bits += 5;
    if (bits >= 8) {
      bits -= 8;
      bytes.push((value >>> bits) & 255);
      value &= (1 << bits) - 1;
    }
  }
  const counter = Buffer.alloc(8);
  counter.writeBigUInt64BE(BigInt(Math.floor((Date.now() / 1000 + offsetSeconds) / 30)));
  const hash = createHmac('sha1', Buffer.from(bytes)).update(counter).digest();
  const offset = hash.at(-1) & 15;
  return String((hash.readUInt32BE(offset) & 0x7fffffff) % 1_000_000).padStart(6, '0');
}

async function apiResponse(page, pathname, status, method = null) {
  const response = await page.waitForResponse(response => new URL(response.url()).origin === apiOrigin &&
    new URL(response.url()).pathname === pathname && response.status() === status &&
    (method === null || response.request().method() === method));
  const tabId = response.request().headers()['x-medsoft-tab-id'];
  assert.match(tabId ?? '', /^[a-f0-9]{32}$/, 'Browser API requests must identify their current tab.');
  tabIds.set(page, tabId);
  const body = status === 204 ? null : await response.json();
  return { json: async () => body };
}

async function apiRequest(page, bearer, method, pathname, body = undefined) {
  const tabId = tabIds.get(page);
  assert(tabId, 'The browser scenario must observe an API request from this tab before direct API checks.');
  return page.evaluate(async ({ origin, bearer, method, pathname, body, tabId }) => {
    const headers = { Authorization: bearer, 'X-MedSoft-Tab-Id': tabId };
    if (!['GET', 'HEAD', 'OPTIONS'].includes(method)) {
      const csrfResponse = await fetch(origin + '/api/auth/csrf', { credentials: 'include', headers });
      if (!csrfResponse.ok) return { status: csrfResponse.status(), body: null };
      headers['X-CSRF-TOKEN'] = (await csrfResponse.json()).requestToken;
    }
    if (body !== undefined) headers['Content-Type'] = 'application/json';
    const response = await fetch(origin + pathname, { method, credentials: 'include', headers,
      body: body === undefined ? undefined : JSON.stringify(body) });
    return { status: response.status, body: response.ok && response.status !== 204 ? await response.json() : null };
  }, { origin: apiOrigin, bearer, method, pathname, body, tabId });
}

async function login(page, credentials, administrator = false, code = null) {
  const loginPath = administrator ? '/admin/login' : '/login';
  const current = new URL(page.url());
  if (current.pathname !== loginPath || current.search) {
    await page.goto(webOrigin + loginPath);
  }
  await page.getByLabel('Логин', { exact: true }).fill(credentials.userName);
  await page.getByLabel('Пароль', { exact: true }).fill(credentials.password);
  if (administrator) {
    assert.equal(await page.getByLabel('Код подтверждения', { exact: true }).count(), 0,
      'The password page must not contain a TOTP field.');
    const [challenge] = await Promise.all([
      apiResponse(page, '/api/auth/admin/login', 200, 'POST'),
      page.waitForURL(url => url.pathname === '/admin/login/confirm'),
      page.getByRole('button', { name: 'Продолжить', exact: true }).click()
    ]);
    const firstStep = await challenge.json();
    assert.equal(firstStep.accessToken, undefined, 'The password step must not issue an access token.');
    await page.reload();
    await page.getByLabel('Код подтверждения', { exact: true }).waitFor();
    if (process.env.BROWSER_UI_SCREENSHOT_DIRECTORY) await page.screenshot({
      path: `${process.env.BROWSER_UI_SCREENSHOT_DIRECTORY}/administrator-code.png`, fullPage: true
    });
    await page.getByLabel('Код подтверждения', { exact: true }).fill(code);
    assert.equal(await page.getByLabel('Пароль', { exact: true }).count(), 0,
      'The confirmation page must not contain the password.');
  }
  const home = administrator ? '/admin/medical-workers' : '/medical/patients';
  const list = administrator ? '/api/medical-workers' : '/api/patients';
  const [authenticated] = await Promise.all([
    apiResponse(page, administrator ? '/api/auth/admin/confirm' : '/api/auth/login', 200, 'POST'),
    apiResponse(page, list, 200, 'GET'),
    page.waitForURL(url => url.pathname === home),
    page.getByRole('button', { name: administrator ? 'Подтвердить вход' : 'Войти', exact: true }).click()
  ]);
  await page.getByRole('heading', { name: administrator ? 'Медработники' : 'Пациенты', exact: true }).waitFor();
  return authenticated.json();
}

async function logout(page) {
  await Promise.all([
    apiResponse(page, '/api/auth/logout', 204, 'POST'),
    page.waitForURL(url => url.pathname === '/login' || url.pathname === '/admin/login'),
    page.getByRole('button', { name: 'Выйти', exact: true }).click()
  ]);
  await page.getByLabel('Логин', { exact: true }).waitFor();
}

async function fillSetup(page, credentials) {
  await page.getByLabel('Одноразовый токен настройки', { exact: true }).fill(credentials.ownerToken);
  await page.getByLabel('Пароль администратора', { exact: true }).fill(credentials.administratorPassword);
  await page.getByLabel('Повторите пароль', { exact: true }).fill(credentials.administratorPassword);
  await Promise.all([apiResponse(page, '/api/auth/setup/begin', 200, 'POST'),
    page.getByRole('button', { name: 'Продолжить', exact: true }).click()]);
  const field = page.getByLabel('Секрет TOTP', { exact: true });
  await field.waitFor();
  return field.inputValue();
}

async function createAccount(page, bearer, role, userName) {
  const details = role === 'nurse' ? { firstName: 'Медсестра', position: 'Медицинская сестра' }
    : role === 'laboratory-technician' ? { firstName: 'Лаборант', position: 'Лаборант' }
    : { firstName: 'Сотрудник', position: 'Сотрудник' };
  const worker = await apiRequest(page, bearer, 'POST', '/api/medical-workers', {
    ...details, lastName: 'Браузерный', ...(role === 'other' ? {} : { role })
  });
  assert.equal(worker.status, 201, 'The administrator must be able to create a medical worker.');
  assert.equal(worker.body.role, role, 'An omitted medical role must default to other.');
  const account = await apiRequest(page, bearer, 'POST', `/api/medical-workers/${worker.body.id}/account`, { userName });
  assert.equal(account.status, 201, 'The administrator must be able to create a linked account.');
  assert(typeof account.body.generatedPassword === 'string', 'Account creation must return its generated password.');
  return { userName, password: account.body.generatedPassword };
}

async function visibleRevocation(page, administratorBearer, accountId, reason, count) {
  const audit = await apiRequest(page, administratorBearer, 'GET', '/api/audit?Page=1&PageSize=100');
  assert.equal(audit.status, 200, 'The administrator must read revocation audit events.');
  const event = audit.body.items.find(item => item.eventType === 'AccountChanged' &&
    item.metadata.accountId === accountId && item.metadata.revokeReason === reason &&
    item.metadata.revokedRefreshTokenCount === count);
  assert(event, 'The audit must store the exact revocation reason, account and session count.');
  await page.bringToFront();
  await page.locator(`tr[data-event-id="${event.eventId}"]`)
    .getByText(`Отозвано refresh-токенов: ${count}.`, { exact: true })
    .waitFor({ state: 'visible', timeout: 2500 });
}

async function verifyRevocation(page, administratorBearer, credentials, accountId, reason, operation, restore = null) {
  const browser = page.context().browser();
  assert(browser, 'The revocation scenario requires an independent browser context.');
  const context = await browser.newContext();
  try {
    await bufferBrowserApiResponses(context);
    const employee = await context.newPage();
    await employee.goto(webOrigin + '/login');
    const authenticated = await login(employee, credentials);
    assert(typeof authenticated.accessToken === 'string', 'Login must issue an access token.');
    const result = await operation();
    assert.equal(result.status, 200, 'The administrator revocation operation must succeed.');
    if (result.body?.revokedRefreshTokenCount !== undefined)
      assert.equal(result.body.revokedRefreshTokenCount, 1, 'Exactly one independently logged-in session must be revoked.');
    const oldBearer = 'Bearer ' + authenticated.accessToken;
    const deniedAccess = await apiRequest(employee, oldBearer, 'GET', '/api/patients');
    assert.equal(deniedAccess.status, 401, 'The pre-change access token must lose access immediately.');
    const deniedRefresh = await apiRequest(employee, oldBearer, 'POST', '/api/auth/refresh');
    assert.equal(deniedRefresh.status, 401, 'The independent employee refresh cookie must be revoked.');
    await visibleRevocation(page, administratorBearer, accountId, reason, 1);
    return result;
  } finally {
    await context.close();
    if (restore) await restore();
  }
}

async function restoreTab(page, userName) {
  const tabId = tabIds.get(page);
  const [response] = await Promise.all([
    apiResponse(page, '/api/auth/refresh', 200, 'POST'),
    page.reload()
  ]);
  const tokens = await response.json();
  assert.equal(tokens.session.userName, userName, 'Reload must restore the user belonging to this tab.');
  assert.equal(tabIds.get(page), tabId, 'Reload must preserve the tab identifier.');
  await page.locator('.mode-label').filter({ hasText: userName + ' ·' }).waitFor();
  return tokens;
}

async function assertSession(page, tokens, userName) {
  const session = await apiRequest(page, 'Bearer ' + tokens.accessToken, 'GET', '/api/auth/session');
  assert.equal(session.status, 200, 'Another tab must not revoke this tab access token.');
  assert.equal(session.body.userName, userName, 'A tab must retain its own account identity.');
}

async function verifyParallelTabs(administrator, administratorBearer, administratorName, doctor, nurse, evidence) {
  const context = administrator.context();
  const pages = [];
  const administratorTokens = { accessToken: administratorBearer.slice('Bearer '.length) };
  try {
    const doctorPage = await context.newPage();
    pages.push(doctorPage);
    await doctorPage.goto(webOrigin + '/login');
    await doctorPage.getByLabel('Логин', { exact: true }).waitFor();
    let doctorTokens = await login(doctorPage, doctor);
    const nursePage = await context.newPage();
    pages.push(nursePage);
    await nursePage.goto(webOrigin + '/login');
    await nursePage.getByLabel('Логин', { exact: true }).waitFor();
    let nurseTokens = await login(nursePage, nurse);

    const identifiers = [administrator, doctorPage, nursePage].map(page => tabIds.get(page));
    assert.equal(new Set(identifiers).size, 3, 'Pages in one browser context must receive independent tab identifiers.');
    const cookies = await context.cookies(apiOrigin);
    for (const tabId of identifiers) {
      const cookie = cookies.find(item => item.name === 'MedSoft.Refresh.' + tabId);
      assert(cookie?.httpOnly && cookie.secure && cookie.path === '/',
        'Each tab must have an independent HttpOnly Secure refresh cookie.');
    }
    await Promise.all([
      assertSession(administrator, administratorTokens, administratorName),
      assertSession(doctorPage, doctorTokens, doctor.userName),
      assertSession(nursePage, nurseTokens, nurse.userName)
    ]);
    const mutations = await Promise.all([
      apiRequest(doctorPage, 'Bearer ' + doctorTokens.accessToken, 'POST', '/api/patients', {
        firstName: 'Врач', lastName: 'Параллельный', birthDate: '2000-01-01', sex: 'Unknown'
      }),
      apiRequest(nursePage, 'Bearer ' + nurseTokens.accessToken, 'POST', '/api/patients', {
        firstName: 'Медсестра', lastName: 'Параллельный', birthDate: '2000-01-02', sex: 'Unknown'
      })
    ]);
    assert(mutations.every(result => result.status === 201),
      'Different accounts in tabs sharing browser cookies must perform mutations concurrently.');
    evidence.differentAccountsMutateInParallel = true;
    for (let round = 0; round < 2; round++) {
      [doctorTokens, nurseTokens] = await Promise.all([
        restoreTab(doctorPage, doctor.userName), restoreTab(nursePage, nurse.userName)
      ]);
      await assertSession(administrator, administratorTokens, administratorName);
    }
    evidence.differentAccountsReloadIndependently = true;

    const [duplicate] = await Promise.all([
      context.waitForEvent('page'),
      doctorPage.evaluate(origin => { window.open(origin + '/login', '_blank'); }, webOrigin)
    ]);
    pages.push(duplicate);
    await duplicate.getByLabel('Логин', { exact: true }).waitFor();
    let duplicateTokens = await login(duplicate, doctor);
    assert.notEqual(tabIds.get(duplicate), tabIds.get(doctorPage),
      'An opener-created tab must not reuse the copied sessionStorage identifier.');
    const decode = token => JSON.parse(Buffer.from(token.split('.')[1], 'base64url').toString());
    const first = decode(doctorTokens.accessToken);
    const second = decode(duplicateTokens.accessToken);
    assert.equal(first.sub, second.sub, 'Both independent sessions must belong to the same doctor.');
    assert.notEqual(first.sid, second.sid, 'The same account must receive independent session families in separate tabs.');
    assert.notEqual(first.jti, second.jti, 'The same account must receive independent session rows in separate tabs.');
    await assertSession(doctorPage, doctorTokens, doctor.userName);
    [doctorTokens, duplicateTokens] = await Promise.all([
      restoreTab(doctorPage, doctor.userName), restoreTab(duplicate, doctor.userName)
    ]);
    await Promise.all([
      assertSession(doctorPage, doctorTokens, doctor.userName),
      assertSession(duplicate, duplicateTokens, doctor.userName),
      assertSession(nursePage, nurseTokens, nurse.userName)
    ]);
    evidence.sameAccountTabsHaveIndependentSessions = true;
    evidence.openerTabStorageIsolated = true;

    await logout(duplicate);
    await duplicate.reload();
    await duplicate.getByLabel('Логин', { exact: true }).waitFor();
    await logout(nursePage);
    await Promise.all([
      assertSession(administrator, administratorTokens, administratorName),
      assertSession(doctorPage, doctorTokens, doctor.userName)
    ]);
    doctorTokens = await restoreTab(doctorPage, doctor.userName);
    await assertSession(doctorPage, doctorTokens, doctor.userName);
    evidence.logoutDoesNotAffectOtherTabs = true;
    await logout(doctorPage);
    await assertSession(administrator, administratorTokens, administratorName);
  } finally {
    await Promise.all(pages.map(page => page.close()));
  }
}

export async function verifyBrowserSystem(page, credentials, evidence) {
  assert(typeof credentials.ownerToken === 'string' && typeof credentials.administratorPassword === 'string',
    'The system scenario requires ownerToken and administratorPassword in its private credentials file.');
  const administratorName = credentials.administratorUserName || 'admin';
  const bearers = new Map();
  const rememberBearer = request => {
    if (new URL(request.url()).origin !== apiOrigin) return;
    const bearer = request.headers().authorization;
    if (!bearer?.startsWith('Bearer ')) return;
    try {
      const payload = JSON.parse(Buffer.from(bearer.slice(7).split('.')[1], 'base64url').toString());
      if (typeof payload.name === 'string') bearers.set(payload.name, bearer);
    } catch { }
  };
  page.on('request', rememberBearer);
  try {
    evidence.stage = 'setup';
    await page.waitForURL(url => url.pathname === '/setup');
    const previousSecret = await fillSetup(page, credentials);
    await page.getByRole('button', { name: 'Начать заново', exact: true }).click();
    await page.getByLabel('Одноразовый токен настройки', { exact: true }).waitFor();
    assert.equal(await page.getByLabel('Пароль администратора', { exact: true }).inputValue(), '',
      'Restart must clear the previous password.');
    const secret = await fillSetup(page, credentials);
    assert(secret !== previousSecret, 'Restart must issue a different TOTP secret.');
    evidence.restartSecretRotated = true;
    await page.getByLabel('Код из приложения', { exact: true }).fill(totp(secret));
    await Promise.all([apiResponse(page, '/api/auth/setup/confirm', 204, 'POST'),
      page.getByRole('button', { name: 'Завершить настройку', exact: true }).click()]);
    await page.getByText(/Для входа дождитесь следующего шестизначного кода/).waitFor();
    evidence.nextTotpExplained = true;
    await page.getByRole('link', { name: 'К входу администратора', exact: true }).click();
    await login(page, { userName: administratorName, password: credentials.administratorPassword }, true, totp(secret, 30));
    assert(bearers.has(administratorName), 'An authenticated administrator API request must carry its bearer.');
    evidence.administratorLogin = true;

    evidence.stage = 'doctor-account';
    await page.getByRole('link', { name: 'Добавить медработника', exact: true }).click();
    await page.getByRole('heading', { name: 'Новый медработник', exact: true }).waitFor();
    await page.getByLabel('Фамилия', { exact: true }).fill('Браузерный');
    await page.getByLabel('Имя', { exact: true }).fill('Врач');
    await page.getByLabel('Должность', { exact: true }).fill('Врач-терапевт');
    assert.equal(await page.getByLabel('Медицинская роль', { exact: true }).inputValue(), 'Other',
      'A new worker form must select other by default.');
    await page.getByLabel('Медицинская роль', { exact: true }).selectOption({ label: 'Врач' });
    const [createdWorker] = await Promise.all([apiResponse(page, '/api/medical-workers', 201, 'POST'),
      page.getByRole('button', { name: 'Сохранить', exact: true }).click()]);
    const worker = await createdWorker.json();
    await page.waitForURL(url => url.pathname === '/admin/medical-workers');
    const row = page.locator('tr').filter({ has: page.locator(`a[href="/admin/medical-workers/${worker.id}"]`) });
    await row.waitFor();
    assert.equal((await row.locator('[data-label="Роль"]').textContent()).trim(), 'Врач',
      'The worker list must show the stored doctor role.');
    evidence.medicalRoleSaved = true;
    await row.getByRole('link', { name: 'Редактировать: Браузерный Врач', exact: true }).click();
    await page.getByRole('heading', { name: 'Учётная запись сотрудника', exact: true }).waitFor();
    await page.getByLabel('Логин', { exact: true }).waitFor();
    const formBounds = await page.locator('.worker-form').boundingBox();
    const summaryBounds = await page.locator('.worker-summary').boundingBox();
    const accountBounds = await page.locator('.account-panel').boundingBox();
    assert(formBounds && summaryBounds && accountBounds && Math.abs(summaryBounds.x - accountBounds.x) < 2 &&
      Math.abs(summaryBounds.width - accountBounds.width) < 2 &&
      accountBounds.y - (summaryBounds.y + summaryBounds.height) >= 0 &&
      accountBounds.y - (summaryBounds.y + summaryBounds.height) <= 20 &&
      accountBounds.x + accountBounds.width < formBounds.x,
      'The account section must fit beneath the compact summary beside the worker form.');
    if (process.env.BROWSER_UI_SCREENSHOT_DIRECTORY) {
      await page.screenshot({ path: `${process.env.BROWSER_UI_SCREENSHOT_DIRECTORY}/worker-card-desktop.png`, fullPage: true });
      const viewport = page.viewportSize();
      await page.setViewportSize({ width: 390, height: 844 });
      await page.screenshot({ path: `${process.env.BROWSER_UI_SCREENSHOT_DIRECTORY}/worker-card-mobile.png`, fullPage: true });
      const mobile = await page.evaluate(() => ({ content: document.documentElement.scrollWidth, viewport: window.innerWidth }));
      assert(mobile.content <= mobile.viewport,
        `The mobile worker card must not overflow horizontally (${mobile.content} vs ${mobile.viewport}).`);
      await page.setViewportSize(viewport);
    }
    evidence.workerAccountLayoutAligned = true;
    const doctorName = 'browser.doctor';
    await page.getByLabel('Логин', { exact: true }).fill(doctorName);
    const [createdAccount] = await Promise.all([
      apiResponse(page, `/api/medical-workers/${worker.id}/account`, 201, 'POST'),
      page.getByRole('button', { name: 'Создать учётную запись', exact: true }).click()
    ]);
    const account = await createdAccount.json();
    await page.getByLabel('Сгенерированный пароль', { exact: true }).waitFor();
    const oldPassword = await page.getByLabel('Сгенерированный пароль', { exact: true }).inputValue();
    assert(oldPassword === account.generatedPassword, 'The UI must show the actual password returned by account creation.');
    if (process.env.BROWSER_UI_SCREENSHOT_DIRECTORY) {
      const mask = [page.getByLabel('Сгенерированный пароль', { exact: true })];
      await page.locator('#main-content').focus();
      await page.screenshot({ path: `${process.env.BROWSER_UI_SCREENSHOT_DIRECTORY}/worker-account-created.png`,
        fullPage: true, mask, maskColor: '#e7ece5' });
      const viewport = page.viewportSize();
      await page.setViewportSize({ width: 390, height: 844 });
      assert(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth),
        'The generated password and account operations must fit on mobile.');
      await page.screenshot({ path: `${process.env.BROWSER_UI_SCREENSHOT_DIRECTORY}/worker-account-created-mobile.png`,
        fullPage: true, mask, maskColor: '#e7ece5' });
      await page.setViewportSize(viewport);
    }
    await page.getByRole('button', { name: 'Сгенерировать новый пароль', exact: true }).click();
    const [resetResponse] = await Promise.all([
      apiResponse(page, `/api/accounts/${account.account.id}/reset-password`, 200, 'POST'),
      page.getByRole('button', { name: 'Подтвердить', exact: true }).click()
    ]);
    const reset = await resetResponse.json();
    await page.waitForFunction(password => {
      const cards = [...document.querySelectorAll('.password-card input[aria-label="Сгенерированный пароль"]')];
      return cards.length === 1 && cards[0].value === password;
    }, reset.generatedPassword);
    assert(reset.generatedPassword !== oldPassword, 'Password reset must replace the generated credential.');
    assert.equal(await page.locator('.password-card').count(), 1, 'Only the current generated password may remain visible.');
    evidence.createdPasswordReplaced = true;
    const timestamps = await page.locator('.account-facts dd').allTextContents();
    evidence.accountTimestampsHaveSeconds = timestamps.filter(value => /\d{2}\.\d{2}\.\d{4} \d{2}:\d{2}:\d{2}/.test(value)).length === 2;
    assert(evidence.accountTimestampsHaveSeconds, 'Account timestamps must include seconds.');

    evidence.stage = 'audit-latency';
    await page.getByRole('link', { name: 'Аудит', exact: true }).click();
    await page.getByRole('heading', { name: 'Аудит', exact: true }).waitFor();
    await page.locator('tr[data-event-id]').first().waitFor();
    await page.bringToFront();
    await apiResponse(page, '/api/audit', 200, 'GET');
    const visibleRefresh = apiResponse(page, '/api/audit', 200, 'GET');
    const visibilityStarted = performance.now();
    await page.evaluate(() => document.dispatchEvent(new Event('visibilitychange')));
    await visibleRefresh;
    evidence.auditVisibilityRefreshMilliseconds = Math.round(performance.now() - visibilityStarted);
    assert(evidence.auditVisibilityRefreshMilliseconds < 750,
      'Returning to the visible audit page must refresh before the next periodic tick.');
    evidence.auditRefreshOnVisibility = true;
    const administratorBearer = bearers.get(administratorName);
    await visibleRevocation(page, administratorBearer, account.account.id, 'password-reset', 0);
    evidence.zeroRevocationCountVisible = true;
    const mutation = await page.evaluate(async ({ origin, bearer, tabId }) => {
      const headers = { Authorization: bearer, 'X-MedSoft-Tab-Id': tabId };
      const csrf = await fetch(origin + '/api/auth/csrf', { credentials: 'include', headers }).then(response => response.json());
      headers['X-CSRF-TOKEN'] = csrf.requestToken;
      headers['Content-Type'] = 'application/json';
      const startedAt = performance.now();
      const response = await fetch(origin + '/api/medical-workers', { method: 'POST', credentials: 'include', headers,
        body: JSON.stringify({ firstName: 'Аудит', lastName: 'Браузерный', position: 'Проверка аудита', role: 'other' }) });
      return { status: response.status, workerId: response.ok ? (await response.json()).id : null, startedAt };
    }, { origin: apiOrigin, bearer: administratorBearer, tabId: tabIds.get(page) });
    assert.equal(mutation.status, 201, 'The audit latency mutation must succeed.');
    const audit = await apiRequest(page, administratorBearer, 'GET', '/api/audit?Page=1&PageSize=100');
    assert.equal(audit.status, 200, 'The administrator must read the real audit endpoint.');
    const event = audit.body.items.find(item => item.eventType === 'MedicalWorkerCreated' &&
      item.metadata.targetId === mutation.workerId);
    assert(event, 'The measured audit event must be identified by target UUID.');
    await page.locator(`tr[data-event-id="${event.eventId}"]`).waitFor({ state: 'visible', timeout: 2100 });
    const latency = await page.evaluate(startedAt => performance.now() - startedAt, mutation.startedAt);
    evidence.auditDisplayLatencyMilliseconds = Math.round(latency);
    assert(latency <= 2000, 'A committed audit event must become visible in the UI within two seconds.');
    evidence.auditEventMatchedById = true;

    let doctorCredentials = { userName: doctorName, password: reset.generatedPassword };
    evidence.stage = 'password-reset-revocation';
    const activeReset = await verifyRevocation(page, administratorBearer, doctorCredentials, account.account.id,
      'password-reset', () => apiRequest(page, administratorBearer, 'POST',
        `/api/accounts/${account.account.id}/reset-password`));
    assert(typeof activeReset.body.generatedPassword === 'string', 'Reset must return the replacement password.');
    doctorCredentials = { userName: doctorName, password: activeReset.body.generatedPassword };
    evidence.passwordResetRevokesAccessAndRefresh = true;

    evidence.stage = 'deactivation-revocation';
    await verifyRevocation(page, administratorBearer, doctorCredentials, account.account.id,
      'account-deactivated', () => apiRequest(page, administratorBearer, 'PUT',
        `/api/accounts/${account.account.id}/activity`, { isActive: false }),
      async () => {
        const restored = await apiRequest(page, administratorBearer, 'PUT',
          `/api/accounts/${account.account.id}/activity`, { isActive: true });
        assert.equal(restored.status, 200, 'The doctor account must be restored to active after the revocation check.');
      });
    evidence.deactivationRevokesAccessAndRefresh = true;

    evidence.stage = 'medical-role-revocation';
    const workerValues = { firstName: worker.firstName, lastName: worker.lastName, position: worker.position };
    await verifyRevocation(page, administratorBearer, doctorCredentials, account.account.id,
      'medical-role-changed', () => apiRequest(page, administratorBearer, 'PUT',
        `/api/medical-workers/${worker.id}`, { ...workerValues, role: 'nurse' }),
      async () => {
        const restored = await apiRequest(page, administratorBearer, 'PUT',
          `/api/medical-workers/${worker.id}`, { ...workerValues, role: 'doctor' });
        assert.equal(restored.status, 200, 'The doctor role must be restored after the revocation check.');
      });
    evidence.medicalRoleChangeRevokesAccessAndRefresh = true;
    evidence.revocationCountsVisible = true;

    evidence.stage = 'clinical-roles';
    const nurse = await createAccount(page, administratorBearer, 'nurse', 'browser.nurse');
    const reader = await createAccount(page, administratorBearer, 'laboratory-technician', 'browser.lab');
    const other = await createAccount(page, administratorBearer, 'other', 'browser.other');
    evidence.otherRoleDefaultsOnServer = true;
    evidence.stage = 'parallel-browser-tabs';
    await verifyParallelTabs(page, administratorBearer, administratorName, doctorCredentials, nurse, evidence);
    evidence.stage = 'clinical-roles';
    await logout(page);
    await login(page, nurse);
    await page.getByRole('link', { name: 'Добавить пациента', exact: true }).click();
    await page.getByLabel('Фамилия', { exact: true }).fill('Пациент');
    await page.getByLabel('Имя', { exact: true }).fill('Тестовый');
    await page.getByLabel('Дата рождения', { exact: true }).fill('2000-01-01');
    await page.getByLabel('Пол', { exact: true }).selectOption({ label: 'Ж — женский' });
    const [patientResponse] = await Promise.all([apiResponse(page, '/api/patients', 201, 'POST'),
      page.getByRole('button', { name: 'Сохранить', exact: true }).click()]);
    const patient = await patientResponse.json();
    await page.waitForURL(url => url.pathname === '/medical/patients');
    await page.locator(`a[href="/medical/patients/${patient.id}/medical-record"]`).click();
    const [recordResponse] = await Promise.all([
      apiResponse(page, `/api/patients/${patient.id}/medical-record`, 201, 'POST'),
      page.getByRole('button', { name: 'Создать ЭМК', exact: true }).click()
    ]);
    const record = await recordResponse.json();
    await page.getByRole('tab', { name: 'Диагнозы', exact: true }).waitFor();
    assert.equal(await page.getByRole('button', { name: 'Редактировать', exact: true }).count(), 0,
      'The nurse may create a record but must not see medical editing actions.');
    const forbiddenPrescription = await apiRequest(page, bearers.get(nurse.userName), 'POST',
      `/api/medical-records/${record.id}/prescriptions`, { text: 'Synthetic browser prescription' });
    assert.equal(forbiddenPrescription.status, 403, 'The API must independently reject a nurse prescription.');
    evidence.nurseCreatesPatientAndRecord = true;
    evidence.nurseCannotEditRecord = true;
    await logout(page);
    for (const [role, employee] of [['laboratory', reader], ['other', other]]) {
      await login(page, employee);
      assert.equal(await page.getByRole('link', { name: 'Добавить пациента', exact: true }).count(), 0,
        'A read-only medical role must not see patient creation.');
      await page.locator(`a[href="/medical/patients/${patient.id}/medical-record"]`).click();
      await page.getByRole('tab', { name: 'Диагнозы', exact: true }).waitFor();
      assert.equal(await page.getByRole('button', { name: 'Редактировать', exact: true }).count(), 0,
        'A read-only medical role must not see medical record editing.');
      const deniedPatient = await apiRequest(page, bearers.get(employee.userName), 'POST', '/api/patients', {
        firstName: 'Forbidden', lastName: 'Synthetic', birthDate: '2000-01-01', sex: 'Unknown'
      });
      assert.equal(deniedPatient.status, 403, 'The API must reject patient creation by a read-only medical role.');
      const deniedRecord = await apiRequest(page, bearers.get(employee.userName), 'POST',
        `/api/patients/${patient.id}/medical-record`);
      assert.equal(deniedRecord.status, 403, 'The API must reject record creation by a read-only medical role.');
      evidence[role === 'laboratory' ? 'laboratoryRoleReadOnly' : 'otherRoleReadOnly'] = true;
      await logout(page);
    }
    await page.goto(webOrigin + '/login');
    evidence.stage = 'complete';
    return doctorCredentials;
  } finally {
    page.off('request', rememberBearer);
    bearers.clear();
  }
}
