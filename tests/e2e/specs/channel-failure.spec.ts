import { test, expect } from '@playwright/test';
test('channel health is visible in the inbox shell', async ({ page }) => { await page.goto('/'); await page.getByRole('button', { name: 'Open inbox' }).click(); await expect(page.getByText('WhatsApp connected')).toBeVisible(); });
