import { defineConfig } from '@playwright/test';
export default defineConfig({ testDir: './specs', use: { baseURL: process.env.BASE_URL ?? 'http://localhost:5173' }, webServer: { command: 'bun --cwd ../../src/frontend dev', url: 'http://localhost:5173', reuseExistingServer: true } });
