import AxeBuilder from '@axe-core/playwright';
import { expect, test, type Browser, type Locator, type Page } from '@playwright/test';
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

async function expectNoSeriousA11yIssues(label: string, target: Page = page) {
  const results = await new AxeBuilder({ page: target }).withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa']).analyze();
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

/** Picks the option of a select whose text contains the given text (the places are listed by code and name). */
async function choose(select: Locator, text: string) {
  const value = await select.locator('option', { hasText: text }).first().getAttribute('value');
  await select.selectOption(value as string);
}

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

  await expect(tech.getByRole('heading', { name: 'My jobs' })).toBeVisible(); // field staff start at their jobs
  await tech.getByRole('link', { name: 'Assets', exact: true }).first().click();
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

test('service levels: an administrator sets a working calendar and a policy', async () => {
  await page.goto('/setup?section=sla');
  await expect(page.getByRole('heading', { name: 'Working calendars' })).toBeVisible();

  await page.getByRole('button', { name: 'New calendar' }).click();
  const calendar = page.getByRole('dialog', { name: 'New calendar' });
  await calendar.getByRole('textbox', { name: 'Name *' }).fill('Saudi week');
  await calendar.getByRole('button', { name: /Sunday to Thursday/ }).click();
  await calendar.getByLabel('Date').fill('2026-09-23');
  await calendar.getByRole('textbox', { name: 'Name', exact: true }).fill('National Day');
  await calendar.getByRole('button', { name: 'Add', exact: true }).click();
  await expect(calendar.getByText('National Day')).toBeVisible();
  await calendar.getByLabel(/Use for sites that have no calendar/).check();
  await calendar.getByRole('button', { name: 'Save' }).click();
  await expect(page.getByRole('dialog')).toHaveCount(0);
  await expect(page.getByText('Saudi week')).toBeVisible();

  await page.getByRole('button', { name: 'New policy' }).click();
  const policy = page.getByRole('dialog', { name: 'New policy' });
  await policy.getByRole('textbox', { name: 'Name *' }).fill('Standard');
  await policy.getByRole('button', { name: 'Save' }).click();
  await expect(page.getByRole('dialog')).toHaveCount(0);
  await expect(page.getByRole('table', { name: 'Standard' })).toBeVisible();
  await shot('19-service-levels');

  // A bad policy is explained, not silently accepted.
  await page.getByRole('button', { name: 'New policy' }).click();
  const bad = page.getByRole('dialog', { name: 'New policy' });
  await bad.getByRole('textbox', { name: 'Name *' }).fill('Backwards');
  await bad.getByLabel('Resolve (minutes)').first().fill('1');
  await bad.getByLabel('Respond (minutes)').first().fill('500');
  await bad.getByRole('button', { name: 'Save' }).click();
  await expect(bad.getByText(/resolution cannot be sooner than response/)).toBeVisible();
  await bad.getByRole('button', { name: 'Cancel' }).click();
});

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

  // The administrator (an approver) is told, in the app, by the worker.
  await expect(async () => {
    await page.goto('/notifications');
    await expect(page.getByRole('list', { name: 'Notifications' }).getByText(/WO-\d+ was requested/)).toBeVisible({ timeout: 1500 });
  }).toPass({ timeout: 20_000 });
  await expect(page.getByRole('link', { name: /Notifications\s*\d+ unread/ })).toBeVisible();
  await shot('22-notifications');

  // 2. The administrator approves it and assigns the technician.
  await page.goto('/work-orders?view=all');
  await page.getByRole('link', { name: /WO-\d+/ }).first().click();
  await expect(page.getByRole('heading', { name: /Compressor makes a grinding noise/ })).toBeVisible();
  await page.getByRole('button', { name: 'Approve', exact: true }).click();
  await page.getByRole('dialog').getByRole('button', { name: 'Confirm' }).click();
  await expect(page.getByText('Approved', { exact: true }).first()).toBeVisible();
  await expect(page.getByRole('group', { name: 'Service level' })).toContainText('On track'); // approval started the clocks
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

  // The requester was told as it progressed, and opening a notice goes to the request.
  await expect(async () => {
    await requester.goto('/notifications');
    await expect(requester.getByRole('list', { name: 'Notifications' }).getByText(/was approved/)).toBeVisible({ timeout: 1500 });
    await expect(requester.getByRole('list', { name: 'Notifications' }).getByText(/was closed/)).toBeVisible({ timeout: 1500 });
  }).toPass({ timeout: 20_000 });
  await requester.getByRole('list', { name: 'Notifications' }).getByRole('button', { name: /was closed/ }).click();
  await expect(requester.getByRole('heading', { name: /Compressor makes a grinding noise/ })).toBeVisible();
  await requester.goto(url);
  await expect(requester.getByText('Closed', { exact: true }).first()).toBeVisible();
  await expect(requester.locator('dl').getByText('Replaced the bearing.')).toBeVisible();
  await expect(requester.getByText('All fixed, thank you for reporting.')).toBeVisible();
  await expect(requester.getByText('Internal: bearing was worn')).toHaveCount(0);
  await expect(requester.getByRole('list', { name: 'History' }).getByRole('listitem')).toHaveCount(7); // requested, approved, assigned, started, completed, verified, closed
  await requester.context().close();
  await fetch(`${IDP}/__user`, { method: 'POST', body: JSON.stringify({ sub: 'e2e-admin', name: 'E2E Admin', email: 'admin@example.test' }) });
});

test('field work: checklist, time, costs, photo and signature, with completion blocked until the checklist is answered', async ({ browser }) => {
  const PNG = Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==', 'base64');

  // A dispatcher prepares: a checklist template, an hourly rate, and an emergency job assigned to the technician.
  await page.goto('/setup?section=checklists');
  await page.getByRole('button', { name: 'New checklist' }).click();
  const template = page.getByRole('dialog', { name: 'New checklist' });
  await template.getByRole('textbox', { name: 'Name *' }).fill('Pump inspection');
  await template.getByRole('textbox', { name: 'Line 1' }).fill('Isolate power');
  await template.getByRole('button', { name: 'Add a line' }).click();
  await template.getByRole('textbox', { name: 'Line 2' }).fill('Check seals');
  await template.getByRole('button', { name: 'Save' }).click();
  await expect(page.getByText('Pump inspection')).toBeVisible();

  const settings = (await (await api('/work-orders/settings')).json()) as { rowVersion: string | null };
  expect((await api('/work-orders/settings', { method: 'PUT', body: JSON.stringify({ requireApproval: true, requireVerification: true, labourRatePerHour: 80, rowVersion: settings.rowVersion }) })).status).toBe(200);
  const site = ((await (await api(`/locations?q=${SITE}`)).json()) as { items: { id: string }[] }).items[0];
  const users = (await (await api('/users')).json()) as { id: string; email: string }[];
  const techId = users.find((u) => u.email === `tech-${SUFFIX.toLowerCase()}@example.test`)!.id;
  const made = await api('/work-orders', { method: 'POST', body: JSON.stringify({ title: 'Pump seized', type: 'Emergency', priority: 'Urgent', locationId: site.id }) });
  expect(made.status).toBe(201);
  const wo = (await made.json()) as { id: string };
  expect((await api(`/work-orders/${wo.id}/assign`, { method: 'POST', body: JSON.stringify({ assigneeId: techId }) })).status).toBe(200);

  await page.goto(`/work-orders/${wo.id}?tab=checklist`);
  await page.getByLabel('Add a checklist').selectOption({ label: 'Pump inspection' });
  await page.getByRole('button', { name: 'Add', exact: true }).click();
  await expect(page.getByRole('list', { name: 'Checklist' }).getByRole('listitem')).toHaveCount(2);

  // The technician starts; completing is refused until the required lines are answered.
  const tech = await personaPage(browser, `tech-${SUFFIX.toLowerCase()}`, 'Tina Technician');
  await expect(tech.getByRole('link', { name: 'Work orders', exact: true })).toBeVisible();
  await tech.goto(`/work-orders/${wo.id}`);
  await tech.getByRole('button', { name: 'Start work' }).click();
  await tech.getByRole('dialog').getByRole('button', { name: 'Confirm' }).click();
  await expect(tech.getByText('In progress', { exact: true }).first()).toBeVisible();

  await tech.getByRole('button', { name: 'Complete', exact: true }).click();
  await tech.getByRole('dialog').getByLabel('What was done').fill('Replaced the seal');
  await tech.getByRole('dialog').getByRole('button', { name: 'Confirm' }).click();
  await expect(tech.getByRole('dialog').getByText(/1 required checklist item/)).toBeVisible();
  await tech.getByRole('dialog').getByRole('button', { name: 'Cancel' }).click();

  await tech.getByRole('tab', { name: 'Checklist' }).click();
  await tech.getByLabel('Result: Isolate power').selectOption('Done');
  await expect(tech.getByRole('listitem').filter({ hasText: 'Isolate power' }).getByText('Done', { exact: true }).first()).toBeVisible();
  await tech.getByLabel('Result: Check seals').selectOption('Done');
  await expect(tech.getByRole('listitem').filter({ hasText: 'Check seals' }).getByText('Done', { exact: true }).first()).toBeVisible();

  // Time and costs.
  await tech.getByRole('tab', { name: 'Time and costs' }).click();
  await tech.getByLabel('Minutes').fill('45');
  await tech.getByRole('button', { name: 'Record time' }).click();
  await expect(tech.getByRole('cell', { name: '45', exact: true })).toBeVisible();
  await tech.getByLabel('Description').fill('Mechanical seal');
  await tech.getByLabel('Reference').fill('PO-77');
  await tech.getByLabel('Quantity').fill('2');
  await tech.getByLabel('Unit cost').fill('25.5');
  await tech.getByRole('button', { name: 'Add cost' }).click();
  await expect(tech.getByRole('cell', { name: 'Mechanical seal' })).toBeVisible();
  await expect(tech.getByRole('definition').filter({ hasText: /111/ }).first()).toBeVisible(); // 45 min at 80 = 60, plus 2 x 25.5 = 51

  // A photo.
  await tech.getByRole('tab', { name: 'Photos and files' }).click();
  await tech.locator('input[type=file]').setInputFiles({ name: 'seal.png', mimeType: 'image/png', buffer: PNG });
  await tech.getByRole('button', { name: 'Add a photo or file' }).click();
  await expect(tech.getByRole('list', { name: 'Photos and files' }).getByText('seal.png')).toBeVisible();
  await expect(tech.getByRole('img', { name: 'seal.png' })).toBeVisible();

  // A signature drawn on the pad.
  await tech.getByRole('tab', { name: 'Signatures' }).click();
  await tech.getByLabel('Printed name').fill('Tina Technician');
  const pad = tech.getByRole('img', { name: /Signature area/ });
  const box = (await pad.boundingBox())!;
  await tech.mouse.move(box.x + 20, box.y + 20);
  await tech.mouse.down();
  await tech.mouse.move(box.x + 120, box.y + 60, { steps: 6 });
  await tech.mouse.move(box.x + 200, box.y + 30, { steps: 6 });
  await tech.mouse.up();
  await tech.getByRole('button', { name: 'Save signature' }).click();
  await expect(tech.getByRole('list', { name: 'Signatures' }).getByRole('listitem')).toHaveCount(1);

  // Now it completes.
  await tech.getByRole('tab', { name: 'Overview' }).click();
  await tech.getByRole('button', { name: 'Complete', exact: true }).click();
  await tech.getByRole('dialog').getByLabel('What was done').fill('Replaced the seal');
  await tech.getByRole('dialog').getByRole('button', { name: 'Confirm' }).click();
  await expect(tech.getByText('Completed', { exact: true }).first()).toBeVisible();
  await tech.context().close();

  // The administrator sees it all, and the screens are accessible in both languages.
  for (const lang of ['en', 'ar']) {
    await page.goto(`/work-orders/${wo.id}`);
    await page.locator('.prefs select').first().selectOption(lang);
    for (const tab of ['checklist', 'costs', 'files', 'signatures']) {
      await page.goto(`/work-orders/${wo.id}?tab=${tab}`);
      await expect(page.getByRole('tabpanel')).toBeVisible();
      await page.waitForLoadState('networkidle');
      await expectNoSeriousA11yIssues(`work order ${tab} ${lang}`);
    }
    await page.goto('/setup?section=checklists');
    await expect(page.getByRole('tabpanel')).toBeVisible();
    await expectNoSeriousA11yIssues(`checklist setup ${lang}`);
  }

  await page.locator('.prefs select').first().selectOption('en');
  await shot('23-field-work');
  await fetch(`${IDP}/__user`, { method: 'POST', body: JSON.stringify({ sub: 'e2e-admin', name: 'E2E Admin', email: 'admin@example.test' }) });
});

test('offline: a technician works a whole job without a connection, and it all arrives once, with the times it really happened', async ({ browser }) => {
  test.setTimeout(150_000);
  const PNG = Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==', 'base64');
  const site = ((await (await api(`/locations?q=${SITE}`)).json()) as { items: { id: string }[] }).items[0];
  const users = (await (await api('/users')).json()) as { id: string; email: string }[];
  const techId = users.find((u) => u.email === `tech-${SUFFIX.toLowerCase()}@example.test`)!.id;
  const templates = (await (await api('/work-orders/checklists')).json()) as { id: string; name: string }[];

  const made = await api('/work-orders', { method: 'POST', body: JSON.stringify({ title: 'Boiler tripping', type: 'Emergency', priority: 'High', locationId: site.id }) });
  const wo = (await made.json()) as { id: string };
  expect((await api(`/work-orders/${wo.id}/assign`, { method: 'POST', body: JSON.stringify({ assigneeId: techId }) })).status).toBe(200);
  expect((await api(`/work-orders/${wo.id}/checklist`, { method: 'POST', body: JSON.stringify({ templateId: templates.find((t) => t.name === 'Pump inspection')!.id }) })).status).toBe(200);

  // Signed in and online: the jobs are downloaded to the phone, and the app is saved by the service worker.
  const tech = await personaPage(browser, `tech-${SUFFIX.toLowerCase()}`, 'Tina Technician');
  await expect(tech.getByRole('heading', { name: 'My jobs' })).toBeVisible();
  await expect(tech.getByRole('list', { name: 'My jobs' }).getByText('Boiler tripping')).toBeVisible();
  await tech.evaluate(async () => { await navigator.serviceWorker.ready; });
  await tech.waitForFunction(async () => (await (await caches.open('fms-shell-v1')).keys()).length > 3);

  // The connection goes away. Even a reload works, and the job is still there.
  await tech.context().setOffline(true);
  await tech.reload();
  await expect(tech.getByRole('heading', { name: 'My jobs' })).toBeVisible();
  await expect(tech.getByRole('status')).toContainText('Offline');
  await tech.getByRole('link', { name: /Boiler tripping/ }).click();

  const startedAt = Date.now();
  await tech.getByRole('button', { name: 'Start work' }).click();
  await expect(tech.getByText('In progress', { exact: true }).first()).toBeVisible();
  await expect(tech.getByRole('status')).toContainText('waiting to be sent');
  await expectNoSeriousA11yIssues('job screen en', tech);
  await tech.locator('.prefs select').first().selectOption('ar');
  await expect(tech.locator('html')).toHaveAttribute('dir', 'rtl');
  await expectNoSeriousA11yIssues('job screen ar', tech);
  await tech.goto('/jobs');
  await expectNoSeriousA11yIssues('jobs list ar', tech);
  await tech.locator('.prefs select').first().selectOption('en');
  await tech.getByRole('link', { name: /Boiler tripping/ }).click();

  // Completing is blocked on the phone for the same reason as on the server.
  await tech.getByRole('button', { name: 'Complete', exact: true }).click();
  await expect(tech.getByRole('dialog').getByRole('alert')).toContainText('required checklist');
  await tech.getByRole('dialog').getByRole('button', { name: 'Cancel' }).click();

  await tech.getByLabel('Result: Isolate power').selectOption('Done');
  await expect(tech.getByRole('listitem').filter({ hasText: 'Isolate power' }).getByText('Waiting to send')).toBeVisible();
  await tech.getByLabel('Result: Check seals').selectOption('Done');
  await tech.getByLabel('Minutes').fill('30');
  await tech.getByRole('button', { name: 'Record time' }).click();
  await tech.getByLabel('Description').first().fill('Thermostat');
  await tech.getByLabel('Unit cost').fill('40');
  await tech.getByRole('button', { name: 'Add cost' }).click();
  await tech.getByLabel('Add a comment').fill('Tripped on a faulty thermostat');
  await tech.getByRole('button', { name: 'Post comment' }).click();
  await tech.locator('input[type=file]').setInputFiles({ name: 'boiler.png', mimeType: 'image/png', buffer: PNG });
  await expect(tech.getByRole('list', { name: 'Photos and files' }).getByRole('listitem')).toHaveCount(1);

  await tech.getByLabel('Printed name').fill('Tina Technician');
  const pad = tech.getByRole('img', { name: /Signature area/ });
  const box = (await pad.boundingBox())!;
  await tech.mouse.move(box.x + 20, box.y + 20);
  await tech.mouse.down();
  await tech.mouse.move(box.x + 150, box.y + 70, { steps: 6 });
  await tech.mouse.up();
  await tech.getByRole('button', { name: 'Save signature' }).click();
  await expect(tech.getByRole('list', { name: 'Signatures' }).getByRole('listitem')).toHaveCount(1);

  await tech.getByRole('button', { name: 'Complete', exact: true }).click();
  await tech.getByRole('dialog').getByLabel('What was done').fill('Replaced the thermostat');
  await tech.getByRole('dialog').getByRole('button', { name: 'Confirm' }).click();
  await expect(tech.getByText('Completed', { exact: true }).first()).toBeVisible();

  // Nothing has reached the server, and a reload (or a closed app) does not lose any of it.
  const before = (await (await api(`/work-orders/${wo.id}`)).json()) as { order: { status: string } };
  expect(before.order.status).toBe('Assigned');
  await tech.reload();
  await expect(tech.getByRole('status')).toContainText('waiting to be sent');
  await expect(tech.getByText('Completed', { exact: true }).first()).toBeVisible();

  // Let a while pass offline, so that sending late would be visible in the history.
  await tech.waitForTimeout(6000);

  // The connection returns: everything is sent, once.
  await tech.context().setOffline(false);
  await expect(tech.getByRole('status')).toHaveCount(0, { timeout: 40_000 });

  const after = (await (await api(`/work-orders/${wo.id}`)).json()) as {
    order: { status: string; closureCode: string; sla: { respondedAt: string | null } };
    events: { action: string; at: string }[];
    comments: { body: string }[];
  };
  expect(after.order.status).toBe('Completed');
  const started = after.events.find((e) => e.action === 'Started')!;
  expect(Math.abs(Date.parse(started.at) - startedAt)).toBeLessThan(8000); // the moment it was done, not the moment it was sent
  expect(Date.now() - Date.parse(started.at)).toBeGreaterThan(6000);
  expect(Date.parse(after.order.sla.respondedAt ?? started.at)).toBeLessThan(startedAt + 8000);
  expect(after.events.filter((e) => e.action === 'Completed')).toHaveLength(1);
  expect(after.comments.filter((c) => c.body === 'Tripped on a faulty thermostat')).toHaveLength(1);
  const checklist = (await (await api(`/work-orders/${wo.id}/checklist`)).json()) as { result: string }[];
  expect(checklist.every((i) => i.result === 'Done')).toBe(true);
  expect(((await (await api(`/work-orders/${wo.id}/time`)).json()) as unknown[]).length).toBe(1);
  expect(((await (await api(`/work-orders/${wo.id}/costs`)).json()) as unknown[]).length).toBe(1);
  expect(((await (await api(`/work-orders/${wo.id}/attachments`)).json()) as unknown[]).length).toBe(1);
  expect(((await (await api(`/work-orders/${wo.id}/signatures`)).json()) as unknown[]).length).toBe(1);

  await tech.getByRole('link', { name: 'My jobs', exact: true }).click();
  await expect(tech.getByText('No open jobs are assigned to you.')).toBeVisible({ timeout: 20_000 });
  await tech.context().close();
  await fetch(`${IDP}/__user`, { method: 'POST', body: JSON.stringify({ sub: 'e2e-admin', name: 'E2E Admin', email: 'admin@example.test' }) });
});

test('offline: work the server can no longer accept is set aside for the person to see, not lost or forced through', async ({ browser }) => {
  test.setTimeout(120_000);
  const site = ((await (await api(`/locations?q=${SITE}`)).json()) as { items: { id: string }[] }).items[0];
  const users = (await (await api('/users')).json()) as { id: string; email: string }[];
  const techId = users.find((u) => u.email === `tech-${SUFFIX.toLowerCase()}@example.test`)!.id;
  const made = await api('/work-orders', { method: 'POST', body: JSON.stringify({ title: 'Door closer loose', type: 'Emergency', locationId: site.id }) });
  const wo = (await made.json()) as { id: string };
  expect((await api(`/work-orders/${wo.id}/assign`, { method: 'POST', body: JSON.stringify({ assigneeId: techId }) })).status).toBe(200);

  const tech = await personaPage(browser, `tech-${SUFFIX.toLowerCase()}`, 'Tina Technician');
  await expect(tech.getByRole('list', { name: 'My jobs' }).getByText('Door closer loose')).toBeVisible();
  await tech.getByRole('link', { name: /Door closer loose/ }).click();

  await tech.context().setOffline(true);
  await tech.getByRole('button', { name: 'Start work' }).click();
  await tech.getByLabel('Add a comment').fill('Looking at it now');
  await tech.getByRole('button', { name: 'Post comment' }).click();
  await expect(tech.getByRole('status')).toContainText('2 waiting to be sent');

  // Meanwhile the dispatcher cancels the job.
  expect((await api(`/work-orders/${wo.id}/cancel`, { method: 'POST', body: JSON.stringify({ reason: 'Fixed by the contractor' }) })).status).toBe(200);

  await tech.context().setOffline(false);
  await expect(tech.getByRole('status')).toContainText('need attention', { timeout: 40_000 });
  await tech.goto('/jobs');
  await expect(tech.getByRole('alert')).toContainText('These could not be sent');
  await expect(tech.getByRole('alert')).toContainText('Start work');
  await shot('24-sync-issues');

  // Discarding clears the notice; the cancelled job is gone from the phone's list.
  await tech.getByRole('alert').getByRole('button', { name: 'Discard' }).first().click();
  await tech.getByRole('alert').getByRole('button', { name: 'Discard' }).first().click().catch(() => undefined);
  await expect(tech.getByRole('alert')).toHaveCount(0);
  await tech.context().close();
  await fetch(`${IDP}/__user`, { method: 'POST', body: JSON.stringify({ sub: 'e2e-admin', name: 'E2E Admin', email: 'admin@example.test' }) });
});

test('offline: with the session expired and no connection, the saved jobs still open, and the work is sent after signing in again', async ({ browser }) => {
  test.setTimeout(150_000);
  const site = ((await (await api(`/locations?q=${SITE}`)).json()) as { items: { id: string }[] }).items[0];
  const users = (await (await api('/users')).json()) as { id: string; email: string }[];
  const techId = users.find((u) => u.email === `tech-${SUFFIX.toLowerCase()}@example.test`)!.id;
  const made = await api('/work-orders', { method: 'POST', body: JSON.stringify({ title: 'Lift display dark', type: 'Emergency', locationId: site.id }) });
  const wo = (await made.json()) as { id: string };
  expect((await api(`/work-orders/${wo.id}/assign`, { method: 'POST', body: JSON.stringify({ assigneeId: techId }) })).status).toBe(200);

  await fetch(`${IDP}/__ttl?seconds=20`); // sign-in tokens that last 20 seconds
  const tech = await personaPage(browser, `tech-${SUFFIX.toLowerCase()}`, 'Tina Technician');
  try {
    await expect(tech.getByRole('list', { name: 'My jobs' }).getByText('Lift display dark')).toBeVisible();
    await tech.evaluate(async () => { await navigator.serviceWorker.ready; });
    await tech.waitForFunction(async () => (await (await caches.open('fms-shell-v1')).keys()).length > 3);
    await fetch(`${IDP}/__ttl?seconds=3600`);

    await tech.context().setOffline(true);
    await tech.getByRole('link', { name: /Lift display dark/ }).click();
    await tech.getByLabel('Add a comment').fill('Display replaced');
    await tech.getByRole('button', { name: 'Post comment' }).click();
    await expect(tech.getByRole('status')).toContainText('1 waiting to be sent');

    await tech.waitForTimeout(25_000); // the session is now expired, and it cannot be renewed
    await tech.reload();
    await expect(tech.getByRole('heading', { name: /Lift display dark/ })).toBeVisible();
    await expect(tech.getByRole('status')).toContainText('Offline');
    await expect(tech.getByRole('link', { name: 'Assets', exact: true })).toHaveCount(0); // only the saved jobs
    await tech.goto('/assets');
    await expect(tech.getByRole('heading', { name: 'You are offline' })).toBeVisible();

    // Back online: the work waits for a sign-in, then goes.
    await tech.context().setOffline(false);
    await expect(tech.getByRole('button', { name: 'Sign in to send your work' })).toBeVisible({ timeout: 20_000 });
    expect((await (await api(`/work-orders/${wo.id}`)).json() as { comments: unknown[] }).comments).toHaveLength(0);
    await tech.getByRole('button', { name: 'Sign in to send your work' }).click();
    // The status line is also absent while the page is away at the identity provider, so wait for the server to have the comment instead.
    const sentCount = async () => ((await (await api(`/work-orders/${wo.id}`)).json()) as { comments: { body: string }[] }).comments.filter((c) => c.body === 'Display replaced').length;
    await expect.poll(sentCount, { timeout: 60_000 }).toBe(1);
    await expect(tech.getByRole('status')).toHaveCount(0, { timeout: 40_000 });
    expect(await sentCount()).toBe(1); // exactly once
  } finally {
    await fetch(`${IDP}/__ttl?seconds=3600`);
    await tech.context().close();
    await fetch(`${IDP}/__user`, { method: 'POST', body: JSON.stringify({ sub: 'e2e-admin', name: 'E2E Admin', email: 'admin@example.test' }) });
  }
});

test('preventive maintenance: a template and a schedule create a work order ahead of its due date, shown on the forecast', async () => {
  await page.goto('/pm?tab=templates');
  await page.getByRole('button', { name: 'New template' }).click();
  const template = page.getByRole('dialog', { name: 'New template' });
  await template.getByRole('textbox', { name: 'Name *' }).fill('Quarterly chiller service');
  await template.getByLabel('Instructions').fill('Follow the OEM sheet');
  await template.getByRole('button', { name: 'Save' }).click();
  await expect(page.getByRole('dialog')).toHaveCount(0);
  await expect(page.getByText('Quarterly chiller service')).toBeVisible();

  await page.getByRole('tab', { name: 'Schedules' }).click();
  await page.getByRole('button', { name: 'New schedule' }).click();
  const schedule = page.getByRole('dialog', { name: 'New schedule' });
  await schedule.getByRole('textbox', { name: 'Name *' }).fill('Chiller quarterly');
  await schedule.getByRole('combobox', { name: 'Template *' }).selectOption({ label: 'Quarterly chiller service' });
  await schedule.getByLabel('Find an asset').fill(ASSET);
  await schedule.getByRole('combobox', { name: 'Matching assets' }).selectOption({ index: 1 });
  await schedule.getByRole('button', { name: 'Save' }).click();
  await expect(page.getByRole('dialog')).toHaveCount(0);
  await expect(page.getByRole('cell', { name: /Chiller quarterly/ })).toBeVisible();
  await shot('20-pm-schedules');

  // A new schedule is picked up straight away when it is due inside its lead time; the button forces another pass.
  await page.getByRole('tab', { name: 'Forecast' }).click();
  await page.getByRole('button', { name: 'Create due work orders now' }).click();
  await expect(page.getByRole('status').first()).toBeVisible();
  await expect(page.getByRole('link', { name: /WO-\d+/ }).first()).toBeVisible();
  await expect(page.getByRole('cell', { name: /Chiller quarterly/ }).first()).toBeVisible();
  await shot('21-pm-forecast');

  // Compliance: the visit just created is upcoming, so nothing is overdue and nobody is counted against yet.
  await page.getByRole('tab', { name: 'Compliance' }).click();
  await expect(page.getByRole('heading', { name: 'By template' })).toBeVisible();
  await expect(page.getByRole('row', { name: /Quarterly chiller service/ })).toBeVisible();
  await shot('22-pm-compliance');

  for (const lang of ['en', 'ar']) {
    await page.locator('.prefs select').first().selectOption(lang);
    for (const tab of ['forecast', 'schedules', 'templates', 'compliance']) {
      await page.goto(`/pm?tab=${tab}`);
      await expect(page.getByRole('tabpanel')).toBeVisible();
      await expectNoSeriousA11yIssues(`pm ${tab} ${lang}`);
    }
  }
  await page.locator('.prefs select').first().selectOption('en');
});

test('inventory: a store, an item, stock received, parts reserved and used on a work order, and a stock take that needs a second person', async () => {
  const STORE = `MAIN-${SUFFIX}`;
  const ITEM = `FLT-${SUFFIX}`;
  await page.goto('/inventory?tab=stores');
  await page.getByRole('button', { name: 'New store' }).click();
  const store = page.getByRole('dialog', { name: 'New store' });
  await store.getByLabel('Code').fill(STORE);
  await store.getByRole('textbox', { name: 'Name *' }).fill('Main storeroom');
  await store.getByRole('button', { name: 'Save' }).click();
  await expect(page.getByRole('dialog')).toHaveCount(0);
  await expect(page.getByText('Main storeroom')).toBeVisible();

  await page.getByRole('tab', { name: 'Items' }).click();
  await page.getByRole('button', { name: 'New item' }).click();
  const item = page.getByRole('dialog', { name: 'New item' });
  await item.getByRole('textbox', { name: 'Code *' }).fill(ITEM);
  await item.getByRole('textbox', { name: 'Name *' }).fill('Air filter G4');
  await item.getByRole('button', { name: 'Save' }).click();
  await expect(page.getByRole('dialog')).toHaveCount(0);
  await expect(page.getByRole('cell', { name: ITEM })).toBeVisible();

  // Receive stock: the item is found by typing, the cost sets the store's average.
  await page.getByRole('tab', { name: 'Stock', exact: true }).click();
  await page.getByRole('button', { name: 'Receive stock' }).click();
  const receive = page.getByRole('dialog', { name: 'Receive stock' });
  await receive.getByRole('combobox', { name: 'Store' }).selectOption({ index: 1 });
  await receive.getByLabel('Find an item').fill(ITEM);
  await receive.getByRole('combobox', { name: 'Matching items' }).selectOption({ index: 1 });
  await receive.getByLabel('Quantity').fill('10');
  await receive.getByLabel('Unit cost').fill('5');
  await receive.getByRole('button', { name: 'Save' }).click();
  await expect(page.getByRole('dialog')).toHaveCount(0);
  const row = page.getByRole('row', { name: new RegExp(ITEM) });
  await expect(row).toContainText('10 pcs');

  // A reorder level of 4: the row shows it and is not low yet.
  await row.getByRole('button', { name: 'Reorder level' }).click();
  const reorder = page.getByRole('dialog', { name: /Reorder level/ });
  await reorder.getByLabel('Minimum').fill('4');
  await reorder.getByLabel('Maximum').fill('20');
  await reorder.getByRole('button', { name: 'Save' }).click();
  await expect(page.getByRole('dialog')).toHaveCount(0);
  await expect(row).toContainText('4 – 20');
  await shot('23-inventory-stock');

  // Parts on a work order: held, then used.
  const site = ((await (await api(`/locations?q=${SITE}`)).json()) as { items: { id: string }[] }).items[0];
  const wo = (await (await api('/work-orders', { method: 'POST', body: JSON.stringify({ title: 'Replace AHU filter', type: 'Emergency', locationId: site.id }) })).json()) as { id: string };
  await page.goto(`/work-orders/${wo.id}?tab=parts`);
  const add = page.getByRole('group', { name: 'Add a part' });
  await add.getByLabel('Find an item').fill(ITEM);
  await add.getByRole('combobox', { name: 'Matching items' }).selectOption({ index: 1 });
  await add.getByLabel('Quantity').fill('3');
  await add.getByRole('button', { name: 'Reserve' }).click();
  const line = page.getByRole('row', { name: new RegExp(ITEM) });
  await expect(line).toContainText('3 pcs');
  await line.getByRole('button', { name: 'Issue', exact: true }).click();
  const issue = page.getByRole('dialog', { name: /Issue/ });
  await expect(issue.getByLabel('Quantity')).toHaveValue('3');
  await issue.getByRole('button', { name: 'Confirm' }).click();
  await expect(page.getByRole('dialog')).toHaveCount(0);
  await expect(page.getByRole('row', { name: /Parts cost/ })).toContainText('15');
  await shot('24-work-order-parts');

  // Using more than there is, is refused in words.
  await add.getByLabel('Find an item').fill(ITEM);
  await add.getByRole('combobox', { name: 'Matching items' }).selectOption({ index: 1 });
  await add.getByLabel('Quantity').fill('50');
  await add.getByRole('button', { name: 'Issue now' }).click();
  await expect(add.getByText(/No store has that much available/)).toBeVisible();

  // A stock take: the same person cannot count, submit and approve their own work.
  await page.goto('/inventory?tab=takes');
  await page.getByRole('button', { name: 'Start a stock take' }).click();
  const start = page.getByRole('dialog', { name: 'Start a stock take' });
  await start.getByRole('combobox', { name: 'Store' }).selectOption({ index: 1 });
  await start.getByRole('button', { name: 'Save' }).click();
  const take = page.getByRole('dialog', { name: /Stock take:/ });
  await take.getByLabel(new RegExp(`Count of ${ITEM}`)).fill('6');
  await take.getByRole('button', { name: 'Save counts' }).click();
  await expect(take.getByLabel(new RegExp(`Count of ${ITEM}`))).toHaveValue('6');
  await take.getByRole('button', { name: 'Submit for approval' }).click();
  await expect(take.getByText('Waiting for approval').first()).toBeVisible();
  await take.getByRole('button', { name: 'Approve and post differences' }).click();
  await expect(take.getByRole('alert').first()).toBeVisible(); // refused: someone else must approve
  await expect(take.getByText('Waiting for approval').first()).toBeVisible();
  await take.getByRole('button', { name: 'Cancel stock take' }).click();
  await page.getByRole('dialog', { name: 'Cancel stock take' }).getByRole('textbox').fill('Practice run');
  await page.getByRole('dialog', { name: 'Cancel stock take' }).getByRole('button', { name: 'Confirm' }).click();
  await expect(take.getByText('Cancelled').first()).toBeVisible();
  await take.getByRole('button', { name: 'Back' }).click();

  for (const lang of ['en', 'ar']) {
    await page.locator('.prefs select').first().selectOption(lang);
    for (const tab of ['stock', 'items', 'stores', 'takes']) {
      await page.goto(`/inventory?tab=${tab}`);
      await expect(page.getByRole('tabpanel')).toBeVisible();
      await expectNoSeriousA11yIssues(`inventory ${tab} ${lang}`);
    }
    await page.goto(`/work-orders/${wo.id}?tab=parts`);
    await expect(page.getByRole('tabpanel')).toBeVisible();
    await expectNoSeriousA11yIssues(`work order parts ${lang}`);
  }
  await page.locator('.prefs select').first().selectOption('en');
});

test('vendors: a vendor and a contract with coverage and service targets, then a work order given to the vendor under it', async () => {
  const VENDOR = `VEN-${SUFFIX}`;
  const CONTRACT = `CT-${SUFFIX}`;
  const nextYear = new Date(Date.now() + 365 * 86_400_000).toISOString().slice(0, 10);
  await page.goto('/vendors');
  await page.getByRole('button', { name: 'New vendor' }).click();
  const vendor = page.getByRole('dialog', { name: 'New vendor' });
  await vendor.getByRole('textbox', { name: 'Code *' }).fill(VENDOR);
  await vendor.getByRole('textbox', { name: 'Name *' }).fill('CoolAir Services');
  await vendor.getByLabel('Email').fill('ops@coolair.test');
  await vendor.getByRole('button', { name: 'Save' }).click();
  await expect(page.getByRole('dialog')).toHaveCount(0);
  await expect(page.getByRole('row', { name: new RegExp(VENDOR) })).toContainText('Active');

  // A bad email is explained next to the field.
  await page.getByRole('button', { name: 'New vendor' }).click();
  const bad = page.getByRole('dialog', { name: 'New vendor' });
  await bad.getByRole('textbox', { name: 'Code *' }).fill(`${VENDOR}-X`);
  await bad.getByRole('textbox', { name: 'Name *' }).fill('X');
  await bad.getByLabel('Email').fill('nope');
  await bad.getByRole('button', { name: 'Save' }).click();
  await expect(bad.getByText(/valid email/)).toBeVisible();
  await bad.getByRole('button', { name: 'Cancel' }).click();

  await page.getByRole('tab', { name: 'Contracts' }).click();
  await page.getByRole('button', { name: 'New contract' }).click();
  const contract = page.getByRole('dialog', { name: 'New contract' });
  await contract.getByRole('combobox', { name: 'Vendor *' }).selectOption({ index: 1 });
  await contract.getByRole('textbox', { name: 'Number *' }).fill(CONTRACT);
  await contract.getByRole('textbox', { name: 'Title *' }).fill('Chiller AMC');
  await contract.getByLabel('Ends').fill(nextYear);
  await contract.getByRole('button', { name: 'Save' }).click();
  await expect(page.getByRole('dialog')).toHaveCount(0);
  const row = page.getByRole('row', { name: new RegExp(CONTRACT) });
  await expect(row).toContainText('Active');
  await row.getByRole('button', { name: 'Open' }).click();

  const open = page.getByRole('dialog', { name: new RegExp(`Contract ${CONTRACT}`) });
  await open.getByRole('combobox', { name: 'Which' }).selectOption({ index: 1 }); // a site
  await open.getByRole('button', { name: 'Add', exact: true }).click();
  await expect(open.getByRole('listitem').filter({ hasText: 'Riyadh HQ' })).toBeVisible();
  await open.getByLabel('Respond within (High)').fill('60');
  await open.getByLabel('Resolve within (High)').fill('240');
  await open.getByRole('button', { name: 'Save targets' }).click();
  await expect(open.getByLabel('Resolve within (High)')).toHaveValue('240');
  await shot('25-contract');
  await open.getByRole('button', { name: 'Back' }).click();

  // Give a work order to the vendor under the contract; its terms show on the work order.
  const site = ((await (await api(`/locations?q=${SITE}`)).json()) as { items: { id: string }[] }).items[0];
  const wo = (await (await api('/work-orders', { method: 'POST', body: JSON.stringify({ title: 'Chiller not cooling', type: 'Emergency', priority: 'High', locationId: site.id }) })).json()) as { id: string };
  await page.goto(`/work-orders/${wo.id}`);
  await page.getByRole('button', { name: 'Give to a vendor' }).click();
  const give = page.getByRole('dialog', { name: 'Give to a vendor' });
  await give.getByRole('combobox', { name: 'Vendor *' }).selectOption({ index: 1 });
  await give.getByRole('combobox', { name: 'Contract' }).selectOption({ index: 1 });
  await give.getByRole('button', { name: 'Confirm' }).click();
  await expect(page.getByRole('dialog')).toHaveCount(0);
  await expect(page.getByText('CoolAir Services', { exact: true })).toBeVisible();
  await expect(page.getByText(`under contract ${CONTRACT}`)).toBeVisible();
  await expect(page.getByText(/respond within 60 min, resolve within 240 min/)).toBeVisible();
  await shot('26-work-order-vendor');

  for (const lang of ['en', 'ar']) {
    await page.locator('.prefs select').first().selectOption(lang);
    for (const tab of ['vendors', 'contracts']) {
      await page.goto(`/vendors?tab=${tab}`);
      await expect(page.getByRole('tabpanel')).toBeVisible();
      await expectNoSeriousA11yIssues(`vendors ${tab} ${lang}`);
    }
    await page.goto(`/work-orders/${wo.id}`);
    await expect(page.getByRole('tabpanel')).toBeVisible();
    await expectNoSeriousA11yIssues(`work order with vendor ${lang}`);
  }
  await page.locator('.prefs select').first().selectOption('en');
});

test('purchasing: a technician asks, a different person approves, the order is made and received', async ({ browser }) => {
  const tech = await personaPage(browser, `tech-${SUFFIX.toLowerCase()}`, 'Tina Technician');
  try {
    await tech.getByRole('link', { name: 'Purchasing', exact: true }).click();
    await tech.getByRole('button', { name: 'New request' }).click();
    const draft = tech.getByRole('dialog', { name: 'New request' });
    await draft.getByRole('textbox', { name: 'Title *' }).fill('Crane hire for chiller swap');
    await draft.getByLabel('A stock item (goes into a store when it arrives)').uncheck();
    await draft.getByLabel('Description').fill('Mobile crane, per day');
    await draft.getByLabel('Quantity').fill('2');
    await draft.getByLabel('Estimated price (per unit)').fill('1500');
    await expect(draft.getByText('Total: 3,000')).toBeVisible();
    await draft.getByRole('button', { name: 'Save and submit for approval' }).click();
    await expect(tech.getByRole('dialog')).toHaveCount(0);
    await expect(tech.getByRole('row', { name: /Crane hire for chiller swap/ })).toContainText('Waiting for approval');
    await expect(tech.getByRole('tab', { name: 'Orders' })).toHaveCount(0); // not theirs to see
  } finally {
    await tech.context().close();
  }

  // The administrator is not the requester, and holds the highest level.
  await page.goto('/purchasing');
  await page.getByLabel('Waiting for my approval').check();
  const row = page.getByRole('row', { name: /Crane hire for chiller swap/ });
  await expect(row).toContainText('3,000');
  await row.getByRole('button', { name: 'Review' }).click();
  const review = page.getByRole('dialog', { name: /Crane hire for chiller swap/ });
  await expect(review.getByText('needs approval at level 1')).toBeVisible();
  await review.getByRole('button', { name: 'Approve', exact: true }).click();
  await page.getByRole('dialog', { name: 'Approve' }).getByRole('button', { name: 'Confirm' }).click();
  await expect(review.locator('.badge', { hasText: 'Approved' })).toBeVisible();

  // Order it from the vendor made earlier.
  await review.getByRole('button', { name: 'Make an order' }).click();
  const order = page.getByRole('dialog', { name: 'Make an order' });
  await order.getByRole('combobox', { name: 'Vendor *' }).selectOption({ index: 1 });
  await order.getByLabel(/^Mobile crane, per day/).fill('1600');
  await order.getByRole('button', { name: 'Issue the order' }).click();
  await expect(page.getByRole('dialog')).toHaveCount(0);

  await page.getByRole('tab', { name: 'Orders' }).click();
  const poRow = page.getByRole('row', { name: /PO-\d+/ }).first();
  await expect(poRow).toContainText('Issued');
  await poRow.getByRole('button', { name: 'Open' }).click();
  const po = page.getByRole('dialog', { name: /PO-\d+/ });
  await po.getByLabel('Quantity received of Mobile crane, per day').fill('2');
  await po.getByLabel('Delivery note').fill('DN-1');
  await po.getByRole('button', { name: 'Record receipt' }).click();
  await expect(po.locator('.badge', { hasText: 'Received' })).toBeVisible();
  await shot('27-purchase-order');
  await po.getByRole('button', { name: 'Back' }).click();

  for (const lang of ['en', 'ar']) {
    await page.locator('.prefs select').first().selectOption(lang);
    for (const tab of ['requests', 'orders', 'settings']) {
      await page.goto(`/purchasing?tab=${tab}`);
      await expect(page.getByRole('tabpanel')).toBeVisible();
      await expectNoSeriousA11yIssues(`purchasing ${tab} ${lang}`);
    }
  }
  await page.locator('.prefs select').first().selectOption('en');
});

test('accounting connection: turned on by an administrator, a new vendor is sent by the worker and shows its reference', async () => {
  await page.goto('/integrations');
  await expect(page.getByRole('heading', { name: 'Accounting connection' })).toBeVisible();
  await page.getByLabel('Send changes to the accounting system').check();
  await page.getByLabel('Company in the accounting system').fill('E2E');
  await page.getByRole('button', { name: 'Save' }).click();
  await expect(page.getByLabel('Send changes to the accounting system')).toBeChecked();
  // The box is ticked on screen before the save lands; a vendor made before the server has it would (rightly) not be queued.
  await expect.poll(async () => ((await (await api('/integration/erp/settings')).json()) as { enabled: boolean }).enabled).toBe(true);

  // A vendor made now is queued by the outbox, sent by the worker, and its reference comes back to the vendor.
  const code = `ERP-${SUFFIX}`;
  const made = await api('/vendors', { method: 'POST', body: JSON.stringify({ code, name: 'Synced Supplies' }) });
  expect(made.status).toBe(201);
  await expect(async () => {
    await page.goto('/integrations');
    await expect(page.getByRole('row', { name: new RegExp(`Vendor: ${code} Synced Supplies`) })).toContainText('Sent', { timeout: 1500 });
  }).toPass({ timeout: 40_000 });
  await expect(page.getByRole('row', { name: new RegExp(code) })).toContainText('FAKE-VENDOR-');
  await shot('28-accounting');

  await page.goto('/vendors');
  await page.getByLabel('Search').fill(code);
  await expect(page.getByRole('row', { name: new RegExp(code) })).toBeVisible();

  for (const lang of ['en', 'ar']) {
    await page.locator('.prefs select').first().selectOption(lang);
    await page.goto('/integrations');
    await expect(page.getByRole('table')).toBeVisible();
    await expectNoSeriousA11yIssues(`accounting ${lang}`);
  }
  await page.locator('.prefs select').first().selectOption('en');
});

test('vendor portal: an invited vendor user sees only their own jobs and contracts, and nothing of the buyer’s other screens', async ({ browser }) => {
  // The buyer gives a job to the vendor made earlier and invites one of the vendor's people.
  const VENDOR = `VEN-${SUFFIX}`;
  const vendors = (await (await api(`/vendors?q=${VENDOR}`)).json()) as { items: { id: string }[] };
  const vendorId = vendors.items[0].id;
  const site = ((await (await api(`/locations?q=${SITE}`)).json()) as { items: { id: string }[] }).items[0];
  const wo = (await (await api('/work-orders', { method: 'POST', body: JSON.stringify({ title: 'Replace pump seal', type: 'Emergency', priority: 'High', locationId: site.id }) })).json()) as { id: string };
  expect((await api(`/work-orders/${wo.id}/vendor`, { method: 'POST', body: JSON.stringify({ vendorId }) })).status).toBe(200);
  const other = (await (await api('/work-orders', { method: 'POST', body: JSON.stringify({ title: 'Internal only job', type: 'Emergency', priority: 'Low', locationId: site.id }) })).json()) as { id: string };
  expect(other.id).toBeTruthy();

  await page.goto('/vendors');
  await page.getByLabel('Search').fill(VENDOR);
  await page.getByRole('row', { name: new RegExp(VENDOR) }).getByRole('button', { name: 'Portal access' }).click();
  const access = page.getByRole('dialog', { name: /Portal access for/ });
  const sub = `vendor-${SUFFIX.toLowerCase()}`;
  await access.getByLabel('Email of the person to invite').fill(`${sub}@example.test`);
  await access.getByRole('button', { name: 'Create invitation' }).click();
  const link = await access.getByRole('textbox', { name: 'Invitation link' }).inputValue();
  expect(link).toContain('/portal/accept#token=');
  await shot('29-portal-invitation');
  await access.getByRole('button', { name: 'Close' }).click();

  // The vendor's person signs in (no role yet), opens the link, and ends up in the portal.
  const vendor = await personaPage(browser, sub, 'Vera Vendor');
  try {
    await expect(vendor.getByRole('heading', { name: 'No access' })).toBeVisible();
    await vendor.goto(link);
    await expect(vendor.getByText('You now have access.')).toBeVisible();
    expect(vendor.url()).not.toContain('token='); // the secret left the address bar
    await vendor.getByRole('link', { name: 'Open the portal' }).click();
    await expect(vendor.getByRole('heading', { name: 'Vendor portal' })).toBeVisible();
    await expect(vendor.getByRole('navigation').getByRole('link')).toHaveText(['My business', 'Notifications']);

    await vendor.getByRole('tab', { name: 'Jobs' }).click();
    await expect(vendor.getByRole('row', { name: /Replace pump seal/ })).toBeVisible();
    await expect(vendor.getByText('Internal only job')).toHaveCount(0);
    await vendor.getByRole('row', { name: /Replace pump seal/ }).getByRole('button', { name: 'Expand' }).click();
    await vendor.getByLabel('Write to the team').fill('We will be on site tomorrow morning.');
    await vendor.getByRole('button', { name: 'Send' }).click();
    await expect(vendor.getByText('We will be on site tomorrow morning.')).toBeVisible();
    await shot('30-vendor-portal');

    // The buyer's screens are closed to them, in the page and in the API.
    await vendor.goto('/vendors');
    await expect(vendor.getByRole('heading', { name: 'Vendor portal' })).toBeVisible(); // sent back to the portal
    expect(new URL(vendor.url()).pathname).toBe('/portal');
    for (const lang of ['en', 'ar']) {
      await vendor.locator('.prefs select').first().selectOption(lang);
      await vendor.goto('/portal');
      await expect(vendor.getByRole('tabpanel')).toBeVisible();
      await expectNoSeriousA11yIssues(`vendor portal ${lang}`, vendor);
    }
  } finally {
    await vendor.context().close();
  }
  expect((await api('/vendors', { sub })).status).toBe(403);
  expect((await api('/work-orders', { sub })).status).toBe(403);
});

const PNG = Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==', 'base64');

test('spaces: a plan with a pinned room, its capacity, an allocation that cannot overfill it, and occupancy', async () => {
  const floor = ((await (await api(`/locations?q=${FLOOR}`)).json()) as { items: { id: string }[] }).items[0];
  const made = await api('/locations', { method: 'POST', body: JSON.stringify({ type: 'Space', parentId: floor.id, code: `M1-${SUFFIX}`, name: 'Meeting room M1' }) });
  expect(made.status).toBe(201);

  await page.goto('/spaces');
  await choose(page.getByLabel('Floor'), FLOOR);
  await expect(page.getByText('This floor has no plan yet.')).toBeVisible();
  await page.getByLabel('Upload the floor plan').setInputFiles({ name: 'floor3.png', mimeType: 'image/png', buffer: PNG });
  await expect(page.getByRole('img', { name: 'Plan of Floor 3' })).toBeVisible();

  // Pin the room and move it by number, then save.
  await page.getByLabel('Space to place').selectOption({ index: 1 });
  await page.getByRole('button', { name: 'Place on plan' }).click();
  await page.getByLabel(/across the plan, percent/).fill('30');
  await page.getByLabel(/down the plan, percent/).fill('70');
  await Promise.all([page.waitForResponse((r) => r.url().includes('/pins') && r.request().method() === 'PUT' && r.ok()), page.getByRole('button', { name: 'Save' }).click()]);
  await expect(page.getByRole('button', { name: 'Save' })).toBeDisabled();
  await page.reload();
  await choose(page.getByLabel('Floor'), FLOOR);
  await expect(page.getByLabel(/across the plan, percent/)).toHaveValue('30');
  await shot('31-floor-plan');

  // Capacity, then an allocation that fits and one that does not.
  await page.goto('/spaces?tab=spaces');
  await choose(page.getByLabel('Place', { exact: true }), FLOOR);
  await page.getByRole('row', { name: new RegExp(`M1-${SUFFIX}`) }).getByRole('button', { name: 'Edit' }).click();
  const dialog = page.getByRole('dialog', { name: /Space M1/ });
  await dialog.getByLabel('Used as').selectOption('Meeting');
  await dialog.getByLabel('Capacity').fill('6');
  await dialog.getByRole('button', { name: 'Save' }).click();
  await expect(page.getByRole('dialog')).toHaveCount(0);

  await page.goto('/spaces?tab=allocations');
  await page.getByRole('button', { name: 'New allocation' }).click();
  const alloc = page.getByRole('dialog', { name: 'New allocation' });
  await choose(alloc.getByLabel('Floor'), FLOOR);
  await alloc.getByLabel('Space', { exact: true }).selectOption({ index: 1 });
  await alloc.getByLabel('Given to').selectOption('Team');
  await alloc.getByLabel('Team name').fill('Finance');
  await alloc.getByLabel('Number of people').fill('4');
  await alloc.getByRole('button', { name: 'Allocate' }).click();
  await expect(page.getByRole('dialog')).toHaveCount(0);
  await expect(page.getByRole('row', { name: /Finance/ })).toContainText('In force');

  await page.getByRole('button', { name: 'New allocation' }).click();
  const second = page.getByRole('dialog', { name: 'New allocation' });
  await choose(second.getByLabel('Floor'), FLOOR);
  await second.getByLabel('Space', { exact: true }).selectOption({ index: 1 });
  await second.getByLabel('Given to').selectOption('Team');
  await second.getByLabel('Team name').fill('Legal');
  await second.getByLabel('Number of people').fill('3');
  await second.getByRole('button', { name: 'Allocate' }).click();
  await expect(second.getByText(/do not fit/)).toBeVisible();
  await second.getByRole('button', { name: 'Cancel' }).click();
  await shot('32-allocations');

  await page.goto('/spaces?tab=occupancy');
  await choose(page.getByLabel('Place', { exact: true }), SITE);
  await expect(page.getByRole('row', { name: new RegExp(`M1-${SUFFIX}`) })).toContainText('66.7%');

  for (const lang of ['en', 'ar']) {
    await page.locator('.prefs select').first().selectOption(lang);
    for (const tab of ['plans', 'spaces', 'allocations', 'occupancy']) {
      await page.goto(`/spaces?tab=${tab}`);
      await expect(page.getByRole('tabpanel')).toBeVisible();
      await expectNoSeriousA11yIssues(`spaces ${tab} ${lang}`);
    }
  }
  await page.locator('.prefs select').first().selectOption('en');
});

test('bookings: a room is set up, booked without a clash, cancelled, one needing approval waits, and a calendar address follows the bookings', async () => {
  const tomorrow = new Date(Date.now() + 24 * 3600 * 1000).toISOString().slice(0, 10);
  const CODE = `BR-${SUFFIX}`;
  const NEEDS = `HALL-${SUFFIX}`;

  await page.goto('/bookings?tab=resources');
  for (const [code, name, approval] of [[CODE, 'Board room', false], [NEEDS, 'Event hall', true]] as const) {
    await page.getByRole('button', { name: 'New resource' }).click();
    const dialog = page.getByRole('dialog', { name: 'New resource' });
    await dialog.getByLabel('Code').fill(code);
    await dialog.getByLabel('Name', { exact: true }).fill(name);
    await dialog.getByLabel('Capacity').fill('8');
    if (approval) await dialog.getByLabel('Bookings need approval').check();
    await dialog.getByRole('button', { name: 'Save' }).click();
    await expect(page.getByRole('dialog')).toHaveCount(0);
    await expect(page.getByRole('row', { name: new RegExp(code) })).toBeVisible();
  }

  // A rule is enforced by the server and shown next to the field.
  await page.getByRole('button', { name: 'New resource' }).click();
  const bad = page.getByRole('dialog', { name: 'New resource' });
  await bad.getByLabel('Code').fill(`BAD-${SUFFIX}`);
  await bad.getByLabel('Name', { exact: true }).fill('Bad zone');
  await bad.getByLabel('Time zone').fill('Mars/Olympus');
  await bad.getByRole('button', { name: 'Save' }).click();
  await expect(bad.getByText('Unknown time zone')).toBeVisible();
  await bad.getByRole('button', { name: 'Cancel' }).click();

  const bookAt = async (resource: string, time: string, title: string) => {
    await page.goto('/bookings');
    await choose(page.getByLabel('Resource', { exact: true }), resource);
    await page.getByLabel('Day', { exact: true }).fill(tomorrow);
    await page.getByLabel('Title', { exact: true }).fill(title);
    await page.getByLabel('Starts at').fill(time);
    await page.getByRole('button', { name: /^(Book|Request booking)$/ }).click();
  };

  await bookAt(CODE, '10:00', 'Quarterly review');
  await expect(page.getByRole('status').filter({ hasText: 'Booked (1)' })).toBeVisible();
  await expect(page.getByRole('listitem').filter({ hasText: 'Yours' })).toBeVisible(); // it shows as taken, and as ours
  await bookAt(CODE, '10:30', 'Clashing meeting');
  await expect(page.getByRole('alert').filter({ hasText: 'already taken' })).toBeVisible();
  await shot('33-booking');

  await page.goto('/bookings?tab=mine');
  const row = page.getByRole('row', { name: /Quarterly review/ });
  await expect(row).toContainText('Confirmed');
  await row.getByRole('button', { name: 'Cancel booking' }).click();
  await page.getByRole('dialog').getByRole('button', { name: 'Cancel booking' }).click();
  await expect(page.getByRole('row', { name: /Quarterly review/ })).toContainText('Cancelled');

  // Something that needs approval waits; the person who asked cannot decide it.
  await bookAt(NEEDS, '14:00', 'Town hall');
  await expect(page.getByRole('status').filter({ hasText: 'Requested (1)' })).toBeVisible();
  await page.goto('/bookings?tab=approvals');
  const waiting = page.getByRole('row', { name: /Town hall/ });
  await expect(waiting).toContainText('Someone else must decide your own request');
  await expect(waiting.getByRole('button', { name: 'Approve' })).toHaveCount(0);

  // A calendar address follows the bookings; it works without signing in and is shown only once.
  await page.goto('/bookings?tab=mine');
  await page.getByRole('button', { name: 'Make an address' }).click();
  const address = await page.getByLabel('Calendar address').inputValue();
  expect(address).toContain('/api/v1/calendar/');
  const feed = await page.request.get(address.replace(/^https?:\/\/[^/]+/, ''), { headers: { Authorization: '' } });
  expect(feed.status()).toBe(200);
  expect(await feed.text()).toContain('SUMMARY:Town hall');
  await page.reload();
  await expect(page.getByLabel('Calendar address')).toHaveCount(0);
  await page.getByRole('button', { name: 'Stop sharing' }).click();
  await expect(page.getByRole('button', { name: 'Make an address' })).toBeVisible();
  expect((await page.request.get(address.replace(/^https?:\/\/[^/]+/, ''))).status()).toBe(404);

  for (const lang of ['en', 'ar']) {
    await page.locator('.prefs select').first().selectOption(lang);
    for (const tab of ['book', 'mine', 'approvals', 'resources']) {
      await page.goto(`/bookings?tab=${tab}`);
      await expect(page.getByRole('tabpanel')).toBeVisible();
      await expectNoSeriousA11yIssues(`bookings ${tab} ${lang}`);
    }
  }
  await page.locator('.prefs select').first().selectOption('en');
});

test('visitors: a visitor is expected, opens their pass without signing in, is checked in at the desk and out again, and a watchlisted name needs security', async ({ browser }) => {
  await page.goto('/visitors');
  await page.getByRole('button', { name: 'Expect a visitor' }).click();
  const dialog = page.getByRole('dialog', { name: 'Expect a visitor' });
  await dialog.getByRole('textbox', { name: 'Name *' }).fill('Layla Hassan');
  await dialog.getByLabel('Company').fill('Acme Trading');
  await dialog.getByRole('button', { name: 'Expect visitor' }).click();
  await expect(dialog.getByText('Site is required').or(dialog.getByText(/choose the site/i))).toBeVisible(); // the server says what is missing, next to the field
  await choose(dialog.getByLabel('Site'), SITE);
  await dialog.getByRole('button', { name: 'Expect visitor' }).click();

  const passDialog = page.getByRole('dialog', { name: /Pass for Layla Hassan/ });
  await expect(passDialog.getByRole('img', { name: /QR code/ })).toBeVisible();
  const address = await passDialog.getByLabel('Pass address').inputValue();
  expect(address).toContain('/visit/');
  await shot('34-visitor-pass-host');
  await passDialog.getByRole('button', { name: 'Done' }).click();
  await expect(page.getByRole('row', { name: /Layla Hassan/ })).toContainText('Expected');

  // The visitor opens the pass with no account: only their own details, a QR code, nothing else.
  const guest = await (await browser.newContext()).newPage();
  try {
    await guest.goto(address);
    await expect(guest.getByRole('heading', { name: 'Visitor pass' })).toBeVisible();
    await expect(guest.getByText('Layla Hassan')).toBeVisible();
    await expect(guest.getByRole('img', { name: /QR code/ })).toBeVisible();
    await expect(guest.getByText('Acme Trading')).toHaveCount(0);
    await shot('35-visitor-pass');
    await guest.goto('/visit/AAAAAAAAAAAAAAAAAAAAAA');
    await expect(guest.getByText('This pass is not valid')).toBeVisible();
    for (const lang of ['en', 'ar']) {
      await guest.goto(address);
      await guest.locator('select').first().selectOption(lang);
      await expect(guest.getByRole('img', { name: /QR/ })).toBeVisible();
      await expectNoSeriousA11yIssues(`visitor pass ${lang}`, guest);
    }
  } finally {
    await guest.context().close();
  }

  // Reception pastes what the scanner typed, checks the visitor in with a badge, then out.
  await page.goto('/visitors?tab=desk');
  await page.getByLabel('Scan or type the pass code').fill(address);
  await page.getByRole('button', { name: 'Find visit' }).click();
  const checkIn = page.getByRole('dialog', { name: /Check in Layla Hassan/ });
  await checkIn.getByLabel('Badge number').fill('B-7');
  await checkIn.getByRole('button', { name: 'Check in' }).click();
  const badge = page.getByRole('dialog', { name: 'Visitor badge' });
  await expect(badge.getByText('B-7')).toBeVisible();
  await badge.getByRole('button', { name: 'Done' }).click();
  await page.getByLabel('Show').selectOption('onsite');
  const onSite = page.getByRole('row', { name: /Layla Hassan/ });
  await expect(onSite).toContainText('On site');
  await shot('36-front-desk');
  await onSite.getByRole('button', { name: 'Check out' }).click();
  await expect(page.getByText('Nobody is on site.')).toBeVisible();

  // A watchlisted name stops the desk; security lets them in with a reason.
  await page.goto('/visitors?tab=security');
  await page.getByLabel('Name', { exact: true }).fill('Bad Actor');
  await page.getByLabel('Reason', { exact: true }).fill('Trespassed in 2025');
  await page.getByRole('button', { name: 'Add to watchlist' }).click();
  await expect(page.getByRole('row', { name: /Bad Actor/ })).toBeVisible();

  await page.goto('/visitors');
  await page.getByRole('button', { name: 'Expect a visitor' }).click();
  const second = page.getByRole('dialog', { name: 'Expect a visitor' });
  await second.getByRole('textbox', { name: 'Name *' }).fill('bad  ACTOR');
  await choose(second.getByLabel('Site'), SITE);
  await second.getByRole('button', { name: 'Expect visitor' }).click();
  await page.getByRole('dialog', { name: /Pass for/ }).getByRole('button', { name: 'Done' }).click();

  await page.goto('/visitors?tab=desk');
  await page.getByRole('row', { name: /bad {1,2}ACTOR/i }).getByRole('button', { name: 'Check in' }).click();
  const blocked = page.getByRole('dialog', { name: /Check in bad/i });
  await blocked.getByRole('button', { name: 'Check in' }).click();
  await expect(blocked.getByRole('alert').filter({ hasText: 'call security' })).toBeVisible();
  await expect(blocked.getByText('Trespassed')).toHaveCount(0); // reception-level text never says why
  await blocked.getByLabel('Why may this visitor come in?').fill('Cleared by the site manager');
  await blocked.getByRole('button', { name: 'Let in' }).click();
  await page.getByRole('dialog', { name: 'Visitor badge' }).getByRole('button', { name: 'Done' }).click();
  await page.getByLabel('Show').selectOption('onsite');
  await expect(page.getByRole('row', { name: /bad {1,2}ACTOR/i })).toContainText('On site');

  for (const lang of ['en', 'ar']) {
    await page.locator('.prefs select').first().selectOption(lang);
    for (const tab of ['mine', 'desk', 'security']) {
      await page.goto(`/visitors?tab=${tab}`);
      await expect(page.getByRole('tabpanel')).toBeVisible();
      await expectNoSeriousA11yIssues(`visitors ${tab} ${lang}`);
    }
  }
  await page.locator('.prefs select').first().selectOption('en');
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
    await page.goto('/setup?section=sla');
    await expect(page.getByRole('table').first()).toBeVisible();
    await expectNoSeriousA11yIssues(`service levels ${lang}`);
    await page.goto('/notifications');
    await expect(page.getByRole('list').first()).toBeVisible();
    await expectNoSeriousA11yIssues(`notifications ${lang}`);
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
