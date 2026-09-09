import createClient from 'openapi-fetch';
import type { paths, components } from './generated/api';
export const api = createClient<paths>({ baseUrl: '', credentials: 'same-origin' });
api.use({ onRequest({request}) {
  if (!['GET','HEAD'].includes(request.method)) request.headers.set('content-type','application/json');
  return request;
} });
export type Snapshot = components['schemas']['Snapshot'];
export type AgentTask = components['schemas']['AgentTask'];
export type Completion = components['schemas']['Completion'];
export type Device = components['schemas']['DeviceView'];
export type Pairing = components['schemas']['PairingResponse'];
export function unwrap<T>(result: { data?: T; error?: unknown; response: Response }): T {
  if (!result.response.ok || result.data === undefined) {
    const error = result.error;
    throw new Error(typeof error === 'object' && error !== null && 'error' in error ? String(error.error) : 'Request failed (' + result.response.status + ')');
  }
  return result.data;
}
