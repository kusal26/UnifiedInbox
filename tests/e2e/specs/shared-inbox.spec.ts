import { test, expect } from '@playwright/test';
test('agent can open the shared inbox', async ({ page }) => { await page.goto('/'); await page.getByRole('button', { name: 'Open inbox' }).click(); await expect(page.getByRole('heading', { name: 'Shared inbox' })).toBeVisible(); await expect(page.getByText('Jamie Customer')).toBeVisible(); });
