import { test, expect } from '@playwright/test';
test('login requires a known tenant workspace', async ({ page }) => { await page.goto('/'); await page.getByLabel('Workspace slug').fill('unknown'); await page.getByRole('button', { name: 'Open inbox' }).click(); await expect(page.getByRole('heading', { name: 'Welcome back' })).toBeVisible(); });
