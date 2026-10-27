import assert from 'node:assert/strict';
import { createHmac } from 'node:crypto';

const webOrigin = 'https://localhost:5101';
const apiOrigin = 'https://localhost:5201';

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

function apiResponse(page, pathname, status, method = null) {
  return page.waitForResponse(response => new URL(response.url()).origin === apiOrigin &&
    new URL(response.url()).pathname === pathname && response.status() === status &&
    (method === null || response.request().method() === method));
}

async function apiRequest(page, bearer, method, pathname, body = undefined) {
  return page.evaluate(async ({ origin, bearer, method, pathname, body }) => {
    const headers = { Authorization: bearer };
    if (!['GET', 'HEAD', 'OPTIONS'].includes(method)) {
      const csrfResponse = await fetch(origin + '/api/auth/csrf', { credentials: 'include', headers });
      if (!csrfResponse.ok) return { status: csrfResponse.status(), body: null };
      headers['X-CSRF-TOKEN'] = (await csrfResponse.json()).requestToken;
    }
    if (body !== undefined) headers['Content-Type'] = 'application/json';
    const response = await fetch(origin + pathname, { method, credentials: 'include', headers,
      body: body === undefined ? undefined : JSON.stringify(body) });
    return { status: response.status, body: response.ok && response.status !== 204 ? await response.json() : null };
  }, { origin: apiOrigin, bearer, method, pathname, body });
}

async function login(page, credentials, administrator = false, code = null) {
  const loginPath = administrator ? '/admin/login' : '/login';
  const current = new URL(page.url());
  if (current.pathname !== loginPath || current.search) {
    await page.goto(webOrigin + loginPath);
  }
  await page.getByLabel('Логин', { exact: true }).fill(credentials.userName);
  await page.getByLabel('Пароль', { exact: true }).fill(credentials.password);
  if (administrator) await page.getByLabel('Код подтверждения', { exact: true }).fill(code);
  const home = administrator ? '/admin/medical-workers' : '/medical/patients';
  const list = administrator ? '/api/medical-workers' : '/api/patients';
  await Promise.all([
    apiResponse(page, '/api/auth/login', 200, 'POST'), apiResponse(page, list, 200, 'GET'),
    page.waitForURL(url => url.pathname === home),
    page.getByRole('button', { name: 'Войти', exact: true }).click()
  ]);
  await page.getByRole('heading', { name: administrator ? 'Медработники' : 'Пациенты', exact: true }).waitFor();
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
  const worker = await apiRequest(page, bearer, 'POST', '/api/medical-workers', {
    firstName: role === 'nurse' ? 'Медсестра' : 'Лаборант', lastName: 'Браузерный',
    position: role === 'nurse' ? 'Медицинская сестра' : 'Лаборант', role
  });
  assert.equal(worker.status, 201, 'The administrator must be able to create a medical worker.');
  const account = await apiRequest(page, bearer, 'POST', `/api/medical-workers/${worker.body.id}/account`, { userName });
  assert.equal(account.status, 201, 'The administrator must be able to create a linked account.');
  assert(typeof account.body.generatedPassword === 'string', 'Account creation must return its generated password.');
  return { userName, password: account.body.generatedPassword };
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
    await page.getByLabel('Роль', { exact: true }).selectOption({ label: 'Врач' });
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
    const mutation = await page.evaluate(async ({ origin, bearer }) => {
      const headers = { Authorization: bearer };
      const csrf = await fetch(origin + '/api/auth/csrf', { credentials: 'include', headers }).then(response => response.json());
      headers['X-CSRF-TOKEN'] = csrf.requestToken;
      headers['Content-Type'] = 'application/json';
      const startedAt = performance.now();
      const response = await fetch(origin + '/api/medical-workers', { method: 'POST', credentials: 'include', headers,
        body: JSON.stringify({ firstName: 'Аудит', lastName: 'Браузерный', position: 'Проверка аудита', role: 'other' }) });
      return { status: response.status, workerId: response.ok ? (await response.json()).id : null, startedAt };
    }, { origin: apiOrigin, bearer: administratorBearer });
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

    evidence.stage = 'clinical-roles';
    const nurse = await createAccount(page, administratorBearer, 'nurse', 'browser.nurse');
    const reader = await createAccount(page, administratorBearer, 'laboratory-technician', 'browser.lab');
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
    await login(page, reader);
    assert.equal(await page.getByRole('link', { name: 'Добавить пациента', exact: true }).count(), 0,
      'The laboratory role must not see patient creation.');
    await page.locator(`a[href="/medical/patients/${patient.id}/medical-record"]`).click();
    await page.getByRole('tab', { name: 'Диагнозы', exact: true }).waitFor();
    assert.equal(await page.getByRole('button', { name: 'Редактировать', exact: true }).count(), 0,
      'The laboratory role must only read the medical record.');
    const deniedPatient = await apiRequest(page, bearers.get(reader.userName), 'POST', '/api/patients', {
      firstName: 'Forbidden', lastName: 'Synthetic', birthDate: '2000-01-01', sex: 'Unknown'
    });
    assert.equal(deniedPatient.status, 403, 'The API must reject patient creation by the laboratory role.');
    evidence.laboratoryRoleReadOnly = true;
    await logout(page);
    await page.goto(webOrigin + '/login');
    evidence.stage = 'complete';
    return { userName: doctorName, password: reset.generatedPassword };
  } finally {
    page.off('request', rememberBearer);
    bearers.clear();
  }
}
