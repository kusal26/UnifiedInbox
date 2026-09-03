import { test, expect, type APIRequestContext } from '@playwright/test';

const API = process.env.API_URL ?? 'http://127.0.0.1:5020';
const MAILPIT = process.env.MAILPIT_URL ?? 'http://localhost:8025';
const PASSWORD = 'e2e-test-password-1';

export function slug(prefix: string) {
  return `${prefix}-${Date.now().toString(36)}`.slice(0, 48);
}

async function mailpitToken(request: APIRequestContext, to: string, attempts = 30): Promise<string> {
  for (let attempt = 0; attempt < attempts; attempt += 1) {
    const list = await request.get(`${MAILPIT}/api/v1/messages`);
    const body = await list.json();
    const messages = (body.messages ?? []) as Array<{ ID: string; To: Array<{ Address: string }> }>;
    const match = messages.find((message) => message.To?.some((recipient) => recipient.Address === to));
    if (match) {
      const detail = await request.get(`${MAILPIT}/api/v1/message/${match.ID}`);
      const text = (await detail.json()).Text as string;
      return text.split('token: ').at(-1)!.trim();
    }
    await new Promise((resolve) => setTimeout(resolve, 1000));
  }
  throw new Error(`no email arrived for ${to}`);
}

export async function registerOwner(request: APIRequestContext, workspaceSlug: string, email: string) {
  const response = await request.post(`${API}/api/v1/auth/register`, {
    data: { workspaceName: workspaceSlug, workspaceSlug, displayName: 'Owner', email, password: PASSWORD },
  });
  expect(response.status()).toBe(202);
  const token = await mailpitToken(request, email);
  const verified = await request.post(`${API}/api/v1/auth/verify-email`, { data: { token } });
  expect(verified.ok()).toBeTruthy();
}

export async function loginAs(request: APIRequestContext, workspaceSlug: string, email: string, page?: import('@playwright/test').Page) {
  if (page) {
    await page.goto('/login');
    await page.getByLabel('Workspace slug').fill(workspaceSlug);
    await page.getByLabel(/^Email/).fill(email);
    await page.getByLabel(/^Password/).fill(PASSWORD);
    await page.getByRole('button', { name: 'Open inbox' }).click();
    await expect(page.getByRole('heading', { name: 'Conversations' })).toBeVisible();
    return;
  }
  const response = await request.post(`${API}/api/v1/auth/login`, { data: { tenantSlug: workspaceSlug, email, password: PASSWORD } });
  expect(response.ok()).toBeTruthy();
  return (await response.json()).accessToken as string;
}

export { mailpitToken, PASSWORD, API };
