import AxeBuilder from '@axe-core/playwright';
import { expect, test, type Browser, type Page } from '@playwright/test';
import fs from 'node:fs';

// One person's working day, driven through the real browser, web app, API and database. Order matters: later steps use what earlier ones made.
test.describe.configure({ mode: 'serial' });

const IDP = 'http://localhost:9099';
const SUFFIX = Date.now().toString(36).toUpperCase();
const SITE = `RUH-${SUFFIX}`;
const BUILDING = `B1-${SUFFIX}`;
const FLOOR = `F3-${SUFFIX}`;
const ASSET = `CH-${SUFFIX}`;
const CLASS = `HVAC-${SUFFIX}`;

// A minimal, valid-looking PDF: the server decides file types from content.
const PDF = Buffer.from('%PDF-1.7\n1 0 obj\n<<>>\nendobj\ntrailer\n<<>>\n%%EOF');

let page: Page;
const cspViolations: string[] = [];

test.beforeAll(async ({ browser }) => {
  page = await (await browser.newContext({ acceptDownloads: true, viewport: { width: 1280, height: 900 } })).newPage();
  // The production web tier sends a strict content-security policy; any page that breaks it shows up here.
  page.on('console', (m) => m.text().includes('Content Security Policy') && cspViolations.push(m.text()));
  page.on('pageerror', (e) => cspViolations.push(`page error: ${e.message}`));
});

test.afterAll(async () => {
  await page.context().close();
});

async function shot(name: string) {
  fs.mkdirSync('e2e/screens', { recursive: true });
  await page.screenshot({ path: `e2e/screens/${name}.png`, fullPage: true });
}

async function expectNoSeriousA11yIssues(label: string) {
  const results = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa']).analyze();
  const serious = results.violations.filter((v) => v.impact === 'serious' || v.impact === 'critical');
  expect(serious.map((v) => `${label}: ${v.id} (${v.nodes.length}) ${v.help}`)).toEqual([]);
}

test('signing in through the identity provider lands on the app', async () => {
  await page.goto('/');
  await expect(page.getByRole('heading', { name: 'Facility Management' })).toBeVisible();
  await shot('00-signin');
  await page.getByRole('button', { name: /Sign in with fake/ }).click();
  await expect(page).toHaveURL(/\/assets$/);
  await expect(page.getByText('E2E Admin')).toBeVisible();
  expect(page.url()).not.toContain('code=');
  // The access token is in sessionStorage, not localStorage.
  const stores = await page.evaluate(() => ({ local: Object.keys(localStorage), session: Object.keys(sessionStorage) }));
  expect(stores.session.some((k) => k.startsWith('oidc.user:'))).toBe(true);
  expect(stores.local.some((k) => k.startsWith('oidc.'))).toBe(false);
});

test('an asset class and a custom field are set up', async () => {
  await page.getByRole('link', { name: 'Setup', exact: true }).click();
  await page.getByLabel('Asset number').fill(CLASS);
  await page.getByLabel('Name', { exact: true }).fill('HVAC');
  await page.getByLabel('Name (Arabic)').fill('التكييف');
  await page.getByRole('button', { name: 'Save' }).click();
  await expect(page.getByRole('cell', { name: CLASS })).toBeVisible();

  await page.getByRole('tab', { name: 'Custom fields' }).click();
  await page.getByLabel('Key').fill(`tonnage_${SUFFIX.toLowerCase()}`);
  await page.getByLabel('Label (English)').fill('Cooling capacity');
  await page.getByLabel('Label (Arabic)').fill('سعة التبريد');
  await page.getByLabel('Type').selectOption('Number');
  await page.getByRole('button', { name: 'Save' }).click();
  await expect(page.getByRole('cell', { name: `tonnage_${SUFFIX.toLowerCase()}` })).toBeVisible();
  await shot('01-setup');
});

test('a site, building and floor are created and shown as a tree', async () => {
  await page.getByRole('link', { name: 'Locations', exact: true }).click();
  await page.getByRole('button', { name: 'New site' }).click();
  await page.getByLabel('Code').fill(SITE);
  await page.getByLabel('Name', { exact: true }).fill('Riyadh HQ');
  await page.getByLabel('Name (Arabic)').fill('المقر الرئيسي');
  await page.getByLabel('Time zone').fill('Asia/Riyadh');
  await page.getByRole('button', { name: 'Save' }).click();
  await expect(page.getByRole('heading', { name: 'Riyadh HQ' })).toBeVisible();

  await page.getByRole('button', { name: 'Add Building' }).click();
  await page.getByLabel('Code').fill(BUILDING);
  await page.getByLabel('Name', { exact: true }).fill('Tower 1');
  await page.getByRole('button', { name: 'Save' }).click();
  await expect(page.getByRole('heading', { name: 'Tower 1' })).toBeVisible();

  await page.getByRole('button', { name: 'Add Floor' }).click();
  await page.getByLabel('Code').fill(FLOOR);
  await page.getByLabel('Name', { exact: true }).fill('Floor 3');
  await page.getByRole('button', { name: 'Save' }).click();
  await expect(page.getByRole('heading', { name: 'Floor 3' })).toBeVisible();

  const tree = page.getByRole('tree');
  await expect(tree.getByRole('button', { name: new RegExp(SITE) })).toBeVisible();
  await expect(tree.getByRole('button', { name: new RegExp(FLOOR) })).toBeVisible();
  await shot('02-locations');
});

test('server-side validation errors appear next to the fields', async () => {
  await page.getByRole('button', { name: new RegExp(SITE) }).first().click();
  await page.getByRole('button', { name: 'Add Building' }).click();
  await page.getByLabel('Code').fill('bad code');
  await page.getByLabel('Name', { exact: true }).fill('X');
  await page.getByRole('button', { name: 'Save' }).click();
  await expect(page.getByText(/Code is required: 1-50 letters/)).toBeVisible();
  await page.getByRole('button', { name: 'Cancel' }).click();
});

test('an asset is created with a class, a place and a custom field', async () => {
  await page.getByRole('link', { name: 'Assets', exact: true }).click();
  await page.getByRole('link', { name: 'New asset' }).click();
  await page.getByLabel('Asset number').fill(ASSET);
  await page.getByLabel('Name', { exact: true }).fill('Chiller 1');
  await page.getByLabel('Class').selectOption({ label: `${CLASS} · HVAC` });
  const floorOption = page.getByLabel('Location').locator('option', { hasText: 'Floor 3' }).first();
  await page.getByLabel('Location').selectOption(await floorOption.getAttribute('value') ?? '');
  await page.getByLabel('Criticality').selectOption('High');
  await page.getByLabel('Cooling capacity').fill('450');
  await page.getByLabel('Warranty expires').fill('2031-01-14');
  await page.getByLabel(/Start in service/).check();
  await page.getByRole('button', { name: 'Save' }).click();

  await expect(page.getByRole('heading', { name: new RegExp(ASSET) })).toBeVisible();
  await expect(page.getByText('Active', { exact: true }).first()).toBeVisible();
  await expect(page.getByText('450')).toBeVisible();
  await shot('03-asset-details');
});

test('meters take readings, enforce the rules and allow voiding a mistake', async () => {
  await page.getByRole('tab', { name: 'Meters' }).click();
  await page.getByRole('button', { name: 'Add meter' }).click();
  const dialog = page.getByRole('dialog');
  await dialog.getByLabel('Name', { exact: true }).fill('Energy');
  await dialog.getByLabel('Unit').fill('kWh');
  await dialog.getByRole('button', { name: 'Save' }).click();
  await expect(page.getByRole('button', { name: /Energy/ })).toBeVisible();

  const record = async (value: string) => {
    await page.getByLabel(/^Value/).fill(value);
    await page.getByRole('button', { name: 'Record reading' }).last().click();
  };
  await record('1000');
  await expect(page.getByRole('status').filter({ hasText: 'Reading recorded' })).toBeVisible();
  await expect(page.getByRole('cell', { name: /1,000 kWh/ })).toBeVisible();

  await record('900');
  await expect(page.getByText(/cannot go down/)).toBeVisible();

  await page.getByLabel(/Meter was replaced/).check();
  await record('900');
  await expect(page.getByRole('cell', { name: /900 kWh/ })).toBeVisible();

  await page.getByRole('row', { name: /900 kWh/ }).getByRole('button', { name: 'Void' }).click();
  await page.getByRole('dialog').getByLabel('Reason').fill('Typed the wrong meter');
  await page.getByRole('dialog').getByRole('button', { name: 'Void' }).click();
  await expect(page.getByRole('cell', { name: /900 kWh/ })).toHaveCount(0);
  await page.getByLabel('Show voided readings').check();
  await expect(page.getByText('Voided', { exact: true })).toBeVisible();
  await shot('04-meters');
});

test('files upload, download and are removed with a reason; bad files are refused', async () => {
  await page.getByRole('tab', { name: 'Files' }).click();
  await page.getByLabel('File', { exact: true }).setInputFiles({ name: 'دليل التشغيل.pdf', mimeType: 'application/pdf', buffer: PDF });
  await page.getByLabel('Description').fill('Operating manual');
  await page.getByRole('button', { name: 'Upload' }).click();
  await expect(page.getByRole('cell', { name: /دليل التشغيل\.pdf/ })).toBeVisible();

  const [download] = await Promise.all([page.waitForEvent('download'), page.getByRole('button', { name: 'Download' }).click()]);
  expect(download.suggestedFilename()).toBe('دليل التشغيل.pdf');
  expect(fs.readFileSync(await download.path())).toEqual(PDF);

  // An executable renamed to .pdf is refused by the server, which looks at the content.
  await page.getByLabel('File', { exact: true }).setInputFiles({ name: 'setup.pdf', mimeType: 'application/pdf', buffer: Buffer.from([0x4d, 0x5a, 0x90, 0, 3, 0, 0, 0]) });
  await page.getByRole('button', { name: 'Upload' }).click();
  await expect(page.getByText(/file type is not allowed/i)).toBeVisible();

  await page.getByRole('button', { name: 'Delete' }).click();
  await page.getByRole('dialog').getByLabel('Reason').fill('Superseded');
  await page.getByRole('dialog').getByRole('button', { name: 'Delete' }).click();
  await expect(page.getByRole('cell', { name: /دليل التشغيل\.pdf/ })).toHaveCount(0);
});

test('the label shows a QR code that can be printed', async () => {
  await page.getByRole('tab', { name: 'Label' }).click();
  const qr = page.getByRole('img', { name: new RegExp(`QR code for asset ${ASSET}`) });
  await expect(qr).toBeVisible();
  await expect.poll(() => qr.evaluate((img: HTMLImageElement) => img.naturalWidth)).toBeGreaterThan(0);
  await expect(page.getByRole('button', { name: 'Print label' })).toBeVisible();
  await shot('05-label');
});

test('scanning the label address opens the asset, and an unknown label says so', async () => {
  await page.getByRole('tab', { name: 'Label' }).click();
  const tag = (await page.locator('.label-text code').innerText()).trim();
  await page.goto(`/a/${tag.toLowerCase().replace('-', ' ')}`);
  await expect(page.getByRole('heading', { name: new RegExp(ASSET) })).toBeVisible();
  await expect(page).toHaveURL(/\/assets\/[0-9a-f-]{36}$/);
  await page.goto('/a/ZZZZZ-ZZZZZ');
  await expect(page.getByText(/No asset matches this label/)).toBeVisible();
  await page.goBack();
  await page.goBack();
});

test('status changes need a reason when taking an asset out of service', async () => {
  await page.getByRole('button', { name: 'Change status' }).click();
  const dialog = page.getByRole('dialog');
  await dialog.getByLabel('New status').selectOption('OutOfService');
  await expect(dialog.getByRole('button', { name: 'Confirm' })).toBeDisabled();
  await dialog.getByLabel('Reason').fill('Compressor failure');
  await dialog.getByRole('button', { name: 'Confirm' }).click();
  await expect(page.getByText('Out of service', { exact: true }).first()).toBeVisible();
  await expect(page.getByText('Reason: Compressor failure')).toBeVisible();
});

test('the asset list filters, pages and exports', async () => {
  await page.getByRole('link', { name: 'Assets', exact: true }).first().click();
  await page.getByLabel('Search').fill(ASSET);
  await expect(page.getByRole('link', { name: ASSET })).toBeVisible();
  await page.getByLabel('Status').selectOption('Active');
  await expect(page.getByText('No assets match.')).toBeVisible();
  await page.getByLabel('Status').selectOption('');
  const [download] = await Promise.all([page.waitForEvent('download'), page.getByRole('button', { name: 'Export CSV' }).click()]);
  const csv = fs.readFileSync(await download.path(), 'utf8');
  expect(csv).toContain(ASSET);
  expect(csv.startsWith('﻿code,name')).toBe(true);
  await shot('06-assets');
});

test('bulk import checks first, reports problems by line, and imports only a valid file', async () => {
  await page.getByRole('link', { name: 'Import', exact: true }).click();
  await page.getByLabel('Locations').check();

  const bad = `type,code,parentCode,name\nBuilding,N-${SUFFIX},NOPE-${SUFFIX},Orphan\nSite,bad code,,Broken\n`;
  await page.getByLabel('CSV file').setInputFiles({ name: 'bad.csv', mimeType: 'text/csv', buffer: Buffer.from(bad) });
  await page.getByRole('button', { name: 'Check file' }).click();
  await expect(page.getByText(/2 rows have problems/)).toBeVisible();
  await expect(page.getByRole('cell', { name: /no active location with code/ })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Import', exact: true })).toBeDisabled();
  await shot('07-import-errors');

  const good = `type,code,parentCode,name,nameAr\nBuilding,I1-${SUFFIX},${SITE},Imported tower,برج مستورد\nFloor,I2-${SUFFIX},I1-${SUFFIX},Imported floor,\n`;
  await page.getByLabel('CSV file').setInputFiles({ name: 'good.csv', mimeType: 'text/csv', buffer: Buffer.from(good) });
  await expect(page.getByRole('button', { name: 'Import', exact: true })).toBeDisabled(); // not checked yet
  await page.getByRole('button', { name: 'Check file' }).click();
  await expect(page.getByText(/The file is valid. 2 rows will be created/)).toBeVisible();
  await page.getByRole('button', { name: 'Import', exact: true }).click();
  await expect(page.getByText('Imported 2 rows.')).toBeVisible();

  await page.getByRole('link', { name: 'Locations', exact: true }).click();
  await page.getByLabel('Search').fill(`I2-${SUFFIX}`);
  await expect(page.getByRole('button', { name: new RegExp(`I2-${SUFFIX}`) })).toBeVisible();
});

test('Arabic switches the page to right-to-left and the Hijri calendar changes dates', async () => {
  await page.getByRole('link', { name: 'Assets', exact: true }).first().click();
  await page.getByLabel('Search').fill(ASSET);
  const warrantyEn = await page.getByRole('row', { name: new RegExp(ASSET) }).innerText();

  await page.getByLabel('Language').selectOption('ar');
  await expect(page.locator('html')).toHaveAttribute('dir', 'rtl');
  await expect(page.locator('html')).toHaveAttribute('lang', 'ar');
  await expect(page.getByRole('heading', { name: 'الأصول' })).toBeVisible();
  await expect(page.getByRole('link', { name: 'المواقع' })).toBeVisible();
  await shot('08-assets-rtl');

  await page.getByLabel('التقويم').selectOption('hijri');
  const warrantyHijri = await page.getByRole('row', { name: new RegExp(ASSET) }).innerText();
  expect(warrantyHijri).not.toBe(warrantyEn);
  await page.getByLabel('التقويم').selectOption('gregorian');

  await page.getByRole('link', { name: ASSET }).click();
  await expect(page.getByRole('tab', { name: 'الملصق' })).toBeVisible();
  await shot('09-asset-rtl');
  await page.getByLabel('اللغة').selectOption('en');
  await expect(page.locator('html')).toHaveAttribute('dir', 'ltr');
});

test('main screens have no serious accessibility problems in English or Arabic', async () => {
  const visit = async (path: string, ready: () => Promise<void>) => {
    await page.goto(path);
    await ready();
  };
  for (const lang of ['en', 'ar']) {
    await page.locator('.prefs select').first().selectOption(lang); // the language picker is the first control, whatever the current language
    await visit('/assets', () => expect(page.getByRole('table')).toBeVisible());
    await expectNoSeriousA11yIssues(`assets ${lang}`);
    await visit('/locations', () => expect(page.getByRole('tree')).toBeVisible());
    await expectNoSeriousA11yIssues(`locations ${lang}`);
    await visit('/import', () => expect(page.getByLabel(lang === 'en' ? 'CSV file' : 'ملف CSV')).toBeVisible());
    await expectNoSeriousA11yIssues(`import ${lang}`);
    await visit('/setup', () => expect(page.getByRole('tablist')).toBeVisible());
    await expectNoSeriousA11yIssues(`setup ${lang}`);
  }

  await page.locator('.prefs select').first().selectOption('en');
});

async function api(path: string, init: RequestInit & { sub?: string } = {}) {
  const { token } = (await (await fetch(`${IDP}/__token?sub=${init.sub ?? 'e2e-admin'}`)).json()) as { token: string };
  return fetch(`http://localhost:5080/api/v1${path}`, { ...init, headers: { ...init.headers, Authorization: `Bearer ${token}`, 'Content-Type': 'application/json' } });
}

async function personaPage(browser: Browser, sub: string, name: string): Promise<Page> {
  await fetch(`${IDP}/__user`, { method: 'POST', body: JSON.stringify({ sub, name, email: `${sub}@example.test` }) });
  const ctx = await browser.newContext({ acceptDownloads: true });
  const p = await ctx.newPage();
  await p.goto('/');
  await p.getByRole('button', { name: /Sign in with fake/ }).click();
  return p;
}

test('a person with no role sees a clear message, then only what their role allows', async ({ browser }) => {
  const tech = await personaPage(browser, `tech-${SUFFIX.toLowerCase()}`, 'Tina Technician');
  await expect(tech.getByRole('heading', { name: 'No access' })).toBeVisible();

  // The administrator gives them the Technician role on the Users screen.
  const email = `tech-${SUFFIX.toLowerCase()}@example.test`;
  await page.goto('/users');
  const row = page.getByRole('row').filter({ hasText: email });
  await expect(row).toBeVisible();
  await row.getByRole('button', { name: 'Edit roles' }).click();
  const dialog = page.getByRole('dialog', { name: 'Edit roles' });
  await dialog.getByLabel(/^Technician/).check();
  await dialog.getByRole('button', { name: 'Save' }).click();
  await expect(dialog).toHaveCount(0);
  await expect(row).toContainText('Technician');

  await tech.reload();
  await expect(tech.getByRole('link', { name: 'Assets', exact: true }).first()).toBeVisible();
  await expect(tech.getByRole('link', { name: 'Import', exact: true })).toHaveCount(0);
  await expect(tech.getByRole('link', { name: 'Setup', exact: true })).toHaveCount(0);
  await expect(tech.getByRole('link', { name: 'New asset' })).toHaveCount(0);

  await tech.getByLabel('Search').fill(ASSET);
  await tech.getByRole('link', { name: ASSET }).click();
  await tech.getByRole('tab', { name: 'Meters' }).click();
  await expect(tech.getByRole('button', { name: 'Record reading' }).last()).toBeVisible();
  await expect(tech.getByRole('button', { name: 'Add meter' })).toHaveCount(0);
  await expect(tech.getByRole('button', { name: 'Edit' })).toHaveCount(0);
  await expect(tech.getByRole('button', { name: 'Change status' })).toHaveCount(0);

  // Typing the address of a page they cannot use gives them the empty state, not data.
  await tech.goto('/import');
  await expect(tech.getByRole('heading', { name: 'Import' })).toHaveCount(0);

  // Deactivating them locks them out at once; reactivating restores what their role allows.
  const toggle = async (button: string) => {
    await row.getByRole('button', { name: button }).click();
    await page.getByRole('dialog').getByRole('button', { name: 'Confirm' }).click();
    await expect(page.getByRole('dialog')).toHaveCount(0);
  };
  await toggle('Deactivate');
  await expect(row).toContainText('Deactivated');
  await tech.goto('/assets');
  await expect(tech.getByRole('heading', { name: 'Your account has been deactivated' })).toBeVisible();
  await toggle('Reactivate');
  await tech.goto('/assets');
  await expect(tech.getByRole('link', { name: 'Assets', exact: true }).first()).toBeVisible();

  await tech.context().close();
  await fetch(`${IDP}/__user`, { method: 'POST', body: JSON.stringify({ sub: 'e2e-admin', name: 'E2E Admin', email: 'admin@example.test' }) });
});

async function giveRole(sub: string, roleName: string) {
  const users = (await (await api('/users')).json()) as { id: string; email: string }[];
  const user = users.find((u) => u.email === `${sub}@example.test`)!;
  const roles = (await (await api('/roles')).json()) as { id: string; name: string }[];
  const response = await api(`/users/${user.id}/roles`, { method: 'PUT', body: JSON.stringify({ roleIds: [roles.find((r) => r.name === roleName)!.id] }) });
  expect(response.status).toBe(204);
}

test('helpdesk: a requester scans a label, the request is approved, assigned, done, verified and closed by different people', async ({ browser }) => {
  const asset = ((await (await api(`/assets?q=${ASSET}`)).json()) as { items: { tagCode: string }[] }).items[0];

  // 1. A person who may only raise requests scans the label and lands on the report form with the asset already found.
  const requester = await personaPage(browser, `req-${SUFFIX.toLowerCase()}`, 'Rami Requester');
  await expect(requester.getByRole('heading', { name: 'No access' })).toBeVisible();
  await giveRole(`req-${SUFFIX.toLowerCase()}`, 'Requester');
  await requester.goto(`/a/${asset.tagCode}`);
  await expect(requester.getByRole('heading', { name: 'Report a problem' })).toBeVisible();
  await expect(requester.getByText(ASSET).first()).toBeVisible();
  await requester.getByLabel('Title').fill('Compressor makes a grinding noise');
  await requester.getByLabel('Details').fill('Started this morning.');
  await requester.getByRole('button', { name: 'Submit request' }).click();
  await expect(requester.getByRole('heading', { name: /WO-\d+ · Compressor makes a grinding noise/ })).toBeVisible();
  await expect(requester.getByText('Requested', { exact: true }).first()).toBeVisible();
  await expect(requester.getByRole('button', { name: 'Approve' })).toHaveCount(0); // not theirs to approve
  await expect(requester.getByRole('button', { name: 'Cancel request' })).toBeVisible();
  const url = requester.url();

  // 2. The administrator approves it and assigns the technician.
  await page.goto('/work-orders?view=all');
  await page.getByRole('link', { name: /WO-\d+/ }).first().click();
  await expect(page.getByRole('heading', { name: /Compressor makes a grinding noise/ })).toBeVisible();
  await page.getByRole('button', { name: 'Approve', exact: true }).click();
  await page.getByRole('dialog').getByRole('button', { name: 'Confirm' }).click();
  await expect(page.getByText('Approved', { exact: true }).first()).toBeVisible();
  await page.getByRole('button', { name: 'Assign', exact: true }).click();
  await page.getByRole('dialog').getByLabel('Assigned to').selectOption({ label: 'Tina Technician' });
  await page.getByRole('dialog').getByRole('button', { name: 'Confirm' }).click();
  await expect(page.getByText('Assigned', { exact: true }).first()).toBeVisible();
  await shot('20-work-order-assigned');

  // 3. The technician starts and completes it.
  const tech = await personaPage(browser, `tech-${SUFFIX.toLowerCase()}`, 'Tina Technician');
  await expect(tech.getByRole('link', { name: 'Work orders', exact: true })).toBeVisible(); // signed in
  await tech.goto('/work-orders?view=assigned');
  await tech.getByRole('link', { name: /WO-\d+/ }).first().click();
  await expect(tech.getByRole('button', { name: 'Approve' })).toHaveCount(0);
  await tech.getByRole('button', { name: 'Start work' }).click();
  await tech.getByRole('dialog').getByRole('button', { name: 'Confirm' }).click();
  await expect(tech.getByText('In progress', { exact: true }).first()).toBeVisible();
  await tech.getByRole('button', { name: 'Complete', exact: true }).click();
  await tech.getByRole('dialog').getByLabel('What was done').fill('Replaced the bearing.');
  await tech.getByRole('dialog').getByRole('button', { name: 'Confirm' }).click();
  await expect(tech.getByText('Completed', { exact: true }).first()).toBeVisible();
  await expect(tech.getByRole('button', { name: 'Verify' })).toHaveCount(0); // cannot check their own work
  await tech.context().close();

  // 4. Someone else verifies and closes it.
  await page.reload();
  await page.getByRole('button', { name: 'Verify', exact: true }).click();
  await page.getByRole('dialog').getByRole('button', { name: 'Confirm' }).click();
  await expect(page.getByText('Verified', { exact: true }).first()).toBeVisible();
  await page.getByRole('button', { name: 'Close', exact: true }).click();
  await page.getByRole('dialog').getByRole('button', { name: 'Confirm' }).click();
  await expect(page.getByText('Closed', { exact: true }).first()).toBeVisible();
  await expect(page.getByRole('button', { name: 'Edit' })).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'Reopen' })).toBeVisible();

  // 5. The requester sees the outcome and the history, and a comment from staff, but not the internal note.
  await page.getByLabel('Add a comment').fill('Internal: bearing was worn, check the others.');
  await page.getByLabel(/Internal note/).check();
  await page.getByRole('button', { name: 'Post comment' }).click();
  await expect(page.locator('.comments').getByText('Internal: bearing was worn')).toBeVisible();
  await page.getByLabel('Add a comment').fill('All fixed, thank you for reporting.');
  await page.getByRole('button', { name: 'Post comment' }).click();
  await expect(page.getByText('All fixed, thank you for reporting.')).toBeVisible();
  await shot('21-work-order-closed');

  await requester.goto(url);
  await expect(requester.getByText('Closed', { exact: true }).first()).toBeVisible();
  await expect(requester.locator('dl').getByText('Replaced the bearing.')).toBeVisible();
  await expect(requester.getByText('All fixed, thank you for reporting.')).toBeVisible();
  await expect(requester.getByText('Internal: bearing was worn')).toHaveCount(0);
  await expect(requester.getByRole('list', { name: 'History' }).getByRole('listitem')).toHaveCount(7); // requested, approved, assigned, started, completed, verified, closed
  await requester.context().close();
  await fetch(`${IDP}/__user`, { method: 'POST', body: JSON.stringify({ sub: 'e2e-admin', name: 'E2E Admin', email: 'admin@example.test' }) });
});

test('work order screens have no serious accessibility problems in English or Arabic', async () => {
  for (const lang of ['en', 'ar']) {
    await page.goto('/work-orders?view=all');
    await page.locator('.prefs select').first().selectOption(lang);
    await expect(page.getByRole('table')).toBeVisible();
    await expectNoSeriousA11yIssues(`work orders ${lang}`);
    await page.getByRole('link', { name: /WO-\d+/ }).first().click();
    await expect(page.getByRole('list', { name: lang === 'en' ? 'History' : 'السجل' })).toBeVisible();
    await expectNoSeriousA11yIssues(`work order detail ${lang}`);
    await page.goto('/work-orders/new');
    await expect(page.getByRole('form').or(page.locator('form')).first()).toBeVisible();
    await expectNoSeriousA11yIssues(`new request ${lang}`);
    await page.goto('/setup?section=work');
    await expect(page.getByRole('tabpanel')).toBeVisible();
    await expectNoSeriousA11yIssues(`work setup ${lang}`);
  }

  await page.locator('.prefs select').first().selectOption('en');
});

test('a session outlives its access token by renewing silently with the refresh token', async ({ browser }) => {
  // Tokens that last 75 s are renewed about a minute before they expire, so within ~15 s the app must have swapped in a new one.
  await fetch(`${IDP}/__ttl?seconds=75`);
  const who = await personaPage(browser, `renew-${SUFFIX.toLowerCase()}`, 'Rita Renew');
  try {
    await expect(who.getByRole('heading', { name: 'No access' })).toBeVisible();
    const token = () => who.evaluate(() => {
      const key = Object.keys(sessionStorage).find((k) => k.startsWith('oidc.user'))!;
      return (JSON.parse(sessionStorage.getItem(key)!) as { access_token: string }).access_token;
    });
    const first = await token();
    await expect.poll(token, { timeout: 40_000, intervals: [1000] }).not.toBe(first);
    const stats = (await (await fetch(`${IDP}/__stats`)).json()) as { refreshes: number };
    expect(stats.refreshes).toBeGreaterThanOrEqual(1);
    // Still signed in, and the API accepts the renewed token.
    await who.reload();
    await expect(who.getByRole('heading', { name: 'No access' })).toBeVisible();
  } finally {
    await fetch(`${IDP}/__ttl?seconds=3600`);
    await who.context().close();
    await fetch(`${IDP}/__user`, { method: 'POST', body: JSON.stringify({ sub: 'e2e-admin', name: 'E2E Admin', email: 'admin@example.test' }) });
  }
});

test('the strict content-security policy was never violated and the security headers are served', async () => {
  expect(cspViolations).toEqual([]);
  const headers = (await page.request.get('/')).headers();
  expect(headers['x-content-type-options']).toBe('nosniff');
  expect(headers['x-frame-options']).toBe('DENY');
  expect(headers['content-security-policy']).toContain("script-src 'self'");
  // A deep link reloads into the app (the web server falls back to index.html for app routes).
  const response = await page.goto('/assets');
  expect(response?.status()).toBe(200);
});

test('signing out returns to the sign-in page', async () => {
  await page.getByRole('button', { name: 'Sign out' }).click();
  await expect(page.getByRole('heading', { name: 'Facility Management' })).toBeVisible();
  await expect(page.getByRole('button', { name: /Sign in with fake/ })).toBeVisible();
  const session = await page.evaluate(() => Object.keys(sessionStorage).filter((k) => k.startsWith('oidc.user')));
  expect(session).toEqual([]);
});
